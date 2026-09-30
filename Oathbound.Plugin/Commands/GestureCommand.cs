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

    /// Gap between the Penumbra redraw and playing the tied trigger, so the animation reliably starts
    /// after the redraw visually settles instead of racing a visible flicker.
    private const long PlayDelayMs = 500;

    /// How long an active temporary activation survives with no further gesture play before it's
    /// automatically reverted.
    private const long IdleTimeoutMs = 30_000;

    private readonly PluginConfig config;
    private readonly PenumbraIpc penumbra;
    private readonly GestureCatalogScanner scanner;
    private readonly TemporaryModSettingsCoordinator temporarySettings;
    private readonly CatalogStore catalogStore;

    private (GestureTrigger Trigger, long ReadyAtTicks)? pendingPlay;
    private (Guid Collection, string ModDirectory, long IdleUntilTicks)? activeTemporary;

    public int? LastScanTotalMods { get; private set; }
    public string? LastScanError { get; private set; }

    /// Whether there's an active temporary Penumbra activation to revert - lets the Gesture module's
    /// manual Reset control enable/disable itself.
    public bool HasActiveTemporary => activeTemporary is not null;

    public GestureCommand(PluginConfig config, PenumbraIpc penumbra, TemporaryModSettingsCoordinator temporarySettings, CatalogStore catalogStore)
    {
        this.config = config;
        this.penumbra = penumbra;
        this.temporarySettings = temporarySettings;
        this.catalogStore = catalogStore;
        scanner = new GestureCatalogScanner(penumbra, config);
    }

    /// Advanced from Plugin.OnFrameworkUpdate - the same per-frame hook already driving the panic
    /// hotkey, so the delayed play and idle-timeout revert both stay on the framework thread instead of
    /// racing a background Task.
    public void OnFrameworkUpdate()
    {
        var now = Environment.TickCount64;

        if (pendingPlay is { } pending && now >= pending.ReadyAtTicks)
        {
            pendingPlay = null;
            if (!Play(pending.Trigger))
                Plugin.Log.Warning($"Gesture playback failed after redraw for '{pending.Trigger.DisplayName}'.");
        }

        if (activeTemporary is { } active && now >= active.IdleUntilTicks)
        {
            // Never pull the mod out from under an animation that's still playing: a looping emote or a
            // sit/doze pose would drop straight back to vanilla. Keep holding it until the character is back
            // to standing normally, then release.
            if (IsStanding())
                ResetActiveTemporary();
            else
                activeTemporary = active with { IdleUntilTicks = now + StillPlayingRecheckMs };
        }
    }

    /// While the gesture is still playing past its idle timeout, how often to check whether it has ended.
    private const long StillPlayingRecheckMs = 2_000;

    /// Reverts the active temporary gesture activation on demand - used by the manual Reset control and
    /// internally whenever a different mod's temporary activation needs to replace this one.
    public void ResetActiveTemporary()
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

        // Not `.ToDictionary(e => e.Id)` - `StableId` hashes ModDirectory/GroupName/AnimationName/Trigger
        // only, so two distinct groups/options that happen to share a name and produce no detected
        // trigger (common - most options aren't emotes) can legitimately hash to the same id. ToDictionary
        // throws on that; a plain indexer assignment keeps the last-scanned entry for a colliding id
        // instead of crashing the whole rescan.
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

    /// collar/catalog-sync: serializes only `GestureExportEntry`'s slim shape, not the full
    /// `GestureCatalogEntry` - see that type's own doc comment for why (the exported file's size scales
    /// with entry count, not with how many option groups each entry's source mod happens to have).
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

    public bool Apply(GestureAliasDefinition alias)
    {
        if (!string.IsNullOrEmpty(alias.GestureId) && config.GestureMapping.LocalCatalog.TryGetValue(alias.GestureId, out var exact)) return Execute(exact);
        var matches = config.GestureMapping.LocalCatalog.Values.Where(e => e.Trigger != null && e.ModDirectory == alias.ModDirectory &&
            string.Equals(e.Trigger.DisplayName.TrimStart('/'), alias.EmoteName.TrimStart('/'), StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 && Execute(matches[0]);
    }

    public bool ForceApply(string idOrName)
        => ForceApplyDetailed(idOrName).Success;

    public ApplyResult ForceApplyDetailed(string input)
    {
        var resolution = CommandSelector.ResolveGestureDetailed(config.GestureMapping.LocalCatalog.Values, input);
        if (resolution.Entry is null)
            return new ApplyResult(resolution.Status switch
            {
                CommandSelector.ResolutionStatus.Ambiguous => ApplyStatus.Ambiguous,
                CommandSelector.ResolutionStatus.Malformed => ApplyStatus.Malformed,
                _ => ApplyStatus.Missing,
            });
        return ExecuteDetailed(resolution.Entry);
    }

    private bool Execute(GestureCatalogEntry entry)
        => ExecuteDetailed(entry).Success;

    private ApplyResult ExecuteDetailed(GestureCatalogEntry entry)
    {
        if (entry.Trigger is null) return new ApplyResult(ApplyStatus.Missing, entry.AnimationName);
        var collection = penumbra.TryGetLocalPlayerCollectionId();
        if (collection is null) return new ApplyResult(ApplyStatus.CollectionUnavailable, entry.AnimationName);

        // Switching to a different mod's temporary activation must revert whatever was previously
        // active first, so its settings never linger once an unrelated gesture takes over.
        if (activeTemporary is { } active && (active.Collection != collection.Value || active.ModDirectory != entry.ModDirectory))
            ResetActiveTemporary();

        var selections = entry.GroupSelections.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value);
        if (!temporarySettings.Acquire("gesture", collection.Value, entry.ModDirectory, selections))
            return new ApplyResult(ApplyStatus.TemporarySettingsFailed, entry.AnimationName);
        if (!penumbra.TryRedrawLocalPlayer())
        {
            temporarySettings.Release("gesture", collection.Value, entry.ModDirectory);
            return new ApplyResult(ApplyStatus.RedrawFailed, entry.AnimationName);
        }

        var now = Environment.TickCount64;
        activeTemporary = (collection.Value, entry.ModDirectory, now + IdleTimeoutMs);
        pendingPlay = (entry.Trigger, now + PlayDelayMs);
        return new ApplyResult(ApplyStatus.Success, entry.AnimationName);
    }

    /// collar/follow: any emote/pose command played here cancels the game's own follow state, through a
    /// path MovementLockService's UnfollowDetour doesn't cover - fired after every successful play so
    /// FollowCommand can re-assert the leash regardless of which caller (Gesture or a restraint's forced
    /// pose) triggered it. See design.md's "react to gesture playback" decision.
    public static event Action? EmotePlayed;

    /// Internal rather than private: collar/restraints' Arms Cuffed/Legs Cuffed/Full Body Cuffed rules
    /// reuse this exact one-shot trigger playback for their own chosen animation, distinct from Gesture's
    /// own temporary-activation/idle-timeout bookkeeping which those rules deliberately don't share.
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

    /// How long to wait before checking whether a pose command only stood the character up.
    private static readonly TimeSpan PoseRetryDelay = TimeSpan.FromMilliseconds(1500);

    /// Sends /groundsit, /sit or /doze for `emoteModeId` (1-3). Those commands toggle: sent while already
    /// sitting, they stand the character up instead of switching to the newly selected pose. So when the
    /// character isn't in its normal standing state, check again once the transition has played out and,
    /// if it's standing now, send the command a second time to sit in the new pose.
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

    /// How long to let one /cpose step land before reading the pose again.
    private static readonly TimeSpan CPoseStepDelay = TimeSpan.FromMilliseconds(700);
    private const int MaxStandingIdleSteps = 7;

    /// Standing idles have no "switch to pose N" command - /cpose only steps to the next one - so step until
    /// the character's current idle is `target`. Only while standing with the weapon sheathed: there /cpose
    /// would change a sitting pose or the battle stance instead. Capped at one full cycle, so a character
    /// without that many idles doesn't keep cycling.
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
