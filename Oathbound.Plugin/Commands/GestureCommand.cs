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

    private (GestureTrigger Trigger, long ReadyAtTicks)? pendingPlay;
    private (Guid Collection, string ModDirectory, long IdleUntilTicks)? activeTemporary;

    public int? LastScanTotalMods { get; private set; }
    public string? LastScanError { get; private set; }

    public bool HasActiveTemporary => activeTemporary is not null;

    public GestureCommand(PluginConfig config, PenumbraIpc penumbra, TemporaryModSettingsCoordinator temporarySettings, CatalogStore catalogStore)
    {
        this.config = config;
        this.penumbra = penumbra;
        this.temporarySettings = temporarySettings;
        this.catalogStore = catalogStore;
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

        if (activeTemporary is { } active && now >= active.IdleUntilTicks)
        {
            // Never pull the mod out from under a looping emote or pose; wait until the character stands normally.
            if (IsStanding())
                ResetActiveTemporary();
            else
                activeTemporary = active with { IdleUntilTicks = now + StillPlayingRecheckMs };
        }
    }

    private const long StillPlayingRecheckMs = 2_000;

    /// Also called whenever a different mod's activation replaces this one.
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

        // Revert the previous mod's activation first so its settings never linger.
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
