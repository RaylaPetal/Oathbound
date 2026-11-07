using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Oathbound.Plugin.Ipc;

namespace Oathbound.Plugin.Commands;

public enum NavStatus
{
    Idle,
    Preparing,
    Mounting,
    Moving,
    Stuck,
    Landing,
    Arrived,
    /// No path at all before moving began.
    NoPath,
    /// The goal can't be reached on foot and flying isn't possible here.
    Unreachable,
}

/// What differs between the journeys that share a Navigator.
public sealed class NavOptions
{
    /// Read every check, so a moving goal is followed.
    public required Func<Vector3> Goal { get; init; }

    /// Where the journey ends; Landing then dismounts.
    public required Func<Vector3, bool> IsAtGoal { get; init; }

    public float GroundSearchXZ { get; init; } = 5f;
    public float GroundSearchY { get; init; } = 5f;

    /// A flying path ends on the floor under the goal rather than at the goal itself.
    public bool LandOnFloor { get; init; }

    /// Null: any ground path is followed. Otherwise a ground path ending where this is false mounts to fly there,
    /// and is Unreachable when flying isn't possible.
    public Func<Vector3, bool>? GroundPathReaches { get; init; }
}

/// One vnavmesh journey on the local character: Mount Roulette when the goal is far (or out of reach on foot), a
/// flying path while mounted somewhere flying is allowed, the ground otherwise, then landing and dismounting at the
/// goal. Stuck retries until the owner gives up. Stop never dismounts.
public sealed class Navigator
{
    private const float MountDistance = 40f;
    private const float RetargetDistance = 3f;
    private const float ProgressMinDelta = 1f;
    private const float FloorSearch = 2f;
    private const uint MountRouletteAction = 9;
    private const uint DismountAction = 23;

    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DismountTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProgressWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly VnavmeshIpc vnavmesh;
    private readonly string name;

    private NavOptions? options;
    private NavStatus status;
    private DateTime statusSince;
    private bool mountTried;
    private bool dismountSent;

    private CancellationTokenSource? pathCancel;
    private Task<List<Vector3>>? pendingPath;
    private bool pendingPathFly;
    private bool flying;
    private bool navigationStarted;
    private Vector3 navTarget;
    private DateTime lastCheck;
    private DateTime lastRetry;
    private DateTime progressWindowStart;
    private float progressWindowDistance;

    public Navigator(VnavmeshIpc vnavmesh, string name)
    {
        this.vnavmesh = vnavmesh;
        this.name = name;
    }

    public NavStatus Status => status;

    /// While its own Mount Roulette is in flight.
    public bool IsMounting => status == NavStatus.Mounting;

    public void Start(NavOptions journey)
    {
        Cancel();
        options = journey;
        mountTried = false;
        dismountSent = false;
        flying = false;
        navigationStarted = false;
        Enter(NavStatus.Preparing);
    }

    public NavStatus Tick()
    {
        switch (status)
        {
            case NavStatus.Preparing:
                if (Plugin.ObjectTable.LocalPlayer is not null && vnavmesh.TryIsReady())
                    BeginMountingOrMoving();
                break;
            case NavStatus.Mounting:
                if (Plugin.Condition[ConditionFlag.Mounted] || DateTime.UtcNow - statusSince > MountTimeout)
                    BeginMoving();
                break;
            case NavStatus.Moving:
            case NavStatus.Stuck:
                UpdateMoving();
                break;
            case NavStatus.Landing:
                UpdateLanding();
                break;
        }
        return status;
    }

    /// Drops the journey without touching vnavmesh, for when something else already stopped it.
    public void Cancel()
    {
        CancelPendingPath();
        status = NavStatus.Idle;
    }

    public void Stop()
    {
        Cancel();
        vnavmesh.TryStop();
    }

    private void Enter(NavStatus next)
    {
        status = next;
        statusSince = DateTime.UtcNow;
    }

    private Vector3 Goal() => options!.Goal();

    private void BeginMountingOrMoving()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null || Plugin.Condition[ConditionFlag.Mounted] || Vector3.Distance(player.Position, Goal()) <= MountDistance || !TryMount())
            BeginMoving();
    }

    /// Mount Roulette only when the game says it's usable here.
    private unsafe bool TryMount()
    {
        var actions = ActionManager.Instance();
        if (actions == null || actions->GetActionStatus(ActionType.GeneralAction, MountRouletteAction) != 0)
            return false;
        mountTried = true;
        // Before UseAction: the caged mount block lets this one through only while Mounting.
        Enter(NavStatus.Mounting);
        actions->UseAction(ActionType.GeneralAction, MountRouletteAction);
        return true;
    }

    private void BeginMoving()
    {
        Enter(NavStatus.Moving);
        flying = Plugin.Condition[ConditionFlag.Mounted] && Player.CanFly;
        ResetProgressWindow();
        RequestPath(flying);
    }

    /// Only the current pendingPath is polled, so a path finishing after Stop or a retarget is discarded.
    private void RequestPath(bool fly)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
            return;

        CancelPendingPath();
        var goal = Goal();
        navTarget = goal;
        var to = fly
            ? options!.LandOnFloor ? vnavmesh.TryPointOnFloor(goal, false, FloorSearch) ?? goal : goal
            : vnavmesh.TryNearestPointReachable(goal, options!.GroundSearchXZ, options.GroundSearchY) ?? goal;

        pathCancel = new CancellationTokenSource();
        pendingPathFly = fly;
        pendingPath = vnavmesh.TryPathfind(player.Position, to, fly, pathCancel.Token);
        lastRetry = DateTime.UtcNow;
        if (pendingPath is null)
            OnPathResult(null);
    }

    private void CancelPendingPath()
    {
        pathCancel?.Cancel();
        pathCancel?.Dispose();
        pathCancel = null;
        pendingPath = null;
    }

    private void UpdateMoving()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
            return;

        if (pendingPath is { IsCompleted: true } done)
        {
            pendingPath = null;
            OnPathResult(done.IsCompletedSuccessfully ? done.Result : null);
            if (status is not (NavStatus.Moving or NavStatus.Stuck))
                return;
        }

        if (options!.IsAtGoal(player.Position))
        {
            BeginLanding();
            return;
        }

        var now = DateTime.UtcNow;
        if (now - lastCheck < CheckInterval)
            return;
        lastCheck = now;

        var goal = Goal();
        // Keep the current path until the new one arrives.
        if (pendingPath is null && Vector3.Distance(goal, navTarget) > RetargetDistance)
        {
            RequestPath(flying);
            return;
        }

        UpdateProgress(player.Position, goal, now);
    }

    private void OnPathResult(List<Vector3>? path)
    {
        var reaches = options!.GroundPathReaches;
        if (path is { Count: > 0 })
        {
            if (!pendingPathFly && reaches is not null && !reaches(path[^1]))
            {
                OnGroundCantReach();
                return;
            }
            if (vnavmesh.TryMoveTo(path, pendingPathFly))
                navigationStarted = true;
            return;
        }

        // A flying path can fail near overhangs or interiors - fall back to the ground.
        if (pendingPathFly)
        {
            flying = false;
            RequestPath(false);
            return;
        }

        if (reaches is not null)
        {
            OnGroundCantReach();
            return;
        }
        if (!navigationStarted)
        {
            Enter(NavStatus.NoPath);
            return;
        }
        EnterStuck();
    }

    /// The ground runs out short of the goal: mount once to fly there. Reaching here mounted means flying already failed.
    private void OnGroundCantReach()
    {
        if (!Plugin.Condition[ConditionFlag.Mounted] && !mountTried && TryMount())
            return;
        Plugin.Log.Info($"{name} navigation: the goal can't be reached on foot and flying isn't possible here.");
        Enter(NavStatus.Unreachable);
    }

    private void UpdateProgress(Vector3 position, Vector3 goal, DateTime now)
    {
        var distance = Vector3.Distance(position, goal);
        if (pendingPath is not null)
        {
            ResetProgressWindow(distance);
            return;
        }

        if (progressWindowDistance - distance >= ProgressMinDelta)
        {
            if (status == NavStatus.Stuck)
                Enter(NavStatus.Moving);
            ResetProgressWindow(distance);
            return;
        }

        var pathStopped = navigationStarted && !vnavmesh.TryIsRunning();
        if (pathStopped || now - progressWindowStart >= ProgressWindow)
            EnterStuck();

        if (status == NavStatus.Stuck && now - lastRetry >= RetryInterval)
        {
            ResetProgressWindow(distance);
            RequestPath(flying);
        }
    }

    private void EnterStuck()
    {
        if (status == NavStatus.Stuck)
            return;
        Plugin.Log.Info($"{name} navigation appears stuck - retrying; movement stays locked.");
        Enter(NavStatus.Stuck);
    }

    private void ResetProgressWindow(float? distance = null)
    {
        progressWindowStart = DateTime.UtcNow;
        var player = Plugin.ObjectTable.LocalPlayer;
        progressWindowDistance = distance ?? (player is null ? float.MaxValue : Vector3.Distance(player.Position, Goal()));
    }

    private void BeginLanding()
    {
        CancelPendingPath();
        vnavmesh.TryStop();
        dismountSent = false;
        Enter(NavStatus.Landing);
    }

    /// In the air the game's dismount lands first.
    private unsafe void UpdateLanding()
    {
        if (!Plugin.Condition[ConditionFlag.Mounted])
        {
            Enter(NavStatus.Arrived);
            return;
        }
        if (!dismountSent)
        {
            var actions = ActionManager.Instance();
            if (actions != null)
                actions->UseAction(ActionType.GeneralAction, DismountAction);
            dismountSent = true;
        }
        if (DateTime.UtcNow - statusSince > DismountTimeout)
            Enter(NavStatus.Arrived);
    }
}
