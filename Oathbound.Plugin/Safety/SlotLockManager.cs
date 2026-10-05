using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Glamourer.Api.Enums;

namespace Oathbound.Plugin.Safety;

public readonly record struct SlotLockValue(ulong ItemId, byte Stain, byte Stain2);

/// Per-slot locks shared by every category. Slots are applied with ApplyFlag.Once and re-applied whenever Glamourer
/// reports a change - never Glamourer's actor-wide lock - so categories can each hold their own slots.
public sealed class SlotLockManager : IDisposable
{
    private readonly PluginConfig config;
    private readonly GlamourerIpc glamourer;
    private readonly Dictionary<ApiEquipSlot, (string Owner, SlotLockValue Value)> locks = new();

    /// A lower-priority owner's lock set aside while a higher one holds the slot, restored when it releases.
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

    /// Includes a lock currently set aside under a higher-priority owner.
    public bool HasLock(string owner) => locks.Values.Any(l => l.Owner == owner) || suspended.Values.Any(l => l.Owner == owner);

    /// Run before applying anything, so a refused lock never leaves a partial visual change.
    public bool WouldOverlap(IEnumerable<ApiEquipSlot> slots, string owner, IReadOnlyCollection<string>? canTakeOverFrom = null) =>
        slots.Any(slot => locks.TryGetValue(slot, out var existing) && existing.Owner != owner
            && (canTakeOverFrom is null || !canTakeOverFrom.Contains(existing.Owner)));

    /// For a caller that skipped a slot a higher-priority owner already holds.
    public void SetAsideUnder(ApiEquipSlot slot, string owner, SlotLockValue value)
    {
        suspended[slot] = (owner, value);
        Persist();
    }

    /// For an actionable message after a refusal; use WouldOverlap for yes/no.
    public IReadOnlyList<(ApiEquipSlot Slot, string Owner)> ConflictingLocks(IEnumerable<ApiEquipSlot> slots, string owner) =>
        slots.Where(slot => locks.TryGetValue(slot, out var existing) && existing.Owner != owner)
            .Select(slot => (slot, locks[slot].Owner))
            .ToList();

    /// Lets a caller restore a conflicting slot after a whole-design apply overwrote it.
    public SlotLockValue? GetLockedValue(ApiEquipSlot slot) => locks.TryGetValue(slot, out var existing) ? existing.Value : null;

    public string? LockOwner(ApiEquipSlot slot) => locks.TryGetValue(slot, out var existing) ? existing.Owner : null;

    /// Refuses, changing nothing, if a slot is locked by a different owner (except those in `canTakeOverFrom`,
    /// whose value is set aside). A Glamourer failure partway through a multi-slot request isn't rolled back.
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

    /// Records slots as locked without applying anything. Check WouldOverlap before applying the design.
    public bool TryRegisterAlreadyApplied(string owner, IReadOnlyDictionary<ApiEquipSlot, SlotLockValue> slots)
    {
        if (slots.Count == 0 || WouldOverlap(slots.Keys, owner))
            return false;

        foreach (var (slot, value) in slots)
            locks[slot] = (owner, value);

        Persist();
        return true;
    }

    /// Glamourer can only recompute automation for the whole actor, so every other slot is snapshotted and restored.
    public bool Release(string owner)
    {
        // Set aside under someone else: just drop it, the slot keeps showing the other owner's piece.
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

                // Put back what this owner took the slot over from, rather than the automation value.
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

    /// Drops locks without touching Glamourer (panic does its own full revert). `keepOwner`'s locks survive,
    /// and VerifySoon puts their piece back after the revert.
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

    /// Long enough for a Penumbra redraw requested just before the lock to finish.
    private static readonly TimeSpan VerifyDelay = TimeSpan.FromSeconds(1);

    /// A redraw right before locking can race Glamourer and put the previous piece back without a state-change event.
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
