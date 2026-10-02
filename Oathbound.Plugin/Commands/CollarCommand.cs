using System;
using System.Collections.Generic;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;
using Glamourer.Api.Enums;

namespace Oathbound.Plugin.Commands;

/// The Sub's configured Neck-slot collar, applied at pairing acceptance or by the Owner's `collar lock`.
/// Locks only the Neck slot via SlotLockManager.
public sealed class CollarCommand
{
    /// PanicHandler keeps this owner's lock through panic.
    public const string Owner = "Collar";

    /// Timer-based rather than a Moodles change event.
    private const long MoodleReassertIntervalMs = 10_000;

    private readonly PluginConfig config;
    private readonly SlotLockManager slotLocks;
    private readonly SubRuntimeState runtimeState;
    private readonly MoodlesCommand moodles;

    private long nextMoodleReassertTicks;

    public CollarCommand(PluginConfig config, SlotLockManager slotLocks, SubRuntimeState runtimeState, MoodlesCommand moodles)
    {
        this.config = config;
        this.slotLocks = slotLocks;
        this.runtimeState = runtimeState;
        this.moodles = moodles;
    }

    /// Re-applies the assigned Moodle while the owning pairing is paired, locked or not, so removing it doesn't stick.
    public void OnFrameworkUpdate()
    {
        var owningPairing = config.CollarOwningPairingId is { } id ? config.FindPairingById(id) : null;
        if (owningPairing is not { IsPaired: true } || !config.Permissions.Collar || !config.Collar.HasMoodleAssigned)
            return;

        var now = Environment.TickCount64;
        if (now < nextMoodleReassertTicks)
            return;

        ApplyAssignedMoodle();
    }

    /// Released through the ledger's collar source, so only this one status is removed.
    private void ApplyAssignedMoodle()
    {
        nextMoodleReassertTicks = Environment.TickCount64 + MoodleReassertIntervalMs;
        if (Guid.TryParse(config.Collar.MoodleStatusId, out var statusId))
            moodles.Ledger.Hold(AttachedMoodleLedger.CollarSource, statusId);
    }

    /// Undyed. Refused while locked, so the item can't be swapped out from under the lock.
    public bool ConfigureFromItem(ulong itemId)
    {
        if (slotLocks.HasLock(Owner))
            return false;

        config.Collar.ItemId = itemId;
        config.Collar.Stain = 0;
        config.Collar.Stain2 = 0;
        config.Save();
        return true;
    }

    public void ClearConfiguredCollar()
    {
        if (slotLocks.HasLock(Owner))
            return;

        config.Collar.ItemId = null;
        config.Collar.Stain = 0;
        config.Collar.Stain2 = 0;
        config.Save();
    }

    /// Always takes over as the collar-owning pairing; only one pairing can hold the Neck slot.
    public bool ForceApply(Guid pairingId)
    {
        if (!config.Collar.IsConfigured)
            return false;

        var value = new SlotLockValue(config.Collar.ItemId!.Value, config.Collar.Stain, config.Collar.Stain2);
        if (!slotLocks.TryLock(Owner, new Dictionary<ApiEquipSlot, SlotLockValue> { [ApiEquipSlot.Neck] = value }))
            return false;

        config.CollarOwningPairingId = pairingId;
        config.Save();
        runtimeState.CollarForceLocked = true;
        if (config.Collar.HasMoodleAssigned)
            ApplyAssignedMoodle();

        return true;
    }

    /// Ignored unless `pairingId` owns the collar. The moodle keeps reasserting, since it tracks the pairing.
    public bool ForceUnlock(Guid pairingId)
    {
        if (config.CollarOwningPairingId != pairingId)
            return false;
        if (!slotLocks.HasLock(Owner))
            return false;

        slotLocks.Release(Owner);
        config.CollarOwningPairingId = null;
        config.Save();
        runtimeState.CollarForceLocked = false;

        return true;
    }

    /// Called when the collar-owning pairing ends; never on panic. Clears the moodle even when unlocked.
    public void ReleaseOnUnpair()
    {
        if (slotLocks.HasLock(Owner))
        {
            slotLocks.Release(Owner);
            runtimeState.CollarForceLocked = false;
        }

        config.CollarOwningPairingId = null;
        config.Save();

        if (config.Collar.HasMoodleAssigned)
            moodles.Ledger.Release(AttachedMoodleLedger.CollarSource);
    }
}
