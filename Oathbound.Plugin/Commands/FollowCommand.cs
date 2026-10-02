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

/// collar/leash: the slack leash, gated behind its own "Follow" permission which ChatCommandListener checks
/// before Engage/Release ever runs. Inside the leash length the Sub moves freely; at the edge their outward
/// movement is removed; past it they're steered along the Owner's breadcrumb trail, falling back to the
/// game's own follow when that gets stuck or someone is mounted (design.md D2-D5). collar/leash-travel adds
/// Waiting (the Owner left the area) and Traveling (a leash journey to the Owner is running). collar/leash-mounts
/// adds pillion / mounting alongside / flying through LeashMountController. Slack is the stuck fallback: the
/// Sub barely moved for a while despite being pulled or following, so they get their own steering back until
/// they're within the leash again (collar/leash "The leash goes slack when the Sub is stuck").
public sealed class FollowCommand
{
    private const string Owner = "Follow";

    private enum LeashState { Released, Free, Pulling, Following, Slack, Waiting, Traveling }

    /// collar/leash-travel "The leash waits when the Owner leaves".
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(2);

    // design D3: Owner breadcrumbs - a point every CrumbInterval once the Owner has moved CrumbMinStep.
    private static readonly TimeSpan CrumbInterval = TimeSpan.FromMilliseconds(250);
    private const float CrumbMinStep = 0.5f;
    private const int MaxCrumbs = 64;
    /// A crumb this close (ground distance) counts as reached.
    private const float CrumbReach = 1f;

    // design D4: a pull that hasn't closed the gap by StuckProgress yalms in StuckWindow falls back to follow.
    private static readonly TimeSpan StuckWindow = TimeSpan.FromSeconds(3);
    private const float StuckProgress = 0.5f;

    // collar/leash "The leash goes slack when the Sub is stuck" (design D5-D6): pulled or following, but the
    // Sub's own ground position moved less than StuckSlackMove for StuckSlackWindow - running in place. Slack
    // lasts until the Sub is back within the leash, or SlackRetry has passed and the pull tries again.
    private static readonly TimeSpan StuckSlackWindow = TimeSpan.FromSeconds(5);
    private const float StuckSlackMove = 0.5f;
    private static readonly TimeSpan SlackRetry = TimeSpan.FromSeconds(15);

    /// collar/leash-mounts: pitch (radians) used to bring a flying Sub down when the Owner lands.
    private const float DescendPitch = -0.8f;

    // Normal auto-follow trailing distance is a few yalms; a gap growing past this while following means
    // the game silently dropped follow (e.g. a gesture cancelled it through a path MovementLockService's
    // UnfollowDetour doesn't cover) rather than the Sub just lagging behind.
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
    /// The leashed Owner's name: object ids change between areas, so the Owner is found again by this.
    private string? ownerName;
    /// The Sub-side pairing the leash was engaged by - who a leash-off notice goes to.
    private Guid ownerPairingId;
    private DateTime waitingSince;
    private int requestedLength = LengthOption.DefaultYalms;

    // Written by OnFrameworkUpdate, read by Steer (both on the game's main thread).
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

    /// Whether this client believes the game's follow is on toward the Owner - set whenever we send
    /// `/follow <t>` ourselves, cleared when we stop it. Only then is the stop-follow command sent, since bare
    /// `/follow` is a toggle in-game and would otherwise turn follow back on if it had already dropped.
    private bool followActive;
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

    /// collar/leash-visual: the leashed Owner's game object, read by the leash line for its hand anchor -
    /// more precise than a name lookup. 0 while not leashed, waiting, or traveling.
    public ulong FollowedObjectId => state is LeashState.Waiting or LeashState.Traveling ? 0 : followedObjectId;

    /// Whether the leash is on at all - including while waiting for or traveling to the Owner.
    public bool IsLeashed => state != LeashState.Released;

    /// The pairing the current leash belongs to, or null while not leashed.
    public Guid? LeashedPairingId => state == LeashState.Released ? null : ownerPairingId;

    /// collar/leash "Sub tells the Owner when the leash comes off": raised once whenever an engaged leash
    /// ends, with the pairing it belonged to and why (LeashOffNotifier decides whether to tell the Owner).
    public event Action<Guid, LeashEnd>? LeashEnded;

    /// collar/leash "Sub's longest accepted leash": the requested length shortened to the Sub's limit, read
    /// live so lowering the limit applies to the current leash immediately.
    public float EffectiveLength =>
        Math.Min(requestedLength, Math.Clamp(config.Aliases.Follow.MaxLeashLengthYalms, LengthOption.MinYalms, LengthOption.MaxYalms));

    /// Where the pull hands control back: within the leash by up to 1 yalm (a third of it on short leashes).
    private float ReleaseDistance => EffectiveLength - MathF.Min(1f, EffectiveLength / 3f);

    /// collar/leash: emotes/poses cancel the game's follow outright regardless of distance, so the distance
    /// heuristic in CheckForDesync alone never catches it - re-assert immediately while falling back to follow.
    private void OnEmotePlayed()
    {
        if (state != LeashState.Following || !followActive || followedObjectId == 0) return;
        var owner = Plugin.ObjectTable.FirstOrDefault(o => o.GameObjectId == followedObjectId);
        if (owner is null) return;
        Plugin.TargetManager.Target = owner;
        Chat.SendMessage("/follow <t>");
    }

    /// `source` is the specific pairing whose incoming command triggered this (collar/multi-pairing:
    /// resolved from the tell's own verified sender, not any single configured peer).
    /// `lengthYalms` is the already-validated `length:` option (or the default). `moodleOverride` is the
    /// Owner's optional `leash moodle:"..."` pick (collar/attached-moodles). Re-leashing the same Owner keeps
    /// the leash and its state, only taking the new length and moodle.
    public bool Engage(PairingState? source, int lengthYalms, string? moodleOverride = null)
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
            // collar/leash-travel: the same Owner re-leashing in person - attach here, and drop any trip to them.
            var wasTraveling = state == LeashState.Traveling;
            TryReattach();
            if (wasTraveling)
                teleport.StopLeashJourney("re-leashed in person");
        }

        runtimeState.MovementLockActive = true;
        moodles.HoldAttached(AttachedMoodleLedger.FollowSource, config.Aliases.Follow.AttachedMoodle, moodleOverride);
        return true;
    }

    /// Ends the leash. `reason` says why, for the leash-off notice; LeashEnded is raised only if a leash was on.
    public void Release(LeashEnd reason)
    {
        var wasLeashed = state != LeashState.Released;
        var wasTraveling = state == LeashState.Traveling;
        var pairingId = ownerPairingId;
        state = LeashState.Released;
        moodles.Ledger.Release(AttachedMoodleLedger.FollowSource);
        movementLock.ClearSteering(Owner);
        StopFollowing();
        // collar/leash-mounts "Release never drops the Sub from the air": automation stops, no dismount.
        mount.Reset();
        followedObjectId = 0;
        ownerName = null;
        crumbs.Clear();
        runtimeState.MovementLockActive = false;
        // collar/leash-travel "Leash ends when the Sub can't travel" (unleash/revert-all mid-trip): the
        // journey stops with the leash. State is already Released, so its end event is ignored.
        if (wasTraveling)
            teleport.StopLeashJourney("the leash was released");
        ownerPairingId = Guid.Empty;
        if (wasLeashed)
            LeashEnded?.Invoke(pairingId, reason);
    }

    /// collar/leash-travel "Sub travels to the Owner on leash travel": only while leashed, and only for the
    /// Owner this Sub is leashed to. Teleport's own guards decide whether the journey can start; if it can't,
    /// the leash ends. A newer trip while traveling replaces the current one (TeleportCommand).
    public (bool Success, string? Reason) TravelTo(TeleportTarget destination, PairingState? source)
    {
        if (state == LeashState.Released)
            return (false, "Not leashed - leash travel ignored.");
        if (source is null || !string.Equals(source.PeerName, ownerName, StringComparison.OrdinalIgnoreCase))
            return (false, "Not leashed to the sender - leash travel ignored.");

        StopFollowing();
        var (success, reason) = teleport.ApplyLeashTravel(destination, source);
        if (!success)
        {
            Plugin.Log.Info($"Leash released: leash travel couldn't start ({reason}).");
            Release(LeashEnd.Travel);
            return (false, reason);
        }
        state = LeashState.Traveling;
        mount.Reset();
        return (true, null);
    }

    private void OnLeashJourneyEnded(bool arrived)
    {
        if (state != LeashState.Traveling) return;
        if (!arrived)
        {
            Plugin.Log.Info("Leash released: the leash journey was stopped.");
            Release(LeashEnd.Travel);
            return;
        }
        if (!TryReattach())
            EnterWaiting(DateTime.UtcNow);
    }

    /// Finds the leashed Owner again by name (a fresh object id) and goes back to Free.
    private bool TryReattach()
    {
        var owner = Plugin.ObjectTable.FirstOrDefault(o => o is IPlayerCharacter && string.Equals(o.Name.TextValue, ownerName, StringComparison.OrdinalIgnoreCase));
        if (owner is null) return false;
        followedObjectId = owner.GameObjectId;
        crumbs.Clear();
        lastCrumbAt = DateTime.MinValue;
        zeroNextFrame = true;
        state = LeashState.Free;
        return true;
    }

    private void EnterWaiting(DateTime now)
    {
        StopFollowing();
        mount.Reset();
        state = LeashState.Waiting;
        waitingSince = now;
        zeroNextFrame = true;
        crumbs.Clear();
    }

    public void OnFrameworkUpdate()
    {
        if (state == LeashState.Released) return;

        // Panic and unpair reset the runtime state and drop every movement claim without coming through
        // Release - finish the job here so a fallback follow doesn't keep walking the Sub.
        if (!runtimeState.MovementLockActive)
        {
            Release(LeashEnd.Other);
            return;
        }

        var now = DateTime.UtcNow;

        // collar/leash-travel: TeleportCommand owns movement during the trip; its end event moves us on. A
        // journey that vanished without one is treated as arrived.
        if (state == LeashState.Traveling)
        {
            if (!teleport.IsLeashJourneyInProgress)
                OnLeashJourneyEnded(arrived: true);
            return;
        }

        if (state == LeashState.Waiting)
        {
            if (TryReattach()) return;
            if (now - waitingSince > WaitTimeout)
            {
                Plugin.Log.Info("Leash released: the Owner did not come back or send leash travel in time.");
                Release(LeashEnd.Timeout);
            }
            return;
        }

        var owner = Plugin.ObjectTable.FirstOrDefault(o => o.GameObjectId == followedObjectId);
        if (owner is null)
        {
            Plugin.Log.Info("Leash waiting: the paired Owner left the current area.");
            EnterWaiting(now);
            return;
        }

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null) return;
        RecordCrumb(owner.Position, now);

        playerGround = Ground(player.Position);
        var offset = playerGround - Ground(owner.Position);
        groundDistance = offset.Length();
        outward = groundDistance > 0.01f ? offset / groundDistance : Vector2.Zero;
        var suspended = movementLock.IsSteerSuspended;

        mount.Tick(owner, player, now);
        if (mount.IsPillion && state is LeashState.Pulling or LeashState.Following or LeashState.Slack)
        {
            // Seated behind the Owner: the game carries the Sub, nothing to pull.
            StopFollowing();
            state = LeashState.Free;
            zeroNextFrame = true;
        }
        else if (mount.HoldStill && state == LeashState.Following)
        {
            // Game follow would walk the Sub out of the mount cast - stand still until mounted, then pull.
            StopFollowing();
            StartPull(now);
        }

        if (state is LeashState.Pulling or LeashState.Following)
        {
            // Held by a restraint, a teleport or a mount cast isn't stuck - only running in place is. Not reset
            // on Pulling -> Following, so a failed pull plus a failed follow add up.
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
                // Back within the leash: the edge holds again. Still out after SlackRetry: pull again.
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
                    // design D5: a restraint or teleport holds the Sub (or a mount cast is in progress) - the
                    // stuck timer waits for it.
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
                // collar/leash-mounts "Fly with the Owner": climb or descend toward the crumb's height.
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

    /// The leash's SteerFunction (MovementLockService design D1): free movement with the outward part removed
    /// at the edge (D2), the breadcrumb direction while pulling (D3), and untouched input otherwise.
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

    /// collar/leash-mounts (design D3): the flying counterpart of Steer. The Sub keeps their own pitch while free;
    /// the pull aims in 3D; and a Sub left in the air after the Owner lands is brought down.
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

    /// Stops pulling or following and hands the Sub their own steering - Steer/FlySteer pass input through
    /// untouched in Slack, edge included. The leash itself (line, icon, moodle, leash travel) carries on, and
    /// the Owner isn't told: nothing ended.
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

    /// The oldest crumb still ahead of the Sub: everything older than the crumb nearest to them is behind,
    /// and crumbs within reach are done. With none left, the Owner themselves.
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

    /// collar/leash-mounts "Game follow only as a fallback" (design D4): swimming/diving always; a mounted Owner
    /// only when the mount controller isn't carrying the Sub along (no acknowledgement, the Sub hopped off, or
    /// mounting failed here). The 3 s stuck rule in Pulling still applies on top.
    private unsafe bool NeedsGameFollow(IGameObject owner)
    {
        if (Plugin.Condition[ConditionFlag.Swimming] || Plugin.Condition[ConditionFlag.Diving])
            return true;
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)owner.Address;
        return character != null && character->IsMounted() && !mount.HandlesOwnerMount;
    }

    private static Vector2 Ground(Vector3 position) => new(position.X, position.Z);

    /// Self-heal while falling back to follow: a gesture (or anything else) can silently cancel the game's
    /// follow. Polled on an interval, and only re-sends `/follow <t>` once the gap has grown past normal
    /// trailing distance without closing, so a Sub simply lagging a step behind never triggers it.
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
