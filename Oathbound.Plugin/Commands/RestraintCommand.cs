using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Safety;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Glamourer.Api.Enums;

namespace Oathbound.Plugin.Commands;

/// Applies restraint devices: locks their gear slot and activates their restriction rules. Sub toggle via its word;
/// Owner force-apply locks out the Sub's own controls while active.
public sealed class RestraintCommand
{
    private const string ExportPrefix = "OATHBOUND-RESTRAINT-V1|";
    private const string ConfiguredExportPrefix = "OATHBOUND-RESTRAINT-CONFIG-V1|";
    public string? LastFailureReason { get; private set; }
    private const string Owner = "Restraints";

    /// A restraint may take over a locked outfit's slot (restored on release). The collar's Neck lock still refuses it.
    private static readonly string[] TakesOverFrom = [OutfitCommand.SlotLockOwner];
    private const long PlayDelayMs = 500;

    private readonly PluginConfig config;
    private readonly GlamourerIpc glamourer;
    private readonly PenumbraIpc penumbra;
    private readonly SlotLockManager slotLocks;
    private readonly RestrictionRuleManager restrictionRules;
    private readonly SubRuntimeState runtimeState;
    private readonly GestureCatalogScanner catalogScanner;
    private readonly TemporaryModSettingsCoordinator temporarySettings;
    private readonly ChatGagService chatGagService;
    private readonly CatalogStore catalogStore;
    private readonly MoodlesCommand moodles;
    public int? LastScanTotalMods { get; private set; }
    public int LastScanMatchedMods { get; private set; }
    public string? LastScanError { get; private set; }

    public RestraintCommand(PluginConfig config, GlamourerIpc glamourer, PenumbraIpc penumbra, SlotLockManager slotLocks, RestrictionRuleManager restrictionRules, SubRuntimeState runtimeState, TemporaryModSettingsCoordinator temporarySettings, ChatGagService chatGagService, CatalogStore catalogStore, MoodlesCommand moodles)
    {
        this.config = config;
        this.glamourer = glamourer;
        this.penumbra = penumbra;
        this.slotLocks = slotLocks;
        this.restrictionRules = restrictionRules;
        this.runtimeState = runtimeState;
        this.temporarySettings = temporarySettings;
        this.chatGagService = chatGagService;
        this.catalogStore = catalogStore;
        this.moodles = moodles;
        catalogScanner = new GestureCatalogScanner(penumbra, config);
    }

    public void RescanCatalog()
    {
        var scope = catalogScanner.ResolveRestraintScope();
        var result = catalogScanner.ScanMods(scope);
        LastScanTotalMods = result.TotalMods;
        LastScanMatchedMods = scope.Count;
        LastScanError = result.Error;
        if (result.Error is not null) return;
        config.RestraintMapping.LocalCatalog = result.Entries.ToDictionary(e => e.Id, e => new RestraintCatalogEntry
        {
            Id = e.Id, ModDirectory = e.ModDirectory, ModName = e.ModName,
            GroupSelections = e.SavedSelections, ModEnabled = e.ModEnabled, ChangedItemIds = e.ChangedItemIds.ToList(),
        });
        catalogStore.Save(config);
    }

    public string ExportCatalog() => string.Join("\n", config.RestraintMapping.LocalCatalog.Values
        .OrderBy(e => e.ModName)
        .Select(e => EncodeExport(RestraintCatalogExportEntry.From(e))));

    public static bool TryParseExport(string line, out RestraintCatalogExportEntry? entry)
    {
        entry = null;
        if (!line.StartsWith(ExportPrefix, StringComparison.Ordinal)) return false;
        try
        {
            entry = JsonSerializer.Deserialize<RestraintCatalogExportEntry>(Encoding.UTF8.GetString(Convert.FromBase64String(line[ExportPrefix.Length..])));
            return entry is { Id.Length: 16, ModName.Length: > 0 and <= 160 }
                && entry.Id.All(char.IsAsciiLetterOrDigit);
        }
        catch { return false; }
    }

    public static string EncodeExport(RestraintCatalogExportEntry entry) =>
        ExportPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry)));

    public static string EncodeConfiguredExport(ConfiguredModRestraintExportEntry entry) =>
        ConfiguredExportPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry)));

    public static bool TryParseConfiguredExport(string line, out ConfiguredModRestraintExportEntry? entry)
    {
        entry = null;
        if (!line.StartsWith(ConfiguredExportPrefix, StringComparison.Ordinal)) return false;
        try
        {
            entry = JsonSerializer.Deserialize<ConfiguredModRestraintExportEntry>(Encoding.UTF8.GetString(Convert.FromBase64String(line[ConfiguredExportPrefix.Length..])));
            return entry is { Id.Length: > 0 and <= 64, CatalogId.Length: 16, Name.Length: > 0 and <= 80,
                ItemId: > 0, Rules.Count: > 0 }
                && entry.CatalogId.All(char.IsAsciiLetterOrDigit);
        }
        catch { return false; }
    }

    public IReadOnlySet<string> ActiveDeviceIds => activeDeviceIds;
    private readonly HashSet<string> activeDeviceIds = new();

    /// Keyed by (device, rule kind): one device can hold several bound animations. Kept apart from GestureCommand
    /// so a restraint's animation is never hit by Gesture's idle-timeout revert.
    private readonly Dictionary<(string DeviceId, RestraintRuleKind Kind), (Guid Collection, string ModDirectory)> boundAnimations = new();
    private readonly Dictionary<(string DeviceId, RestraintRuleKind Kind), (GestureTrigger Trigger, long ReadyAtTicks)> pendingBoundPlays = new();
    private readonly Dictionary<string, (Guid Collection, string ModDirectory)> activeCatalogOverrides = new();

    public bool IsActive(string deviceId) => activeDeviceIds.Contains(deviceId);

    /// A captured device's name, or a configured mod restraint's alias - the same word bare (toggle) or after `restraint lock`.
    private RestraintDeviceDefinition? FindDeviceByWord(string word) =>
        config.RestraintMapping.Devices.Values.FirstOrDefault(d => string.Equals(d.Name.Trim(), word.Trim(), StringComparison.OrdinalIgnoreCase));

    private ConfiguredModRestraint? FindConfiguredModByWord(string word) =>
        config.RestraintMapping.ConfiguredMods.FirstOrDefault(m => m.Alias.Trim().Length > 0
            && string.Equals(m.Alias.Trim(), word.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool MatchesWord(string word) => FindDeviceByWord(word) is not null || FindConfiguredModByWord(word) is not null;

    private static string CatalogRuntimeId(string catalogId) => $"catalog:{catalogId}";

    /// Refused while an Owner force-lock is in effect.
    public bool ToggleByWord(string word)
    {
        LastFailureReason = null;
        if (runtimeState.RestraintsForceLocked)
        {
            LastFailureReason = "restraints are currently force-locked by your Owner";
            return false;
        }

        if (FindDeviceByWord(word) is { } device)
            return activeDeviceIds.Contains(device.Id) ? Release(device.Id) : ApplyDevice(device.Id, device);

        if (FindConfiguredModByWord(word) is { } mod)
        {
            var runtimeId = CatalogRuntimeId(mod.CatalogId);
            if (activeDeviceIds.Contains(runtimeId))
                return Release(runtimeId);
            if (mod.ItemId is not { } itemId || mod.Rules.Count == 0)
            {
                LastFailureReason = "that configured restraint has no item or rules yet";
                return false;
            }
            return ApplyCatalog(mod.CatalogId, itemId, mod.Rules, moodleOverride: null);
        }

        LastFailureReason = $"no restraint named \"{word}\"";
        return false;
    }

    private bool Release(string deviceId)
    {
        if (!activeDeviceIds.Contains(deviceId))
            return false;

        ReleaseDevice(deviceId);
        return true;
    }

    /// Always force-locks. `moodleOverride` (here and below) replaces the device's default moodle.
    public bool ForceApply(string deviceName, string? moodleOverride = null, RestraintLock restraintLock = default)
    {
        LastFailureReason = null;
        var entry = FindDeviceByWord(deviceName);
        if (entry is null)
        {
            if (FindConfiguredModByWord(deviceName) is { ItemId: { } itemId } mod && mod.Rules.Count > 0)
                return ForceApplyCatalog(mod.CatalogId, itemId, mod.Rules, moodleOverride, restraintLock);
            LastFailureReason = $"device \"{deviceName}\" was not found";
            return false;
        }

        if (!ApplyDevice(entry.Id, entry, moodleOverride))
            return false;

        EngageLock(restraintLock);
        return true;
    }

    /// A Custom Trigger is an Owner command, so this doesn't use the Sub's Toggle (refused while force-locked).
    public bool ForceApplyById(string deviceId, RestraintLock restraintLock = default)
    {
        LastFailureReason = null;
        if (!config.RestraintMapping.Devices.TryGetValue(deviceId, out var device))
        {
            LastFailureReason = $"saved device id {deviceId} is stale";
            return false;
        }

        // Idempotent: don't toggle an active device back off.
        if (activeDeviceIds.Contains(deviceId))
        {
            Replay(deviceId, device.Rules);
            EngageLock(restraintLock);
            return true;
        }

        if (!ApplyDevice(device.Id, device))
            return false;

        EngageLock(restraintLock);
        return true;
    }

    /// Activates exactly the Owner's rules, ignoring the Sub's own rules for that device.
    public bool ForceApply(string deviceName, List<RestraintRuleAssignment> rules, string? moodleOverride = null, RestraintLock restraintLock = default)
    {
        var captured = config.RestraintMapping.Devices.Values
            .FirstOrDefault(d => string.Equals(d.Name, deviceName, StringComparison.OrdinalIgnoreCase));
        if (captured is null)
            return false;

        var device = new RestraintDeviceDefinition
        {
            Id = captured.Id,
            Slot = captured.Slot,
            ItemId = captured.ItemId,
            Stain = captured.Stain,
            Stain2 = captured.Stain2,
            Name = captured.Name,
            Rules = rules,
            AttachedMoodle = captured.AttachedMoodle,
        };

        if (!ApplyDevice(device.Id, device, moodleOverride))
            return false;

        EngageLock(restraintLock);
        return true;
    }

    /// The device id is derived from slot+item, so release/conflict tracking works without a Sub-side device.
    public bool ForceApplyAdHoc(ApiEquipSlot? slot, ulong? itemId, string label, List<RestraintRuleAssignment> rules, string? moodleOverride = null, RestraintLock restraintLock = default)
    {
        var device = new RestraintDeviceDefinition
        {
            Id = slot is not null && itemId is not null ? $"adhoc:{slot}:{itemId}" : $"adhoc:rulesonly:{label}",
            Slot = slot,
            ItemId = itemId,
            Stain = 0,
            Stain2 = 0,
            Name = label,
            Rules = rules,
        };

        // No Sub-side device behind an ad-hoc apply, so only an Owner moodle override applies.
        if (!ApplyDevice(device.Id, device, moodleOverride))
            return false;

        EngageLock(restraintLock);
        return true;
    }

    public bool ForceApplyCatalog(string catalogId, ulong itemId, List<RestraintRuleAssignment> rules, string? moodleOverride = null, RestraintLock restraintLock = default)
    {
        if (activeCatalogOverrides.ContainsKey(CatalogRuntimeId(catalogId)))
        {
            Replay(CatalogRuntimeId(catalogId), rules);
            // Re-sending an already-worn restraint still (re)sets the lock.
            EngageLock(restraintLock);
            return true;
        }
        if (!ApplyCatalog(catalogId, itemId, rules, moodleOverride))
            return false;
        EngageLock(restraintLock);
        return true;
    }

    /// No force-lock here; ForceApplyCatalog adds it for Owner commands.
    private unsafe bool ApplyCatalog(string catalogId, ulong itemId, List<RestraintRuleAssignment> rules, string? moodleOverride)
    {
        LastFailureReason = null;
        if (!config.RestraintMapping.LocalCatalog.TryGetValue(catalogId, out var entry))
        {
            LastFailureReason = "that shared restraint is stale or no longer allowed";
            return false;
        }
        var runtimeId = CatalogRuntimeId(catalogId);
        if (activeCatalogOverrides.ContainsKey(runtimeId))
            return true;
        var slot = itemId == 0 ? null : GlamourerIpc.GetItemSlot((uint)itemId);
        if (itemId == 0 || slot is null || slotLocks.WouldOverlap([slot.Value], Owner, TakesOverFrom))
        {
            LastFailureReason = itemId == 0 || slot is null ? "the restraint item is not valid for an equipment slot" : $"equipment slot {slot} is locked by another feature";
            return false;
        }
        if (restrictionRules.WouldConflict(rules, runtimeId))
        {
            LastFailureReason = "a restriction rule conflicts with an active restraint";
            return false;
        }
        if (!restrictionRules.CanActivate(rules, out var unavailable))
        {
            LastFailureReason = $"{unavailable} enforcement is unavailable";
            return false;
        }
        var boundRules = rules.Where(r => r.Kind is RestraintRuleKind.ArmsCuffed or RestraintRuleKind.LegsCuffed or RestraintRuleKind.FullBodyCuffed
            || (r.Kind == RestraintRuleKind.Gagged && !string.IsNullOrWhiteSpace(r.AnimationId))
            || (r.Kind == RestraintRuleKind.ForcedPose && r.PoseModeId == 0)).ToList();
        if (boundRules.Any(r => ResolveAnimation(r.AnimationId) is null))
        {
            LastFailureReason = "a selected cuff animation is missing, stale, or ambiguous";
            return false;
        }
        if (rules.Any(r => r.Kind == RestraintRuleKind.ForcedPose) && PlayerState.Instance() == null)
        {
            LastFailureReason = "the local pose state is unavailable";
            return false;
        }
        var collection = penumbra.TryGetLocalPlayerCollectionId();
        if (collection is null) { LastFailureReason = "the local Penumbra collection is unavailable"; return false; }
        var selections = entry.GroupSelections.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value);
        if (!temporarySettings.Acquire(runtimeId, collection.Value, entry.ModDirectory, selections))
        { LastFailureReason = "Penumbra could not apply the restraint option"; return false; }
        if (!penumbra.TryRedrawLocalPlayer())
        {
            temporarySettings.Release(runtimeId, collection.Value, entry.ModDirectory);
            LastFailureReason = "Penumbra could not redraw after applying the restraint";
            return false;
        }
        if (!slotLocks.TryLock(Owner, new Dictionary<ApiEquipSlot, SlotLockValue> { [slot.Value] = new(itemId, 0, 0) }, TakesOverFrom))
        {
            temporarySettings.Release(runtimeId, collection.Value, entry.ModDirectory);
            penumbra.TryRedrawLocalPlayer();
            LastFailureReason = $"Glamourer could not equip or lock {slot}";
            return false;
        }
        if (rules.Count > 0 && !restrictionRules.TryActivate(runtimeId, rules))
        {
            slotLocks.Release(Owner);
            temporarySettings.Release(runtimeId, collection.Value, entry.ModDirectory);
            penumbra.TryRedrawLocalPlayer();
            LastFailureReason = "restriction enforcement could not be activated";
            return false;
        }
        activeCatalogOverrides[runtimeId] = (collection.Value, entry.ModDirectory);
        var pose = rules.FirstOrDefault(r => r.Kind == RestraintRuleKind.ForcedPose);
        if (pose is not null) ApplyPose(pose.PoseModeId);
        foreach (var rule in boundRules)
        {
            if (EngageBoundAnimation(runtimeId, rule)) continue;
            ReleaseBoundAnimations(runtimeId);
            restrictionRules.Release(runtimeId);
            slotLocks.Release(Owner);
            temporarySettings.Release(runtimeId, collection.Value, entry.ModDirectory);
            activeCatalogOverrides.Remove(runtimeId);
            penumbra.TryRedrawLocalPlayer();
            LastFailureReason = "the selected cuff animation could not be activated; the restraint was rolled back";
            return false;
        }
        foreach (var rule in rules.Where(r => r.Kind == RestraintRuleKind.Gagged))
            chatGagService.ApplyCustomizePreset(runtimeId, rule.CustomizePresetId);
        activeDeviceIds.Add(runtimeId);

        var configured = config.RestraintMapping.ConfiguredMods.FirstOrDefault(m => m.CatalogId == catalogId && m.ItemId == itemId);
        moodles.HoldAttached(AttachedMoodleLedger.RestraintSource(runtimeId), configured?.AttachedMoodle, moodleOverride);
        slotLocks.VerifySoon();
        return true;
    }

    /// Only called after an Owner command actually applied, so a refused command leaves the lock untouched.
    private void EngageLock(RestraintLock restraintLock)
    {
        runtimeState.RestraintsForceLocked = true;
        runtimeState.RestraintsLockExpiresAtUtc = restraintLock.Duration is { } duration ? DateTime.UtcNow + duration : null;
    }

    public bool ForceUnlock()
    {
        // Slot locks survive reloads but device/claim bookkeeping doesn't, so release every layer unconditionally.
        var hadRestraints = activeDeviceIds.Count > 0 || boundAnimations.Count > 0 || activeCatalogOverrides.Count > 0 || runtimeState.RestraintsForceLocked;
        restrictionRules.ReleaseAllForPanic();
        ReleaseAllBoundAnimationsForPanic();
        ReleaseAllCatalogOverrides();
        var gearReleased = slotLocks.Release(Owner);
        runtimeState.RestraintsForceLocked = false;
        moodles.Ledger.ReleaseAllWithPrefix(AttachedMoodleLedger.RestraintPrefix);
        return gearReleased || hadRestraints;
    }

    /// Plays bound animations after Penumbra's redraw settles; playing immediately races the rebuild.
    public void OnFrameworkUpdate()
    {
        // Deferred until the character can be changed, including an end time that passed while unloaded.
        if (runtimeState.RestraintsForceLocked
            && runtimeState.RestraintsLockExpiresAtUtc is { } expiresAt
            && DateTime.UtcNow >= expiresAt
            && Plugin.ClientState.IsLoggedIn
            && Plugin.ObjectTable.LocalPlayer is not null
            && !Plugin.Condition[ConditionFlag.BetweenAreas]
            && !Plugin.Condition[ConditionFlag.BetweenAreas51])
        {
            Plugin.Log.Information("Timed restraints lock expired - releasing restraints.");
            ForceUnlock();
        }

        var now = Environment.TickCount64;
        foreach (var (key, pending) in pendingBoundPlays.Where(x => now >= x.Value.ReadyAtTicks).ToList())
        {
            pendingBoundPlays.Remove(key);
            if (boundAnimations.ContainsKey(key))
                GestureCommand.Play(pending.Trigger);
        }
    }

    /// Both the slot and rule checks run before anything applies, so a refused apply leaves nothing partial.
    private unsafe bool ApplyDevice(string deviceId, RestraintDeviceDefinition device, string? moodleOverride = null)
    {
        var hasGear = device.Slot is not null && device.ItemId is not null;
        if (hasGear && slotLocks.WouldOverlap([device.Slot!.Value], Owner, TakesOverFrom))
        {
            var conflicts = slotLocks.ConflictingLocks([device.Slot!.Value], Owner);
            LastFailureReason = conflicts.Count > 0
                ? $"equipment slot {device.Slot} is locked by {conflicts[0].Owner}"
                : $"equipment slot {device.Slot} is already locked";
            Plugin.Log.Warning($"Restraint apply refused for \"{device.Name}\": a locked slot is already held by a different owner.");
            return false;
        }
        if (restrictionRules.WouldConflict(device.Rules, deviceId))
        {
            LastFailureReason = "a pose or cuff rule conflicts with a different active restraint; unlock existing restraints first";
            Plugin.Log.Warning($"Restraint apply refused for \"{device.Name}\": a restriction rule conflicts with a different device already active.");
            return false;
        }
        if (!restrictionRules.CanActivate(device.Rules, out var unavailable))
        {
            LastFailureReason = $"{unavailable} enforcement is unavailable";
            Plugin.Log.Warning($"Restraint apply refused for '{device.Name}': {unavailable} enforcement is unavailable.");
            return false;
        }
        if (device.Rules.Any(r => r.Kind == RestraintRuleKind.ForcedPose) && PlayerState.Instance() == null)
        {
            LastFailureReason = "the local pose state is unavailable";
            Plugin.Log.Warning($"Restraint apply refused for '{device.Name}': pose state is unavailable.");
            return false;
        }
        var boundRules = device.Rules.Where(r => r.Kind is RestraintRuleKind.ArmsCuffed or RestraintRuleKind.LegsCuffed or RestraintRuleKind.FullBodyCuffed
            || (r.Kind == RestraintRuleKind.Gagged && !string.IsNullOrWhiteSpace(r.AnimationId))
            || (r.Kind == RestraintRuleKind.ForcedPose && r.PoseModeId == 0)).ToList();
        if (boundRules.Any(r => ResolveAnimation(r.AnimationId) is null))
        {
            LastFailureReason = "a selected cuff animation is missing, stale, or ambiguous; edit the restraint and select it again";
            Plugin.Log.Warning($"Restraint apply refused for '{device.Name}': a bound animation is missing, stale, or ambiguous.");
            return false;
        }

        if (hasGear)
        {
            var value = new SlotLockValue(device.ItemId!.Value, device.Stain, device.Stain2);
            if (!slotLocks.TryLock(Owner, new Dictionary<ApiEquipSlot, SlotLockValue> { [device.Slot!.Value] = value }, TakesOverFrom))
            {
                LastFailureReason = $"Glamourer could not apply or lock equipment slot {device.Slot}";
                Plugin.Log.Warning($"Restraint apply failed for \"{device.Name}\": could not apply/lock its slot.");
                return false;
            }
        }

        if (device.Rules.Count > 0 && !restrictionRules.TryActivate(deviceId, device.Rules))
        {
            LastFailureReason = "restriction enforcement could not be activated";
            slotLocks.Release(Owner);
            return false;
        }

        var pose = device.Rules.FirstOrDefault(r => r.Kind == RestraintRuleKind.ForcedPose);
        if (pose is not null)
            ApplyPose(pose.PoseModeId);

        foreach (var rule in boundRules)
        {
            if (EngageBoundAnimation(deviceId, rule)) continue;
            ReleaseBoundAnimations(deviceId);
            restrictionRules.Release(deviceId);
            slotLocks.Release(Owner);
            Plugin.Log.Warning($"Restraint apply rolled back for '{device.Name}': bound animation activation failed.");
            LastFailureReason = "Penumbra could not activate the selected cuff animation; the restraint was rolled back";
            return false;
        }

        foreach (var rule in device.Rules.Where(r => r.Kind == RestraintRuleKind.Gagged))
            chatGagService.ApplyCustomizePreset(deviceId, rule.CustomizePresetId);

        activeDeviceIds.Add(deviceId);
        moodles.HoldAttached(AttachedMoodleLedger.RestraintSource(deviceId), device.AttachedMoodle, moodleOverride);
        if (hasGear)
            slotLocks.VerifySoon();
        return true;
    }

    /// One-shot pose at apply time.
    private static unsafe void ApplyPose(int poseModeId)
    {
        var playerState = PlayerState.Instance();
        if (playerState == null || poseModeId is < 1 or > 3)
            return;

        var poseType = poseModeId switch
        {
            1 => EmoteController.PoseType.GroundSit,
            2 => EmoteController.PoseType.Sit,
            3 => EmoteController.PoseType.Doze,
            _ => throw new ArgumentOutOfRangeException(nameof(poseModeId)),
        };
        playerState->SelectedPoses[(int)poseType] = 0;
        GestureCommand.SendPoseCommand(poseModeId);
    }

    /// Re-sent while already on: replay its pose/animations so the Sub is put back in position.
    private void Replay(string deviceId, IEnumerable<RestraintRuleAssignment> rules)
    {
        var ruleList = rules.ToList();
        if (ruleList.FirstOrDefault(r => r.Kind == RestraintRuleKind.ForcedPose && r.PoseModeId != 0) is { } pose)
            ApplyPose(pose.PoseModeId);

        var now = Environment.TickCount64;
        foreach (var rule in ruleList)
        {
            if (!boundAnimations.ContainsKey((deviceId, rule.Kind)))
                continue;
            if (ResolveAnimation(rule.AnimationId)?.PlayableTrigger is { } trigger)
                pendingBoundPlays[(deviceId, rule.Kind)] = (trigger, now);
        }
    }

    /// Silently does nothing if the animation is missing or stale; the device's other rules still apply.
    private GestureCatalogEntry? ResolveAnimation(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return null;
        var resolved = CommandSelector.ResolveGestureDetailed(config.GestureMapping.LocalCatalog.Values, selector, requireTrigger: false).Entry;
        if (resolved is not null)
            return resolved;

        // Restraint rules are comma-separated, so commas in animation labels travel as a middle dot - restore them.
        return selector.Contains('·')
            ? CommandSelector.ResolveGestureDetailed(config.GestureMapping.LocalCatalog.Values, selector.Replace('·', ','), requireTrigger: false).Entry
            : null;
    }

    private bool EngageBoundAnimation(string deviceId, RestraintRuleAssignment rule)
    {
        if (ResolveAnimation(rule.AnimationId) is not { } entry
            || string.IsNullOrWhiteSpace(entry.ModDirectory)
            || entry.GroupSelections.Count == 0)
        {
            Plugin.Log.Warning($"Restraint {rule.Kind} rule refused to engage: animation '{rule.AnimationId}' is unavailable.");
            return false;
        }

        var collection = penumbra.TryGetLocalPlayerCollectionId();
        if (collection is null)
            return false;

        var selections = entry.GroupSelections.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value);
        var claimOwner = $"bound:{deviceId}:{rule.Kind}";
        if (!temporarySettings.Acquire(claimOwner, collection.Value, entry.ModDirectory, selections))
            return false;
        if (!penumbra.TryRedrawLocalPlayer())
        {
            temporarySettings.Release(claimOwner, collection.Value, entry.ModDirectory);
            return false;
        }

        boundAnimations[(deviceId, rule.Kind)] = (collection.Value, entry.ModDirectory);
        if (entry.PlayableTrigger is { } boundTrigger)
            pendingBoundPlays[(deviceId, rule.Kind)] = (boundTrigger, Environment.TickCount64 + PlayDelayMs);
        return true;
    }

    private void ReleaseBoundAnimations(string deviceId)
    {
        var removedAny = false;
        foreach (var key in boundAnimations.Keys.Where(k => k.DeviceId == deviceId).ToList())
        {
            pendingBoundPlays.Remove(key);
            var (collection, modDirectory) = boundAnimations[key];
            removedAny |= temporarySettings.Release($"bound:{deviceId}:{key.Kind}", collection, modDirectory);
            boundAnimations.Remove(key);
        }
        if (removedAny && !penumbra.TryRedrawLocalPlayer())
            Plugin.Log.Warning($"Restraint release for '{deviceId}' removed temporary animation settings, but the Penumbra redraw failed; a manual redraw may be required.");
    }

    /// Drops the device bookkeeping too; panic's other steps tear down slots and rules.
    public void ReleaseAllBoundAnimationsForPanic()
    {
        var removedAny = false;
        foreach (var (key, value) in boundAnimations)
            removedAny |= temporarySettings.Release($"bound:{key.DeviceId}:{key.Kind}", value.Collection, value.ModDirectory);
        boundAnimations.Clear();
        pendingBoundPlays.Clear();
        activeDeviceIds.Clear();
        moodles.Ledger.ReleaseAllWithPrefix(AttachedMoodleLedger.RestraintPrefix);
        chatGagService.RevertAllCustomizePresetsForPanic();
        ReleaseAllCatalogOverrides();
        if (removedAny && !penumbra.TryRedrawLocalPlayer())
            Plugin.Log.Warning("Restraint teardown removed temporary animation settings, but the Penumbra redraw failed; a manual redraw may be required.");
    }

    private void ReleaseAllCatalogOverrides()
    {
        var removed = false;
        foreach (var (owner, value) in activeCatalogOverrides)
            removed |= temporarySettings.Release(owner, value.Collection, value.ModDirectory);
        foreach (var id in activeCatalogOverrides.Keys)
            restrictionRules.Release(id);
        activeCatalogOverrides.Clear();
        if (removed) penumbra.TryRedrawLocalPlayer();
    }

    /// Once nothing is left active, the Owner's force-lock and timer go too.
    public void ReleaseDevices(IEnumerable<string> deviceIds)
    {
        foreach (var deviceId in deviceIds.ToList())
            if (activeDeviceIds.Contains(deviceId))
                ReleaseDevice(deviceId);
        if (activeDeviceIds.Count == 0)
            runtimeState.RestraintsForceLocked = false;
    }

    private void ReleaseDevice(string deviceId)
    {
        restrictionRules.Release(deviceId);
        activeDeviceIds.Remove(deviceId);
        ReleaseBoundAnimations(deviceId);
        chatGagService.RevertCustomizePreset(deviceId);
        moodles.Ledger.Release(AttachedMoodleLedger.RestraintSource(deviceId));

        if (activeCatalogOverrides.Remove(deviceId, out var catalogOverride)
            && temporarySettings.Release(deviceId, catalogOverride.Collection, catalogOverride.ModDirectory))
            penumbra.TryRedrawLocalPlayer();

        // SlotLockManager.Release drops every slot the owner holds, so wait for the last device.
        if (activeDeviceIds.Count == 0)
            slotLocks.Release(Owner);
    }

    /// Undyed. Refuses a device with neither gear nor a rule.
    public bool CaptureDeviceFromItem(ApiEquipSlot? slot, ulong? itemId, string name, List<RestraintRuleAssignment> rules, AttachedMoodleRef? attachedMoodle = null)
    {
        if (slot is null && itemId is null && rules.Count == 0)
            return false;

        var device = new RestraintDeviceDefinition
        {
            Slot = slot,
            ItemId = itemId,
            Stain = 0,
            Stain2 = 0,
            Name = name,
            Rules = rules,
            AttachedMoodle = attachedMoodle,
        };
        config.RestraintMapping.Devices[device.Id] = device;
        config.Save();
        return true;
    }

    public void RemoveDevice(string id)
    {
        if (activeDeviceIds.Contains(id))
            ReleaseDevice(id);
        config.RestraintMapping.Devices.Remove(id);
        config.Save();
    }

    private const string RulesToken = "rules:";

    /// The name is always quoted so the parser finds where it ends; an older Sub fails closed on it.
    public static string BuildLockCommand(string deviceName, List<RestraintRuleAssignment> rules)
    {
        var tokens = rules.SelectMany(RuleTokens);
        return $"restraint lock \"{deviceName}\" {RulesToken}{string.Join(',', tokens)}";
    }

    /// Gagged may emit a second `gagcplus` token, always right after `gagged`.
    private static IEnumerable<string> RuleTokens(RestraintRuleAssignment r)
    {
        switch (r.Kind)
        {
            case RestraintRuleKind.ForcedPose:
                yield return r.PoseModeId == 0 ? $"posemod={ReadableAnimation(r)}" : $"pose={r.PoseModeId}";
                break;
            case RestraintRuleKind.WalkOnly:
                yield return "walkonly";
                break;
            case RestraintRuleKind.ActionBlock:
                yield return "actionblock";
                break;
            case RestraintRuleKind.Gagged:
                yield return string.IsNullOrWhiteSpace(r.AnimationId) ? "gagged" : $"gagged={ReadableAnimation(r)}";
                if (!string.IsNullOrWhiteSpace(r.CustomizePresetId))
                    yield return $"gagcplus={ReadableCustomizePreset(r)}";
                break;
            case RestraintRuleKind.ArmsCuffed:
                yield return $"armscuffed={ReadableAnimation(r)}";
                break;
            case RestraintRuleKind.LegsCuffed:
                yield return $"legscuffed={ReadableAnimation(r)}";
                break;
            case RestraintRuleKind.FullBodyCuffed:
                yield return $"fullbodycuffed={ReadableAnimation(r)}";
                break;
        }
    }

    /// Shared with CustomTriggerCommand so both encode rules identically.
    public static string EncodeRuleTokens(List<RestraintRuleAssignment> rules) => string.Join(',', rules.SelectMany(RuleTokens));
    public static List<RestraintRuleAssignment> DecodeRuleTokens(string tokens) => ParseRuleTokens(tokens);

    public static string BuildCatalogLockCommand(string catalogId, string label, ulong itemId, List<RestraintRuleAssignment> rules)
    {
        if (catalogId.Length is < 8 or > 64 || catalogId.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Invalid restraint catalog identity.", nameof(catalogId));
        var ruleText = BuildLockCommand(label.Replace('"', '\''), rules);
        var suffix = ruleText[(ruleText.IndexOf(RulesToken, StringComparison.Ordinal) + RulesToken.Length)..];
        return $"restraint catalog {catalogId} \"{label.Replace('"', '\'')}\" item:{itemId} {RulesToken}{suffix}";
    }

    public static bool TryParseCatalogCommand(string remainder, out string catalogId, out ulong itemId, out List<RestraintRuleAssignment> rules)
    {
        catalogId = ""; itemId = 0; rules = [];
        if (remainder.Length > 400) return false;
        var (id, tail) = SplitFirstToken(remainder);
        if (id.Length is < 8 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c))) return false;
        if (!tail.StartsWith('"')) return false;
        var closing = tail.IndexOf('"', 1);
        if (closing < 0) return false;
        var after = tail[(closing + 1)..].Trim();
        var (itemToken, afterItem) = SplitFirstToken(after);
        if (!itemToken.StartsWith("item:", StringComparison.OrdinalIgnoreCase) ||
            !ulong.TryParse(itemToken[5..], out itemId) || itemId == 0)
            return false;
        after = afterItem.Trim();
        if (!after.StartsWith(RulesToken, StringComparison.OrdinalIgnoreCase)) return false;
        rules = ParseRuleTokens(after[RulesToken.Length..]);
        catalogId = id;
        return rules.Count > 0;
    }

    /// A legacy unquoted name with no rules parses as the whole remainder.
    public static bool TryParseLockCommand(string remainder, out string deviceName, out List<RestraintRuleAssignment>? rules)
    {
        rules = null;
        var trimmed = remainder.Trim();

        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            if (closing < 0)
            {
                deviceName = trimmed.Trim('"');
                return deviceName.Length > 0;
            }

            deviceName = trimmed[1..closing];
            var tail = trimmed[(closing + 1)..].Trim();
            if (tail.StartsWith(RulesToken, StringComparison.OrdinalIgnoreCase))
                rules = ParseRuleTokens(tail[RulesToken.Length..]);

            return deviceName.Length > 0;
        }

        deviceName = trimmed;
        return deviceName.Length > 0;
    }

    /// Keeps the slot/item positions fixed when the ad-hoc device has no gear.
    private const string NoGearToken = "-";

    /// Carries the full definition inline, since there's no Sub-side name to look up.
    public static string BuildWearCommand(ApiEquipSlot? slot, ulong? itemId, string label, List<RestraintRuleAssignment> rules)
    {
        var tokens = rules.SelectMany(RuleTokens);

        var slotText = slot is null ? NoGearToken : slot.Value.ToString();
        var itemText = itemId is null ? NoGearToken : itemId.Value.ToString();
        return $"restraint wear {slotText} {itemText} \"{label}\" {RulesToken}{string.Join(',', tokens)}";
    }

    /// Fails closed on any malformed segment, and on a device with neither gear nor rules.
    public static bool TryParseWearCommand(string remainder, out ApiEquipSlot? slot, out ulong? itemId, out string label, out List<RestraintRuleAssignment> rules)
    {
        slot = null;
        itemId = null;
        label = "";
        rules = [];

        var (slotToken, afterSlot) = SplitFirstToken(remainder);
        if (slotToken != NoGearToken)
        {
            if (!Enum.TryParse<ApiEquipSlot>(slotToken, true, out var parsedSlot))
                return false;
            slot = parsedSlot;
        }

        var (itemToken, afterItem) = SplitFirstToken(afterSlot);
        if (itemToken != NoGearToken)
        {
            if (!ulong.TryParse(itemToken, out var parsedItemId))
                return false;
            itemId = parsedItemId;
        }

        if ((slot is null) != (itemId is null))
            return false;

        var trimmed = afterItem.Trim();
        if (!trimmed.StartsWith('"'))
            return false;

        var closing = trimmed.IndexOf('"', 1);
        if (closing < 0)
            return false;

        label = trimmed[1..closing];
        var tail = trimmed[(closing + 1)..].Trim();
        if (tail.StartsWith(RulesToken, StringComparison.OrdinalIgnoreCase))
            rules = ParseRuleTokens(tail[RulesToken.Length..]);

        return label.Length > 0 && rules.Count > 0;
    }

    private static (string First, string Remainder) SplitFirstToken(string text)
    {
        var trimmed = text.Trim();
        var spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex < 0 ? (trimmed, "") : (trimmed[..spaceIndex], trimmed[(spaceIndex + 1)..].Trim());
    }

    private static List<RestraintRuleAssignment> ParseRuleTokens(string tokens)
    {
        var rules = new List<RestraintRuleAssignment>();
        foreach (var token in tokens.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("posemod=", StringComparison.OrdinalIgnoreCase))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ForcedPose, PoseModeId = 0, AnimationId = token["posemod=".Length..] });
            else if (token.StartsWith("pose=", StringComparison.OrdinalIgnoreCase) && int.TryParse(token.AsSpan(5), out var poseId))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ForcedPose, PoseModeId = poseId });
            else if (token.Equals("walkonly", StringComparison.OrdinalIgnoreCase))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.WalkOnly });
            else if (token.Equals("actionblock", StringComparison.OrdinalIgnoreCase))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ActionBlock });
            else if (token.Equals("gagged", StringComparison.OrdinalIgnoreCase))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.Gagged });
            else if (token.StartsWith("gagged=", StringComparison.OrdinalIgnoreCase))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.Gagged, AnimationId = token["gagged=".Length..] });
            else if (token.StartsWith("gagcplus=", StringComparison.OrdinalIgnoreCase))
            {
                if (rules.LastOrDefault(r => r.Kind == RestraintRuleKind.Gagged) is { } gagged)
                    gagged.CustomizePresetId = token["gagcplus=".Length..];
            }
            else if (token.StartsWith("armscuffed=", StringComparison.OrdinalIgnoreCase))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ArmsCuffed, AnimationId = token["armscuffed=".Length..] });
            else if (token.StartsWith("legscuffed=", StringComparison.OrdinalIgnoreCase))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.LegsCuffed, AnimationId = token["legscuffed=".Length..] });
            else if (token.StartsWith("fullbodycuffed=", StringComparison.OrdinalIgnoreCase))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.FullBodyCuffed, AnimationId = token["fullbodycuffed=".Length..] });
        }
        return rules;
    }

    /// Prefer the catalog id (stable on the Sub's side) over the label, which can go stale or be ambiguous.
    private static string ReadableAnimation(RestraintRuleAssignment rule) =>
        (string.IsNullOrWhiteSpace(rule.AnimationId) ? rule.AnimationLabel : rule.AnimationId)?.Replace(',', '·') ?? "";

    private static string ReadableCustomizePreset(RestraintRuleAssignment rule) =>
        (string.IsNullOrWhiteSpace(rule.CustomizePresetLabel) ? rule.CustomizePresetId : rule.CustomizePresetLabel)!.Replace(',', '·');

    public IReadOnlyList<string> ExportNames() =>
        config.RestraintMapping.Devices.Values.Select(d => d.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    public IEnumerable<string> ExportEntries() =>
        ExportCatalog().Split('\n', StringSplitOptions.RemoveEmptyEntries).Concat(
            config.RestraintMapping.ConfiguredMods
                .Where(x => config.RestraintMapping.LocalCatalog.ContainsKey(x.CatalogId) && x.Rules.Count > 0 && x.ItemId > 0)
                .OrderBy(x => x.Name)
                .Select(x => EncodeConfiguredExport(ConfiguredModRestraintExportEntry.From(x))));
}
