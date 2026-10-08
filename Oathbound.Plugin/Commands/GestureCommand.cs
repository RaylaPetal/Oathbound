using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Oathbound.Plugin.Commands;

public sealed class GestureCommand
{
    public enum ApplyStatus { Success, Missing, Ambiguous, Malformed, CollectionUnavailable, TemporarySettingsFailed, RedrawFailed }
    public readonly record struct ApplyResult(ApplyStatus Status, string? DisplayName = null)
    {
        public bool Success => Status == ApplyStatus.Success;
    }
    private const string ExportPrefix = "COLLAR-GESTURE-V1|";

    /// Lets the Penumbra redraw settle before playing, instead of racing a visible flicker.
    private const long PlayDelayMs = 500;

    /// An idle temporary activation is reverted after this long.
    private const long IdleTimeoutMs = 30_000;

    private readonly PluginConfig config;
    private readonly PenumbraIpc penumbra;
    private readonly GestureCatalogScanner scanner;
    private readonly TemporaryModSettingsCoordinator temporarySettings;
    private readonly CatalogStore catalogStore;
    private readonly MovementLockService movementLock;

    /// MovementLockService claim owner for an Owner-sent animation's hold.
    private const string HoldOwner = "gesture";

    private (GestureTrigger Trigger, long ReadyAtTicks)? pendingPlay;
    /// Selections is the option set's signature, so replaying the same animation can tell nothing needs changing.
    private (Guid Collection, string ModDirectory, string Selections, long IdleUntilTicks)? activeTemporary;
    /// The trigger the Sub is held in, so Stop can stand them up from a seated pose.
    private GestureTrigger? heldTrigger;
    private Guid? heldBySourcePairingId;
    /// The held slash emote's row, so Stop knows whether it's still looping and a one-shot knows when it's over.
    private (ushort EmoteId, bool Looping)? heldEmote;
    private bool heldEmoteSeenPlaying;
    private long heldEmoteDeadlineTicks;

    /// A one-shot whose end is never seen still frees the Sub after this long.
    private const long OneShotCapMs = 30_000;

    /// A timed hold ends by itself at this tick, one-shot or looping alike.
    private long? heldUntilTicks;
    /// Told whether the hold ran its full time (true) or ended any other way (false).
    private Action<bool>? holdEnded;

    public const int MinHoldSeconds = 1;
    public const int MaxHoldSeconds = 3600;

    public double? HoldSecondsLeft => heldUntilTicks is { } until ? Math.Max(0, (until - Environment.TickCount64) / 1000.0) : null;

    public int? LastScanTotalMods { get; private set; }
    public string? LastScanError { get; private set; }

    public bool HasActiveTemporary => activeTemporary is not null;

    public bool IsHeld => heldTrigger is not null;

    public string? HeldName => heldTrigger?.DisplayName;

    public GestureCommand(PluginConfig config, PenumbraIpc penumbra, TemporaryModSettingsCoordinator temporarySettings, CatalogStore catalogStore, MovementLockService movementLock)
    {
        this.config = config;
        this.penumbra = penumbra;
        this.temporarySettings = temporarySettings;
        this.catalogStore = catalogStore;
        this.movementLock = movementLock;
        scanner = new GestureCatalogScanner(penumbra, config);
    }

    /// Keeps the delayed play and idle revert on the framework thread.
    public void OnFrameworkUpdate()
    {
        var now = Environment.TickCount64;

        if (pendingPlay is { } pending && now >= pending.ReadyAtTicks)
        {
            pendingPlay = null;
            if (!Play(pending.Trigger))
                Plugin.Log.Warning($"Gesture playback failed after redraw for '{pending.Trigger.DisplayName}'.");
        }

        if (heldUntilTicks is { } until && now >= until)
        {
            var ended = holdEnded;
            holdEnded = null;
            Stop();
            ended?.Invoke(true);
        }
        else if (pendingPlay is null && heldUntilTicks is null && heldEmote is { Looping: false } oneShot)
            WatchOneShot(oneShot.EmoteId, now);

        // A held animation keeps its mod until Stop.
        if (heldTrigger is null && activeTemporary is { } active && now >= active.IdleUntilTicks)
        {
            // Never pull the mod out from under a looping emote or pose; wait until the character stands normally.
            if (IsStanding())
                ResetActiveTemporary();
            else
                activeTemporary = active with { IdleUntilTicks = now + StillPlayingRecheckMs };
        }
    }

    private const long StillPlayingRecheckMs = 2_000;

    /// Revert all and panic go through here, so they also end the hold.
    public void ResetActiveTemporary()
    {
        Stop();
        ReleaseTemporary();
    }

    /// Ends an Owner-sent animation: movement back, mod off, up out of a seated pose, and a looping slash emote
    /// ended with a tiny step (the game has no cancel call; moving is how it ends one). Returns whether anything was held.
    public bool Stop()
    {
        pendingPlay = null;
        if (heldTrigger is not { } trigger)
            return false;

        var emote = heldEmote;
        ReleaseHold();
        if (trigger.Kind != GestureTriggerKind.SlashCommand && trigger.EmoteModeId is >= 1 and <= 3 && !IsStanding())
            Chat.SendMessage(trigger.EmoteModeId switch { 1 => "/groundsit", 2 => "/sit", _ => "/doze" });
        if (emote is { Looping: true } looping && CurrentEmoteId() == looping.EmoteId)
            movementLock.RequestStepPulse();
        return true;
    }

    private void ReleaseHold()
    {
        var ended = holdEnded;
        holdEnded = null;
        heldTrigger = null;
        heldBySourcePairingId = null;
        heldEmote = null;
        heldUntilTicks = null;
        movementLock.ReleaseImmobilize(HoldOwner);
        ReleaseTemporary();
        ended?.Invoke(false);
    }

    /// Released only once the emote was seen playing and then seen over, so the gap before it starts doesn't count.
    private void WatchOneShot(ushort emoteId, long now)
    {
        var current = CurrentEmoteId();
        if (current == emoteId)
        {
            if (!heldEmoteSeenPlaying)
                Plugin.Log.Debug($"One-shot emote {emoteId} started.");
            heldEmoteSeenPlaying = true;
        }
        else if (heldEmoteSeenPlaying)
        {
            Plugin.Log.Debug($"One-shot emote {emoteId} over (emote now {current}); releasing the hold.");
            ReleaseHold();
            return;
        }

        if (now >= heldEmoteDeadlineTicks)
        {
            Plugin.Log.Debug($"One-shot emote {emoteId} hit the {OneShotCapMs / 1000}s cap (seen playing: {heldEmoteSeenPlaying}); releasing the hold.");
            ReleaseHold();
        }
    }

    private static unsafe ushort CurrentEmoteId()
    {
        var player = Control.GetLocalPlayer();
        return player == null ? (ushort)0 : player->EmoteController.EmoteId;
    }

    /// Pairing-ended events don't say which pairing, so check whether it was the one holding the Sub.
    public void StopIfSourcePairingEnded()
    {
        if (heldBySourcePairingId is { } id && config.FindPairingById(id) is not { IsPaired: true })
            Stop();
    }

    /// Also called whenever a different mod's activation replaces this one.
    private void ReleaseTemporary()
    {
        if (activeTemporary is not { } active)
            return;

        temporarySettings.Release("gesture", active.Collection, active.ModDirectory);
        activeTemporary = null;
    }

    public void Rescan()
    {
        var result = scanner.Scan();
        LastScanTotalMods = result.TotalMods;
        LastScanError = result.Error;
        if (result.Error != null) return;

        // Not ToDictionary: distinct options can hash to the same StableId, and ToDictionary would throw.
        var catalog = new Dictionary<string, GestureCatalogEntry>();
        foreach (var entry in result.Entries)
            catalog[entry.Id] = entry;
        config.GestureMapping.LocalCatalog = catalog;
        catalogStore.Save(config);
        MigrateAliases();
        config.Save();
    }

    public IReadOnlyList<(string Directory, string Name, string? SortPath)> GetInstalledMods()
    {
        var mods = penumbra.TryGetModList();
        return mods is null ? [] : mods.Select(x => (x.Key, x.Value, penumbra.TryGetModPath(x.Key, x.Value))).OrderBy(x => x.Value).ToList();
    }

    /// Exports the slim GestureExportEntry shape so the file scales with entry count only.
    public string ExportCatalog() => string.Join("\n", config.GestureMapping.LocalCatalog.Values
        .Where(e => !string.IsNullOrWhiteSpace(e.ModDirectory) && e.GroupSelections.Count > 0).OrderBy(e => e.Label)
        .Select(e => ExportPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(GestureExportEntry.From(e))))));

    public static bool TryParseExport(string line, out GestureExportEntry? entry)
    {
        entry = null;
        if (!line.StartsWith(ExportPrefix, StringComparison.Ordinal)) return false;
        try
        {
            entry = JsonSerializer.Deserialize<GestureExportEntry>(Encoding.UTF8.GetString(Convert.FromBase64String(line[ExportPrefix.Length..])));
            return entry is { Id.Length: > 0, ModName.Length: > 0, AnimationName.Length: > 0 };
        }
        catch { return false; }
    }

    /// `sourcePairingId` is the Owner pairing that sent it; null for a local test.
    public bool Apply(GestureAliasDefinition alias, Guid? sourcePairingId)
    {
        if (!string.IsNullOrEmpty(alias.GestureId) && config.GestureMapping.LocalCatalog.TryGetValue(alias.GestureId, out var exact)) return Execute(exact, sourcePairingId);
        var matches = config.GestureMapping.LocalCatalog.Values.Where(e => e.Trigger != null && e.ModDirectory == alias.ModDirectory &&
            string.Equals(e.Trigger.DisplayName.TrimStart('/'), alias.EmoteName.TrimStart('/'), StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 && Execute(matches[0], sourcePairingId);
    }

    public bool ForceApply(string idOrName, Guid? sourcePairingId)
        => ForceApplyDetailed(idOrName, sourcePairingId).Success;

    public ApplyResult ForceApplyDetailed(string input, Guid? sourcePairingId, int? holdSeconds = null)
    {
        var resolution = CommandSelector.ResolveGestureDetailed(config.GestureMapping.LocalCatalog.Values, input);
        if (resolution.Entry is null)
            return new ApplyResult(resolution.Status switch
            {
                CommandSelector.ResolutionStatus.Ambiguous => ApplyStatus.Ambiguous,
                CommandSelector.ResolutionStatus.Malformed => ApplyStatus.Malformed,
                _ => ApplyStatus.Missing,
            });
        return ExecuteDetailed(resolution.Entry, sourcePairingId, holdSeconds);
    }

    /// The Sub's own Perform on an oath's modded animation, resolved by its catalog id.
    public ApplyResult PlayHeld(string catalogId, Guid sourcePairingId, int holdSeconds, Action<bool> onEnd) =>
        config.GestureMapping.LocalCatalog.TryGetValue(catalogId, out var entry)
            ? ExecuteDetailed(entry, sourcePairingId, holdSeconds, onEnd)
            : new ApplyResult(ApplyStatus.Missing);

    /// The Sub's own Perform on a vanilla emote: the same hold, with no mod.
    public void PlayHeld(GestureTrigger trigger, Guid sourcePairingId, int holdSeconds, Action<bool> onEnd)
    {
        if (activeTemporary is not null)
        {
            // A modded animation still active would otherwise replace the vanilla emote.
            ReleaseTemporary();
            penumbra.TryRedrawLocalPlayer();
        }
        BeginHold(trigger, sourcePairingId, holdSeconds, onEnd, Environment.TickCount64, PlayDelayMs);
    }

    private bool Execute(GestureCatalogEntry entry, Guid? sourcePairingId)
        => ExecuteDetailed(entry, sourcePairingId).Success;

    private ApplyResult ExecuteDetailed(GestureCatalogEntry entry, Guid? sourcePairingId, int? holdSeconds = null, Action<bool>? onEnd = null)
    {
        if (entry.Trigger is null) return new ApplyResult(ApplyStatus.Missing, entry.AnimationName);
        var collection = penumbra.TryGetLocalPlayerCollectionId();
        if (collection is null) return new ApplyResult(ApplyStatus.CollectionUnavailable, entry.AnimationName);

        // Revert the previous mod's activation first so its settings never linger.
        if (activeTemporary is { } active && (active.Collection != collection.Value || active.ModDirectory != entry.ModDirectory))
            ReleaseTemporary();

        var signature = SelectionSignature(entry.GroupSelections);
        // Rewriting the settings and redrawing makes sync plugins re-send the character (a visible flash for
        // everyone synced) and drop the animation files they only learn about when they play.
        var alreadyOn = activeTemporary is { } on && on.Collection == collection.Value && on.ModDirectory == entry.ModDirectory && on.Selections == signature;
        if (!alreadyOn)
        {
            var selections = entry.GroupSelections.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value);
            if (!temporarySettings.Acquire("gesture", collection.Value, entry.ModDirectory, selections))
                return new ApplyResult(ApplyStatus.TemporarySettingsFailed, entry.AnimationName);
            if (!penumbra.TryRedrawLocalPlayer())
            {
                temporarySettings.Release("gesture", collection.Value, entry.ModDirectory);
                return new ApplyResult(ApplyStatus.RedrawFailed, entry.AnimationName);
            }
        }

        var now = Environment.TickCount64;
        activeTemporary = (collection.Value, entry.ModDirectory, signature, now + IdleTimeoutMs);
        BeginHold(entry.Trigger, sourcePairingId, holdSeconds, onEnd, now, alreadyOn ? 0 : PlayDelayMs);
        return new ApplyResult(ApplyStatus.Success, entry.AnimationName);
    }

    private static string SelectionSignature(Dictionary<string, List<string>> selections) =>
        string.Join("|", selections.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key + "=" + string.Join(",", x.Value.OrderBy(v => v, StringComparer.Ordinal))));

    /// Every caller is an Owner command or the Sub's Perform on an oath they swore, so it holds the Sub like a Forced Pose.
    /// `playDelayMs` lets a redraw settle first; with nothing redrawn it plays on the next frame.
    private void BeginHold(GestureTrigger trigger, Guid? sourcePairingId, int? holdSeconds, Action<bool>? onEnd, long now, long playDelayMs)
    {
        // A hold being replaced didn't run its course.
        var replaced = holdEnded;
        holdEnded = null;
        replaced?.Invoke(false);

        pendingPlay = (trigger, now + playDelayMs);
        heldTrigger = trigger;
        heldBySourcePairingId = sourcePairingId;
        // Unknown commands stay held until stopped, like before.
        heldEmote = trigger.Kind == GestureTriggerKind.SlashCommand ? GestureTriggerResolver.LookupEmoteMode(trigger.SlashCommand) : null;
        heldEmoteSeenPlaying = false;
        heldEmoteDeadlineTicks = now + playDelayMs + OneShotCapMs;
        heldUntilTicks = holdSeconds is { } seconds ? now + playDelayMs + Math.Clamp(seconds, MinHoldSeconds, MaxHoldSeconds) * 1000L : null;
        holdEnded = onEnd;
        Plugin.Log.Debug($"Holding for {trigger.DisplayName}: emote {heldEmote?.EmoteId.ToString() ?? "unknown"}, " +
            (heldUntilTicks is not null ? $"timed {holdSeconds}s" : heldEmote is { Looping: false } ? "one-shot" : "holds until stopped") + ".");
        movementLock.EngageImmobilize(HoldOwner);
    }

    /// Emotes cancel the game's follow through a path MovementLockService doesn't hook, so FollowCommand re-asserts on this.
    public static event Action? EmotePlayed;

    /// Internal: restraint cuff rules reuse this one-shot playback without Gesture's idle bookkeeping.
    internal static unsafe bool Play(GestureTrigger trigger)
    {
        if (trigger.Kind == GestureTriggerKind.SlashCommand)
        {
            if (string.IsNullOrWhiteSpace(trigger.SlashCommand)) return false;
            Chat.SendMessage($"/{trigger.SlashCommand.TrimStart('/')} motion");
            EmotePlayed?.Invoke();
            return true;
        }
        if (trigger.EmoteModeId == 0)
        {
            RotateStandingIdle(trigger.CPoseState);
            EmotePlayed?.Invoke();
            return true;
        }
        var playerState = PlayerState.Instance();
        if (playerState == null || trigger.EmoteModeId is < 1 or > 3) return false;
        var poseType = trigger.EmoteModeId switch
        {
            1 => EmoteController.PoseType.GroundSit,
            2 => EmoteController.PoseType.Sit,
            3 => EmoteController.PoseType.Doze,
            _ => throw new ArgumentOutOfRangeException(),
        };
        playerState->SelectedPoses[(int)poseType] = trigger.CPoseState;
        SendPoseCommand((int)trigger.EmoteModeId);
        EmotePlayed?.Invoke();
        return true;
    }

    private static readonly TimeSpan PoseRetryDelay = TimeSpan.FromMilliseconds(1500);

    /// /groundsit, /sit and /doze toggle: sent while seated they stand the character up. If that happened, send it again.
    internal static unsafe void SendPoseCommand(int emoteModeId)
    {
        var command = emoteModeId switch { 1 => "/groundsit", 2 => "/sit", 3 => "/doze", _ => "" };
        if (command.Length == 0)
            return;

        var wasStanding = IsStanding();
        Chat.SendMessage(command);
        if (wasStanding)
            return;

        Plugin.Framework.RunOnTick(() =>
        {
            if (IsStanding())
                Chat.SendMessage(command);
        }, PoseRetryDelay);
    }

    private static readonly TimeSpan CPoseStepDelay = TimeSpan.FromMilliseconds(700);
    private const int MaxStandingIdleSteps = 7;

    /// /cpose only steps to the next idle, so step until it's `target` - only while standing with the weapon sheathed.
    /// Capped at one full cycle.
    internal static unsafe void RotateStandingIdle(byte target, int stepsLeft = MaxStandingIdleSteps)
    {
        var player = Control.GetLocalPlayer();
        if (player == null || player->Mode != CharacterModes.Normal || player->IsWeaponDrawn)
            return;
        if (player->EmoteController.CPoseState == target)
            return;
        if (stepsLeft == 0)
        {
            Plugin.Log.Warning($"Couldn't rotate to standing idle {target}: still on {player->EmoteController.CPoseState} after a full /cpose cycle.");
            return;
        }

        Chat.SendMessage("/cpose");
        Plugin.Framework.RunOnTick(() => RotateStandingIdle(target, stepsLeft - 1), CPoseStepDelay);
    }

    private static unsafe bool IsStanding()
    {
        var player = Control.GetLocalPlayer();
        return player == null || player->Mode == CharacterModes.Normal;
    }

    private void MigrateAliases()
    {
        foreach (var alias in config.Aliases.Gestures.Where(a => string.IsNullOrEmpty(a.GestureId)))
        {
            var hits = config.GestureMapping.LocalCatalog.Values.Where(e => e.Trigger != null && e.ModDirectory == alias.ModDirectory &&
                e.Trigger.DisplayName.TrimStart('/').Equals(alias.EmoteName.TrimStart('/'), StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count != 1) continue;
            alias.GestureId = hits[0].Id;
            alias.AnimationName = hits[0].AnimationName;
        }
    }
}
