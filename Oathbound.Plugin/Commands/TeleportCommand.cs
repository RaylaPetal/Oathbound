using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.ImGuiNotification;
using ECommons.Automation;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;

namespace Oathbound.Plugin.Commands;

/// In journey order. Idle means no journey.
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

public enum LeashJourneyOutcome { Arrived, Failed, StoppedBySub }

/// The Sub's whole journey to the Owner: world change, aetheryte or ward travel, instance change, then vnavmesh
/// navigation, with movement locked throughout. Every guard runs on the Sub's client before anything moves.
/// Every end goes through Stop/Finish, so the lock and vnavmesh are always torn down together.
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
    private static readonly TimeSpan TravelStallTimeout = TimeSpan.FromSeconds(90);
    private const float TravelProgressDistance = 2f;

    private readonly PluginConfig config;
    private readonly LifestreamIpc lifestream;
    private readonly VnavmeshIpc vnavmesh;
    private readonly MovementLockService movementLock;

    // Fixed at Apply time.
    private TeleportTarget? target;
    private Guid? sourcePairingId;
    private string? ownerName;
    private uint aetheryteId;
    private string? districtName;
    private int housingPlot;
    /// The Teleporting stage is a jump straight to the Sub's own estate in the Owner's ward.
    private bool estateTravel;
    /// Started by ApplyLeashTravel rather than the Owner's own Teleport.
    private bool isLeashJourney;

    private TeleportStage stage;
    private DateTime stageStartedAt;
    private bool observedTravelBusy;
    private bool dismountSent;
    private DateTime lastProgressAt;
    private Vector3? lastProgressPosition;

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
    public bool IsLeashJourneyInProgress => IsInProgress && isLeashJourney;

    /// Not raised when a newer leash travel replaces the journey.
    public event Action<LeashJourneyOutcome>? LeashJourneyEnded;

    /// A leash journey from the same pairing is replaced silently; anything else in progress wins.
    public (bool Success, string? Reason) ApplyLeashTravel(TeleportTarget destination, PairingState source)
    {
        if (IsInProgress)
        {
            if (!isLeashJourney || sourcePairingId != source.Id)
                return (false, "Refused: a Teleport is already in progress.");
            StopCore("replaced by a newer leash travel", LeashJourneyOutcome.Failed, notifyLeash: false);
        }

        var result = Apply(destination, source);
        if (result.Success)
            isLeashJourney = true;
        return result;
    }

    public void StopLeashJourney(string reason)
    {
        if (IsLeashJourneyInProgress)
            Stop(reason);
    }

    /// 0..1 while the zone's navmesh is building, otherwise negative.
    public float NavBuildProgress => stage == TeleportStage.PreparingNav ? vnavmesh.TryGetBuildProgress() : -1f;

    /// Guards run in a fixed order so the reported reason is the first real blocker.
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
        estateTravel = false;

        if (destination.IsHousingWard)
        {
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
            // Skips travel when already closer than any attuned aetheryte in the same world, zone and instance.
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

        // Input stays suppressed for the whole journey; Teleporting adds full immobilize for the cast and loading screen.
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

        if (IsLifestreamTravelStage(stage) && HasTravelStalled())
        {
            Fail($"Travel stalled while {StageLabel(stage)}.");
            return;
        }

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
                if (estateTravel)
                {
                    if (TeleportDestinations.CurrentWard() != (target.Ward, target.Subdivision))
                    {
                        Fail($"The estate teleport didn't reach ward {target.Ward}{(target.Subdivision ? " (subdivision)" : "")}.");
                        return;
                    }
                    EnterStage(TeleportStage.PreparingNav);
                    return;
                }
                BeginChangingInstance();
                return;

            case TeleportStage.ChangingInstance:
                // A failed or refused instance change isn't fatal: navigate in the current instance.
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

    /// Safe to call when idle.
    public void Stop(string reason) => StopCore(reason, LeashJourneyOutcome.Failed, notifyLeash: true);

    /// The Sub's own Stop button: a leash journey stopped this way isn't retried.
    public void StopBySub(string reason) => StopCore(reason, LeashJourneyOutcome.StoppedBySub, notifyLeash: true);

    private void StopCore(string reason, LeashJourneyOutcome outcome, bool notifyLeash)
    {
        if (!IsInProgress)
            return;

        var wasLifestreamStage = stage is TeleportStage.ChangingWorld or TeleportStage.TravelingToWard or TeleportStage.Teleporting or TeleportStage.ChangingInstance;
        CancelPendingPath();
        vnavmesh.TryStop();
        if (wasLifestreamStage)
        {
            lifestream.TryAbort();
            // Lifestream may have left its autorun on. After the abort so its queue can't turn it back on.
            Chat.SendMessage("/automove off");
        }
        movementLock.ReleaseImmobilize(LockOwner);
        movementLock.ReleaseSuppressInput(LockOwner);
        Plugin.Log.Info($"Teleport stopped at {stage}: {reason}");
        stage = TeleportStage.Idle;
        target = null;
        sourcePairingId = null;
        ownerName = null;
        var wasLeashJourney = isLeashJourney;
        isLeashJourney = false;
        if (wasLeashJourney && notifyLeash)
            LeashJourneyEnded?.Invoke(outcome);
    }

    /// Pairing-ended events don't say which pairing, so check whether it was this journey's.
    public void StopIfSourcePairingEnded()
    {
        if (!IsInProgress || sourcePairingId is not { } id)
            return;
        if (config.FindPairingById(id) is not { IsPaired: true })
            Stop("the pairing that sent it ended");
    }

    /// Before navigation began: lock released, Sub told why.
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
        StopCore("arrived", LeashJourneyOutcome.Arrived, notifyLeash: true);
    }

    private void EnterStage(TeleportStage next)
    {
        stage = next;
        stageStartedAt = DateTime.UtcNow;
        observedTravelBusy = false;
        lastProgressAt = stageStartedAt;
        lastProgressPosition = Plugin.ObjectTable.LocalPlayer?.Position;

        // Lifestream walks to aetherytes with autorun, so it's allowed only while Lifestream drives.
        if (IsLifestreamTravelStage(next))
            movementLock.AllowAutorun(LockOwner);
        else
            movementLock.DisallowAutorun(LockOwner);
    }

    private static bool IsLifestreamTravelStage(TeleportStage s) =>
        s is TeleportStage.ChangingWorld or TeleportStage.TravelingToWard or TeleportStage.ChangingInstance;

    /// A loading screen, zone change or TravelProgressDistance of movement counts as progress.
    private bool HasTravelStalled()
    {
        var now = DateTime.UtcNow;
        var position = Plugin.ObjectTable.LocalPlayer?.Position;
        if (position is null || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]
            || lastProgressPosition is null || Vector3.Distance(position.Value, lastProgressPosition.Value) >= TravelProgressDistance)
        {
            lastProgressAt = now;
            lastProgressPosition = position;
            return false;
        }
        return now - lastProgressAt > TravelStallTimeout;
    }

    private static string StageLabel(TeleportStage s) => s switch
    {
        TeleportStage.ChangingWorld => "changing world",
        TeleportStage.TravelingToWard => "traveling to ward",
        TeleportStage.ChangingInstance => "changing instance",
        _ => s.ToString(),
    };

    private void BeginChangingWorld()
    {
        EnterStage(TeleportStage.ChangingWorld);
        if (!lifestream.TryChangeWorld(target!.World))
            Fail($"Failed to change world to \"{target.World}\".");
    }

    private void BeginTravelInWorld()
    {
        // Looked up here rather than in Apply: estates only count on their own world, which is now the Owner's.
        if (target!.IsHousingWard
            && TeleportDestinations.FindEstateInWard(lifestream, target.Territory, target.Ward, target.Subdivision, target.Position) is { } estate)
        {
            estateTravel = true;
            aetheryteId = estate.AetheryteId;
            EnterStage(TeleportStage.Teleporting);
            movementLock.EngageImmobilize(LockOwner);
            if (!lifestream.TryTeleport(estate.AetheryteId, estate.SubIndex))
            {
                movementLock.ReleaseImmobilize(LockOwner);
                Fail("Lifestream failed to teleport to your estate in your Owner's ward.");
            }
            return;
        }

        if (target.IsHousingWard)
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
        // subIndex 0 covers every plain aetheryte.
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

    /// Lifestream reports done when it fires the action, before the cast or loading screen. So travel finishes only after
    /// being seen busy and then idle. Null while waiting, false if travel never started within TravelStartTimeout.
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

    private unsafe void BeginMountingOrNavigating()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        var goal = LiveGoal();
        if (player is null || Plugin.Condition[ConditionFlag.Mounted] || Vector3.Distance(player.Position, goal) <= MountDistance)
        {
            BeginNavigating();
            return;
        }

        // Mount Roulette only when the game says it's usable here.
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

    /// The Owner's live position while visible, otherwise the commanded position.
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

    /// Only the current pendingPath is polled, so a path finishing after Stop or a retarget is discarded.
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
        if (path is { Count: > 0 })
        {
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

        if (!navigationStarted)
        {
            Fail("No path to your Owner could be found.");
            return;
        }
        EnterStuck();
    }

    /// Stuck never releases the lock - it retries, and the header offers Stop.
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
