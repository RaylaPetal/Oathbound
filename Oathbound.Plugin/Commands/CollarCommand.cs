using System;
using System.Collections.Generic;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;
using Glamourer.Api.Enums;

namespace Oathbound.Plugin.Commands;

/// The Sub's configured collar (Neck item and/or left ring), applied at pairing acceptance or by the Owner's
/// `collar lock`. Both pieces lock under one SlotLockManager owner, so they're applied and released together.
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

    /// How often a locked collar whose lock isn't held (after a restart, or while Glamourer is down) is retried.
    private const long RestoreRetryMs = 5_000;
    private long nextRestoreTicks;

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
        if (owningPairing is not { IsPaired: true } || !config.Permissions.Collar)
            return;

        RestoreLockIfNeeded(owningPairing.Id);
        if (!config.Collar.HasMoodleAssigned)
            return;

        var now = Environment.TickCount64;
        if (now < nextMoodleReassertTicks)
            return;

        ApplyAssignedMoodle();
    }

    /// The slot lock lives in memory only, so a restart or reload would otherwise leave a locked collar unenforced.
    private void RestoreLockIfNeeded(Guid pairingId)
    {
        if (!runtimeState.CollarForceLocked || slotLocks.HasLock(Owner) || !Plugin.ClientState.IsLoggedIn)
            return;
        var now = Environment.TickCount64;
        if (now < nextRestoreTicks)
            return;
        nextRestoreTicks = now + RestoreRetryMs;
        if (ForceApply(pairingId))
            Plugin.Log.Information("Collar lock restored.");
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

    /// Undyed. Refused while locked, like the Neck item.
    public bool ConfigureRingFromItem(ulong itemId)
    {
        if (slotLocks.HasLock(Owner))
            return false;

        config.Collar.RingItemId = itemId;
        config.Collar.RingStain = 0;
        config.Collar.RingStain2 = 0;
        config.Save();
        return true;
    }

    public void ClearConfiguredRing()
    {
        if (slotLocks.HasLock(Owner))
            return;

        config.Collar.RingItemId = null;
        config.Collar.RingStain = 0;
        config.Collar.RingStain2 = 0;
        config.Save();
    }

    /// Always takes over as the collar-owning pairing; only one pairing can hold the collar slots.
    /// One TryLock for both pieces, so a conflict on either slot locks neither.
    public bool ForceApply(Guid pairingId)
    {
        var collar = config.Collar;
        if (!collar.IsConfigured)
            return false;

        var pieces = new Dictionary<ApiEquipSlot, SlotLockValue>();
        if (collar.ItemId is { } neckItem)
            pieces[ApiEquipSlot.Neck] = new SlotLockValue(neckItem, collar.Stain, collar.Stain2);
        if (collar.RingItemId is { } ringItem)
            pieces[ApiEquipSlot.LFinger] = new SlotLockValue(ringItem, collar.RingStain, collar.RingStain2);
        if (!slotLocks.TryLock(Owner, pieces))
        {
            // TryLock doesn't roll back a Glamourer failure on the second slot; don't leave half a collar locked.
            if (!runtimeState.CollarForceLocked && slotLocks.HasLock(Owner))
                slotLocks.Release(Owner);
            return false;
        }

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
