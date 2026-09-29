using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;
using ECommons.Automation;

namespace Oathbound.Plugin.Commands;

/// collar/follow: movement-lock (leash) enforcement, gated behind its own "Follow" permission which
/// ChatCommandListener checks before Engage/Release ever runs - the same dedicated opt-in the spec
/// requires, kept separate from the other three categories by construction.
public sealed class FollowCommand
{
    private const string Owner = "Follow";

    // Normal auto-follow trailing distance is a few yalms; a gap growing past this while the lock is
    // still engaged means the game silently dropped follow (e.g. a gesture cancelled it through a path
    // MovementLockService's UnfollowDetour doesn't cover) rather than the Sub just lagging behind.
    private const float DesyncDistanceThreshold = 7f;
    private static readonly TimeSpan DesyncCheckInterval = TimeSpan.FromSeconds(1);

    private readonly MovementLockService movementLock;
    private readonly SubRuntimeState runtimeState;
    private readonly PluginConfig config;
    private readonly MoodlesCommand moodles;
    private ulong followedObjectId;

    /// collar/leash-visual: the leashed Owner's game object, read by the leash line for its hand anchor -
    /// more precise than a name lookup. 0 while not leashed.
    public ulong FollowedObjectId => followedObjectId;

    /// Whether this client believes the Sub is currently following the leashed Owner - set whenever we
    /// send `/follow <t>` ourselves, cleared on Release. Release only sends the stop-follow command when
    /// this is true, since bare `/follow` is a toggle in-game and would otherwise turn follow back on if
    /// it had already dropped.
    private bool followActive;
    private DateTime lastDesyncCheck;
    private float lastDesyncDistance;

    public FollowCommand(PluginConfig config, MovementLockService movementLock, SubRuntimeState runtimeState, MoodlesCommand moodles)
    {
        this.config = config;
        this.movementLock = movementLock;
        this.runtimeState = runtimeState;
        this.moodles = moodles;
        GestureCommand.EmotePlayed += OnEmotePlayed;
    }

    /// collar/follow: emotes/poses cancel the game's follow outright regardless of distance from the
    /// leashed Owner (e.g. a Sub spanked in place), so the distance heuristic in CheckForDesync alone
    /// never catches it - re-assert immediately whenever any gesture/pose actually plays.
    private void OnEmotePlayed()
    {
        if (!runtimeState.MovementLockActive || !followActive || followedObjectId == 0) return;
        var owner = Plugin.ObjectTable.FirstOrDefault(o => o.GameObjectId == followedObjectId);
        if (owner is null) return;
        Plugin.TargetManager.Target = owner;
        Chat.SendMessage("/follow <t>");
    }

    /// `peerName` is the specific Owner-side pairing whose incoming command triggered this (collar/
    /// multi-pairing: resolved from the tell's own verified sender, not any single configured peer).
    /// `moodleOverride` is the Owner's optional `leash moodle:"..."` pick (collar/attached-moodles).
    public bool Engage(string? peerName, string? moodleOverride = null)
    {
        if (!movementLock.IsAvailable || peerName is null)
            return false;

        var owner = Plugin.ObjectTable.FirstOrDefault(o => string.Equals(o.Name.TextValue, peerName, StringComparison.OrdinalIgnoreCase));
        if (owner is null)
        {
            Plugin.Log.Warning($"Leash refused: paired Owner '{peerName}' is not a targetable player in the current area.");
            return false;
        }

        Plugin.TargetManager.Target = owner;
        Chat.SendMessage("/follow <t>");
        followActive = true;
        lastDesyncCheck = DateTime.UtcNow;
        lastDesyncDistance = 0f;

        movementLock.EngagePreserveFollow(Owner);
        followedObjectId = owner.GameObjectId;
        runtimeState.MovementLockActive = true;
        moodles.HoldAttached(AttachedMoodleLedger.FollowSource, config.Aliases.Follow.AttachedMoodle, moodleOverride);
        return true;
    }

    public void Release()
    {
        moodles.Ledger.Release(AttachedMoodleLedger.FollowSource);
        movementLock.ReleasePreserveFollow(Owner);
        if (followActive)
            Chat.SendMessage("/follow");
        followActive = false;
        followedObjectId = 0;
        runtimeState.MovementLockActive = false;
    }

    public void OnFrameworkUpdate()
    {
        if (!runtimeState.MovementLockActive || followedObjectId == 0) return;
        var owner = Plugin.ObjectTable.FirstOrDefault(o => o.GameObjectId == followedObjectId);
        if (owner is null)
        {
            Plugin.Log.Warning("Leash released: the paired Owner is no longer present in the current area.");
            Release();
            return;
        }

        CheckForDesync(owner);
    }

    /// collar/follow: self-heal for a gesture (or anything else) silently cancelling follow while the
    /// lock is still engaged - see design.md. Polled on an interval rather than every frame, and only
    /// re-sends `/follow <t>` once the gap has grown past normal trailing distance without closing, so a
    /// Sub simply lagging a step behind never triggers it.
    private void CheckForDesync(IGameObject owner)
    {
        if (!followActive) return;
        var now = DateTime.UtcNow;
        if (now - lastDesyncCheck < DesyncCheckInterval) return;

        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null) return;

        var distance = Vector3.Distance(player.Position, owner.Position);
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
