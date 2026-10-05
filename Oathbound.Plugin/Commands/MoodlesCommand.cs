using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.Commands;

/// The Owner's moodle override, matched by name against the Sub's scanned catalog. Applies immediately.
public sealed class MoodlesCommand
{
    private readonly PluginConfig config;
    private readonly MoodlesIpc moodles;
    private readonly CatalogStore catalogStore;

    /// Every apply/remove goes through here, so removal is one status at a time and respects other holders.
    public AttachedMoodleLedger Ledger { get; }

    /// So the UI can say "found N" before anything is picked.
    public int? LastScanTotalStatuses { get; private set; }
    public MoodlesScanStatus? LastScanStatus { get; private set; }
    public string? LastScanError { get; private set; }

    public MoodlesCommand(PluginConfig config, MoodlesIpc moodles, CatalogStore catalogStore, AttachedMoodleLedger ledger)
    {
        this.config = config;
        this.moodles = moodles;
        this.catalogStore = catalogStore;
        Ledger = ledger;
    }

    /// Individual statuses, not presets, so the Owner can command a single status.
    public void Rescan()
    {
        var result = moodles.GetOwnStatuses();
        LastScanStatus = result.Status;
        LastScanError = result.Error;
        if (result.Status != MoodlesScanStatus.Success)
            return;

        LastScanTotalStatuses = result.Statuses.Count;
        config.MoodlesMapping.LocalCatalog = result.Statuses
            .Select(s => new MoodlesStatusEntry { StatusId = s.Id.ToString(), Name = s.Name })
            .ToDictionary(e => e.StatusId);
        catalogStore.Save(config);
    }

    /// Case-insensitive; the Owner only knows status names.
    public bool ForceApply(string statusName) =>
        TryResolveStatusId(statusName, out var statusId) && Ledger.Hold(AttachedMoodleLedger.ManualSource(statusId), statusId);

    /// A status name or a `"name" #hash` selector.
    public bool TryResolveStatusId(string statusName, out Guid statusId)
    {
        statusId = Guid.Empty;
        if (CommandSelector.TryRead(statusName, out var selector, out var tail) && tail.Length == 0) statusName = selector;
        var entry = CommandSelector.ResolveMoodle(config.MoodlesMapping.LocalCatalog.Values, statusName);
        return entry is not null && Guid.TryParse(entry.StatusId, out statusId);
    }

    /// Keeps moodles an active outfit/restraint/leash/collar holds.
    public bool ForceClear() => Ledger.ClearUnheld();

    /// Applied from its data; never held by the ledger, so nothing puts it back once the Sub removes it.
    public LocalTestResult ApplyCustom(CustomMoodle moodle, string applier) =>
        moodles.ApplyData(moodle, applier) is { } problem
            ? LocalTestResult.Fail(problem)
            : LocalTestResult.Ok($"Custom moodle \"{MoodlesTextFormat.StripMarkup(moodle.Title)}\" applied.");

    public bool RemoveCustom(Guid id) => moodles.RemoveStatus(id);

    /// Both the Moodles permission and the Sub's opt-in to moodles their Owner writes.
    public bool CustomAllowed => config.Permissions.Moodles && config.Permissions.OwnerWrittenMoodles;

    /// The Owner's `moodle:` override when allowed and found, else the Sub's default; with neither, releases
    /// `source`. The Sub's default needs no Moodles permission - the action itself was already gated.
    public void HoldAttached(string source, AttachedMoodleRef? subDefault, string? ownerOverride)
    {
        Guid? statusId = null;
        if (!string.IsNullOrWhiteSpace(ownerOverride))
        {
            if (!config.Permissions.Moodles)
                Plugin.Log.Information($"Attached moodle override \"{ownerOverride}\" ignored for {source}: Moodles permission is off.");
            else if (TryResolveStatusId(ownerOverride, out var overrideId))
                statusId = overrideId;
            else
                Plugin.Log.Warning($"Attached moodle override \"{ownerOverride}\" for {source} matched no Moodles status - using the default.");
        }

        if (statusId is null && subDefault is { } fallback && fallback.StatusId != Guid.Empty)
            statusId = fallback.StatusId;

        if (statusId is { } id)
            Ledger.Hold(source, id);
        else
            Ledger.Release(source);
    }

    /// By StatusId first, then by name, so an alias survives a rescan that renamed its status.
    public bool Apply(MoodlesAliasDefinition alias)
    {
        if (!string.IsNullOrEmpty(alias.StatusId) && config.MoodlesMapping.LocalCatalog.TryGetValue(alias.StatusId, out var exact))
            return Guid.TryParse(exact.StatusId, out var statusId) && Ledger.Hold(AttachedMoodleLedger.ManualSource(statusId), statusId);

        return ForceApply(alias.StatusName);
    }

    public bool Clear() => ForceClear();

    public IReadOnlyList<string> ExportNames() =>
        config.MoodlesMapping.LocalCatalog.Values.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
}
