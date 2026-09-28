using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.ImGuiNotification;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;

namespace Oathbound.Plugin.Commands;

/// collar/teleport stages, in journey order (design.md D4). Idle means no journey.
public enum TeleportStage
{
    Idle,
    ChangingWorld,
    TravelingToWard,
    Teleporting,
    ChangingInstance,
    PreparingNav,
    Mounting,
    Navigating,
    Stuck,
    Arriving,
}

/// collar/teleport: the Sub's whole "come to the Owner" journey - world change, aetheryte teleport or
/// housing-ward travel, instance change, then vnavmesh navigation (mounting and flying when far) to within
/// a few yalms of the Owner - with the Sub's movement locked from start to finish (design.md D4).
///
/// Every guard runs on the Sub's own client before anything moves; the Sub's client protects itself and never
/// trusts the Owner's tell to have already checked duty/combat/permission. Every way a journey can end - arrival,
/// the header's Stop, panic, revert-all, the source pairing ending, a failure before navigation - goes
/// through `Stop`/`Finish`, so the lock and vnavmesh are always torn down together (design.md D10).
public sealed class TeleportCommand
{
    private const string LockOwner = "Teleport";

    private const float ArrivalDistance = 3f;
    private const float MountDistance = 40f;
    private const float RetargetDistance = 3f;
    private const float ProgressMinDelta = 1f;
    private const uint MountRouletteAction = 9;
    private const uint DismountAction = 23;

    private static readonly TimeSpan TravelStartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DismountTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProgressWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly PluginConfig config;
    private readonly LifestreamIpc lifestream;
    private readonly VnavmeshIpc vnavmesh;
    private readonly MovementLockService movementLock;

    // Journey plan, fixed at Apply time.
    private TeleportTarget? target;
    private Guid? sourcePairingId;
    private string? ownerName;
    private uint aetheryteId;
    private string? districtName;
    private int housingPlot;

    // Stage bookkeeping.
    private TeleportStage stage;
    private DateTime stageStartedAt;
    private bool observedTravelBusy;
    private bool dismountSent;

    // Navigation.
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

    public TeleportCommand(PluginConfig config, LifestreamIpc lifestream, VnavmeshIpc vnavmesh, MovementLockService movementLock)
    {
        this.config = config;
        this.lifestream = lifestream;
        this.vnavmesh = vnavmesh;
        this.movementLock = movementLock;
    }

    public TeleportStage Stage => stage;
    public bool IsInProgress => stage != TeleportStage.Idle;

    /// 0..1 while the zone's navmesh is still building, otherwise negative (see VnavmeshIpc).
    public float NavBuildProgress => stage == TeleportStage.PreparingNav ? vnavmesh.TryGetBuildProgress() : -1f;

    /// collar/teleport "Sub refuses Teleport when travel cannot safely happen": checked in the spec's order so
    /// the reason reported is always the first thing actually blocking the command. On success the journey has
    /// started and runs from OnFrameworkUpdate.
    public (bool Success, string? Reason) Apply(TeleportTarget destination, PairingState? source)
    {
        if (!config.Permissions.Teleport)
            return (false, "Teleport permission is not enabled.");
        if (!config.TosAcknowledged)
            return (false, "The automation-risk acknowledgement has not been given.");
        if (Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56] || Plugin.Condition[ConditionFlag.BoundByDuty95])
            return (false, "Refused: currently bound by a duty.");
        if (Plugin.Condition[ConditionFlag.InCombat])
            return (false, "Refused: currently in combat.");
        if (IsInProgress)
            return (false, "Refused: a Teleport is already in progress.");
        if (!lifestream.IsAvailable)
            return (false, "Lifestream is not installed or not responding.");
        if (!vnavmesh.IsAvailable)
            return (false, "vnavmesh is not installed or not responding.");
        if (!movementLock.IsImmobilizeAvailable)
            return (false, "Refused: the movement lock can't be enforced on this game version.");
        if (TeleportDestinations.IsHousingInterior(destination.Territory))
            return (false, "Refused: housing interiors are not supported.");

        var player = Plugin.ObjectTable.LocalPlayer;
        var currentWorld = player?.CurrentWorld.Value.Name.ExtractText();
        if (player is null || currentWorld is null)
            return (false, "Refused: your character can't be read right now.");

        var sameWorld = string.Equals(currentWorld, destination.World, StringComparison.OrdinalIgnoreCase);
        var sameTerritory = sameWorld && Plugin.ClientState.TerritoryType == destination.Territory;
        bool skipTravel;
        aetheryteId = 0;
        districtName = null;
        housingPlot = 0;

        if (destination.IsHousingWard)
        {
            // collar/teleport "Sub travels to the Owner's housing ward".
            skipTravel = sameTerritory && TeleportDestinations.CurrentWard() == (destination.Ward, destination.Subdivision);
            if (!skipTravel)
            {
                districtName = TeleportDestinations.FindDistrictName(lifestream, destination.Territory);
                if (districtName is null)
                    return (false, "Refused: Lifestream doesn't recognize your Owner's residential district.");
                if (TeleportDestinations.FindNearestPlot(lifestream, destination.Territory, destination.Subdivision, destination.Position) is not { } plot)
                    return (false, "Refused: Lifestream has no plot data for your Owner's ward.");
                housingPlot = plot;
            }
        }
        else
        {
            // collar/teleport "Sub chooses the arrival aetheryte" - including the skip shortcut when already
            // closer than any attuned aetheryte in the same world, zone, and instance.
            var aetheryte = TeleportDestinations.FindNearestAttunedAetheryte(destination.Territory, destination.Position);
            var sameInstance = destination.Instance == 0 || TeleportDestinations.CurrentPublicInstance() == destination.Instance;
            var ownDistance = Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), new Vector2(destination.Position.X, destination.Position.Z));
            skipTravel = sameTerritory && sameInstance && (aetheryte is null || ownDistance < aetheryte.Value.Distance);
            if (!skipTravel && aetheryte is null)
                return (false, "Refused: you have no attuned aetheryte in your Owner's zone.");
            aetheryteId = aetheryte?.AetheryteId ?? 0;
        }

        lastFailure = null;
        target = destination;
        sourcePairingId = source?.Id;
        ownerName = source?.PeerName;
        navigationStarted = false;
        flying = false;
        dismountSent = false;

        // design.md D5: input stays suppressed for the whole journey; Teleporting adds full immobilize on top
        // for the cast and loading screen only.
        movementLock.EngageSuppressInput(LockOwner);

        if (skipTravel)
            EnterStage(TeleportStage.PreparingNav);
        else if (!sameWorld)
            BeginChangingWorld();
        else
            BeginTravelInWorld();

        return IsInProgress ? (true, null) : (false, lastFailure ?? "Teleport failed to start.");
    }

    private string? lastFailure;

    public void OnFrameworkUpdate()
    {
        if (!IsInProgress || target is null)
            return;

        switch (stage)
        {
            case TeleportStage.ChangingWorld:
                if (WaitForTravel() is not { } worldDone) return;
                if (!worldDone) { Fail("Lifestream didn't start the world change."); return; }
                var world = Plugin.ObjectTable.LocalPlayer?.CurrentWorld.Value.Name.ExtractText();
                if (!string.Equals(world, target.World, StringComparison.OrdinalIgnoreCase)) { Fail($"World change didn't reach \"{target.World}\"."); return; }
                BeginTravelInWorld();
                return;

            case TeleportStage.TravelingToWard:
                if (WaitForTravel() is not { } wardDone) return;
                if (!wardDone) { Fail("Lifestream didn't start traveling to your Owner's ward."); return; }
                if (Plugin.ClientState.TerritoryType != target.Territory || TeleportDestinations.CurrentWard() != (target.Ward, target.Subdivision))
                {
                    Fail($"Ward travel didn't reach ward {target.Ward}{(target.Subdivision ? " (subdivision)" : "")}.");
                    return;
                }
                EnterStage(TeleportStage.PreparingNav);
                return;

            case TeleportStage.Teleporting:
                if (WaitForTravel() is not { } teleportDone) return;
                movementLock.ReleaseImmobilize(LockOwner);
                if (!teleportDone) { Fail("The teleport didn't start."); return; }
                if (Plugin.ClientState.TerritoryType != target.Territory) { Fail("The teleport didn't arrive in your Owner's zone."); return; }
                BeginChangingInstance();
                return;

            case TeleportStage.ChangingInstance:
                // A failed or refused instance change is not fatal (spec): navigate in the current instance.
                if (WaitForTravel() is null) return;
                EnterStage(TeleportStage.PreparingNav);
                return;

            case TeleportStage.PreparingNav:
                if (Plugin.ObjectTable.LocalPlayer is null || !vnavmesh.TryIsReady()) return;
                BeginMountingOrNavigating();
                return;

            case TeleportStage.Mounting:
                if (Plugin.Condition[ConditionFlag.Mounted] || DateTime.UtcNow - stageStartedAt > MountTimeout)
                    BeginNavigating();
                return;

            case TeleportStage.Navigating:
            case TeleportStage.Stuck:
                UpdateNavigation();
                return;

            case TeleportStage.Arriving:
                UpdateArriving();
                return;
        }
    }

    /// collar/teleport "Sub can stop an in-progress Teleport from the header" + panic/revert-all/unpair: halts
    /// everything immediately. Safe to call when idle.
    public void Stop(string reason)
    {
        if (!IsInProgress)
            return;

        var wasLifestreamStage = stage is TeleportStage.ChangingWorld or TeleportStage.TravelingToWard or TeleportStage.Teleporting or TeleportStage.ChangingInstance;
        CancelPendingPath();
        vnavmesh.TryStop();
        if (wasLifestreamStage)
            lifestream.TryAbort();
        movementLock.ReleaseImmobilize(LockOwner);
        movementLock.ReleaseSuppressInput(LockOwner);
        Plugin.Log.Info($"Teleport stopped at {stage}: {reason}");
        stage = TeleportStage.Idle;
        target = null;
        sourcePairingId = null;
        ownerName = null;
    }

    /// collar/teleport "Panic, Revert all, and unpair end an in-progress Teleport" (unpair case): PairingService
    /// and RevocationService only announce that *some* pairing ended, so check whether it was this journey's.
    public void StopIfSourcePairingEnded()
    {
        if (!IsInProgress || sourcePairingId is not { } id)
            return;
        if (config.FindPairingById(id) is not { IsPaired: true })
            Stop("the pairing that sent it ended");
    }

    /// Fails a journey before navigation began (spec: lock released, Sub told why).
    private void Fail(string reason)
    {
        lastFailure = reason;
        Stop(reason);
        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = "Teleport stopped",
            Content = reason,
            Type = NotificationType.Warning,
        });
    }

    private void Finish()
    {
        Stop("arrived");
    }

    private void EnterStage(TeleportStage next)
    {
        stage = next;
        stageStartedAt = DateTime.UtcNow;
        observedTravelBusy = false;
    }

    // --- Travel stages -----------------------------------------------------------------------------------

    private void BeginChangingWorld()
    {
        EnterStage(TeleportStage.ChangingWorld);
        if (!lifestream.TryChangeWorld(target!.World))
            Fail($"Failed to change world to \"{target.World}\".");
    }

    private void BeginTravelInWorld()
    {
        if (target!.IsHousingWard)
        {
            EnterStage(TeleportStage.TravelingToWard);
            if (lifestream.TryBuildAddressBookEntry(target.World, districtName!, target.Ward, housingPlot) is not { } entry)
            {
                Fail("Lifestream couldn't build an address for your Owner's ward.");
                return;
            }
            if (!lifestream.TryGoToHousingAddress(entry))
                Fail("Lifestream failed to start traveling to your Owner's ward.");
            return;
        }

        EnterStage(TeleportStage.Teleporting);
        movementLock.EngageImmobilize(LockOwner);
        // subIndex 0 covers every plain aetheryte; the id always comes from FindNearestAttunedAetheryte.
        if (!lifestream.TryTeleport(aetheryteId, 0))
        {
            movementLock.ReleaseImmobilize(LockOwner);
            Fail("Lifestream failed to teleport to the chosen aetheryte.");
        }
    }

    private void BeginChangingInstance()
    {
        EnterStage(TeleportStage.ChangingInstance);
        var wanted = target!.Instance;
        if (wanted == 0 || lifestream.TryGetCurrentInstance() == wanted || !lifestream.TryCanChangeInstance() || !lifestream.TryChangeInstance(wanted))
            EnterStage(TeleportStage.PreparingNav);
    }

    /// `Lifestream.IsBusy` alone isn't enough: Lifestream considers a teleport "done" as soon as it fires the
    /// in-game action, before the cast bar (Casting87) or loading screen (BetweenAreas) actually runs. So travel
    /// only counts as finished after it has been observed busy at least once and then gone idle. Returns null
    /// while still waiting, true when finished, false when travel never started within TravelStartTimeout.
    private bool? WaitForTravel()
    {
        if (IsTravelBusy())
        {
            observedTravelBusy = true;
            return null;
        }
        if (observedTravelBusy)
            return Plugin.ObjectTable.LocalPlayer is null ? null : true;
        return DateTime.UtcNow - stageStartedAt > TravelStartTimeout ? false : null;
    }

    private bool IsTravelBusy() =>
        lifestream.TryIsBusy()
        || Plugin.Condition[ConditionFlag.Casting87]
        || Plugin.Condition[ConditionFlag.BetweenAreas]
        || Plugin.Condition[ConditionFlag.BetweenAreas51];

    // --- Navigation --------------------------------------------------------------------------------------

    private unsafe void BeginMountingOrNavigating()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var goal = LiveGoal();
        if (player is null || Plugin.Condition[ConditionFlag.Mounted] || Vector3.Distance(player.Position, goal) <= MountDistance)
        {
            BeginNavigating();
            return;
        }

        // design.md D8: Mount Roulette only when the game says it's usable here (not in cities/indoors).
        var actions = ActionManager.Instance();
        if (actions == null || actions->GetActionStatus(ActionType.GeneralAction, MountRouletteAction) != 0)
        {
            BeginNavigating();
            return;
        }
        EnterStage(TeleportStage.Mounting);
        actions->UseAction(ActionType.GeneralAction, MountRouletteAction);
    }

    private void BeginNavigating()
    {
        EnterStage(TeleportStage.Navigating);
        flying = Plugin.Condition[ConditionFlag.Mounted] && Player.CanFly;
        ResetProgressWindow();
        RequestPath(flying);
    }

    /// The Owner's live position while their character is visible to this client, otherwise the position the
    /// command carried (spec: "the destination SHALL follow the Owner's live position").
    private Vector3 LiveGoal()
    {
        var owner = FindOwner();
        return owner?.Position ?? target!.Position;
    }

    private IGameObject? FindOwner()
    {
        if (ownerName is null)
            return null;
        return Plugin.ObjectTable.FirstOrDefault(o => o is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter
            && string.Equals(o.Name.TextValue, ownerName, StringComparison.OrdinalIgnoreCase));
    }

    /// design.md D6: a cancelable pathfind this class owns. Only the current `pendingPath` is ever polled, and Stop
    /// or a retarget drops that reference (and cancels its token), so a path that finishes after
    /// Stop or a newer retarget is discarded instead of handed to vnavmesh.
    private void RequestPath(bool fly)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
            return;

        CancelPendingPath();
        var goal = LiveGoal();
        navTarget = goal;
        var to = fly ? goal : vnavmesh.TryNearestPointReachable(goal, 5f, 5f) ?? goal;

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

    private void UpdateNavigation()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
            return;

        if (pendingPath is { IsCompleted: true } done)
        {
            pendingPath = null;
            OnPathResult(done.IsCompletedSuccessfully ? done.Result : null);
            if (!IsInProgress)
                return;
        }

        var goal = LiveGoal();
        if (Vector3.Distance(player.Position, goal) <= ArrivalDistance)
        {
            BeginArriving();
            return;
        }

        var now = DateTime.UtcNow;
        if (now - lastCheck < CheckInterval)
            return;
        lastCheck = now;

        // design.md D7 retarget: follow the Owner once visible, without dropping the current path until the new
        // one arrives (Path.MoveTo simply replaces the waypoints).
        if (pendingPath is null && Vector3.Distance(goal, navTarget) > RetargetDistance)
        {
            RequestPath(flying);
            return;
        }

        UpdateProgress(player.Position, goal, now);
    }

    private void OnPathResult(List<Vector3>? path)
    {
        if (path is { Count: > 0 })
        {
            if (vnavmesh.TryMoveTo(path, pendingPathFly))
                navigationStarted = true;
            return;
        }

        // A flying path can fail near overhangs/interiors - fall back to the ground straight away.
        if (pendingPathFly)
        {
            flying = false;
            RequestPath(false);
            return;
        }

        if (!navigationStarted)
        {
            Fail("No path to your Owner could be found.");
            return;
        }
        EnterStuck();
    }

    /// design.md D7: stuck = distance to the goal hasn't dropped by ProgressMinDelta over ProgressWindow, or
    /// vnavmesh stopped (its own StopOnStuck) short of the goal. Stuck never releases the lock - it retries,
    /// and the header offers Stop.
    private void UpdateProgress(Vector3 position, Vector3 goal, DateTime now)
    {
        var distance = Vector3.Distance(position, goal);
        if (pendingPath is not null)
        {
            ResetProgressWindow(distance);
            return;
        }

        var madeProgress = progressWindowDistance - distance >= ProgressMinDelta;
        if (madeProgress)
        {
            if (stage == TeleportStage.Stuck)
                EnterStage(TeleportStage.Navigating);
            ResetProgressWindow(distance);
            return;
        }

        var pathStopped = navigationStarted && !vnavmesh.TryIsRunning();
        if (pathStopped || now - progressWindowStart >= ProgressWindow)
            EnterStuck();

        if (stage == TeleportStage.Stuck && now - lastRetry >= RetryInterval)
        {
            ResetProgressWindow(distance);
            RequestPath(flying);
        }
    }

    private void EnterStuck()
    {
        if (stage != TeleportStage.Stuck)
        {
            Plugin.Log.Info("Teleport navigation appears stuck - retrying; movement stays locked.");
            stage = TeleportStage.Stuck;
        }
    }

    private void ResetProgressWindow(float? distance = null)
    {
        progressWindowStart = DateTime.UtcNow;
        var player = Plugin.ObjectTable.LocalPlayer;
        progressWindowDistance = distance ?? (player is null ? float.MaxValue : Vector3.Distance(player.Position, LiveGoal()));
    }

    private void BeginArriving()
    {
        CancelPendingPath();
        vnavmesh.TryStop();
        EnterStage(TeleportStage.Arriving);
    }

    private unsafe void UpdateArriving()
    {
        if (!Plugin.Condition[ConditionFlag.Mounted])
        {
            Finish();
            return;
        }
        if (!dismountSent)
        {
            var actions = ActionManager.Instance();
            if (actions != null)
                actions->UseAction(ActionType.GeneralAction, DismountAction);
            dismountSent = true;
        }
        if (DateTime.UtcNow - stageStartedAt > DismountTimeout)
            Finish();
    }
}
