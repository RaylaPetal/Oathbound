using System;
using System.Collections.Generic;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;
using Glamourer.Api.Enums;

namespace Oathbound.Plugin.Commands;

/// collar/collaring: the Sub's configured Neck-slot collar, applied and locked automatically at pairing
/// acceptance (see PairingCommand.AcceptPeer). No `ForceApply(name)` taking Owner input the way Outfit/
/// Title do - there's only ever one configured collar item, so the Owner's `collar lock` override needs no
/// argument, it just (re)applies whatever the Sub already configured. Locks only the Neck slot via
/// SlotLockManager (collar/slot-locking) - never Glamourer's own actor-wide lock.
public sealed class CollarCommand
{
    /// The SlotLockManager owner name - PanicHandler keeps this owner's lock through panic.
    public const string Owner = "Collar";

    /// How often the assigned collar Moodle is re-applied while the collar is locked (design.md's
    /// timer-based reassertion, not a Moodles change-notification event - see design.md's Decisions for
    /// why). `Environment.TickCount64`-based, matching GestureCommand.OnFrameworkUpdate's own timing style.
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

    /// Advanced from Plugin.OnFrameworkUpdate - re-applies the collar's assigned Moodle on an interval for
    /// as long as the Sub is paired with "Collar" permission enabled, independent of whether the collar's
    /// Neck-slot lock itself is active, so removing it through Moodles' own UI (or unlocking the collar)
    /// doesn't stick (collar/collaring "Manually removing the assigned Moodle does not stick while
    /// unlocked but still paired"). A no-op whenever unpaired, the permission is off, or no Moodle is
    /// assigned.
    public void OnFrameworkUpdate()
    {
        // collar/collaring: re-asserts for as long as the collar-owning pairing (see ForceApply) remains
        // paired - not "any pairing," since only that one relationship's authority governs this device's
        // single Neck slot.
        var owningPairing = config.CollarOwningPairingId is { } id ? config.FindPairingById(id) : null;
        if (owningPairing is not { IsPaired: true } || !config.Permissions.Collar || !config.Collar.HasMoodleAssigned)
            return;

        var now = Environment.TickCount64;
        if (now < nextMoodleReassertTicks)
            return;

        ApplyAssignedMoodle();
    }

    /// Held through the attached-moodle ledger's `collar` source (collar/attached-moodles "Collar moodle is
    /// removed without clearing other moodles") - re-holding every interval re-applies it, and releasing
    /// removes only this one status, never every moodle on the Sub.
    private void ApplyAssignedMoodle()
    {
        nextMoodleReassertTicks = Environment.TickCount64 + MoodleReassertIntervalMs;
        if (Guid.TryParse(config.Collar.MoodleStatusId, out var statusId))
            moodles.Ledger.Hold(AttachedMoodleLedger.CollarSource, statusId);
    }

    /// Saves an item picked from the Neck-locked `ItemPickerWindow` as the Sub's configured collar -
    /// collar/collaring's "Sub configures their own collar item." Mirrors `RestraintCommand.
    /// CaptureDeviceFromItem`'s shape - no Glamourer read, undyed (stain 0/0). Refuses while a collar lock
    /// is active, so the configured item can't be swapped out from under an already-applied lock.
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

    /// Applies and locks the Sub's configured collar item to the Neck slot only - called automatically
    /// from pairing acceptance (collar/pairing's "Accepting a pairing request applies a configured
    /// collar"), and also directly via the Owner's `collar lock` override (e.g. to re-attach it after
    /// `collar unlock`, or to apply it for the first time if it wasn't configured/enabled yet when pairing
    /// was accepted). If a Moodle is assigned, it applies alongside the item and its periodic
    /// re-assertion (OnFrameworkUpdate) begins - collar/collaring "Assigned Moodle applies alongside the
    /// collar at acceptance"/"Owner's re-lock also resumes the assigned Moodle".
    /// `pairingId` is the pairing whose authority this apply/lock is exercised under - collar/collaring
    /// "Collar applied and locked on pairing acceptance" and "Owner can (re-)apply the collar directly":
    /// the Neck slot can only ever be locked by one pairing at a time, so this always takes over as the
    /// collar-owning pairing, superseding whichever pairing (if any) owned it before.
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

    /// The Owner's `collar unlock` override - releases only the Neck-slot lock. Ignored when `pairingId`
    /// is not the current collar-owning pairing (collar/collaring "A non-owning pairing's release command
    /// is ignored"). The assigned Moodle (if any) is untouched and keeps being periodically re-asserted,
    /// since it now tracks the pairing rather than the lock - collar/collaring "Owner's release also clears
    /// the assigned Moodle" (no longer true; see that scenario's updated body).
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

    /// Called when the collar-owning pairing ends - the Sub's own release or a verified peer notice
    /// (PairingService.ReleasePeer / EndFromVerifiedPeerNotice) - collar/collaring "Only the owning Owner or
    /// unpairing removes the collar". Panic never comes through here: the collar outlives it. Releases the
    /// Neck-slot lock if one is held, and clears the assigned Moodle (if any) unconditionally rather than gating on the lock, since the Moodle
    /// can now be actively reasserting while paired but unlocked. A no-op on both fronts when there's
    /// nothing to release.
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
