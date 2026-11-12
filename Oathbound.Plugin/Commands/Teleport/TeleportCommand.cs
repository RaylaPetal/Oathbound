using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.ImGuiNotification;
using ECommons.Automation;
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

/// Cancelled is an end the Owner already knows about or caused (panic, revert all, unpair, unload, replaced).
public enum LeashJourneyOutcome { Arrived, Failed, StoppedBySub, Cancelled }

/// The Sub's whole journey to the Owner: world change, aetheryte or ward travel, instance change, then vnavmesh
/// navigation, with movement locked throughout. Every guard runs on the Sub's client before anything moves.
/// Every end goes through Stop/Finish, so the lock and vnavmesh are always torn down together.
public sealed class TeleportCommand
{
    private const string LockOwner = "Teleport";

    private const float ArrivalDistance = 3f;

    private static readonly TimeSpan TravelStartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TravelStallTimeout = TimeSpan.FromSeconds(90);
    private const float TravelProgressDistance = 2f;

    private readonly PluginConfig config;
    private readonly LifestreamIpc lifestream;
    private readonly VnavmeshIpc vnavmesh;
    private readonly MovementLockService movementLock;
    private readonly Navigator navigator;
    private readonly NavOptions navOptions;

    // Fixed at Apply time.
    private TeleportTarget? target;
    private Guid? sourcePairingId;
    private string? ownerName;
    private uint aetheryteId;
    private string? districtName;
    private int housingPlot;
    /// The Teleporting stage is a jump straight to the Sub's own estate in the Owner's ward.
    private bool estateTravel;
    /// Set only once the journey has started, so a failure inside Apply is reported by Apply's result alone.
    private JourneyOrigin? origin;

    private TeleportStage stage;
    private DateTime stageStartedAt;
    private bool observedTravelBusy;
    private DateTime lastProgressAt;
    private Vector3? lastProgressPosition;

    public TeleportCommand(PluginConfig config, LifestreamIpc lifestream, VnavmeshIpc vnavmesh, MovementLockService movementLock)
    {
        this.config = config;
        this.lifestream = lifestream;
        this.vnavmesh = vnavmesh;
        this.movementLock = movementLock;
        navigator = new Navigator(vnavmesh, "Teleport");
        navOptions = new NavOptions
        {
            Goal = LiveGoal,
            IsAtGoal = position => Vector3.Distance(position, LiveGoal()) <= ArrivalDistance,
        };
    }

    public TeleportStage Stage => stage;
    public bool IsInProgress => stage != TeleportStage.Idle;
    public bool IsLeashJourneyInProgress => IsInProgress && origin == JourneyOrigin.Leash;

    /// Set once by Plugin. A caged Sub can't be teleported by anyone.
    public Func<bool> IsCaged { get; set; } = static () => false;

    /// Not raised when a newer leash travel replaces the journey. The code is meaningful for Failed and StoppedBySub.
    public event Action<LeashJourneyOutcome, TeleportRefusal>? LeashJourneyEnded;

    /// An Owner-sent journey that started and then failed or was stopped by the Sub.
    public event Action<Guid, TeleportRefusal>? OwnerTeleportFailed;

    /// A leash journey from the same pairing is replaced silently; anything else in progress wins.
    public (bool Success, string? Reason, TeleportRefusal Code) ApplyLeashTravel(TeleportTarget destination, PairingState source)
    {
        if (IsInProgress)
        {
            if (origin != JourneyOrigin.Leash || sourcePairingId != source.Id)
                return (false, "Refused: a Teleport is already in progress.", TeleportRefusal.Busy);
            StopCore("replaced by a newer leash travel", LeashJourneyOutcome.Cancelled, TeleportRefusal.Other, notifyLeash: false);
        }

        return Apply(destination, source, JourneyOrigin.Leash);
    }

    public void StopLeashJourney(string reason)
    {
        if (IsLeashJourneyInProgress)
            Stop(reason);
    }

    /// 0..1 while the zone's navmesh is building, otherwise negative.
    public float NavBuildProgress => stage == TeleportStage.PreparingNav ? vnavmesh.TryGetBuildProgress() : -1f;

    /// Guards run in a fixed order so the reported reason is the first real blocker.
    public event Action? OwnerTeleported;

    public (bool Success, string? Reason, TeleportRefusal Code) Apply(TeleportTarget destination, PairingState? source, JourneyOrigin journeyOrigin)
    {
        if (IsCaged())
            return Refuse(TeleportRefusal.Caged, "Refused: you're caged.");
        if (!config.Permissions.Teleport)
            return Refuse(TeleportRefusal.Permission, "Teleport permission is not enabled.");
        if (!config.TosAcknowledged)
            return Refuse(TeleportRefusal.Permission, "The automation-risk acknowledgement has not been given.");
        if (Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56] || Plugin.Condition[ConditionFlag.BoundByDuty95])
            return Refuse(TeleportRefusal.Busy, "Refused: currently bound by a duty.");
        if (Plugin.Condition[ConditionFlag.InCombat])
            return Refuse(TeleportRefusal.Busy, "Refused: currently in combat.");
        if (IsInProgress)
            return Refuse(TeleportRefusal.Busy, "Refused: a Teleport is already in progress.");
        if (!lifestream.IsAvailable)
            return Refuse(TeleportRefusal.Setup, "Lifestream is not installed or not responding.");
        if (!vnavmesh.IsAvailable)
            return Refuse(TeleportRefusal.Setup, "vnavmesh is not installed or not responding.");
        if (!movementLock.IsImmobilizeAvailable)
            return Refuse(TeleportRefusal.Setup, "Refused: the movement lock can't be enforced on this game version.");
        if (TeleportDestinations.IsHousingInterior(destination.Territory))
            return Refuse(TeleportRefusal.Interior, "Refused: housing interiors are not supported.");

        var player = Plugin.ObjectTable.LocalPlayer;
        var currentWorld = player?.CurrentWorld.Value.Name.ExtractText();
        if (player is null || currentWorld is null)
            return Refuse(TeleportRefusal.Busy, "Refused: your character can't be read right now.");

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
                    return Refuse(TeleportRefusal.Setup, "Refused: Lifestream doesn't recognize your Owner's residential district.");
                if (TeleportDestinations.FindNearestPlot(lifestream, destination.Territory, destination.Subdivision, destination.Position) is not { } plot)
                    return Refuse(TeleportRefusal.Setup, "Refused: Lifestream has no plot data for your Owner's ward.");
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
                return Refuse(TeleportRefusal.NoAetheryte, "Refused: you have no attuned aetheryte in your Owner's zone.");
            aetheryteId = aetheryte?.AetheryteId ?? 0;
        }

        lastFailure = null;
        lastFailureCode = TeleportRefusal.Other;
        target = destination;
        sourcePairingId = source?.Id;
        ownerName = source?.PeerName;

        // Input stays suppressed for the whole journey; Teleporting adds full immobilize for the cast and loading screen.
        movementLock.EngageSuppressInput(LockOwner);

        if (skipTravel)
            BeginNav();
        else if (!sameWorld)
            BeginChangingWorld();
        else
            BeginTravelInWorld();

        if (!IsInProgress)
            return (false, lastFailure ?? "Teleport failed to start.", lastFailureCode);

        origin = journeyOrigin;
        if (source is not null)
            OwnerTeleported?.Invoke();
        return (true, null, TeleportRefusal.Other);
    }

    private static (bool Success, string? Reason, TeleportRefusal Code) Refuse(TeleportRefusal code, string reason) => (false, reason, code);

    private string? lastFailure;
    private TeleportRefusal lastFailureCode;

    public void OnFrameworkUpdate()
    {
        if (!IsInProgress || target is null)
            return;

        if (IsLifestreamTravelStage(stage) && HasTravelStalled())
        {
            Fail(TeleportRefusal.Travel, $"Travel stalled while {StageLabel(stage)}.");
            return;
        }

        switch (stage)
        {
            case TeleportStage.ChangingWorld:
                if (WaitForTravel() is not { } worldDone) return;
                if (!worldDone) { Fail(TeleportRefusal.Travel, "Lifestream didn't start the world change."); return; }
                var world = Plugin.ObjectTable.LocalPlayer?.CurrentWorld.Value.Name.ExtractText();
                if (!string.Equals(world, target.World, StringComparison.OrdinalIgnoreCase)) { Fail(TeleportRefusal.Travel, $"World change didn't reach \"{target.World}\"."); return; }
                BeginTravelInWorld();
                return;

            case TeleportStage.TravelingToWard:
                if (WaitForTravel() is not { } wardDone) return;
                if (!wardDone) { Fail(TeleportRefusal.Travel, "Lifestream didn't start traveling to your Owner's ward."); return; }
                if (Plugin.ClientState.TerritoryType != target.Territory || TeleportDestinations.CurrentWard() != (target.Ward, target.Subdivision))
                {
                    Fail(TeleportRefusal.Travel, $"Ward travel didn't reach ward {target.Ward}{(target.Subdivision ? " (subdivision)" : "")}.");
                    return;
                }
                BeginNav();
                return;

            case TeleportStage.Teleporting:
                if (WaitForTravel() is not { } teleportDone) return;
                movementLock.ReleaseImmobilize(LockOwner);
                if (!teleportDone) { Fail(TeleportRefusal.Travel, "The teleport didn't start."); return; }
                if (Plugin.ClientState.TerritoryType != target.Territory) { Fail(TeleportRefusal.Travel, "The teleport didn't arrive in your Owner's zone."); return; }
                if (estateTravel)
                {
                    if (TeleportDestinations.CurrentWard() != (target.Ward, target.Subdivision))
                    {
                        Fail(TeleportRefusal.Travel, $"The estate teleport didn't reach ward {target.Ward}{(target.Subdivision ? " (subdivision)" : "")}.");
                        return;
                    }
                    BeginNav();
                    return;
                }
                BeginChangingInstance();
                return;

            case TeleportStage.ChangingInstance:
                // A failed or refused instance change isn't fatal: navigate in the current instance.
                if (WaitForTravel() is null) return;
                BeginNav();
                return;

            case TeleportStage.PreparingNav:
            case TeleportStage.Mounting:
            case TeleportStage.Navigating:
            case TeleportStage.Stuck:
            case TeleportStage.Arriving:
                UpdateNavigation();
                return;
        }
    }

    /// Safe to call when idle. For ends the Owner already knows about or caused: never reported back to them.
    public void Stop(string reason) => StopCore(reason, LeashJourneyOutcome.Cancelled, TeleportRefusal.Other, notifyLeash: true);

    /// The Sub's own Stop button: a leash journey stopped this way isn't retried.
    public void StopBySub(string reason) => StopCore(reason, LeashJourneyOutcome.StoppedBySub, TeleportRefusal.Stopped, notifyLeash: true);

    private void StopCore(string reason, LeashJourneyOutcome outcome, TeleportRefusal code, bool notifyLeash)
    {
        if (!IsInProgress)
            return;

        var wasLifestreamStage = stage is TeleportStage.ChangingWorld or TeleportStage.TravelingToWard or TeleportStage.Teleporting or TeleportStage.ChangingInstance;
        navigator.Stop();
        if (wasLifestreamStage)
        {
            lifestream.TryAbort();
            // Lifestream may have left its autorun on. After the abort so its queue can't turn it back on.
            Chat.SendMessage("/automove off");
        }
        movementLock.ReleaseImmobilize(LockOwner);
        movementLock.ReleaseSuppressInput(LockOwner);
        Plugin.Log.Info($"Teleport stopped at {stage}: {reason}");
        var endedOrigin = origin;
        var endedPairingId = sourcePairingId;
        stage = TeleportStage.Idle;
        target = null;
        sourcePairingId = null;
        ownerName = null;
        origin = null;
        if (endedOrigin == JourneyOrigin.Leash && notifyLeash)
            LeashJourneyEnded?.Invoke(outcome, code);
        if (endedOrigin == JourneyOrigin.Owner && outcome is LeashJourneyOutcome.Failed or LeashJourneyOutcome.StoppedBySub && endedPairingId is { } pairingId)
            OwnerTeleportFailed?.Invoke(pairingId, code);
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
    private void Fail(TeleportRefusal code, string reason)
    {
        lastFailure = reason;
        lastFailureCode = code;
        StopCore(reason, LeashJourneyOutcome.Failed, code, notifyLeash: true);
        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = "Teleport stopped",
            Content = reason,
            Type = NotificationType.Warning,
        });
    }

    private void Finish()
    {
        StopCore("arrived", LeashJourneyOutcome.Arrived, TeleportRefusal.Other, notifyLeash: true);
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
            Fail(TeleportRefusal.Travel, $"Failed to change world to \"{target.World}\".");
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
                Fail(TeleportRefusal.Travel, "Lifestream failed to teleport to your estate in your Owner's ward.");
            }
            return;
        }

        if (target.IsHousingWard)
        {
            EnterStage(TeleportStage.TravelingToWard);
            if (lifestream.TryBuildAddressBookEntry(target.World, districtName!, target.Ward, housingPlot) is not { } entry)
            {
                Fail(TeleportRefusal.Travel, "Lifestream couldn't build an address for your Owner's ward.");
                return;
            }
            if (!lifestream.TryGoToHousingAddress(entry))
                Fail(TeleportRefusal.Travel, "Lifestream failed to start traveling to your Owner's ward.");
            return;
        }

        EnterStage(TeleportStage.Teleporting);
        movementLock.EngageImmobilize(LockOwner);
        // subIndex 0 covers every plain aetheryte.
        if (!lifestream.TryTeleport(aetheryteId, 0))
        {
            movementLock.ReleaseImmobilize(LockOwner);
            Fail(TeleportRefusal.Travel, "Lifestream failed to teleport to the chosen aetheryte.");
        }
    }

    private void BeginChangingInstance()
    {
        EnterStage(TeleportStage.ChangingInstance);
        var wanted = target!.Instance;
        if (wanted == 0 || lifestream.TryGetCurrentInstance() == wanted || !lifestream.TryCanChangeInstance() || !lifestream.TryChangeInstance(wanted))
            BeginNav();
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

    private void BeginNav()
    {
        EnterStage(TeleportStage.PreparingNav);
        navigator.Start(navOptions);
    }

    private void UpdateNavigation()
    {
        switch (navigator.Tick())
        {
            case NavStatus.Preparing: stage = TeleportStage.PreparingNav; break;
            case NavStatus.Mounting: stage = TeleportStage.Mounting; break;
            case NavStatus.Moving: stage = TeleportStage.Navigating; break;
            case NavStatus.Stuck: stage = TeleportStage.Stuck; break;
            case NavStatus.Landing: stage = TeleportStage.Arriving; break;
            case NavStatus.Arrived: Finish(); break;
            default: Fail(TeleportRefusal.NoPath, "No path to your Owner could be found."); break;
        }
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
}
