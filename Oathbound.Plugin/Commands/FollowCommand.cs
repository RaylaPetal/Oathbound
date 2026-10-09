using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.ImGuiNotification;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;
using ECommons.Automation;

namespace Oathbound.Plugin.Commands;

/// The leash. Inside its length the Sub moves freely; at the edge outward movement is removed; past it they're
/// steered along the Owner's breadcrumbs, falling back to game follow when stuck or mounted. Waiting (the pause) and
/// Traveling cover being apart and have no time limit - only Release ends a leash. Slack hands a stuck Sub their own
/// steering until they're back in range.
public sealed class FollowCommand
{
    private const string Owner = "Follow";

    private enum LeashState { Released, Free, Pulling, Following, Slack, Waiting, Traveling }

    /// Within this an Owner's leash travel just re-attaches and the pull closes the gap; farther, a journey does.
    private const float ReattachReach = 30f;
    /// Lets the position settle after loading in before a kept trip is retried.
    private static readonly TimeSpan RetrySettleDelay = TimeSpan.FromSeconds(1);

    // Owner breadcrumbs: a point every CrumbInterval once the Owner has moved CrumbMinStep.
    private static readonly TimeSpan CrumbInterval = TimeSpan.FromMilliseconds(250);
    private const float CrumbMinStep = 0.5f;
    private const int MaxCrumbs = 64;
    private const float CrumbReach = 1f;

    // A pull that hasn't closed the gap by StuckProgress in StuckWindow falls back to game follow.
    private static readonly TimeSpan StuckWindow = TimeSpan.FromSeconds(3);
    private const float StuckProgress = 0.5f;

    // Pulled or following but moved less than StuckSlackMove in StuckSlackWindow: running in place.
    private static readonly TimeSpan StuckSlackWindow = TimeSpan.FromSeconds(5);
    private const float StuckSlackMove = 0.5f;
    private static readonly TimeSpan SlackRetry = TimeSpan.FromSeconds(15);

    /// Pitch (radians) used to bring a flying Sub down when the Owner lands.
    private const float DescendPitch = -0.8f;

    // Normal follow trails a few yalms; a gap growing past this means the game silently dropped follow.
    private const float DesyncDistanceThreshold = 7f;
    private static readonly TimeSpan DesyncCheckInterval = TimeSpan.FromSeconds(1);

    private readonly MovementLockService movementLock;
    private readonly SubRuntimeState runtimeState;
    private readonly PluginConfig config;
    private readonly MoodlesCommand moodles;
    private readonly TeleportCommand teleport;
    private readonly LeashMountController mount;
    private readonly List<Vector3> crumbs = new();

    private LeashState state;
    private ulong followedObjectId;
    /// Object ids change between areas, so the Owner is found again by name.
    private string? ownerName;
    private Guid ownerPairingId;

    /// The Owner's latest leash travel, retried while paused on the Sub's area change or combat end.
    private (TeleportTarget Destination, Guid PairingId)? keptTrip;
    private (string World, uint Territory, int Instance)? retryArea;
    private DateTime? loadedAt;
    private bool areaChangePending;
    private uint moodleTerritory;
    private DateTime? moodleAreaLoadedAt;
    private bool wasInCombat;
    private bool pauseNotified;
    private int requestedLength = LengthOption.DefaultYalms;
    /// From the Owner's `duty:pause`: while the Sub is in a duty the leash waits, even with the Owner right there.
    private bool pauseInDuties;

    // Written by OnFrameworkUpdate, read by Steer (both on the main thread).
    private Vector2 outward;
    private float groundDistance;
    private Vector2 pullDirection;
    private float pullPitch;
    private bool zeroNextFrame;

    private DateTime lastCrumbAt;
    private float pullBestDistance;
    private DateTime pullProgressAt;

    private Vector2 playerGround;
    private Vector2 stuckAnchor;
    private DateTime stuckSince;
    private DateTime slackSince;

    /// Bare `/follow` is a toggle, so the stop is only sent when we know follow is on.
    private bool followActive;
    private bool gameHeldLastFrame;
    private DateTime lastDesyncCheck;
    private float lastDesyncDistance;

    public FollowCommand(PluginConfig config, MovementLockService movementLock, SubRuntimeState runtimeState, MoodlesCommand moodles, TeleportCommand teleport)
    {
        this.config = config;
        this.movementLock = movementLock;
        this.runtimeState = runtimeState;
        this.moodles = moodles;
        this.teleport = teleport;
        mount = new LeashMountController(config);
        GestureCommand.EmotePlayed += OnEmotePlayed;
        teleport.LeashJourneyEnded += OnLeashJourneyEnded;
    }

    /// Read by the leash line for its hand anchor. 0 while waiting or traveling.
    public ulong FollowedObjectId => state is LeashState.Waiting or LeashState.Traveling ? 0 : followedObjectId;

    /// Including while waiting for or traveling to the Owner.
    public bool IsLeashed => state != LeashState.Released;

    public Guid? LeashedPairingId => state == LeashState.Released ? null : ownerPairingId;

    /// Turns on the duty pause for the leash already running, without re-engaging it.
    public void PauseInDuties()
    {
        if (state != LeashState.Released)
            pauseInDuties = true;
    }

    /// Waiting for, or traveling to, the Owner.
    public bool IsPaused => state is LeashState.Waiting or LeashState.Traveling;

    public bool IsSlack => state == LeashState.Slack;

    /// Raised once whenever an engaged leash ends, with its pairing and why.
    public event Action<Guid, LeashEnd>? LeashEnded;

    /// Read live so lowering the Sub's limit applies to the current leash immediately.
    public float EffectiveLength =>
        Math.Min(requestedLength, Math.Clamp(config.Aliases.Follow.MaxLeashLengthYalms, LengthOption.MinYalms, LengthOption.MaxYalms));

    /// Where the pull hands control back: up to 1 yalm inside the leash (a third of it on short leashes).
    private float ReleaseDistance => EffectiveLength - MathF.Min(1f, EffectiveLength / 3f);

    /// Emotes cancel game follow regardless of distance, which CheckForDesync wouldn't catch - re-assert immediately.
    private void OnEmotePlayed()
    {
        if (state != LeashState.Following || !followActive || followedObjectId == 0) return;
        var owner = Plugin.ObjectTable.FirstOrDefault(o => o.GameObjectId == followedObjectId);
        if (owner is null) return;
        Plugin.TargetManager.Target = owner;
        Chat.SendMessage("/follow <t>");
    }

    /// Re-leashing the same Owner keeps the leash and its state, taking only the new length and moodle.
    public bool Engage(PairingState? source, int lengthYalms, string? moodleOverride = null, bool pauseInDuties = false)
    {
        var peerName = source?.PeerName;
        if (!movementLock.IsSteerAvailable || source is null || string.IsNullOrEmpty(peerName))
            return false;

        var owner = Plugin.ObjectTable.FirstOrDefault(o => string.Equals(o.Name.TextValue, peerName, StringComparison.OrdinalIgnoreCase));
        if (owner is null)
        {
            Plugin.Log.Warning($"Leash refused: paired Owner '{peerName}' is not a targetable player in the current area.");
            return false;
        }

        if (state != LeashState.Released && !string.Equals(ownerName, owner.Name.TextValue, StringComparison.OrdinalIgnoreCase))
            Release(LeashEnd.Other);

        requestedLength = lengthYalms;
        this.pauseInDuties = pauseInDuties;
        if (state == LeashState.Released)
        {
            followedObjectId = owner.GameObjectId;
            ownerName = owner.Name.TextValue;
            ownerPairingId = source.Id;
            crumbs.Clear();
            lastCrumbAt = DateTime.MinValue;
            zeroNextFrame = false;
            state = LeashState.Free;
            movementLock.SetSteering(Owner, Steer);
            movementLock.SetFlySteering(Owner, FlySteer);
            mount.Reset();
        }
        else if (state is LeashState.Waiting or LeashState.Traveling)
        {
            // The same Owner re-leashing in person: attach here and drop any trip to them.
            var wasTraveling = state == LeashState.Traveling;
            TryReattach();
            if (wasTraveling)
                teleport.StopLeashJourney("re-leashed in person");
        }

        runtimeState.MovementLockActive = true;
        moodles.HoldAttached(AttachedMoodleLedger.FollowSource, config.Aliases.Follow.AttachedMoodle, moodleOverride);
        moodleTerritory = Plugin.ClientState.TerritoryType;
        return true;
    }

    /// LeashEnded is raised only if a leash was on.
    public void Release(LeashEnd reason)
    {
        var wasLeashed = state != LeashState.Released;
        var wasTraveling = state == LeashState.Traveling;
        var pairingId = ownerPairingId;
        state = LeashState.Released;
        moodles.Ledger.Release(AttachedMoodleLedger.FollowSource);
        movementLock.ClearSteering(Owner);
        StopFollowing();
        // Automation stops, but the Sub is never dismounted mid-air.
        mount.Reset();
        followedObjectId = 0;
        ownerName = null;
        keptTrip = null;
        pauseNotified = false;
        crumbs.Clear();
        runtimeState.MovementLockActive = false;
        // State is already Released, so the journey's end event is ignored.
        if (wasTraveling)
            teleport.StopLeashJourney("the leash was released");
        ownerPairingId = Guid.Empty;
        if (wasLeashed)
            LeashEnded?.Invoke(pairingId, reason);
    }

    /// Only for the Owner this Sub is leashed to. A journey that can't start pauses the leash and keeps the trip.
    public (bool Success, string? Reason) TravelTo(TeleportTarget destination, PairingState? source)
    {
        if (state == LeashState.Released)
            return (false, "Not leashed - leash travel ignored.");
        if (source is null || !string.Equals(source.PeerName, ownerName, StringComparison.OrdinalIgnoreCase))
            return (false, "Not leashed to the sender - leash travel ignored.");

        keptTrip = (destination, source.Id);
        if (FindOwner() is { } owner && Plugin.ObjectTable.LocalPlayer is { } player &&
            Vector2.Distance(Ground(player.Position), Ground(owner.Position)) <= ReattachReach)
        {
            // Already together (the Owner walked out of a door the Sub was waiting at): no journey needed.
            var wasTraveling = state == LeashState.Traveling;
            if (state is LeashState.Waiting or LeashState.Traveling)
                TryReattach();
            keptTrip = null;
            if (wasTraveling)
                teleport.StopLeashJourney("the Owner is already here");
            return (true, null);
        }

        var (success, reason) = StartTrip(destination, source);
        if (success)
            return (true, null);

        Plugin.Log.Info($"Leash paused: leash travel couldn't start ({reason}).");
        EnterWaiting(notify: false);
        pauseNotified = true;
        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = "Leash paused",
            Content = $"You couldn't follow {ownerName}: {reason} Your leash stays on and picks back up when you're together again.",
            Type = NotificationType.Info,
        });
        return (false, reason);
    }

    private (bool Success, string? Reason) StartTrip(TeleportTarget destination, PairingState source)
    {
        StopFollowing();
        var result = teleport.ApplyLeashTravel(destination, source);
        if (!result.Success)
            return result;
        state = LeashState.Traveling;
        mount.Reset();
        return result;
    }

    /// A failed retry keeps the trip for the next trigger and tells the Sub nothing new.
    private void RetryKeptTrip()
    {
        if (keptTrip is not { } trip || config.FindPairingById(trip.PairingId) is not { IsPaired: true } source)
        {
            keptTrip = null;
            return;
        }
        var (success, reason) = StartTrip(trip.Destination, source);
        Plugin.Log.Info(success ? "Leash: retrying the kept trip to the Owner." : $"Leash: kept trip still can't start ({reason}).");
    }

    private void OnLeashJourneyEnded(LeashJourneyOutcome outcome)
    {
        if (state != LeashState.Traveling) return;
        // Stopping is the Sub choosing not to go, so it isn't retried.
        if (outcome == LeashJourneyOutcome.StoppedBySub)
            keptTrip = null;
        if (outcome != LeashJourneyOutcome.Arrived)
        {
            Plugin.Log.Info($"Leash paused: the leash journey ended ({outcome}).");
            EnterWaiting();
            return;
        }
        if (!TryReattach())
            EnterWaiting();
    }

    private IGameObject? FindOwner() =>
        Plugin.ObjectTable.FirstOrDefault(o => o is IPlayerCharacter && string.Equals(o.Name.TextValue, ownerName, StringComparison.OrdinalIgnoreCase));

    private bool DutyPaused => pauseInDuties && TeleportDestinations.InDuty();

    private bool TryReattach()
    {
        if (DutyPaused) return false;
        var owner = FindOwner();
        if (owner is null) return false;
        followedObjectId = owner.GameObjectId;
        crumbs.Clear();
        lastCrumbAt = DateTime.MinValue;
        zeroNextFrame = true;
        state = LeashState.Free;
        keptTrip = null;
        pauseNotified = false;
        return true;
    }

    private void EnterWaiting(bool notify = true)
    {
        StopFollowing();
        mount.Reset();
        state = LeashState.Waiting;
        zeroNextFrame = true;
        crumbs.Clear();
        // Only area changes during the pause trigger a retry, so a failed journey's own hops don't loop.
        retryArea = null;
        areaChangePending = false;
        if (!notify || pauseNotified) return;
        pauseNotified = true;
        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = "Leash paused",
            Content = $"Your leash stays on while you're apart from {ownerName}. You can move freely, and it picks back up when you're together again.",
            Type = NotificationType.Info,
        });
    }

    /// True once per settled area change on the Sub's side, or when combat ends.
    private bool RetryTriggered(DateTime now)
    {
        var inCombat = Plugin.Condition[ConditionFlag.InCombat];
        var combatEnded = wasInCombat && !inCombat;
        wasInCombat = inCombat;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            loadedAt = null;
            return false;
        }
        loadedAt ??= now;
        var area = (player.CurrentWorld.Value.Name.ExtractText(), Plugin.ClientState.TerritoryType, TeleportDestinations.CurrentPublicInstance());
        if (retryArea is null)
            retryArea = area;
        else if (area != retryArea.Value)
        {
            retryArea = area;
            areaChangePending = true;
        }
        if (areaChangePending && now - loadedAt.Value >= RetrySettleDelay)
        {
            areaChangePending = false;
            return true;
        }
        return combatEnded;
    }

    /// Moodles can drop a status across a zone load, which would leave the leash on without its moodle.
    private void ReassertMoodleAfterAreaChange(DateTime now)
    {
        if (Plugin.ObjectTable.LocalPlayer is null || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            moodleAreaLoadedAt = null;
            return;
        }
        moodleAreaLoadedAt ??= now;
        var territory = Plugin.ClientState.TerritoryType;
        if (territory == moodleTerritory || now - moodleAreaLoadedAt.Value < RetrySettleDelay)
            return;
        moodleTerritory = territory;
        moodles.Ledger.Reassert(AttachedMoodleLedger.FollowSource);
    }

    public void OnFrameworkUpdate()
    {
        if (state == LeashState.Released) return;

        // Panic and unpair drop every movement claim without calling Release - finish the job so a fallback follow stops.
        if (!runtimeState.MovementLockActive)
        {
            Release(LeashEnd.Other);
            return;
        }

        var now = DateTime.UtcNow;
        ReassertMoodleAfterAreaChange(now);

        // TeleportCommand owns movement during the trip. A journey that vanished without an end event counts as arrived.
        if (state == LeashState.Traveling)
        {
            if (!teleport.IsLeashJourneyInProgress)
                OnLeashJourneyEnded(LeashJourneyOutcome.Arrived);
            return;
        }

        if (DutyPaused)
        {
            if (state != LeashState.Waiting)
            {
                Plugin.Log.Info("Leash paused: the Sub is in a duty.");
                EnterWaiting(notify: false);
                pauseNotified = true;
                Plugin.NotificationManager.AddNotification(new Notification
                {
                    Title = "Leash paused",
                    Content = $"{ownerName} pauses your leash in duties. You can move freely, and it picks back up when you leave.",
                    Type = NotificationType.Info,
                });
            }
            return;
        }

        if (state == LeashState.Waiting)
        {
            if (TryReattach()) return;
            if (RetryTriggered(now) && keptTrip is not null)
                RetryKeptTrip();
            return;
        }

        var owner = Plugin.ObjectTable.FirstOrDefault(o => o.GameObjectId == followedObjectId);
        if (owner is null)
        {
            Plugin.Log.Info("Leash paused: the paired Owner left the current area.");
            EnterWaiting();
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null) return;
        RecordCrumb(owner.Position, now);

        playerGround = Ground(player.Position);
        var offset = playerGround - Ground(owner.Position);
        groundDistance = offset.Length();
        outward = groundDistance > 0.01f ? offset / groundDistance : Vector2.Zero;

        // Dead, loading or in a cutscene, the Sub can't move at all: wait instead of counting it as stuck.
        if (GameHoldsSub())
        {
            gameHeldLastFrame = true;
            if (state is LeashState.Pulling or LeashState.Following)
            {
                // The game ends its own follow here; sending the /follow toggle could turn it back on.
                movementLock.ReleasePreserveFollow(Owner);
                followActive = false;
                state = LeashState.Pulling;
                pullDirection = Vector2.Zero;
                pullProgressAt = now;
                ResetStuck(now);
            }
            return;
        }
        if (gameHeldLastFrame)
        {
            gameHeldLastFrame = false;
            if (state is LeashState.Pulling or LeashState.Following)
                StartPull(now);
            zeroNextFrame = true;
        }

        var suspended = movementLock.IsSteerSuspended;

        mount.Tick(owner, player, now);
        if (mount.IsPillion && state is LeashState.Pulling or LeashState.Following or LeashState.Slack)
        {
            // Seated behind the Owner: the game carries the Sub.
            StopFollowing();
            state = LeashState.Free;
            zeroNextFrame = true;
        }
        else if (mount.HoldStill && state == LeashState.Following)
        {
            // Game follow would walk the Sub out of the mount cast.
            StopFollowing();
            StartPull(now);
        }

        if (state is LeashState.Pulling or LeashState.Following)
        {
            // Being held by a restraint, a teleport or a mount cast isn't stuck. Not reset on Pulling -> Following, so both add up.
            if (suspended || mount.HoldStill || mount.IsPillion || Vector2.Distance(playerGround, stuckAnchor) >= StuckSlackMove)
                ResetStuck(now);
            else if (now - stuckSince >= StuckSlackWindow && groundDistance > EffectiveLength)
                EnterSlack(now);
        }

        switch (state)
        {
            case LeashState.Free:
                if (groundDistance > EffectiveLength && !mount.IsPillion)
                    StartPull(now);
                break;

            case LeashState.Slack:
                if (groundDistance <= EffectiveLength)
                    state = LeashState.Free;
                else if (now - slackSince >= SlackRetry)
                    StartPull(now);
                break;

            case LeashState.Pulling:
                if (groundDistance <= ReleaseDistance)
                {
                    state = LeashState.Free;
                    zeroNextFrame = true;
                    break;
                }
                if (suspended || mount.HoldStill)
                {
                    // A restraint, teleport or mount cast holds the Sub; the stuck timer waits.
                    pullProgressAt = now;
                    break;
                }
                if (NeedsGameFollow(owner))
                {
                    StartFollowing(owner, now);
                    break;
                }
                if (groundDistance < pullBestDistance - StuckProgress)
                {
                    pullBestDistance = groundDistance;
                    pullProgressAt = now;
                }
                else if (now - pullProgressAt > StuckWindow)
                {
                    Plugin.Log.Info("Leash pull stuck: falling back to follow.");
                    StartFollowing(owner, now);
                    break;
                }
                var target = NextCrumb(player.Position, owner.Position);
                var toTarget = Ground(target) - Ground(player.Position);
                var horizontal = toTarget.Length();
                pullDirection = horizontal > 0.01f ? toTarget / horizontal : Vector2.Zero;
                // Climb or descend toward the crumb's height.
                pullPitch = MathF.Atan2(target.Y - player.Position.Y, MathF.Max(horizontal, 0.5f));
                break;

            case LeashState.Following:
                if (groundDistance <= ReleaseDistance && !NeedsGameFollow(owner))
                {
                    StopFollowing();
                    state = LeashState.Free;
                    zeroNextFrame = true;
                    break;
                }
                CheckForDesync(owner, player.Position, now);
                break;
        }
    }

    /// Outward part removed at the edge, breadcrumb direction while pulling, input untouched otherwise.
    private Vector2 Steer(Vector2 wish)
    {
        if (zeroNextFrame)
        {
            zeroNextFrame = false;
            return Vector2.Zero;
        }
        if (mount.HoldStill)
            return Vector2.Zero;

        switch (state)
        {
            case LeashState.Pulling:
                return pullDirection;
            case LeashState.Free when groundDistance >= EffectiveLength && !mount.IsPillion:
                var away = Vector2.Dot(wish, outward);
                return away > 0f ? wish - away * outward : wish;
            default:
                return wish;
        }
    }

    /// The Sub keeps their own pitch while free; a Sub left in the air after the Owner lands is brought down.
    private (Vector2 Horizontal, float Pitch) FlySteer(Vector2 wish, float pitch)
    {
        if (zeroNextFrame)
        {
            zeroNextFrame = false;
            return (Vector2.Zero, 0f);
        }
        if (mount.HoldStill)
            return (Vector2.Zero, 0f);
        if (state == LeashState.Pulling)
            return (pullDirection, pullPitch);
        if (mount.Descend)
        {
            var direction = wish.LengthSquared() > 0.01f ? Vector2.Normalize(wish) : outward != Vector2.Zero ? -outward : Vector2.UnitY;
            return (direction, DescendPitch);
        }
        return (Steer(wish), pitch);
    }

    private void StartPull(DateTime now)
    {
        state = LeashState.Pulling;
        pullBestDistance = groundDistance;
        pullProgressAt = now;
        pullDirection = Vector2.Zero;
        ResetStuck(now);
    }

    private void ResetStuck(DateTime now)
    {
        stuckAnchor = playerGround;
        stuckSince = now;
    }

    /// Steer/FlySteer pass input through untouched in Slack. Nothing ended, so the Owner isn't told.
    private void EnterSlack(DateTime now)
    {
        Plugin.Log.Info("Leash stuck: going slack until the Sub is back within the leash.");
        StopFollowing();
        state = LeashState.Slack;
        slackSince = now;
        zeroNextFrame = true;
        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = "Leash went slack",
            Content = "You were stuck, so your leash went slack. Walk back toward your Owner and it tightens again.",
            Type = NotificationType.Info,
        });
    }

    private void StartFollowing(IGameObject owner, DateTime now)
    {
        state = LeashState.Following;
        Plugin.TargetManager.Target = owner;
        Chat.SendMessage("/follow <t>");
        followActive = true;
        lastDesyncCheck = now;
        lastDesyncDistance = 0f;
        movementLock.EngagePreserveFollow(Owner);
    }

    private void StopFollowing()
    {
        movementLock.ReleasePreserveFollow(Owner);
        if (followActive)
            Chat.SendMessage("/follow");
        followActive = false;
    }

    private void RecordCrumb(Vector3 ownerPosition, DateTime now)
    {
        if (now - lastCrumbAt < CrumbInterval) return;
        if (crumbs.Count > 0 && Vector2.Distance(Ground(crumbs[^1]), Ground(ownerPosition)) < CrumbMinStep) return;
        lastCrumbAt = now;
        crumbs.Add(ownerPosition);
        if (crumbs.Count > MaxCrumbs)
            crumbs.RemoveAt(0);
    }

    /// The oldest crumb still ahead of the Sub; with none left, the Owner.
    private Vector3 NextCrumb(Vector3 subPosition, Vector3 ownerPosition)
    {
        var sub = Ground(subPosition);
        if (crumbs.Count > 0)
        {
            var nearest = 0;
            var nearestDistance = float.MaxValue;
            for (var i = 0; i < crumbs.Count; i++)
            {
                var d = Vector2.Distance(sub, Ground(crumbs[i]));
                if (d < nearestDistance) { nearestDistance = d; nearest = i; }
            }
            crumbs.RemoveRange(0, nearest);
            while (crumbs.Count > 0 && Vector2.Distance(sub, Ground(crumbs[0])) <= CrumbReach)
                crumbs.RemoveAt(0);
        }

        return crumbs.Count > 0 ? crumbs[0] : ownerPosition;
    }

    /// Swimming/diving always; a mounted Owner only when the mount controller isn't carrying the Sub.
    private unsafe bool NeedsGameFollow(IGameObject owner)
    {
        if (Plugin.Condition[ConditionFlag.Swimming] || Plugin.Condition[ConditionFlag.Diving])
            return true;
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)owner.Address;
        return character != null && character->IsMounted() && !mount.HandlesOwnerMount;
    }

    private static Vector2 Ground(Vector3 position) => new(position.X, position.Z);

    private static bool GameHoldsSub()
    {
        var c = Plugin.Condition;
        return c[ConditionFlag.Unconscious] || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]
            || c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78]
            || c[ConditionFlag.Occupied33] || c[ConditionFlag.OccupiedInEvent] || c[ConditionFlag.OccupiedInQuestEvent]
            || c[ConditionFlag.Jumping61];
    }

    /// Re-sends `/follow` only once the gap grows past normal trailing distance without closing.
    private void CheckForDesync(IGameObject owner, Vector3 playerPosition, DateTime now)
    {
        if (!followActive || now - lastDesyncCheck < DesyncCheckInterval) return;

        var distance = Vector3.Distance(playerPosition, owner.Position);
        var isDesynced = distance > DesyncDistanceThreshold && distance >= lastDesyncDistance;
        lastDesyncCheck = now;
        lastDesyncDistance = distance;
        if (!isDesynced) return;

        Plugin.Log.Info("Leash desync detected: re-sending follow to the paired Owner.");
        Plugin.TargetManager.Target = owner;
        Chat.SendMessage("/follow <t>");
        lastDesyncDistance = 0f;
    }
}
