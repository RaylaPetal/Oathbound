using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Glamourer.Api.Enums;

namespace Oathbound.Plugin.Safety;

public readonly record struct SlotLockValue(ulong ItemId, byte Stain, byte Stain2);

/// collar/slot-locking: the shared per-slot Glamourer lock every action category (Collar, Outfit, and
/// eventually Restraints) builds on. Never touches Glamourer's own actor-wide `Combination` lock - every
/// slot is applied with `ApplyFlag.Once` only, and "locked" is purely this plugin's own bookkeeping plus
/// an enforcement loop that reapplies a locked slot the moment Glamourer reports it changed. This is what
/// lets multiple categories each hold their own slot(s) locked at the same time without conflicting, and
/// lets every slot nobody has locked stay completely free to edit through any means.
public sealed class SlotLockManager : IDisposable
{
    private readonly PluginConfig config;
    private readonly GlamourerIpc glamourer;
    private readonly Dictionary<ApiEquipSlot, (string Owner, SlotLockValue Value)> locks = new();

    /// Locks a higher-priority owner took over (see TryLock's `canTakeOverFrom`): the lower owner's slot
    /// value, set aside while the higher owner holds the slot, and put back - re-applied and re-locked -
    /// when the higher owner releases it. So a restraint worn over a locked outfit leaves the outfit's own
    /// piece and lock in place underneath instead of discarding them.
    private readonly Dictionary<ApiEquipSlot, (string Owner, SlotLockValue Value)> suspended = new();
    private bool isEnforcing;

    public SlotLockManager(PluginConfig config, GlamourerIpc glamourer)
    {
        this.config = config;
        this.glamourer = glamourer;

        foreach (var entry in config.SlotLocks)
            locks[entry.Slot] = (entry.Owner, new SlotLockValue(entry.ItemId, entry.Stain, entry.Stain2));
        foreach (var entry in config.SuspendedSlotLocks)
            suspended[entry.Slot] = (entry.Owner, new SlotLockValue(entry.ItemId, entry.Stain, entry.Stain2));

        glamourer.LocalPlayerStateChanged += OnLocalPlayerStateChanged;
    }

    public void Dispose() => glamourer.LocalPlayerStateChanged -= OnLocalPlayerStateChanged;

    /// Includes a lock that's currently set aside under a higher-priority owner - the owner still holds
    /// it, it just isn't the one showing.
    public bool HasLock(string owner) => locks.Values.Any(l => l.Owner == owner) || suspended.Values.Any(l => l.Owner == owner);

    /// True if any of `slots` is currently locked by an owner other than `owner` (ignoring owners `owner`
    /// may take a slot over from) - the overlap check callers should run *before* visually applying
    /// anything, so a refused lock never leaves a partial visual change behind (collar/slot-locking's
    /// overlap-refusal requirement).
    public bool WouldOverlap(IEnumerable<ApiEquipSlot> slots, string owner, IReadOnlyCollection<string>? canTakeOverFrom = null) =>
        slots.Any(slot => locks.TryGetValue(slot, out var existing) && existing.Owner != owner
            && (canTakeOverFrom is null || !canTakeOverFrom.Contains(existing.Owner)));

    /// Records a lower-priority owner's value for `slot` as set aside under whoever holds it now, so it's
    /// restored when that holder releases - for a caller that skipped a slot because a higher-priority
    /// owner already has it (OutfitCommand applying a locked outfit under an active restraint).
    public void SetAsideUnder(ApiEquipSlot slot, string owner, SlotLockValue value)
    {
        suspended[slot] = (owner, value);
        Persist();
    }

    /// Names exactly which of `slots` are conflicting and who holds each one - a refused apply is
    /// otherwise a dead end to diagnose (a design that happens to also touch the Sub's locked Neck slot,
    /// say, just fails with no indication of which slot or owner is in the way; this was hard enough to
    /// track down once that it's worth never repeating). Callers that just need the yes/no should keep
    /// using WouldOverlap - this is for producing an actionable message once a refusal has already
    /// happened.
    public IReadOnlyList<(ApiEquipSlot Slot, string Owner)> ConflictingLocks(IEnumerable<ApiEquipSlot> slots, string owner) =>
        slots.Where(slot => locks.TryGetValue(slot, out var existing) && existing.Owner != owner)
            .Select(slot => (slot, locks[slot].Owner))
            .ToList();

    /// The value currently locked at `slot`, regardless of owner - lets a caller that skips a conflicting
    /// slot (rather than refusing outright) restore it to what it's actually supposed to be after a whole-
    /// design apply visually overwrote it (OutfitCommand.ApplyDesign's per-slot-skip behavior).
    public SlotLockValue? GetLockedValue(ApiEquipSlot slot) => locks.TryGetValue(slot, out var existing) ? existing.Value : null;

    /// Applies and locks every requested slot for `owner` via `SetItemOnce`. Refuses (changing nothing) if
    /// any requested slot is already locked by a *different* owner. Re-locking the same owner's own
    /// slot(s) is always allowed. Used where nothing has been applied to Glamourer yet (Collar's single
    /// item) - see TryRegisterAlreadyApplied for a design already applied via ApplyDesign. Only ever
    /// called with one slot today (Collar's Neck), so a Glamourer failure partway through a multi-slot
    /// request isn't rolled back - worth revisiting if a future multi-slot caller (e.g. Restraints) needs
    /// that guarantee.
    /// `canTakeOverFrom`: lower-priority owners whose lock on a requested slot this owner may take over
    /// (Restraints over Outfit) - their value is set aside and restored when this owner releases the slot.
    public bool TryLock(string owner, IReadOnlyDictionary<ApiEquipSlot, SlotLockValue> slots, IReadOnlyCollection<string>? canTakeOverFrom = null)
    {
        if (slots.Count == 0)
            return false;
        if (WouldOverlap(slots.Keys, owner, canTakeOverFrom))
        {
            var conflicting = slots.Keys.Where(s => locks.TryGetValue(s, out var existing) && existing.Owner != owner);
            Plugin.Log.Warning($"SlotLockManager: \"{owner}\" refused - slot(s) already locked by a different owner: {string.Join(", ", conflicting.Select(s => $"{s} ({locks[s].Owner})"))}.");
            return false;
        }

        foreach (var (slot, value) in slots)
        {
            var ec = glamourer.SetItemOnce(slot, value.ItemId, new List<byte> { value.Stain, value.Stain2 });
            if (ec != GlamourerApiEc.Success)
            {
                Plugin.Log.Warning($"SlotLockManager: failed to apply {slot} for \"{owner}\": {ec}.");
                return false;
            }
            if (locks.TryGetValue(slot, out var previous) && previous.Owner != owner)
                suspended[slot] = previous;
            locks[slot] = (owner, value);
        }

        Persist();
        return true;
    }

    /// Records slots as locked without applying anything to Glamourer - for a caller (OutfitCommand) that
    /// already applied a whole design via `ApplyDesign` and just wants the resulting per-slot values
    /// tracked/enforced from here on. Refuses (recording nothing) on the same overlap condition as
    /// TryLock; callers should check WouldOverlap before applying the design in the first place so a
    /// refused lock never leaves a partial visual change behind.
    public bool TryRegisterAlreadyApplied(string owner, IReadOnlyDictionary<ApiEquipSlot, SlotLockValue> slots)
    {
        if (slots.Count == 0 || WouldOverlap(slots.Keys, owner))
            return false;

        foreach (var (slot, value) in slots)
            locks[slot] = (owner, value);

        Persist();
        return true;
    }

    /// Releases every slot `owner` currently holds. The released slot(s) pick up Glamourer's
    /// automation-managed value; every other slot (another owner's active lock, or a slot the Sub freely
    /// customized) is snapshotted first and reapplied afterward so it ends up exactly where it was -
    /// design.md's "snapshot-revert-restore" decision, since Glamourer can only recompute automation for
    /// the whole actor at once.
    public bool Release(string owner)
    {
        // A lock of this owner's that's currently set aside under someone else just goes away - the slot
        // keeps showing the other owner's piece, so there's nothing to revert.
        var droppedSetAside = suspended.Where(kv => kv.Value.Owner == owner).Select(kv => kv.Key).ToList();
        foreach (var slot in droppedSetAside)
            suspended.Remove(slot);

        var releasedSlots = locks.Where(kv => kv.Value.Owner == owner).Select(kv => kv.Key).ToHashSet();
        if (releasedSlots.Count == 0)
        {
            if (droppedSetAside.Count > 0)
                Persist();
            return true;
        }

        isEnforcing = true;
        try
        {
            var snapshot = new Dictionary<ApiEquipSlot, GlamourerEquippedItem>();
            foreach (var slot in LockableEquipSlots.All)
            {
                if (glamourer.GetEquipSlotValue(slot) is { } value)
                    snapshot[slot] = value;
            }

            var revertResult = glamourer.RevertToAutomationEquipmentOnly();
            if (revertResult != GlamourerApiEc.Success)
            {
                Plugin.Log.Warning($"SlotLockManager: failed to release {owner}'s slots because Glamourer refused the equipment revert: {revertResult}.");
                return false;
            }

            var restored = true;
            foreach (var (slot, value) in snapshot)
            {
                if (releasedSlots.Contains(slot))
                    continue;
                var ec = glamourer.SetItemOnce(slot, value.ItemId, new List<byte> { value.Stain, value.Stain2 });
                if (ec == GlamourerApiEc.Success)
                    continue;
                restored = false;
                Plugin.Log.Warning($"SlotLockManager: released {owner}, but Glamourer failed to restore preserved slot {slot}: {ec}.");
            }

            foreach (var slot in releasedSlots)
            {
                locks.Remove(slot);

                // Put back whatever this owner took the slot over from (e.g. the locked outfit's piece
                // under a restraint that's now removed), instead of leaving the automation value.
                if (!suspended.Remove(slot, out var underneath))
                    continue;
                var ec = glamourer.SetItemOnce(slot, underneath.Value.ItemId, new List<byte> { underneath.Value.Stain, underneath.Value.Stain2 });
                if (ec != GlamourerApiEc.Success)
                {
                    restored = false;
                    Plugin.Log.Warning($"SlotLockManager: released {owner}, but Glamourer failed to restore {underneath.Owner}'s {slot}: {ec}.");
                }
                locks[slot] = underneath;
            }

            Persist();
            return restored;
        }
        finally
        {
            isEnforcing = false;
        }
    }

    /// Panic's own release: drops every tracked lock without touching Glamourer at all, since
    /// PanicHandler already performs its own unconditional whole-actor revert - see design.md's "Panic
    /// keeps a single, unconditional whole-actor revert." `keepOwner`'s locks survive (collar/collaring
    /// "Panic leaves the collar on"): one set aside under an owner being dropped (a restraint over the
    /// collar) becomes the showing lock again, and VerifySoon puts its piece back after the revert, the
    /// same way OutfitCommand.RevertToBase relies on enforcement for the collar.
    public void ReleaseAllForPanic(string? keepOwner = null)
    {
        var kept = locks.Where(kv => kv.Value.Owner == keepOwner)
            .Concat(suspended.Where(kv => kv.Value.Owner == keepOwner))
            .ToList();
        locks.Clear();
        suspended.Clear();
        foreach (var (slot, entry) in kept)
            locks[slot] = entry;
        Persist();
        if (locks.Count > 0)
            VerifySoon();
    }

    private void OnLocalPlayerStateChanged() => Enforce(logReapplied: false);

    /// How long after an apply to re-check the locked slots - long enough for a Penumbra redraw requested
    /// just before the lock to have finished.
    private static readonly TimeSpan VerifyDelay = TimeSpan.FromSeconds(1);

    /// Re-checks every locked slot shortly after an apply and puts back any that don't show their locked
    /// piece. An apply that requests a Penumbra redraw right before locking (a restraint mod) races that
    /// redraw: Glamourer re-applying the actor's state afterwards can put the previous piece - a locked
    /// outfit's - back, without the state-change event enforcement normally reacts to.
    public void VerifySoon() => Plugin.Framework.RunOnTick(() => Enforce(logReapplied: true), VerifyDelay);

    private void Enforce(bool logReapplied)
    {
        if (isEnforcing || locks.Count == 0)
            return;

        isEnforcing = true;
        try
        {
            foreach (var (slot, entry) in locks)
            {
                var current = glamourer.GetEquipSlotValue(slot);
                if (current is { } value && value.ItemId == entry.Value.ItemId && value.Stain == entry.Value.Stain && value.Stain2 == entry.Value.Stain2)
                    continue;

                var ec = glamourer.SetItemOnce(slot, entry.Value.ItemId, new List<byte> { entry.Value.Stain, entry.Value.Stain2 });
                if (logReapplied)
                    Plugin.Log.Information($"SlotLockManager: {slot} showed {current?.ItemId.ToString() ?? "nothing"} instead of {entry.Owner}'s locked item {entry.Value.ItemId} - re-applied ({ec}).");
            }
        }
        finally
        {
            isEnforcing = false;
        }
    }

    private void Persist()
    {
        config.SlotLocks = locks.Select(kv => new SlotLockEntry
        {
            Slot = kv.Key,
            Owner = kv.Value.Owner,
            ItemId = kv.Value.Value.ItemId,
            Stain = kv.Value.Value.Stain,
            Stain2 = kv.Value.Value.Stain2,
        }).ToList();
        config.SuspendedSlotLocks = suspended.Select(kv => new SlotLockEntry
        {
            Slot = kv.Key,
            Owner = kv.Value.Owner,
            ItemId = kv.Value.Value.ItemId,
            Stain = kv.Value.Value.Stain,
            Stain2 = kv.Value.Value.Stain2,
        }).ToList();
        config.Save();
    }
}
