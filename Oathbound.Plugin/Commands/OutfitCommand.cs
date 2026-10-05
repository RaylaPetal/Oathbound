using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Safety;
using Glamourer.Api.Enums;

namespace Oathbound.Plugin.Commands;

/// Alias-triggered outfit changes plus the Owner's force-apply, which locks out the Sub's own aliases until released.
/// Locking only locks the slots the design changes.
public sealed class OutfitCommand
{
    private const string Owner = "Outfit";

    /// Restraints may take over slots this owner holds.
    public const string SlotLockOwner = Owner;

    private const string RestraintsSlotLockOwner = "Restraints";

    private readonly PluginConfig config;
    private readonly GlamourerIpc glamourer;
    private readonly SlotLockManager slotLocks;
    private readonly SubRuntimeState runtimeState;
    private readonly MoodlesCommand moodles;

    /// The pairing whose Owner's command (or rulebook) is being run; set by ChatCommandListener around dispatch.
    public Guid? CommandSourcePairingId { get; set; }

    /// (pairing, design id, design name) after an apply that came from that pairing's Owner.
    public event Action<Guid, Guid, string>? OwnerDesignApplied;

    /// The Owner's revert all took every outfit off.
    public event Action? RevertedToBase;

    /// Before the allowlist filter.
    public int? LastScanTotalDesigns { get; private set; }

    /// Null on success.
    public string? LastScanError { get; private set; }

    public OutfitCommand(PluginConfig config, GlamourerIpc glamourer, SlotLockManager slotLocks, SubRuntimeState runtimeState, MoodlesCommand moodles)
    {
        this.config = config;
        this.glamourer = glamourer;
        this.slotLocks = slotLocks;
        this.runtimeState = runtimeState;
        this.moodles = moodles;
    }

    public (bool Success, string? Reason) Apply(OutfitAliasDefinition alias)
    {
        if (runtimeState.OutfitForceLocked)
            return (false, "the outfit is currently force-locked by your Owner.");

        return ApplyDesign(alias.DesignId, alias.DesignName, alias.Locked, alias.AttachedMoodle, moodleOverride: null);
    }

    /// Also clears the outfit's attached moodle even when nothing was locked. Never changes the look.
    public bool Unlock()
    {
        if (runtimeState.OutfitForceLocked)
            return false;

        var hadLock = slotLocks.HasLock(Owner);
        if (hadLock)
            slotLocks.Release(Owner);
        var hadMoodle = moodles.Ledger.Release(AttachedMoodleLedger.OutfitSource);
        return hadLock || hadMoodle;
    }

    /// The Owner only knows the design's name. The default moodle comes from the first alias for this design that has one.
    /// `lockOutfit` false is `outfit wear`: nothing is locked.
    public (bool Success, string? Reason) ForceApply(string designName, string? moodleOverride = null, bool lockOutfit = true)
    {
        var design = config.WardrobeMapping.LocalDesigns.Values
            .FirstOrDefault(d => string.Equals(d.Name, designName, StringComparison.OrdinalIgnoreCase));
        if (design is null)
            return (false, $"no wardrobe design named \"{designName}\" in your Sub's scanned catalog.");

        var aliasMoodle = config.Aliases.Outfits.FirstOrDefault(a => a.DesignId == design.DesignId && a.AttachedMoodle is not null)?.AttachedMoodle;
        var (success, reason) = ApplyDesign(design.DesignId, designName, lockOutfit, aliasMoodle, moodleOverride);
        if (!success)
            return (false, reason);

        runtimeState.OutfitForceLocked = lockOutfit;
        return (true, null);
    }

    /// The Owner's `revert all`. The collar's slot lock is kept, so enforcement (and VerifySoon) puts its piece back.
    /// The caller releases restraints first so only the collar is left to reassert.
    public bool RevertToBase()
    {
        RevertedToBase?.Invoke();
        ForceUnlock();
        var reverted = glamourer.RevertToAutomationFull() == GlamourerApiEc.Success;
        slotLocks.VerifySoon();
        return reverted;
    }

    /// The Sub's "Put it back on": the same design again, leaving locks and the attached moodle as they are; any slot
    /// another lock holds (a restraint, the collar) is put back by enforcement.
    public bool Reapply(Guid designId)
    {
        if (glamourer.ApplyDesign(designId) != GlamourerApiEc.Success)
            return false;
        slotLocks.VerifySoon();
        return true;
    }

    public bool ForceUnlock()
    {
        var hadLock = slotLocks.HasLock(Owner);
        if (hadLock)
            slotLocks.Release(Owner);
        runtimeState.OutfitForceLocked = false;
        var hadMoodle = moodles.Ledger.Release(AttachedMoodleLedger.OutfitSource);
        return hadLock || hadMoodle;
    }

    /// A slot already locked by another owner (e.g. the collar's Neck) is restored right after the apply, so it never
    /// visibly changes; every other slot the design changes applies and locks normally.
    private (bool Success, string? Reason) ApplyDesign(Guid designId, string designName, bool locked, AttachedMoodleRef? defaultMoodle, string? moodleOverride)
    {
        // Drop the previous outfit's locks first, so enforcement doesn't snap back to the old pieces.
        if (slotLocks.HasLock(Owner))
            slotLocks.Release(Owner);

        var slots = locked ? glamourer.GetDesignEquipSlots(designId) : new HashSet<ApiEquipSlot>();
        var conflicts = locked ? slotLocks.ConflictingLocks(slots, Owner) : [];

        var ec = glamourer.ApplyDesign(designId);
        if (ec != GlamourerApiEc.Success)
        {
            var reason = $"Glamourer refused the apply: {ec}.";
            Plugin.Log.Warning($"Outfit apply failed for \"{designName}\": {reason}");
            return (false, reason);
        }

        // This one's moodle replaces the previous outfit's, or clears it.
        moodles.HoldAttached(AttachedMoodleLedger.OutfitSource, defaultMoodle, moodleOverride);
        if (CommandSourcePairingId is { } sourcePairingId)
            OwnerDesignApplied?.Invoke(sourcePairingId, designId, designName);

        foreach (var (slot, conflictOwner) in conflicts)
        {
            // A slot an active restraint covers keeps its piece; this outfit's piece is set aside for later.
            if (conflictOwner == RestraintsSlotLockOwner && glamourer.GetEquipSlotValue(slot) is { } designPiece)
                slotLocks.SetAsideUnder(slot, Owner, new SlotLockValue(designPiece.ItemId, designPiece.Stain, designPiece.Stain2));
            if (slotLocks.GetLockedValue(slot) is { } existingLock)
                glamourer.SetItemOnce(slot, existingLock.ItemId, [existingLock.Stain, existingLock.Stain2]);
        }

        if (!locked || slots.Count == 0)
            return (true, null);

        var toLock = new Dictionary<ApiEquipSlot, SlotLockValue>();
        foreach (var slot in slots.Except(conflicts.Select(c => c.Slot)))
        {
            if (glamourer.GetEquipSlotValue(slot) is { } value)
                toLock[slot] = new SlotLockValue(value.ItemId, value.Stain, value.Stain2);
        }

        if (toLock.Count > 0 && !slotLocks.TryRegisterAlreadyApplied(Owner, toLock))
            return (false, "the design applied visually, but locking its slots afterward failed unexpectedly.");

        if (conflicts.Count == 0)
            return (true, null);

        var skipped = string.Join(", ", conflicts.Select(c => $"{c.Slot} (kept {c.Owner}'s)"));
        return (true, $"Skipped {skipped} - already locked by something else.");
    }

    /// An empty folder scope includes all designs.
    public void Rescan()
    {
        // An unavailable Glamourer throws here, before anything is touched, so the previous catalog survives.
        IReadOnlyList<GlamourerDesign> allDesigns;
        try
        {
            allDesigns = glamourer.GetDesigns();
        }
        catch (Exception ex)
        {
            LastScanError = "Glamourer is not available - kept the previously scanned designs.";
            Plugin.Log.Warning(ex, "Wardrobe rescan skipped: Glamourer IPC unavailable.");
            return;
        }
        LastScanError = null;
        LastScanTotalDesigns = allDesigns.Count;

        var allowlist = config.WardrobeFolderAllowlist;
        var matched = allowlist.Count == 0
            ? allDesigns
            : allDesigns.Where(d => allowlist.Any(folder => IsUnderFolder(d.FullPath, folder))).ToList();

        var entries = matched.Select(d => new WardrobeDesignEntry { DesignId = d.Id, Name = d.DisplayName });
        config.WardrobeMapping.LocalDesigns = entries.ToDictionary(e => e.DesignId);
        config.Save();
    }

    private static bool IsUnderFolder(string fullPath, string folder) =>
        fullPath.StartsWith(folder.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> ExportNames() =>
        config.WardrobeMapping.LocalDesigns.Values.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
}
