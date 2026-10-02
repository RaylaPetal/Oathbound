using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.Commands;

/// One Aliases export entry: the bare alias word plus a readable summary for the Owner. Only the bare word ever crosses chat.
public class AliasExportEntry
{
    public string Alias { get; set; } = "";
    public string Description { get; set; } = "";

    /// The design/gesture/moodle a single-action alias applies, for import-time dedup. Null on older exports.
    public string? Target { get; set; }

    /// The self-contained command the Owner's copy sends, so it keeps working after the Sub deletes the preset.
    /// Null on an older export, where the Owner sends the bare alias word.
    public string? Command { get; set; }

    public string? Moodle { get; set; }

    public AliasExportEntry() { }
    public AliasExportEntry(string alias, string description, string? target = null)
    {
        Alias = alias;
        Description = description;
        Target = target;
    }
}

/// Error is set only when the file isn't a recognizable export at all.
public readonly record struct CatalogImportResult(int Title, int Wardrobe, int Gesture, int Moodles, int Restraints, int Bundles, int Duplicates, string? Error)
{
    public int TotalAdded => Title + Wardrobe + Gesture + Moodles + Restraints + Bundles;
}

public readonly record struct CatalogSnapshotResult(int Added, int Updated, int Removed, int Duplicates, string? Error);

/// Builds the sectioned catalog export and splits an imported one back into each category's quick-command list.
public sealed class CatalogSyncService
{
    private const string TitleAliasesHeader = "## TITLE_ALIASES";
    private const string WardrobeHeader = "## WARDROBE";
    private const string WardrobeAliasesHeader = "## WARDROBE_ALIASES";
    private const string GestureHeader = "## GESTURE";
    private const string GestureAliasesHeader = "## GESTURE_ALIASES";
    private const string MoodlesHeader = "## MOODLES";
    private const string MoodlesAliasesHeader = "## MOODLES_ALIASES";
    private const string RestraintsHeader = "## RESTRAINTS";
    private const string RestraintsAliasesHeader = "## RESTRAINTS_ALIASES";

    /// Header name kept for compatibility: an older export's flat alias section still parses into the bundle list.
    private const string BundlesHeader = "## ALIASES";

    private const string AliasExportPrefix = "COLLAR-ALIAS-V1|";

    private static readonly string[] KnownHeaders =
    [
        TitleAliasesHeader, WardrobeHeader, WardrobeAliasesHeader, GestureHeader, GestureAliasesHeader,
        MoodlesHeader, MoodlesAliasesHeader, RestraintsHeader, RestraintsAliasesHeader, BundlesHeader,
    ];

    private readonly PluginConfig config;
    private readonly OutfitCommand outfit;
    private readonly GestureCommand gesture;
    private readonly MoodlesCommand moodles;
    private readonly RestraintCommand restraints;
    private readonly CatalogStore catalogStore;

    public CatalogSyncService(PluginConfig config, OutfitCommand outfit, GestureCommand gesture, MoodlesCommand moodles, RestraintCommand restraints, CatalogStore catalogStore)
    {
        this.config = config;
        this.outfit = outfit;
        this.gesture = gesture;
        this.moodles = moodles;
        this.restraints = restraints;
        this.catalogStore = catalogStore;
    }

    /// Replaces this pair's previously imported entries (adds, updates, removes); manual entries and other pairs'
    /// imports are never touched. Nothing is mutated until every category parses, and Save runs once at the end.
    /// Null when it doesn't apply, and the caller falls back to ParseImport's add-only import.
    public CatalogSnapshotResult? TryApplyFileAsPairSnapshot(string exportText, PairingState? pairing)
    {
        if (pairing is not { Direction: PairingDirection.OwnerSide, PairIdHash: { Length: > 0 } pairIdHash })
            return null;
        var sections = SplitSections(exportText);
        if (sections.Count == 0 || !ValidateRelaySnapshot(sections, out _))
            return null;
        return ApplyRelaySnapshot(exportText, pairIdHash);
    }

    public CatalogSnapshotResult ApplyRelaySnapshot(string exportText, string sourcePairIdHash)
    {
        if (string.IsNullOrWhiteSpace(exportText))
            return new CatalogSnapshotResult(0, 0, 0, 0, "Snapshot is empty - nothing imported.");

        var sections = SplitSections(exportText);
        if (sections.Count == 0)
            return new CatalogSnapshotResult(0, 0, 0, 0, "Snapshot doesn't look like a Collar export (no recognized sections) - nothing imported.");
        if (!ValidateRelaySnapshot(sections, out var validationError))
            return new CatalogSnapshotResult(0, 0, 0, 0, validationError);

        var quick = config.QuickCommands;

        // This pair's own prior entries are about to be replaced, so they're excluded from the duplicate check.
        var usedCommandsExcludingThisPair = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in quick.Titles.Concat(quick.Outfits).Concat(quick.Gestures).Concat(quick.Moodles).Concat(quick.Restraints).Concat(quick.Aliases))
            if (cmd.SourcePairIdHash != sourcePairIdHash)
                usedCommandsExcludingThisPair.Add(cmd.Command);

        var duplicates = 0;

        var newTitles = new List<QuickCommand>();
        if (sections.TryGetValue(TitleAliasesHeader, out var ta))
            ImportAliasLines(ta, newTitles, usedCommandsExcludingThisPair, ref duplicates);

        var newOutfits = new List<QuickCommand>();
        if (sections.TryGetValue(WardrobeHeader, out var w))
            ImportPlainNames(w, newOutfits, name => $"outfit lock {name}", usedCommandsExcludingThisPair, name => name, ref duplicates);
        if (sections.TryGetValue(WardrobeAliasesHeader, out var wa))
            ImportAliasLines(wa, newOutfits, usedCommandsExcludingThisPair, ref duplicates);

        var newGestures = new List<QuickCommand>();
        var stagedGestureCatalog = new Dictionary<string, GestureExportEntry>(config.GestureMapping.ImportedPeerCatalog);
        var gestureCatalogRefreshed = sections.TryGetValue(GestureHeader, out var g);
        if (gestureCatalogRefreshed)
        {
            stagedGestureCatalog.Clear();
            ImportGestureLines(g!, newGestures, usedCommandsExcludingThisPair, ref duplicates, stagedGestureCatalog);
        }
        if (sections.TryGetValue(GestureAliasesHeader, out var ga))
            ImportAliasLines(ga, newGestures, usedCommandsExcludingThisPair, ref duplicates);

        var newMoodles = new List<QuickCommand>();
        if (sections.TryGetValue(MoodlesHeader, out var m))
            ImportPlainNames(m, newMoodles, name => $"moodle apply {CommandSelector.Quote(CommandSelector.MoodleSelector(name, m))}", usedCommandsExcludingThisPair, MoodlesTextFormat.StripMarkup, ref duplicates);
        if (sections.TryGetValue(MoodlesAliasesHeader, out var ma))
            ImportAliasLines(ma, newMoodles, usedCommandsExcludingThisPair, ref duplicates);

        var newRestraints = new List<QuickCommand>();
        var stagedRestraintCatalog = new Dictionary<string, RestraintCatalogExportEntry>(config.RestraintMapping.ImportedPeerCatalog);
        stagedRestraintCatalog.Clear();
        if (sections.TryGetValue(RestraintsHeader, out var r))
            ImportRestraintLines(r, newRestraints, usedCommandsExcludingThisPair, ref duplicates, stagedRestraintCatalog);
        if (sections.TryGetValue(RestraintsAliasesHeader, out var ra))
            ImportAliasLines(ra, newRestraints, usedCommandsExcludingThisPair, ref duplicates);

        var newBundles = new List<QuickCommand>();
        if (sections.TryGetValue(BundlesHeader, out var bundles))
            ImportAliasLines(bundles, newBundles, usedCommandsExcludingThisPair, ref duplicates);

        foreach (var entry in newTitles.Concat(newOutfits).Concat(newGestures).Concat(newMoodles).Concat(newRestraints).Concat(newBundles))
            entry.SourcePairIdHash = sourcePairIdHash;

        var added = 0;
        var updated = 0;
        var removed = 0;

        var oldTitles = quick.Titles; var oldOutfits = quick.Outfits; var oldGestures = quick.Gestures;
        var oldMoodles = quick.Moodles; var oldRestraints = quick.Restraints; var oldAliases = quick.Aliases;
        var oldGestureCatalog = config.GestureMapping.ImportedPeerCatalog;
        var oldRestraintCatalog = config.RestraintMapping.ImportedPeerCatalog;
        quick.Titles = ReconcileCategory(oldTitles, newTitles, sourcePairIdHash, ref added, ref updated, ref removed);
        quick.Outfits = ReconcileCategory(oldOutfits, newOutfits, sourcePairIdHash, ref added, ref updated, ref removed);
        quick.Gestures = ReconcileCategory(oldGestures, newGestures, sourcePairIdHash, ref added, ref updated, ref removed, carryForwardExtra: CarryForwardGestureFields);
        quick.Moodles = ReconcileCategory(oldMoodles, newMoodles, sourcePairIdHash, ref added, ref updated, ref removed);
        quick.Restraints = ReconcileCategory(oldRestraints, newRestraints, sourcePairIdHash, ref added, ref updated, ref removed, carryForwardExtra: CarryForwardRestraintRules);
        quick.Aliases = ReconcileCategory(oldAliases, newBundles, sourcePairIdHash, ref added, ref updated, ref removed);
        if (gestureCatalogRefreshed)
            config.GestureMapping.ImportedPeerCatalog = stagedGestureCatalog;
        config.RestraintMapping.ImportedPeerCatalog = stagedRestraintCatalog;

        try
        {
            catalogStore.Save(config);
            config.Save();
        }
        catch (Exception ex)
        {
            quick.Titles = oldTitles; quick.Outfits = oldOutfits; quick.Gestures = oldGestures;
            quick.Moodles = oldMoodles; quick.Restraints = oldRestraints; quick.Aliases = oldAliases;
            config.GestureMapping.ImportedPeerCatalog = oldGestureCatalog;
            config.RestraintMapping.ImportedPeerCatalog = oldRestraintCatalog;
            return new CatalogSnapshotResult(0, 0, 0, duplicates, $"Could not save the imported snapshot: {ex.Message}");
        }
        return new CatalogSnapshotResult(added, updated, removed, duplicates, null);
    }

    private static void CarryForwardGestureFields(QuickCommand from, QuickCommand to)
    {
        to.GestureModName = from.GestureModName;
        to.GestureGroupName = from.GestureGroupName;
        to.GestureGroupOrder = from.GestureGroupOrder;
        to.GestureOptionOrder = from.GestureOptionOrder;
    }

    private static void CarryForwardRestraintRules(QuickCommand from, QuickCommand to)
    {
        to.RestraintRules = from.RestraintRules;
        to.RestraintCatalogId ??= from.RestraintCatalogId;
    }

    /// Matches by Target (else Label) so a replaced entry keeps its favorite flag and Owner-side fields.
    /// Entries from other sources pass through; this pair's unmatched entries are dropped.
    private static List<QuickCommand> ReconcileCategory(
        List<QuickCommand> existing,
        List<QuickCommand> incoming,
        string sourcePairIdHash,
        ref int added,
        ref int updated,
        ref int removed,
        Action<QuickCommand, QuickCommand>? carryForwardExtra = null)
    {
        var previousFromThisPair = existing.Where(c => c.SourcePairIdHash == sourcePairIdHash).ToList();
        var result = existing.Where(c => c.SourcePairIdHash != sourcePairIdHash).ToList();

        foreach (var incomingEntry in incoming)
        {
            var match = previousFromThisPair.FirstOrDefault(p =>
                incomingEntry.Target is not null && p.Target is not null
                    ? string.Equals(p.Target, incomingEntry.Target, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(p.Label, incomingEntry.Label, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                incomingEntry.IsFavorite = match.IsFavorite;
                // The Owner's own timer picks aren't part of the Sub's export.
                incomingEntry.LockSeconds = match.LockSeconds;
                incomingEntry.FavoriteLockSeconds = match.FavoriteLockSeconds;
                carryForwardExtra?.Invoke(match, incomingEntry);
                updated++;
            }
            else
            {
                added++;
            }
            result.Add(incomingEntry);
        }

        removed += previousFromThisPair.Count(p => !incoming.Any(i =>
            i.Target is not null && p.Target is not null
                ? string.Equals(p.Target, i.Target, StringComparison.OrdinalIgnoreCase)
                : string.Equals(p.Label, i.Label, StringComparison.OrdinalIgnoreCase)));

        result.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// Explicit and Owner-initiated only, so adopting relay sync never silently deletes legacy imports.
    public int AssociateLegacyImportsWithPair(string sourcePairIdHash)
    {
        var quick = config.QuickCommands;
        var count = 0;
        foreach (var list in new[] { quick.Titles, quick.Outfits, quick.Gestures, quick.Moodles, quick.Restraints })
        {
            foreach (var entry in list.Where(c => c.SourcePairIdHash is null && c.Source == ImportSource.Imported))
            {
                entry.SourcePairIdHash = sourcePairIdHash;
                count++;
            }
        }
        if (count > 0) config.Save();
        return count;
    }

    public int ResetLegacyImports()
    {
        var quick = config.QuickCommands;
        var count = 0;
        foreach (var list in new List<List<QuickCommand>> { quick.Titles, quick.Outfits, quick.Gestures, quick.Moodles, quick.Restraints })
            count += list.RemoveAll(c => c.SourcePairIdHash is null && c.Source == ImportSource.Imported);
        if (count > 0) config.Save();
        return count;
    }

    /// Every header is emitted even when empty, so a cleared category isn't misread as "section absent".
    public string BuildExport()
    {
        var sb = new StringBuilder();
        AppendSection(sb, TitleAliasesHeader, ExportCategoryAliasEntries(CustomTriggerActionKind.Title, config.Aliases.Titles.Select(a => new AliasExportEntry(a.Alias, DescribeTitleAlias(a)) { Command = SharedPresetCommands.Title(a) })).Select(EncodeAliasEntry));
        AppendSection(sb, WardrobeHeader, outfit.ExportNames());
        AppendSection(sb, WardrobeAliasesHeader, ExportCategoryAliasEntries(CustomTriggerActionKind.Outfit, config.Aliases.Outfits.Select(a => new AliasExportEntry(a.Alias, DescribeOutfitAlias(a), a.DesignName) { Command = SharedPresetCommands.Outfit(a, config), Moodle = SharedPresetCommands.MoodleName(a.AttachedMoodle) })).Select(EncodeAliasEntry));
        AppendSection(sb, GestureHeader, gesture.ExportCatalog().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        AppendSection(sb, GestureAliasesHeader, ExportCategoryAliasEntries(CustomTriggerActionKind.Gesture, config.Aliases.Gestures.Select(a => new AliasExportEntry(a.Alias, DescribeGestureAlias(a), a.GestureId) { Command = SharedPresetCommands.Gesture(a, config) })).Select(EncodeAliasEntry));
        AppendSection(sb, MoodlesHeader, moodles.ExportNames());
        AppendSection(sb, MoodlesAliasesHeader, ExportCategoryAliasEntries(CustomTriggerActionKind.Moodle, config.Aliases.Moodles.Select(a => new AliasExportEntry(a.Alias, DescribeMoodleAlias(a), MoodlesTextFormat.StripMarkup(a.StatusName)) { Command = SharedPresetCommands.Moodle(a, config) })).Select(EncodeAliasEntry));
        AppendSection(sb, RestraintsHeader, restraints.ExportEntries());
        AppendSection(sb, RestraintsAliasesHeader, ExportCategoryAliasEntries(CustomTriggerActionKind.Restraint, RestraintWordEntries()).Select(EncodeAliasEntry));
        AppendSection(sb, BundlesHeader, ExportBundleEntries().Select(EncodeAliasEntry));
        return sb.ToString();
    }

    public bool TryBuildBoundedExport(out string export, out string? error)
    {
        export = BuildExport();
        if (FitsPlaintextLimit(export))
        {
            error = null;
            return true;
        }
        export = "";
        error = "Catalog exceeds the local plaintext limit and was not uploaded.";
        return false;
    }

    public static bool FitsPlaintextLimit(string export) =>
        Encoding.UTF8.GetByteCount(export) <= RelayProtocolConstants.CatalogPlaintextMaxBytes;

    private static void AppendSection(StringBuilder sb, string header, IEnumerable<string> lines)
    {
        sb.Append(header).Append('\n');
        foreach (var line in lines)
            sb.Append(line).Append('\n');
    }

    private IReadOnlyList<AliasExportEntry> ExportCategoryAliasEntries(CustomTriggerActionKind kind, IEnumerable<AliasExportEntry> categoryDefinitions) =>
        DedupSort(categoryDefinitions.Concat(SingleActionTriggerEntries(kind)));

    private IEnumerable<AliasExportEntry> SingleActionTriggerEntries(CustomTriggerActionKind kind) =>
        config.Aliases.CustomTriggers
            .Where(t => t.Actions.Count == 1 && t.Actions[0].Kind == kind)
            .Select(t => new AliasExportEntry(t.Alias, DescribeCustomTrigger(t), TargetForSingleAction(t.Actions[0])) { Command = SharedPresetCommands.CustomTrigger(t, config) })
            // A trigger that can't be copied (too long for one message, or its restraint is gone) isn't shared.
            .Where(e => e.Command is not null);

    /// Only Outfit/Gesture/Moodle have a target the Owner's import can match on.
    private static string? TargetForSingleAction(CustomTriggerAction action) => action.Kind switch
    {
        CustomTriggerActionKind.Outfit => action.OutfitDesignName,
        CustomTriggerActionKind.Gesture => action.GestureId,
        CustomTriggerActionKind.Moodle => MoodlesTextFormat.StripMarkup(action.MoodleStatusName),
        _ => null,
    };

    /// Follow words and the singleton clear/unlock aliases are excluded: the Owner has fixed rows for them.
    /// Multi-action triggers and single-action Chat triggers are all that's left here.
    private IReadOnlyList<AliasExportEntry> ExportBundleEntries() =>
        DedupSort(config.Aliases.CustomTriggers
            .Where(t => t.Actions.Count >= 2 || (t.Actions.Count == 1 && t.Actions[0].Kind == CustomTriggerActionKind.Chat))
            .Select(t => new AliasExportEntry(t.Alias, DescribeCustomTrigger(t)) { Command = SharedPresetCommands.CustomTrigger(t, config) })
            .Where(e => e.Command is not null));

    private static IReadOnlyList<AliasExportEntry> DedupSort(IEnumerable<AliasExportEntry> entries) =>
        entries.Where(e => e.Alias.Length > 0)
            .GroupBy(e => e.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.Alias, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string DescribeTitleAlias(TitleAliasDefinition a) => $"Title: \"{a.Text}\" ({(a.IsPrefix ? "prefix" : "suffix")})";
    private static string DescribeOutfitAlias(OutfitAliasDefinition a) => $"Outfit: {a.DesignName}{(a.Locked ? " (locks its slots)" : "")}";
    private static string DescribeGestureAlias(GestureAliasDefinition a) => $"Gesture: {(a.AnimationName.Length > 0 ? a.AnimationName : a.EmoteName)}";

    /// The Sub's rules-only restraints, shared as self-contained `restraint wear` copies. Mod restraints travel in the RESTRAINTS section.
    private IEnumerable<AliasExportEntry> RestraintWordEntries() =>
        config.RestraintMapping.Devices.Values
            .Where(d => d.Name.Trim().Length > 0 && d.Rules.Count > 0)
            .Select(d => new AliasExportEntry(d.Name.Trim(), $"Restraint: {d.Name.Trim()} ({string.Join(", ", d.Rules.Select(r => r.Kind))})")
            {
                Command = SharedPresetCommands.RulesOnlyRestraint(d),
                Moodle = SharedPresetCommands.MoodleName(d.AttachedMoodle),
            });
    private static string DescribeMoodleAlias(MoodlesAliasDefinition a) => $"Moodle: {MoodlesTextFormat.StripMarkup(a.StatusName)}";
    private static string DescribeCustomTrigger(CustomTriggerDefinition a) => $"Custom Trigger: {string.Join(", ", a.Actions.Select(CustomTriggerCommand.Summarize))}";

    private static string EncodeAliasEntry(AliasExportEntry entry) =>
        AliasExportPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry)));

    /// Fails closed: an unparseable line is skipped, not imported with a made-up description.
    private static bool TryParseAliasEntry(string line, out AliasExportEntry entry)
    {
        entry = new AliasExportEntry();
        if (!line.StartsWith(AliasExportPrefix, StringComparison.Ordinal))
            return false;

        try
        {
            var decoded = JsonSerializer.Deserialize<AliasExportEntry>(Encoding.UTF8.GetString(Convert.FromBase64String(line[AliasExportPrefix.Length..])));
            if (decoded is null || decoded.Alias.Length == 0)
                return false;

            entry = decoded;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    /// Header present but empty leaves that list untouched; a file with none of the known headers is rejected.
    public CatalogImportResult ParseImport(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new CatalogImportResult(0, 0, 0, 0, 0, 0, 0, "File is empty - nothing to import.");

        var sections = SplitSections(text);
        if (sections.Count == 0)
            return new CatalogImportResult(0, 0, 0, 0, 0, 0, 0, "File doesn't look like a Collar export (no recognized sections) - nothing imported.");

        var quick = config.QuickCommands;
        var stagedTitles = CloneQuickList(quick.Titles);
        var stagedOutfits = CloneQuickList(quick.Outfits);
        var stagedGestures = CloneQuickList(quick.Gestures);
        var stagedMoodles = CloneQuickList(quick.Moodles);
        var stagedRestraints = CloneQuickList(quick.Restraints);
        var stagedAliases = CloneQuickList(quick.Aliases);
        var stagedGestureCatalog = new Dictionary<string, GestureExportEntry>(config.GestureMapping.ImportedPeerCatalog);
        var stagedRestraintCatalog = new Dictionary<string, RestraintCatalogExportEntry>(config.RestraintMapping.ImportedPeerCatalog);

        // Seeded from every saved command, and grown as categories import, so duplicates are caught across categories and within this file.
        var usedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cmd in quick.Titles.Concat(quick.Outfits).Concat(quick.Gestures).Concat(quick.Moodles).Concat(quick.Restraints).Concat(quick.Aliases))
            usedCommands.Add(cmd.Command);

        var duplicates = 0;

        var titleAdded = sections.TryGetValue(TitleAliasesHeader, out var ta)
            ? ImportAliasLines(ta, stagedTitles, usedCommands, ref duplicates)
            : 0;

        var wardrobeAdded = sections.TryGetValue(WardrobeHeader, out var w)
            ? ImportPlainNames(w, stagedOutfits, name => $"outfit lock {name}", usedCommands, name => name, ref duplicates)
            : 0;
        wardrobeAdded += sections.TryGetValue(WardrobeAliasesHeader, out var wa)
            ? ImportAliasLines(wa, stagedOutfits, usedCommands, ref duplicates)
            : 0;

        var gestureCatalogRefreshed = sections.TryGetValue(GestureHeader, out var g);
        if (gestureCatalogRefreshed)
            stagedGestureCatalog.Clear();
        var gestureAdded = gestureCatalogRefreshed
            ? ImportGestureLines(g!, stagedGestures, usedCommands, ref duplicates, stagedGestureCatalog)
            : 0;
        gestureAdded += sections.TryGetValue(GestureAliasesHeader, out var ga)
            ? ImportAliasLines(ga, stagedGestures, usedCommands, ref duplicates)
            : 0;
        if (gestureCatalogRefreshed)
        {
            foreach (var cmd in stagedGestures.Where(c => c.Target is not null && stagedGestureCatalog.ContainsKey(c.Target)))
            {
                var entry = stagedGestureCatalog[cmd.Target!];
                cmd.Command = $"gesture {CommandSelector.Quote(CommandSelector.GestureSelector(entry, stagedGestureCatalog.Values))}";
            }
        }

        var moodlesAdded = sections.TryGetValue(MoodlesHeader, out var m)
            ? ImportPlainNames(m, stagedMoodles, name => $"moodle apply {CommandSelector.Quote(CommandSelector.MoodleSelector(name, m))}", usedCommands, MoodlesTextFormat.StripMarkup, ref duplicates)
            : 0;
        moodlesAdded += sections.TryGetValue(MoodlesAliasesHeader, out var ma)
            ? ImportAliasLines(ma, stagedMoodles, usedCommands, ref duplicates)
            : 0;

        var restraintCatalogRefreshed = sections.TryGetValue(RestraintsHeader, out var r) &&
            r.Any(line => line.StartsWith("OATHBOUND-RESTRAINT-V1|", StringComparison.Ordinal) ||
                          line.StartsWith("OATHBOUND-RESTRAINT-CONFIG-V1|", StringComparison.Ordinal));
        // Without this Clear(), a re-import keeps stale rows for mods that changed id.
        if (restraintCatalogRefreshed)
            stagedRestraintCatalog.Clear();
        var restraintsAdded = sections.TryGetValue(RestraintsHeader, out r)
            ? ImportRestraintLines(r, stagedRestraints, usedCommands, ref duplicates, stagedRestraintCatalog)
            : 0;
        restraintsAdded += sections.TryGetValue(RestraintsAliasesHeader, out var ra)
            ? ImportAliasLines(ra, stagedRestraints, usedCommands, ref duplicates)
            : 0;

        // Also where an older export's flat "## ALIASES" section lands, unsplit.
        var bundlesAdded = sections.TryGetValue(BundlesHeader, out var b)
            ? ImportAliasLines(b, stagedAliases, usedCommands, ref duplicates)
            : 0;

        if (titleAdded + wardrobeAdded + gestureAdded + moodlesAdded + restraintsAdded + bundlesAdded > 0 || gestureCatalogRefreshed || restraintCatalogRefreshed)
        {
            var oldTitles = quick.Titles; var oldOutfits = quick.Outfits; var oldGestures = quick.Gestures;
            var oldMoodles = quick.Moodles; var oldRestraints = quick.Restraints; var oldAliases = quick.Aliases;
            var oldGestureCatalog = config.GestureMapping.ImportedPeerCatalog;
            var oldRestraintCatalog = config.RestraintMapping.ImportedPeerCatalog;
            quick.Titles = stagedTitles;
            quick.Outfits = stagedOutfits;
            quick.Gestures = stagedGestures;
            quick.Moodles = stagedMoodles;
            quick.Restraints = stagedRestraints;
            quick.Aliases = stagedAliases;
            if (gestureCatalogRefreshed)
                config.GestureMapping.ImportedPeerCatalog = stagedGestureCatalog;
            config.RestraintMapping.ImportedPeerCatalog = stagedRestraintCatalog;
            try
            {
                catalogStore.Save(config);
                config.Save();
            }
            catch (Exception ex)
            {
                quick.Titles = oldTitles; quick.Outfits = oldOutfits; quick.Gestures = oldGestures;
                quick.Moodles = oldMoodles; quick.Restraints = oldRestraints; quick.Aliases = oldAliases;
                config.GestureMapping.ImportedPeerCatalog = oldGestureCatalog;
                config.RestraintMapping.ImportedPeerCatalog = oldRestraintCatalog;
                return new CatalogImportResult(0, 0, 0, 0, 0, 0, duplicates, $"Import could not be saved: {ex.Message}");
            }
        }

        return new CatalogImportResult(titleAdded, wardrobeAdded, gestureAdded, moodlesAdded, restraintsAdded, bundlesAdded, duplicates, null);
    }

    private static List<QuickCommand> CloneQuickList(List<QuickCommand> source) =>
        JsonSerializer.Deserialize<List<QuickCommand>>(JsonSerializer.Serialize(source)) ?? new List<QuickCommand>();

    /// Lines before the first known header, or under an unknown one, are ignored.
    private static Dictionary<string, List<string>> SplitSections(string text)
    {
        var result = new Dictionary<string, List<string>>();
        List<string>? current = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (KnownHeaders.Contains(line))
            {
                current = result[line] = new List<string>();
                continue;
            }
            if (current is null)
                continue;
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
                current.Add(trimmed);
        }

        return result;
    }

    /// Relay snapshots must be complete, or a truncated response would be read as deleting categories.
    private static bool ValidateRelaySnapshot(Dictionary<string, List<string>> sections, out string? error)
    {
        foreach (var header in KnownHeaders)
        {
            if (!sections.ContainsKey(header))
            {
                error = $"Snapshot was incomplete (missing {header}) - existing imports were left unchanged.";
                return false;
            }
        }

        foreach (var header in new[] { TitleAliasesHeader, WardrobeAliasesHeader, GestureAliasesHeader, MoodlesAliasesHeader, RestraintsAliasesHeader, BundlesHeader })
        {
            if (sections[header].Any(line => !TryParseAliasEntry(line, out _)))
            {
                error = $"Snapshot contained a malformed entry in {header} - existing imports were left unchanged.";
                return false;
            }
        }

        if (sections[GestureHeader].Any(line => !GestureCommand.TryParseExport(line, out var entry) || entry is null))
        {
            error = "Snapshot contained a malformed gesture entry - existing imports were left unchanged.";
            return false;
        }

        if (sections[RestraintsHeader].Any(line => line.StartsWith("OATHBOUND-RESTRAINT-CONFIG-", StringComparison.Ordinal) &&
            (!RestraintCommand.TryParseConfiguredExport(line, out var configured) || configured is null)))
        {
            error = "Snapshot contained a malformed configured restraint - existing imports were left unchanged.";
            return false;
        }
        if (sections[RestraintsHeader].Any(line => line.StartsWith("OATHBOUND-RESTRAINT-V1|", StringComparison.Ordinal) &&
            (!RestraintCommand.TryParseExport(line, out var entry) || entry is null)))
        {
            error = "Snapshot contained a malformed restraint entry - existing imports were left unchanged.";
            return false;
        }

        foreach (var header in new[] { WardrobeHeader, MoodlesHeader })
        {
            if (sections[header].Any(line => line.Length > 80 || line.IndexOfAny(['{', '}', ';', '<', '>', '\t']) >= 0 ||
                line.Contains("http://", StringComparison.OrdinalIgnoreCase) || line.Contains("https://", StringComparison.OrdinalIgnoreCase)))
            {
                error = $"Snapshot contained an unsafe entry in {header} - existing imports were left unchanged.";
                return false;
            }
        }

        if (sections[RestraintsHeader].Any(line => !line.StartsWith("OATHBOUND-RESTRAINT-V1|", StringComparison.Ordinal) &&
            !line.StartsWith("OATHBOUND-RESTRAINT-CONFIG-V1|", StringComparison.Ordinal) &&
            (line.Length > 80 || line.IndexOfAny(['{', '}', ';', '<', '>', '\t']) >= 0 ||
             line.Contains("http://", StringComparison.OrdinalIgnoreCase) || line.Contains("https://", StringComparison.OrdinalIgnoreCase))))
        {
            error = "Snapshot contained an unsafe legacy restraint entry - existing imports were left unchanged.";
            return false;
        }

        error = null;
        return true;
    }

    /// Skips a malformed line instead of aborting the category. A null `targetSelector` opts out of the same-target check.
    private int ImportPlainNames(IEnumerable<string> lines, List<QuickCommand> target, Func<string, string> toCommand, HashSet<string> usedCommands, Func<string, string>? targetSelector, ref int duplicates)
    {
        var added = 0;
        foreach (var line in lines)
        {
            if (line.Length > 80 || line.IndexOfAny(['{', '}', ';', '<', '>', '\t']) >= 0 ||
                line.Contains("http://", StringComparison.OrdinalIgnoreCase) || line.Contains("https://", StringComparison.OrdinalIgnoreCase))
                continue;

            var command = toCommand(line);
            var targetValue = targetSelector?.Invoke(line);
            var isDuplicateTarget = targetValue is not null && target.Any(existing => existing.Target is not null && string.Equals(existing.Target, targetValue, StringComparison.OrdinalIgnoreCase));
            if (isDuplicateTarget || usedCommands.Contains(command))
            {
                duplicates++;
                continue;
            }

            target.Add(new QuickCommand { Label = line, Command = command, Source = ImportSource.Imported, Target = targetValue });
            usedCommands.Add(command);
            added++;
        }

        if (added > 0)
            target.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return added;
    }

    /// Command stays the bare alias word (what's sent); Label adds the description for display.
    private static int ImportAliasLines(IEnumerable<string> lines, List<QuickCommand> target, HashSet<string> usedCommands, ref int duplicates)
    {
        var added = 0;
        foreach (var line in lines)
        {
            if (!TryParseAliasEntry(line, out var entry))
                continue;

            // Deduped only on the exact command, so an alias with its own moodle/lock isn't swallowed by the plain entry.
            if (entry.Command is { Length: > 0 } copyCommand)
            {
                if (usedCommands.Contains(copyCommand))
                {
                    duplicates++;
                    continue;
                }
                target.Add(new QuickCommand
                {
                    Label = $"{entry.Alias} — {entry.Description}",
                    Command = copyCommand,
                    Source = ImportSource.Imported,
                    // No Target: a copy is identified by its own label on re-sync.
                    MoodleOverride = entry.Moodle,
                });
                usedCommands.Add(copyCommand);
                added++;
                continue;
            }

            var isDuplicateTarget = entry.Target is not null && target.Any(existing => existing.Target is not null && string.Equals(existing.Target, entry.Target, StringComparison.OrdinalIgnoreCase));
            if (isDuplicateTarget || usedCommands.Contains(entry.Alias))
            {
                duplicates++;
                continue;
            }

            target.Add(new QuickCommand { Label = $"{entry.Alias} — {entry.Description}", Command = entry.Alias, Source = ImportSource.Imported, Target = entry.Target });
            usedCommands.Add(entry.Alias);
            added++;
        }

        if (added > 0)
            target.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return added;
    }

    private int ImportGestureLines(IEnumerable<string> lines, List<QuickCommand> target, HashSet<string> usedCommands, ref int duplicates, Dictionary<string, GestureExportEntry>? importedCatalog = null)
    {
        importedCatalog ??= config.GestureMapping.ImportedPeerCatalog;
        var added = 0;
        foreach (var line in lines)
        {
            if (!GestureCommand.TryParseExport(line, out var entry) || entry is null)
                continue;

            importedCatalog[entry.Id] = entry;

            // Triggerless entries are only for restraint selection; there's nothing for Gesture to play.
            if (entry.Trigger is null)
                continue;

            var command = $"gesture {CommandSelector.Quote(CommandSelector.GestureSelector(entry, importedCatalog.Values))}";
            var isDuplicateTarget = target.Any(existing => existing.Target is not null && string.Equals(existing.Target, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (isDuplicateTarget || usedCommands.Contains(command))
            {
                duplicates++;
                continue;
            }

            target.Add(new QuickCommand
            {
                // Display only; identity is Target = entry.Id.
                Label = entry.DisplayLabel,
                Command = command,
                GestureModName = entry.ModName,
                GestureGroupName = entry.GroupName,
                GestureGroupOrder = entry.GroupOrder,
                GestureOptionOrder = entry.OptionOrder,
                Source = ImportSource.Imported,
                Target = entry.Id,
            });
            usedCommands.Add(command);
            added++;
        }

        if (added > 0)
            target.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        return added;
    }


    private int ImportRestraintLines(IEnumerable<string> lines, List<QuickCommand> target, HashSet<string> usedCommands,
        ref int duplicates, Dictionary<string, RestraintCatalogExportEntry> importedCatalog)
    {
        var added = 0;
        foreach (var line in lines)
        {
            if (RestraintCommand.TryParseConfiguredExport(line, out var configured) && configured is not null)
            {
                var command = RestraintCommand.BuildCatalogLockCommand(configured.CatalogId, configured.Name,
                    configured.ItemId!.Value, configured.Rules);
                // By the configured entry's Id, not CatalogId: the same mod can be configured more than once.
                if (target.Any(x => x.Target == configured.Id) || usedCommands.Contains(command))
                {
                    duplicates++;
                    continue;
                }
                target.Add(new QuickCommand
                {
                    Label = configured.Name,
                    Command = command,
                    Source = ImportSource.Imported,
                    Target = configured.Id,
                    RestraintCatalogId = configured.CatalogId,
                    RestraintItemId = configured.ItemId,
                    RestraintRules = configured.Rules,
                    MoodleOverride = configured.Moodle,
                });
                usedCommands.Add(command);
                added++;
                continue;
            }
            if (RestraintCommand.TryParseExport(line, out var entry) && entry is not null)
            {
                importedCatalog[entry.Id] = entry;
                continue;
            }
            // Legacy name-only restraint entries are retired; old saved commands still parse.
            continue;
        }
        return added;
    }
}
