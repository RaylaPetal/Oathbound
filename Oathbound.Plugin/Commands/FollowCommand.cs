using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;
using ECommons.Automation;

namespace Oathbound.Plugin.Commands;

/// collar/leash: the slack leash, gated behind its own "Follow" permission which ChatCommandListener checks
/// before Engage/Release ever runs. Inside the leash length the Sub moves freely; at the edge their outward
/// movement is removed; past it they're steered along the Owner's breadcrumb trail, falling back to the
/// game's own follow when that gets stuck or someone is mounted (design.md D2-D5). collar/leash-travel adds
/// Waiting (the Owner left the area) and Traveling (a leash journey to the Owner is running).
public sealed class FollowCommand
{
    private const string Owner = "Follow";

    private enum LeashState { Released, Free, Pulling, Following, Waiting, Traveling }

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
    private readonly List<Vector3> crumbs = new();

    private LeashState state;
    private ulong followedObjectId;
    /// The leashed Owner's name: object ids change between areas, so the Owner is found again by this.
    private string? ownerName;
    private DateTime waitingSince;
    private int requestedLength = LengthOption.DefaultYalms;

    // Written by OnFrameworkUpdate, read by Steer (both on the game's main thread).
    private Vector2 outward;
    private float groundDistance;
    private Vector2 pullDirection;
    private bool zeroNextFrame;

    private DateTime lastCrumbAt;
    private float pullBestDistance;
    private DateTime pullProgressAt;

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
        GestureCommand.EmotePlayed += OnEmotePlayed;
        teleport.LeashJourneyEnded += OnLeashJourneyEnded;
    }

    /// collar/leash-visual: the leashed Owner's game object, read by the leash line for its hand anchor -
    /// more precise than a name lookup. 0 while not leashed, waiting, or traveling.
    public ulong FollowedObjectId => state is LeashState.Waiting or LeashState.Traveling ? 0 : followedObjectId;

    /// Whether the leash is on at all - including while waiting for or traveling to the Owner.
    public bool IsLeashed => state != LeashState.Released;

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

    /// `peerName` is the specific Owner-side pairing whose incoming command triggered this (collar/
    /// multi-pairing: resolved from the tell's own verified sender, not any single configured peer).
    /// `lengthYalms` is the already-validated `length:` option (or the default). `moodleOverride` is the
    /// Owner's optional `leash moodle:"..."` pick (collar/attached-moodles). Re-leashing the same Owner keeps
    /// the leash and its state, only taking the new length and moodle.
    public bool Engage(string? peerName, int lengthYalms, string? moodleOverride = null)
    {
        if (!movementLock.IsSteerAvailable || peerName is null)
            return false;

        var owner = Plugin.ObjectTable.FirstOrDefault(o => string.Equals(o.Name.TextValue, peerName, StringComparison.OrdinalIgnoreCase));
        if (owner is null)
        {
            Plugin.Log.Warning($"Leash refused: paired Owner '{peerName}' is not a targetable player in the current area.");
            return false;
        }

        if (state != LeashState.Released && !string.Equals(ownerName, owner.Name.TextValue, StringComparison.OrdinalIgnoreCase))
            Release();

        requestedLength = lengthYalms;
        if (state == LeashState.Released)
        {
            followedObjectId = owner.GameObjectId;
            ownerName = owner.Name.TextValue;
            crumbs.Clear();
            lastCrumbAt = DateTime.MinValue;
            zeroNextFrame = false;
            state = LeashState.Free;
            movementLock.SetSteering(Owner, Steer);
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

    public void Release()
    {
        var wasTraveling = state == LeashState.Traveling;
        state = LeashState.Released;
        moodles.Ledger.Release(AttachedMoodleLedger.FollowSource);
        movementLock.ClearSteering(Owner);
        StopFollowing();
        followedObjectId = 0;
        ownerName = null;
        crumbs.Clear();
        runtimeState.MovementLockActive = false;
        // collar/leash-travel "Leash ends when the Sub can't travel" (unleash/revert-all mid-trip): the
        // journey stops with the leash. State is already Released, so its end event is ignored.
        if (wasTraveling)
            teleport.StopLeashJourney("the leash was released");
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
            Release();
            return (false, reason);
        }
        state = LeashState.Traveling;
        return (true, null);
    }

    private void OnLeashJourneyEnded(bool arrived)
    {
        if (state != LeashState.Traveling) return;
        if (!arrived)
        {
            Plugin.Log.Info("Leash released: the leash journey was stopped.");
            Release();
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
            Release();
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
                Release();
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

        var offset = Ground(player.Position) - Ground(owner.Position);
        groundDistance = offset.Length();
        outward = groundDistance > 0.01f ? offset / groundDistance : Vector2.Zero;
        var suspended = movementLock.IsSteerSuspended;

        switch (state)
        {
            case LeashState.Free:
                if (groundDistance > EffectiveLength)
                    StartPull(now);
                break;

            case LeashState.Pulling:
                if (groundDistance <= ReleaseDistance)
                {
                    state = LeashState.Free;
                    zeroNextFrame = true;
                    break;
                }
                if (suspended)
                {
                    // design D5: a restraint or teleport holds the Sub - the stuck timer waits for it.
                    pullProgressAt = now;
                    break;
                }
                if (AnyoneMountedOrAirborne(owner))
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
                pullDirection = DirectionToNextCrumb(player.Position, owner.Position);
                break;

            case LeashState.Following:
                if (groundDistance <= ReleaseDistance && !AnyoneMountedOrAirborne(owner))
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

        switch (state)
        {
            case LeashState.Pulling:
                return pullDirection;
            case LeashState.Free when groundDistance >= EffectiveLength:
                var away = Vector2.Dot(wish, outward);
                return away > 0f ? wish - away * outward : wish;
            default:
                return wish;
        }
    }

    private void StartPull(DateTime now)
    {
        state = LeashState.Pulling;
        pullBestDistance = groundDistance;
        pullProgressAt = now;
        pullDirection = Vector2.Zero;
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
    /// and crumbs within reach are done. With none left, straight at the Owner.
    private Vector2 DirectionToNextCrumb(Vector3 subPosition, Vector3 ownerPosition)
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

        var target = crumbs.Count > 0 ? Ground(crumbs[0]) : Ground(ownerPosition);
        var toTarget = target - sub;
        var length = toTarget.Length();
        return length > 0.01f ? toTarget / length : Vector2.Zero;
    }

    private static unsafe bool AnyoneMountedOrAirborne(IGameObject owner)
    {
        if (Plugin.Condition[ConditionFlag.Mounted] || Plugin.Condition[ConditionFlag.InFlight]
            || Plugin.Condition[ConditionFlag.Swimming] || Plugin.Condition[ConditionFlag.Diving])
            return true;
        var character = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)owner.Address;
        return character != null && character->IsMounted();
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
