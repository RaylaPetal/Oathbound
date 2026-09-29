using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.Commands;

/// One entry in the Aliases export section: the bare alias word plus a human-readable summary of what it
/// does - see AliasBook's doc comment for why the Owner is deliberately shown this, unlike the live wire
/// tell (which only ever carries the bare alias word). A plain mutable class, matching GestureExportEntry's
/// own shape, rather than a record - plays safest with System.Text.Json's default (de)serialization.
public class AliasExportEntry
{
    public string Alias { get; set; } = "";
    public string Description { get; set; } = "";

    /// collar/catalog-sync "Import skips commands that duplicate an existing quick command": the design
    /// id, gesture id, or Moodles status name this alias applies - only ever set for a single-action
    /// Outfit/Gesture/Moodle alias (never Title, Restraint, or a multi-action bundle), so import-time
    /// dedup can recognize "this alias and that plain scanned entry are the same target" without parsing
    /// the human-readable Description. Null on an older export predating this field.
    public string? Target { get; set; }

    /// collar/catalog-sync "shared presets are copies": the self-contained Owner command this preset
    /// stands for (SharedPresetCommands) - what the Owner's imported copy actually sends, so it keeps working
    /// after the Sub deletes the preset. `Alias` stays as the recognizable label. Null on an export from an
    /// older Sub, where the Owner falls back to sending the bare alias word as before; an older Owner
    /// ignores it the same way.
    public string? Command { get; set; }

    /// The preset's attached moodle name, imported as the Owner's per-command moodle pick for the copy.
    public string? Moodle { get; set; }

    public AliasExportEntry() { }
    public AliasExportEntry(string alias, string description, string? target = null)
    {
        Alias = alias;
        Description = description;
        Target = target;
    }
}

/// The per-category added-command counts from a single ParseImport call, plus an overall error when the
/// file wasn't recognizable as a Collar export at all (as opposed to a recognized-but-partially-empty one,
/// which still returns zero-valued counts with Error null). Wardrobe/Gesture/Moodles/Restraints each fold
/// together that category's scanned-name adds and its single-action-alias adds, since both now land in the
/// same Owner quick-command list (collar/catalog-sync's "Owner imports alias names as one-off quick
/// commands", reworked to route by category). Bundles covers only genuinely multi-action Custom Triggers
/// (plus a single-action Chat trigger, which has no matching category list of its own).
public readonly record struct CatalogImportResult(int Title, int Wardrobe, int Gesture, int Moodles, int Restraints, int Bundles, int Duplicates, string? Error)
{
    public int TotalAdded => Title + Wardrobe + Gesture + Moodles + Restraints + Bundles;
}

/// collar/catalog-sync: the outcome of a single relay snapshot's atomic apply (see
/// CatalogSyncService.ApplyRelaySnapshot) - Added/Updated/Removed span every category together, since the
/// Owner-facing status only ever needs to say "your Sub's catalog changed," not break it down by category.
public readonly record struct CatalogSnapshotResult(int Added, int Updated, int Removed, int Duplicates, string? Error);

/// collar/catalog-sync: composes each category's existing export output into one sectioned text file, and
/// splits an imported file back into each category's quick-command list, using the same matching/dedup
/// behavior each category's own individual import already had (moved here from CollarWindow so the
/// unified flow has one implementation instead of three copies). Does not replace or change any category's
/// own scan/apply logic - only the scan-trigger/export/import UX is unified (design.md's Non-Goals).
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

    /// Kept under its original header name for backward compatibility - an export from before this change
    /// mixed every single- and multi-action alias/trigger here, and an old file's "## ALIASES" section
    /// still parses today, landing entirely in the Custom Trigger Bundle list (design.md's "Risks" and
    /// tasks.md 2.5). Going forward this section only ever receives genuinely multi-action Custom Triggers
    /// (plus a single-action Chat trigger - see ExportBundleEntries).
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

    /// collar/catalog-sync "Automatic import replaces one peer snapshot atomically". Unlike ParseImport
    /// (manual file import, purely additive/dedup, never removes anything), a relay snapshot from a given
    /// pair is a *replacement* of that pair's own previously-imported entries: anything from this pair not
    /// present in the new snapshot is removed, anything present is added or updated, and a stable-identity
    /// match (Target, or Label when a category has no Target) carries forward IsFavorite and
    /// presentation-only fields from the entry it replaces. Manual entries and other pairs' imports are
    /// never touched. Nothing is mutated until every category has been parsed successfully and reconciled
    /// in memory; config.Save() is called at most once, at the very end - a parse failure partway through
    /// leaves the prior snapshot completely intact (task 6.4/6.5).
    /// collar/catalog-sync "sharing again replaces the previous share": a Sub's export file imported while
    /// paired as their Owner is treated exactly like a relay refresh from that pairing - this Sub's
    /// previously imported copies are replaced with the file's set (removed ones go away), and the Owner's
    /// own commands are never touched. Null when that doesn't apply (no active Owner-side pairing with a
    /// relay pair id, or not a complete current export) - the caller then falls back to ParseImport's
    /// add-only import.
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

        // Cross-source duplicate prevention still applies (a Sub's alias can't collide with a manual entry
        // or another pair's import), but this pair's *own* prior entries are excluded from that check -
        // they're about to be replaced, not compared against their own successors.
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

    /// Matches by stable identity (Target when the category has one, else Label) against the entries this
    /// pair previously contributed to `existing`, so a matched entry keeps its favorite flag and any
    /// presentation-only fields the Owner can't get back from the Sub's export alone (restraint rules,
    /// gesture grouping). Everything from another source (manual, another pair) passes through untouched;
    /// everything previously from this pair that has no match in `incoming` is dropped (removed).
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
                // collar/restraint-lock-timer: the Owner's own timer picks aren't part of the Sub's export.
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

    /// collar/catalog-sync "explicit legacy-import associate/reset path" (task 6.6): a legacy imported
    /// entry (SourcePairIdHash null, Source Imported - i.e. from before relay sync existed, or from a
    /// manual file import) is never touched by ApplyRelaySnapshot's reconciliation. Associating it with a
    /// pair lets the *next* relay snapshot from that pair reconcile it normally (update or remove);
    /// resetting drops every such legacy-imported entry outright. Both are explicit, Owner-initiated
    /// actions - never automatic, so adopting relay sync can never silently delete unscoped legacy imports.
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

    /// Every category's header is always emitted, even with zero body lines - an empty category is
    /// explicitly represented rather than omitted (collar/catalog-sync's "empty category still
    /// represented" requirement), so a re-export after clearing a category can't be misread on import as
    /// "section absent, leave existing quick commands alone."
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

    /// collar/catalog-sync "Exporting every catalog to one file": a category's own alias definitions plus
    /// any Custom Trigger that bundles exactly one action of that same category, deduplicated by alias
    /// word - each carries a human-readable summary of what it does alongside the bare word, so an Owner
    /// importing this file knows what they're actually sending (see AliasBook's doc comment for why this
    /// is a deliberate choice, not an oversight - the live wire tell during real commanding still only
    /// ever carries the bare alias word, unaffected by this).
    private IReadOnlyList<AliasExportEntry> ExportCategoryAliasEntries(CustomTriggerActionKind kind, IEnumerable<AliasExportEntry> categoryDefinitions) =>
        DedupSort(categoryDefinitions.Concat(SingleActionTriggerEntries(kind)));

    private IEnumerable<AliasExportEntry> SingleActionTriggerEntries(CustomTriggerActionKind kind) =>
        config.Aliases.CustomTriggers
            .Where(t => t.Actions.Count == 1 && t.Actions[0].Kind == kind)
            .Select(t => new AliasExportEntry(t.Alias, DescribeCustomTrigger(t), TargetForSingleAction(t.Actions[0])) { Command = SharedPresetCommands.CustomTrigger(t, config) })
            // A trigger that can't be copied (an action too long for one message, or its restraint is gone) isn't shared.
            .Where(e => e.Command is not null);

    /// collar/catalog-sync "Import skips commands that duplicate an existing quick command": only Outfit/
    /// Gesture/Moodle carry a target identity the Owner's import can match on - Title (free text) and
    /// Restraint (Sub-captured, not scan-derived) fall through to null, same as their own alias
    /// definitions never populate a `Target` on export.
    private static string? TargetForSingleAction(CustomTriggerAction action) => action.Kind switch
    {
        CustomTriggerActionKind.Outfit => action.OutfitDesignName,
        CustomTriggerActionKind.Gesture => action.GestureId,
        CustomTriggerActionKind.Moodle => MoodlesTextFormat.StripMarkup(action.MoodleStatusName),
        _ => null,
    };

    /// Follow's fixed engage/release words and the singleton Clear-title/Unlock-outfit/Clear-moodle
    /// aliases are deliberately excluded from every section above - the Owner already has dedicated fixed
    /// quick-command rows for all of those, so exporting them would be redundant. Custom Triggers that
    /// bundle two or more actions have no single matching category, so they - along with a single-action
    /// Chat trigger, since Chat has no Owner-side category list of its own - are the only entries left in
    /// the Custom Trigger Bundle section.
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

    /// The Sub's rules-only restraints, shared into the restraint section. Configured mod restraints aren't listed here: they already travel as full definitions in the
    /// RESTRAINTS section. Each rules-only restraint is shared as a self-contained copy
    /// (`restraint wear - - "<name>" rules:...`) with its moodle as the Owner's pick.
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

    /// Fails closed (returns false) on anything that isn't a well-formed encoded alias line - an older/
    /// hand-edited export's bare-word Aliases lines (this format's predecessor) no longer parse, so they're
    /// silently skipped rather than imported with a fabricated description, matching `ImportGestureLines`'s
    /// own "unparseable line is skipped, not fatal" behavior.
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

    /// Populates every category's quick-command list from its corresponding section. A section header
    /// present with zero body lines leaves that category's list untouched (nothing to add). A section
    /// entirely absent from the file (not a well-formed Collar export, or an older/hand-edited one) is
    /// likewise skipped rather than erroring the whole import - only a file with none of the ten
    /// recognized headers at all is rejected outright.
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

        // collar/catalog-sync "Import skips commands that duplicate an existing quick command": seeded
        // once, before any category import runs, from every command already saved anywhere - not just the
        // category currently being populated - so a shared alias word is caught regardless of import
        // order, and each import call below adds to it as it goes so a duplicate introduced earlier in
        // this same file is caught too.
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
        // Mirrors gestureCatalogRefreshed's Clear() above - without it, re-importing (e.g. from the Sync
        // tab's "Import commands") only ever adds to stagedRestraintCatalog, never removing entries the
        // new export no longer carries, so a mod whose stable id changed (or was simply re-scanned) shows
        // up as a second, stale row alongside the current one instead of replacing it.
        if (restraintCatalogRefreshed)
            stagedRestraintCatalog.Clear();
        var restraintsAdded = sections.TryGetValue(RestraintsHeader, out r)
            ? ImportRestraintLines(r, stagedRestraints, usedCommands, ref duplicates, stagedRestraintCatalog)
            : 0;
        restraintsAdded += sections.TryGetValue(RestraintsAliasesHeader, out var ra)
            ? ImportAliasLines(ra, stagedRestraints, usedCommands, ref duplicates)
            : 0;

        // Also the landing spot for an older export's flat "## ALIASES" section, which mixed single- and
        // multi-action entries together - those all land here unchanged, never split out retroactively
        // into a single category's list (tasks.md 2.5's backward-compatibility requirement).
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

    /// Splits on the ten known "## " headers; a line before the first recognized header, or under an
    /// unrecognized header, is ignored rather than treated as an error - tolerant of a hand-edited or
    /// future-versioned file that still carries the sections this version understands.
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

    /// Relay snapshots are produced by this version's BuildExport, so unlike tolerant manual imports they
    /// must be complete and every structured line must parse. This prevents a truncated/corrupt response
    /// from being interpreted as intentional category deletion during replacement reconciliation.
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

    /// Same matching/dedup and line-sanity guards `ImportQuickCommands` used to apply per-button - skips
    /// an individual malformed line instead of aborting the whole category (task 2.3's "malformed line is
    /// skipped, not fatal" behavior - a deliberate improvement over the old clipboard importer's
    /// abort-on-first-bad-line, since a file can legitimately mix well-formed entries across categories).
    /// `usedCommands` is the shared, whole-import command set (collar/catalog-sync's cross-category
    /// duplicate check); `targetSelector`, when non-null, normalizes the scanned name into the same
    /// identity space a same-category alias's exported `Target` uses (e.g. markup-stripped for Moodles),
    /// enabling the same-target check - passing null (Restraints) opts a category out of that check
    /// entirely, matching "Import skips commands that duplicate an existing quick command"'s exclusions.
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

    /// collar/catalog-sync: unlike the other categories' `ImportPlainNames`, `Label` and `Command` diverge
    /// here - `Command` stays the bare alias word (what's actually sent, trigger-phrase-prefixed, in the
    /// wire tell), while `Label` carries the alias word plus its description, so the Owner sees what
    /// they're about to send without changing what actually gets sent. Category-agnostic on purpose - an
    /// alias only ever resolves against the Sub's own dictionary by its bare word, regardless of which
    /// category list the Owner's copy of it lives in, so this same helper backs Title/Outfit/Gesture/
    /// Restraint/Moodle single-action aliases and the Custom Trigger Bundle list alike.
    /// `entry.Target` is only ever non-null for a single-action Outfit/Gesture/Moodle alias (see
    /// `TargetForSingleAction`/the per-category export calls in `BuildExport`) - for every other category
    /// this always compiles to "no target to compare," so the same-target check below is inert there
    /// without needing an explicit per-category opt-out.
    private static int ImportAliasLines(IEnumerable<string> lines, List<QuickCommand> target, HashSet<string> usedCommands, ref int duplicates)
    {
        var added = 0;
        foreach (var line in lines)
        {
            if (!TryParseAliasEntry(line, out var entry))
                continue;

            // collar/catalog-sync "shared presets are copies": a current Sub sends the self-contained command
            // the preset stands for - that's what the copy sends, so it keeps working after the Sub deletes
            // the preset. Deduped only on the exact command, not the target, so e.g. an outfit alias with its
            // own moodle or lock choice isn't swallowed by the plain design-name entry for the same design.
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
                    // No Target: a copy is identified by its own label on re-sync, not by the design/
                    // animation it applies (which a plain catalog entry may share).
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

            // Triggerless entries are exported for restraint enable-only selection, but are not ordinary
            // Gesture commands: without a pose/emote there is nothing for the Gesture category to play.
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
                // What the Owner reads (collar/animation-labels); identity is Target = entry.Id, never this text.
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
                // Dedup/reconcile by the configured entry's own stable Id, not its CatalogId - a Sub can
                // configure the same mod more than once with different restriction rules (collar/restraints
                // "create a mod restraint for the same mod"), and each such entry must import as its own
                // quick command rather than only the first one surviving.
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
            // Legacy name-only restraint catalog entries are intentionally retired. The runtime parser
            // remains tolerant of old saved commands, but new imports never recreate that UI model.
            continue;
        }
        return added;
    }
}
