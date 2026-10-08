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

    /// Engaged restraints, oldest first, with the rules each was engaged with (an Owner's rules can replace the Sub's).
    public IReadOnlyList<(string Id, IReadOnlyList<RestraintRuleAssignment> Rules)> Engaged => engaged;

    /// The heaviest level among active Gagged rules; Heavy when none says otherwise.
    public GagLevel ActiveGagLevel
    {
        get
        {
            var gags = engaged.SelectMany(e => e.Rules).Where(r => r.Kind == RestraintRuleKind.Gagged).ToList();
            return gags.Count == 0 ? GagLevel.Heavy : (GagLevel)gags.Min(r => (int)r.GagLevel);
        }
    }
    private readonly List<(string Id, IReadOnlyList<RestraintRuleAssignment> Rules)> engaged = new();

    /// What the engaged restraints' cuff rules draw.
    public CuffSet DrawnCuffs
    {
        get
        {
            var set = CuffSet.None;
            foreach (var (_, rules) in engaged)
                set |= CuffSets.Drawn(rules);
            return set;
        }
    }

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

    public IReadOnlyList<WornRestraint> Worn => config.WornRestraints;

    private WornRestraint? FindWorn(string runtimeId) => config.WornRestraints.FirstOrDefault(w => w.RuntimeId == runtimeId);

    /// A locked restraint can't be taken off by its word; applying a different one is still allowed.
    public bool ToggleByWord(string word)
    {
        LastFailureReason = null;
        if (FindDeviceByWord(word) is { } device)
        {
            if (activeDeviceIds.Contains(device.Id))
                return ReleaseUnlocked(device.Id);
            if (!ApplyDevice(device.Id, device))
                return false;
            Track(DeviceRecord(device, WornRestraintKind.Device, device.Name, null));
            return true;
        }

        if (FindConfiguredModByWord(word) is { } mod)
        {
            var runtimeId = CatalogRuntimeId(mod.CatalogId);
            if (activeDeviceIds.Contains(runtimeId))
                return ReleaseUnlocked(runtimeId);
            if (mod.ItemId is not { } itemId || mod.Rules.Count == 0)
            {
                LastFailureReason = "that configured restraint has no item or rules yet";
                return false;
            }
            if (!ApplyCatalog(mod.CatalogId, itemId, mod.Rules, moodleOverride: null))
                return false;
            Track(CatalogRecord(mod.CatalogId, itemId, mod.Alias, mod.Rules, null));
            return true;
        }

        LastFailureReason = $"no restraint named \"{word}\"";
        return false;
    }

    /// The Sub's own way to take off a restraint that isn't locked.
    public bool ReleaseUnlocked(string runtimeId)
    {
        if (FindWorn(runtimeId)?.Lock is not null)
        {
            LastFailureReason = "that restraint is locked by your Owner";
            return false;
        }
        if (!activeDeviceIds.Contains(runtimeId))
            return false;
        ReleaseDevice(runtimeId);
        return true;
    }

    /// Re-applying an already worn restraint keeps its lock until SetLock replaces it.
    private void Track(WornRestraint entry)
    {
        var index = config.WornRestraints.FindIndex(w => w.RuntimeId == entry.RuntimeId);
        if (index >= 0)
        {
            entry.Lock = config.WornRestraints[index].Lock;
            config.WornRestraints[index] = entry;
        }
        else
            config.WornRestraints.Add(entry);
        config.Save();
    }

    private static WornRestraint DeviceRecord(RestraintDeviceDefinition device, WornRestraintKind kind, string reference, string? moodleOverride) => new()
    {
        RuntimeId = device.Id,
        Kind = kind,
        Reference = reference.Trim(),
        DeviceId = kind == WornRestraintKind.Device ? device.Id : null,
        Slot = device.Slot,
        ItemId = device.ItemId,
        Stain = device.Stain,
        Stain2 = device.Stain2,
        Rules = device.Rules.ToList(),
        MoodleOverride = moodleOverride,
    };

    private static WornRestraint CatalogRecord(string catalogId, ulong itemId, string reference, List<RestraintRuleAssignment> rules, string? moodleOverride) => new()
    {
        RuntimeId = CatalogRuntimeId(catalogId),
        Kind = WornRestraintKind.Catalog,
        Reference = reference.Trim(),
        CatalogId = catalogId,
        Slot = GlamourerIpc.GetItemSlot((uint)itemId),
        ItemId = itemId,
        Rules = rules.ToList(),
        MoodleOverride = moodleOverride,
    };

    /// Always locks. `moodleOverride` (here and below) replaces the device's default moodle.
    public bool ForceApply(string deviceName, string? moodleOverride = null, RestraintLock restraintLock = default)
    {
        LastFailureReason = null;
        var entry = FindDeviceByWord(deviceName);
        if (entry is null)
        {
            if (FindConfiguredModByWord(deviceName) is { ItemId: { } itemId } mod && mod.Rules.Count > 0)
                return ForceApplyCatalog(mod.CatalogId, itemId, mod.Rules, moodleOverride, restraintLock, deviceName);
            LastFailureReason = $"device \"{deviceName}\" was not found";
            return false;
        }

        return ApplyAndLock(entry, WornRestraintKind.Device, deviceName, moodleOverride, restraintLock);
    }

    /// A Custom Trigger is an Owner command, so this doesn't use the Sub's Toggle (refused while locked).
    public bool ForceApplyById(string deviceId, RestraintLock restraintLock = default)
    {
        LastFailureReason = null;
        if (!config.RestraintMapping.Devices.TryGetValue(deviceId, out var device))
        {
            LastFailureReason = $"saved device id {deviceId} is stale";
            return false;
        }
        return ApplyAndLock(device, WornRestraintKind.Device, device.Name, null, restraintLock);
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
        return ApplyAndLock(device, WornRestraintKind.Device, deviceName, moodleOverride, restraintLock);
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
        return ApplyAndLock(device, WornRestraintKind.AdHoc, label, moodleOverride, restraintLock);
    }

    /// Re-sending a worn restraint replays its pose and still (re)sets its lock.
    private bool ApplyAndLock(RestraintDeviceDefinition device, WornRestraintKind kind, string reference, string? moodleOverride, RestraintLock restraintLock)
    {
        if (activeDeviceIds.Contains(device.Id))
            Replay(device.Id, device.Rules);
        else if (!ApplyDevice(device.Id, device, moodleOverride))
            return false;
        Track(DeviceRecord(device, kind, reference, moodleOverride));
        SetLock(device.Id, restraintLock);
        return true;
    }

    /// `reference` defaults to the Sub's name for that mod restraint.
    public bool ForceApplyCatalog(string catalogId, ulong itemId, List<RestraintRuleAssignment> rules, string? moodleOverride = null, RestraintLock restraintLock = default, string? reference = null)
    {
        var runtimeId = CatalogRuntimeId(catalogId);
        if (activeCatalogOverrides.ContainsKey(runtimeId))
            Replay(runtimeId, rules);
        else if (!ApplyCatalog(catalogId, itemId, rules, moodleOverride))
            return false;
        reference ??= config.RestraintMapping.ConfiguredMods.FirstOrDefault(m => m.CatalogId == catalogId)?.Name
            ?? config.RestraintMapping.LocalCatalog.GetValueOrDefault(catalogId)?.ModName ?? catalogId;
        Track(CatalogRecord(catalogId, itemId, reference, rules, moodleOverride));
        SetLock(runtimeId, restraintLock);
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
        var boundRules = rules.Where(r => (CuffSets.IsCuff(r.Kind) && !string.IsNullOrWhiteSpace(r.AnimationId))
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
        var forceRedraw = config.RestraintMapping.ConfiguredMods.FirstOrDefault(m => m.CatalogId == catalogId && m.ItemId == itemId)?.RedrawOnApply == true;
        if (!penumbra.TryRedrawLocalPlayer(forceRedraw))
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
        engaged.Add((runtimeId, rules.ToList()));

        var configured = config.RestraintMapping.ConfiguredMods.FirstOrDefault(m => m.CatalogId == catalogId && m.ItemId == itemId);
        moodles.HoldAttached(AttachedMoodleLedger.RestraintSource(runtimeId), configured?.AttachedMoodle, moodleOverride);
        slotLocks.VerifySoon();
        return true;
    }

    /// Set by the Owner command's dispatch just before it applies; the lock it engages takes them over.
    public StruggleSetting PendingStruggle { get; set; }
    public Guid? PendingLockPairingId { get; set; }

    /// (pairing that set the lock, restraint name) after the Sub struggled free of it.
    public event Action<Guid?, string>? StruggledFree;
    /// (pairing that set the lock, restraint name) after the Sub unlocked it with its key.
    public event Action<Guid?, string>? KeyUnlocked;

    /// Only called after an Owner command actually applied, so a refused command leaves the lock untouched. The latest
    /// lock command for a restraint decides, so one without a struggle setting or key takes them away.
    private void SetLock(string runtimeId, RestraintLock restraintLock)
    {
        if (FindWorn(runtimeId) is not { } worn)
            return;
        worn.Lock = new WornRestraintLock
        {
            ByPairingId = PendingLockPairingId,
            ExpiresAtUtc = restraintLock.Duration is { } duration ? DateTime.UtcNow + duration : null,
            StruggleLevel = PendingStruggle.Level,
            StrugglePenaltyMinutes = PendingStruggle.PenaltyMinutes,
            Key = restraintLock.Key,
        };
        config.Save();
    }

    public static StruggleSetting? StruggleAvailable(WornRestraint worn) =>
        worn.Lock is { } l && l.Struggle.Allowed ? l.Struggle : null;

    /// Null while the Sub may try now.
    public static TimeSpan? StruggleWait(WornRestraint worn) => Remaining(worn.Lock?.StruggleNextTryUtc);

    public static TimeSpan? KeyWait(WornRestraint worn) => Remaining(worn.Lock?.KeyNextTryUtc);

    private static TimeSpan? Remaining(DateTime? until) =>
        until is { } next && next > DateTime.UtcNow ? next - DateTime.UtcNow : null;

    /// The Sub's own click only. One roll; an escape releases that restraint exactly as its timer running out does.
    public LocalTestResult Struggle(string runtimeId)
    {
        if (FindWorn(runtimeId) is not { Lock: { } restraintLock } worn || StruggleAvailable(worn) is not { } setting)
            return LocalTestResult.Fail("That restraint can't be struggled against.");
        if (StruggleWait(worn) is { } wait)
            return LocalTestResult.Fail($"You can try again in {RestraintLock.Format(wait)}.");
        if (Random.Shared.NextDouble() < RestraintStruggle.Chance(setting.Level))
        {
            Plugin.Log.Information("Struggled free of a restraint.");
            ReleaseDevice(runtimeId);
            StruggledFree?.Invoke(restraintLock.ByPairingId, worn.Reference);
            return LocalTestResult.Ok($"You struggled free of {worn.Reference}!");
        }
        restraintLock.StruggleNextTryUtc = DateTime.UtcNow + RestraintStruggle.Wait(setting.Level);
        var penalty = "";
        if (setting.PenaltyMinutes > 0 && restraintLock.ExpiresAtUtc is { } expiresAt)
        {
            var max = DateTime.UtcNow + RestraintLock.MaxDuration;
            var shifted = expiresAt.AddMinutes(setting.PenaltyMinutes);
            restraintLock.ExpiresAtUtc = shifted > max ? max : shifted;
            penalty = $" The lock tightened: +{setting.PenaltyMinutes}m.";
        }
        config.Save();
        return LocalTestResult.Fail($"{worn.Reference} holds.{penalty} Try again in {RestraintLock.Format(RestraintStruggle.Wait(setting.Level))}.");
    }

    /// The Sub's own click only. A wrong guess starts a wait, so the key can't be found by rapid retries in-game.
    public LocalTestResult TryKey(string runtimeId, string guess)
    {
        if (FindWorn(runtimeId) is not { Lock: { Key: { } key } restraintLock } worn)
            return LocalTestResult.Fail("That restraint has no key.");
        if (KeyWait(worn) is { } wait)
            return LocalTestResult.Fail($"You can try again in {RestraintLock.Format(wait)}.");
        if (!RestraintKey.Matches(key, guess))
        {
            restraintLock.KeyNextTryUtc = DateTime.UtcNow + RestraintKey.WrongGuessWait;
            config.Save();
            return LocalTestResult.Fail($"That key doesn't fit. Try again in {RestraintLock.Format(RestraintKey.WrongGuessWait)}.");
        }
        Plugin.Log.Information("Unlocked a restraint with its key.");
        ReleaseDevice(runtimeId);
        KeyUnlocked?.Invoke(restraintLock.ByPairingId, worn.Reference);
        return LocalTestResult.Ok($"{worn.Reference} unlocked.");
    }

    public const int MinTimerAdjustSeconds = 60;
    public const int MaxTimerAdjustSeconds = 604800;

    /// `+<seconds>` or `-<seconds>`.
    public static bool TryParseTimerAdjust(string text, out int deltaSeconds)
    {
        deltaSeconds = 0;
        var t = text.Trim();
        if (t.Length < 2 || (t[0] != '+' && t[0] != '-'))
            return false;
        if (!int.TryParse(t[1..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || seconds is < MinTimerAdjustSeconds or > MaxTimerAdjustSeconds)
            return false;
        deltaSeconds = t[0] == '-' ? -seconds : seconds;
        return true;
    }

    /// Shifts every Timed lock; Permanent and unlocked restraints are left alone. One shifted past now is released.
    public LocalTestResult AdjustTimedLock(int deltaSeconds)
    {
        var timed = config.WornRestraints.Where(w => w.Lock?.ExpiresAtUtc is not null).ToList();
        if (timed.Count == 0)
            return LocalTestResult.Fail("No restraint is under a timed lock - nothing changed.");
        var now = DateTime.UtcNow;
        var released = 0;
        foreach (var worn in timed)
        {
            var shifted = worn.Lock!.ExpiresAtUtc!.Value.AddSeconds(deltaSeconds);
            if (shifted > now + RestraintLock.MaxDuration)
                shifted = now + RestraintLock.MaxDuration;
            if (shifted <= now)
            {
                ReleaseDevice(worn.RuntimeId);
                released++;
                continue;
            }
            worn.Lock.ExpiresAtUtc = shifted;
        }
        config.Save();
        Plugin.Log.Information($"Restraint timers shifted by {deltaSeconds}s; {released} ran out.");
        return LocalTestResult.Ok(released > 0
            ? $"Restraint timers shifted; {released} ran out and came off."
            : "Restraint timers shifted.");
    }

    /// `restraint unlock <name>`. A locked restraint only opens for the Owner who locked it.
    public LocalTestResult UnlockByReference(string reference, Guid? sourcePairingId)
    {
        var worn = config.WornRestraints.FirstOrDefault(w => string.Equals(w.Reference, reference.Trim(), StringComparison.OrdinalIgnoreCase));
        if (worn is null)
            return LocalTestResult.Fail($"No worn restraint is called \"{reference.Trim()}\".");
        if (worn.Lock is { ByPairingId: { } lockedBy } && sourcePairingId is { } source && lockedBy != source)
            return LocalTestResult.Fail($"\"{worn.Reference}\" was locked by a different Owner.");
        ReleaseDevice(worn.RuntimeId);
        return LocalTestResult.Ok($"\"{worn.Reference}\" unlocked.");
    }

    public bool ForceUnlock()
    {
        // Slot locks survive reloads but device/claim bookkeeping doesn't, so release every layer unconditionally.
        var hadRestraints = activeDeviceIds.Count > 0 || boundAnimations.Count > 0 || activeCatalogOverrides.Count > 0
            || config.WornRestraints.Count > 0 || config.RestraintsForceLocked;
        restrictionRules.ReleaseAllForPanic();
        ReleaseAllBoundAnimationsForPanic();
        ReleaseAllCatalogOverrides();
        var gearReleased = slotLocks.Release(Owner);
        ClearSaved();
        moodles.Ledger.ReleaseAllWithPrefix(AttachedMoodleLedger.RestraintPrefix);
        return gearReleased || hadRestraints;
    }

    /// Panic: everything comes off and nothing is put back after a restart.
    public void ReleaseAllForPanic()
    {
        ReleaseAllBoundAnimationsForPanic();
        ClearSaved();
    }

    private void ClearSaved()
    {
        config.WornRestraints.Clear();
        config.RestraintsForceLocked = false;
        config.Save();
    }

    private const int MaxRestoreAttempts = 4;
    private static readonly TimeSpan RestoreSettle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RestoreRetryDelay = TimeSpan.FromSeconds(5);
    private bool restorePending = true;
    private DateTime? canActSince;
    private DateTime nextRestoreTryUtc;
    private readonly Dictionary<string, int> restoreAttempts = new();

    /// Logout tears down the live restraint state; it's put back from the saved list once the Sub is in again.
    public void OnLogin() => restorePending = true;

    internal static bool CanChangeCharacter() =>
        Plugin.ClientState.IsLoggedIn
        && Plugin.ObjectTable.LocalPlayer is not null
        && !Plugin.Condition[ConditionFlag.BetweenAreas]
        && !Plugin.Condition[ConditionFlag.BetweenAreas51];

    private static bool InCutscene() =>
        Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Plugin.Condition[ConditionFlag.WatchingCutscene] || Plugin.Condition[ConditionFlag.WatchingCutscene78];

    private void ReleaseExpiredLocks()
    {
        var now = DateTime.UtcNow;
        foreach (var worn in config.WornRestraints.Where(w => w.Lock?.ExpiresAtUtc is { } end && now >= end).ToList())
        {
            Plugin.Log.Information($"Timed lock on \"{worn.Reference}\" expired - releasing it.");
            ReleaseDevice(worn.RuntimeId);
        }
    }

    /// Re-applies what was worn before a reload or relog. Anything gone for good is released at once; anything else
    /// (Penumbra or Glamourer not ready yet) gets a few more tries before it's released.
    private void Restore()
    {
        var notice = new List<string>();
        if (config.RestraintsForceLocked && config.WornRestraints.Count == 0)
        {
            Plugin.Log.Information("Releasing a restraints lock saved by an older version.");
            ForceUnlock();
            notice.Add("Oathbound changed how restraint locks work, so the restraints you had locked were released.");
        }
        config.RestraintsForceLocked = false;

        var retry = false;
        foreach (var worn in config.WornRestraints.ToList())
        {
            if (activeDeviceIds.Contains(worn.RuntimeId))
                continue;
            string reason;
            var permanent = false;
            if (!(config.Permissions.Restraints && config.TosAcknowledged))
            {
                reason = "the Restraints permission is off";
                permanent = true;
            }
            else if (TryReapply(worn, out reason, out permanent))
            {
                restoreAttempts.Remove(worn.RuntimeId);
                continue;
            }

            var attempts = restoreAttempts.GetValueOrDefault(worn.RuntimeId) + 1;
            if (!permanent && attempts < MaxRestoreAttempts)
            {
                restoreAttempts[worn.RuntimeId] = attempts;
                retry = true;
                continue;
            }
            restoreAttempts.Remove(worn.RuntimeId);
            Plugin.Log.Warning($"Couldn't put \"{worn.Reference}\" back on ({reason}) - releasing it.");
            ReleaseDevice(worn.RuntimeId);
            notice.Add($"{worn.Reference} came off: {reason}.");
        }

        restorePending = retry;
        nextRestoreTryUtc = DateTime.UtcNow + RestoreRetryDelay;
        if (notice.Count > 0)
            Plugin.NotificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
            {
                Title = "Restraints",
                Content = string.Join("\n", notice),
                Type = Dalamud.Interface.ImGuiNotification.NotificationType.Warning,
            });
    }

    /// `permanent` = the restraint's source is gone, so retrying won't help.
    private bool TryReapply(WornRestraint worn, out string reason, out bool permanent)
    {
        permanent = false;
        LastFailureReason = null;
        bool applied;
        switch (worn.Kind)
        {
            case WornRestraintKind.Device:
                if (worn.DeviceId is null || !config.RestraintMapping.Devices.TryGetValue(worn.DeviceId, out var saved))
                {
                    reason = "it was deleted";
                    permanent = true;
                    return false;
                }
                applied = ApplyDevice(worn.RuntimeId, new RestraintDeviceDefinition
                {
                    Id = saved.Id, Slot = saved.Slot, ItemId = saved.ItemId, Stain = saved.Stain, Stain2 = saved.Stain2,
                    Name = saved.Name, Rules = worn.Rules, AttachedMoodle = saved.AttachedMoodle,
                }, worn.MoodleOverride);
                break;
            case WornRestraintKind.Catalog:
                if (worn.CatalogId is null || worn.ItemId is not { } itemId || !config.RestraintMapping.LocalCatalog.ContainsKey(worn.CatalogId))
                {
                    reason = "its mod is gone";
                    permanent = true;
                    return false;
                }
                applied = ApplyCatalog(worn.CatalogId, itemId, worn.Rules, worn.MoodleOverride);
                break;
            default:
                applied = ApplyDevice(worn.RuntimeId, new RestraintDeviceDefinition
                {
                    Id = worn.RuntimeId, Slot = worn.Slot, ItemId = worn.ItemId, Name = worn.Reference, Rules = worn.Rules,
                }, worn.MoodleOverride);
                break;
        }
        reason = LastFailureReason ?? "it couldn't be applied";
        return applied;
    }

    /// Plays bound animations after Penumbra's redraw settles; playing immediately races the rebuild.
    public void OnFrameworkUpdate()
    {
        // Deferred until the character can be changed, including an end time that passed while unloaded.
        if (CanChangeCharacter())
        {
            ReleaseExpiredLocks();
            canActSince ??= DateTime.UtcNow;
            if (restorePending && !InCutscene() && DateTime.UtcNow - canActSince >= RestoreSettle && DateTime.UtcNow >= nextRestoreTryUtc)
                Restore();
        }
        else
            canActSince = null;

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
        var boundRules = device.Rules.Where(r => (CuffSets.IsCuff(r.Kind) && !string.IsNullOrWhiteSpace(r.AnimationId))
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

        // A saved device's setting applies to the Owner's copies of it too, which share its id.
        var forceRedraw = device.RedrawOnApply || (config.RestraintMapping.Devices.TryGetValue(deviceId, out var saved) && saved.RedrawOnApply);
        foreach (var rule in boundRules)
        {
            if (EngageBoundAnimation(deviceId, rule, forceRedraw)) continue;
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
        engaged.Add((deviceId, device.Rules.ToList()));
        moodles.HoldAttached(AttachedMoodleLedger.RestraintSource(deviceId), device.AttachedMoodle, moodleOverride);
        if (forceRedraw && boundRules.Count == 0)
            penumbra.TryRedrawLocalPlayer(force: true);
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

    private bool EngageBoundAnimation(string deviceId, RestraintRuleAssignment rule, bool forceRedraw = false)
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
        if (!penumbra.TryRedrawLocalPlayer(forceRedraw))
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
        engaged.Clear();
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

    public void ReleaseDevices(IEnumerable<string> deviceIds)
    {
        foreach (var deviceId in deviceIds.ToList())
            if (activeDeviceIds.Contains(deviceId))
                ReleaseDevice(deviceId);
    }

    /// Also works for a saved restraint that isn't live yet (waiting to be put back), releasing its saved slot lock.
    private void ReleaseDevice(string deviceId)
    {
        var worn = FindWorn(deviceId);
        if (worn is not null)
        {
            config.WornRestraints.Remove(worn);
            config.Save();
        }

        restrictionRules.Release(deviceId);
        activeDeviceIds.Remove(deviceId);
        engaged.RemoveAll(e => e.Id == deviceId);
        ReleaseBoundAnimations(deviceId);
        chatGagService.RevertCustomizePreset(deviceId);
        moodles.Ledger.Release(AttachedMoodleLedger.RestraintSource(deviceId));

        if (activeCatalogOverrides.Remove(deviceId, out var catalogOverride)
            && temporarySettings.Release(deviceId, catalogOverride.Collection, catalogOverride.ModDirectory))
            penumbra.TryRedrawLocalPlayer();

        ReleaseSlotOf(worn);
    }

    /// Another worn restraint on the same slot keeps it locked, showing its own piece.
    private void ReleaseSlotOf(WornRestraint? worn)
    {
        if (worn?.Slot is not { } slot)
        {
            if (activeDeviceIds.Count == 0 && config.WornRestraints.Count == 0)
                slotLocks.Release(Owner);
            return;
        }
        if (config.WornRestraints.FirstOrDefault(w => w.Slot == slot && w.ItemId is not null) is { } other)
            slotLocks.TryLock(Owner, new Dictionary<ApiEquipSlot, SlotLockValue> { [slot] = new(other.ItemId!.Value, other.Stain, other.Stain2) }, TakesOverFrom);
        else
            slotLocks.ReleaseSlots(Owner, [slot]);
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
        if (activeDeviceIds.Contains(id) || FindWorn(id) is not null)
            ReleaseDevice(id);
        config.RestraintMapping.Devices.Remove(id);
        config.Save();
    }

    private const string RulesToken = "rules:";

    /// The name is always quoted so the parser finds where it ends; an older Sub fails closed on it.
    public static string BuildLockCommand(string deviceName, List<RestraintRuleAssignment> rules)
    {
        return $"restraint lock \"{deviceName}\" {RulesToken}{EncodeRuleTokens(rules)}";
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
                // Heavy sends nothing, so it stays byte-for-byte what older Subs know; they skip this and gag at Heavy.
                if (r.GagLevel != GagLevel.Heavy)
                    yield return $"{GagLevelToken}{(r.GagLevel == GagLevel.Light ? "light" : "medium")}";
                break;
            case RestraintRuleKind.ArmsCuffed:
                yield return CuffToken("armscuffed", r);
                if (r.Drawn) yield return DrawArmsToken;
                break;
            case RestraintRuleKind.LegsCuffed:
                yield return CuffToken("legscuffed", r);
                if (r.Drawn) yield return DrawLegsToken;
                break;
            case RestraintRuleKind.FullBodyCuffed:
                yield return CuffToken("fullbodycuffed", r);
                if (r.Drawn) yield return DrawFullBodyToken;
                break;
        }
    }

    /// Bare when there's no animation (drawn rules-only cuffs); an older Sub only knows the `=` form and skips it.
    private static string CuffToken(string name, RestraintRuleAssignment r) =>
        string.IsNullOrWhiteSpace(r.AnimationId) && string.IsNullOrWhiteSpace(r.AnimationLabel) ? name : $"{name}={ReadableAnimation(r)}";

    private const string GagLevelToken = "gaglevel=";

    /// One per drawn cuff rule; an older Sub skips them.
    private const string DrawArmsToken = "drawarms";
    private const string DrawLegsToken = "drawlegs";
    private const string DrawFullBodyToken = "drawfullbody";

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

    public static bool TryParseCatalogCommand(string remainder, out string catalogId, out ulong itemId, out List<RestraintRuleAssignment> rules) =>
        TryParseCatalogCommand(remainder, out catalogId, out _, out itemId, out rules);

    public static bool TryParseCatalogCommand(string remainder, out string catalogId, out string label, out ulong itemId, out List<RestraintRuleAssignment> rules)
    {
        catalogId = ""; label = ""; itemId = 0; rules = [];
        if (remainder.Length > 400) return false;
        var (id, tail) = SplitFirstToken(remainder);
        if (id.Length is < 8 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c))) return false;
        if (!tail.StartsWith('"')) return false;
        var closing = tail.IndexOf('"', 1);
        if (closing < 0) return false;
        label = tail[1..closing];
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
        var slotText = slot is null ? NoGearToken : slot.Value.ToString();
        var itemText = itemId is null ? NoGearToken : itemId.Value.ToString();
        return $"restraint wear {slotText} {itemText} \"{label}\" {RulesToken}{EncodeRuleTokens(rules)}";
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
        var drawn = new HashSet<RestraintRuleKind>();
        foreach (var token in tokens.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Equals(DrawArmsToken, StringComparison.OrdinalIgnoreCase))
                drawn.Add(RestraintRuleKind.ArmsCuffed);
            else if (token.Equals(DrawLegsToken, StringComparison.OrdinalIgnoreCase))
                drawn.Add(RestraintRuleKind.LegsCuffed);
            else if (token.Equals(DrawFullBodyToken, StringComparison.OrdinalIgnoreCase))
                drawn.Add(RestraintRuleKind.FullBodyCuffed);
            else if (token.Equals("armscuffed", StringComparison.OrdinalIgnoreCase))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ArmsCuffed });
            else if (token.Equals("legscuffed", StringComparison.OrdinalIgnoreCase))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.LegsCuffed });
            else if (token.Equals("fullbodycuffed", StringComparison.OrdinalIgnoreCase))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.FullBodyCuffed });
            else if (token.StartsWith("posemod=", StringComparison.OrdinalIgnoreCase))
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
            else if (token.StartsWith(GagLevelToken, StringComparison.OrdinalIgnoreCase))
            {
                var level = token[GagLevelToken.Length..];
                if (rules.LastOrDefault(r => r.Kind == RestraintRuleKind.Gagged) is { } gagged)
                    gagged.GagLevel = level.Equals("light", StringComparison.OrdinalIgnoreCase) ? GagLevel.Light
                        : level.Equals("medium", StringComparison.OrdinalIgnoreCase) ? GagLevel.Medium
                        : GagLevel.Heavy;
            }
            else if (token.StartsWith("armscuffed=", StringComparison.OrdinalIgnoreCase))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ArmsCuffed, AnimationId = token["armscuffed=".Length..] });
            else if (token.StartsWith("legscuffed=", StringComparison.OrdinalIgnoreCase))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.LegsCuffed, AnimationId = token["legscuffed=".Length..] });
            else if (token.StartsWith("fullbodycuffed=", StringComparison.OrdinalIgnoreCase))
                rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.FullBodyCuffed, AnimationId = token["fullbodycuffed=".Length..] });
        }
        foreach (var rule in rules.Where(r => drawn.Contains(r.Kind)))
            rule.Drawn = true;
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

    /// Restraints whose picture didn't fit the last export's picture budget.
    public IReadOnlyList<string> PicturesLeftOut { get; private set; } = [];

    public IEnumerable<string> ExportEntries()
    {
        var left = new List<string>();
        var budget = UI.ImageTile.MaxSharedTotalBytes;
        var configured = new List<string>();
        foreach (var mod in config.RestraintMapping.ConfiguredMods
                     .Where(x => config.RestraintMapping.LocalCatalog.ContainsKey(x.CatalogId) && x.Rules.Count > 0 && x.ItemId > 0)
                     .OrderBy(x => x.Name))
        {
            var entry = ConfiguredModRestraintExportEntry.From(mod);
            if (mod.ImageFile is not null)
            {
                if (UI.ImageTile.ReadThumbnail(mod.ThumbnailFile) is { } bytes && bytes.Length <= budget)
                {
                    entry.Picture = Convert.ToBase64String(bytes);
                    budget -= bytes.Length;
                }
                else
                    left.Add(mod.Name);
            }
            configured.Add(EncodeConfiguredExport(entry));
        }
        PicturesLeftOut = left;
        return ExportCatalog().Split('\n', StringSplitOptions.RemoveEmptyEntries).Concat(configured);
    }
}
