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
/// Every holder of a slot also records what the slot showed when it took it, so letting go puts exactly that back.
/// Glamourer can only revert automation for the whole actor, and many Subs don't use automation, so nothing here
/// ever reverts to it.
public sealed class SlotLockManager : IDisposable
{
    private readonly PluginConfig config;
    private readonly GlamourerIpc glamourer;
    private readonly Dictionary<ApiEquipSlot, (string Owner, SlotLockValue Value)> locks = new();

    /// A lower-priority owner's piece set aside while a higher one holds the slot, shown again when it releases.
    /// `Relock` false when that owner doesn't lock it: the piece still comes back, unlocked.
    private readonly Dictionary<ApiEquipSlot, (string Owner, SlotLockValue Value, bool Relock)> suspended = new();

    /// Oldest holder first. A holder is an owner name or `owner:suffix`, so one owner can hold slots for several things.
    private readonly Dictionary<ApiEquipSlot, List<Layer>> layers = new();
    private bool isEnforcing;

    /// `Unknown`: taken by an older version, which never recorded what was there.
    private sealed class Layer
    {
        public string Holder = "";
        public SlotLockValue Below;
        public bool Unknown;
    }

    public SlotLockManager(PluginConfig config, GlamourerIpc glamourer)
    {
        this.config = config;
        this.glamourer = glamourer;

        foreach (var entry in config.SlotLocks)
            locks[entry.Slot] = (entry.Owner, new SlotLockValue(entry.ItemId, entry.Stain, entry.Stain2));
        foreach (var entry in config.SuspendedSlotLocks)
            suspended[entry.Slot] = (entry.Owner, new SlotLockValue(entry.ItemId, entry.Stain, entry.Stain2), !entry.Unlocked);
        foreach (var entry in config.SlotEarlierPieces)
            StackOf(entry.Slot).Add(new Layer { Holder = entry.Holder, Below = new SlotLockValue(entry.ItemId, entry.Stain, entry.Stain2), Unknown = entry.Unknown });

        var placeholders = 0;
        foreach (var (slot, owner) in suspended.Select(kv => (kv.Key, kv.Value.Owner)).Concat(locks.Select(kv => (kv.Key, kv.Value.Owner))))
        {
            var stack = StackOf(slot);
            if (stack.Any(l => IsOwnersHolder(l.Holder, owner)))
                continue;
            stack.Add(new Layer { Holder = owner, Unknown = true });
            placeholders++;
        }
        layers.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList().ForEach(s => layers.Remove(s));
        if (placeholders > 0)
        {
            Plugin.Log.Information($"SlotLockManager: {placeholders} slot(s) were locked before earlier pieces were recorded; releasing them will only end the lock.");
            Persist();
        }

        glamourer.LocalPlayerStateChanged += OnLocalPlayerStateChanged;
    }

    public void Dispose() => glamourer.LocalPlayerStateChanged -= OnLocalPlayerStateChanged;

    private List<Layer> StackOf(ApiEquipSlot slot)
    {
        if (!layers.TryGetValue(slot, out var stack))
            layers[slot] = stack = new List<Layer>();
        return stack;
    }

    private static bool IsOwnersHolder(string holder, string owner) =>
        holder == owner || holder.StartsWith(owner + ":", StringComparison.Ordinal);

    /// Includes a lock currently set aside under a higher-priority owner.
    public bool HasLock(string owner) => locks.Values.Any(l => l.Owner == owner) || suspended.Values.Any(l => l.Owner == owner && l.Relock);

    /// Run before applying anything, so a refused lock never leaves a partial visual change.
    public bool WouldOverlap(IEnumerable<ApiEquipSlot> slots, string owner, IReadOnlyCollection<string>? canTakeOverFrom = null) =>
        slots.Any(slot => locks.TryGetValue(slot, out var existing) && existing.Owner != owner
            && (canTakeOverFrom is null || !canTakeOverFrom.Contains(existing.Owner)));

    /// For a caller that skipped a slot a higher-priority owner already holds. `relock` false shows the piece again
    /// on release without locking it.
    public void SetAsideUnder(ApiEquipSlot slot, string owner, SlotLockValue value, bool relock = true)
    {
        suspended[slot] = (owner, value, relock);
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

    public IReadOnlySet<ApiEquipSlot> ClaimedSlots(string holder) =>
        layers.Where(kv => kv.Value.Any(l => l.Holder == holder)).Select(kv => kv.Key).ToHashSet();

    /// Must run before the slots change, so each records what it shows now. A slot the holder already has keeps what
    /// it recorded the first time. Fails, changing nothing, if a slot can't be read, so nothing is ever put on without
    /// a way back.
    public bool Claim(string holder, IEnumerable<ApiEquipSlot> slots)
    {
        var fresh = new Dictionary<ApiEquipSlot, SlotLockValue>();
        var held = ClaimedSlots(holder);
        foreach (var slot in slots.Distinct().Where(s => !held.Contains(s)))
        {
            if (glamourer.GetEquipSlotValue(slot) is not { } value)
            {
                Plugin.Log.Warning($"SlotLockManager: \"{holder}\" couldn't read what {slot} shows - refusing to take it.");
                return false;
            }
            fresh[slot] = new SlotLockValue(value.ItemId, value.Stain, value.Stain2);
        }
        if (fresh.Count == 0)
            return true;
        foreach (var (slot, below) in fresh)
            StackOf(slot).Add(new Layer { Holder = holder, Below = below });
        Persist();
        return true;
    }

    /// Refuses, changing nothing, if a slot is locked by a different owner (except those in `canTakeOverFrom`,
    /// whose value is set aside) or can't be read. `holder` defaults to the owner. A Glamourer failure partway
    /// through a multi-slot request leaves the slots before it locked.
    public bool TryLock(string owner, IReadOnlyDictionary<ApiEquipSlot, SlotLockValue> slots, IReadOnlyCollection<string>? canTakeOverFrom = null, string? holder = null)
    {
        if (slots.Count == 0)
            return false;
        if (WouldOverlap(slots.Keys, owner, canTakeOverFrom))
        {
            var conflicting = slots.Keys.Where(s => locks.TryGetValue(s, out var existing) && existing.Owner != owner);
            Plugin.Log.Warning($"SlotLockManager: \"{owner}\" refused - slot(s) already locked by a different owner: {string.Join(", ", conflicting.Select(s => $"{s} ({locks[s].Owner})"))}.");
            return false;
        }
        holder ??= owner;
        var alreadyClaimed = ClaimedSlots(holder);
        if (!Claim(holder, slots.Keys))
            return false;

        var lockedNow = new HashSet<ApiEquipSlot>();
        foreach (var (slot, value) in slots)
        {
            var ec = glamourer.SetItemOnce(slot, value.ItemId, new List<byte> { value.Stain, value.Stain2 });
            if (ec != GlamourerApiEc.Success)
            {
                Plugin.Log.Warning($"SlotLockManager: failed to apply {slot} for \"{owner}\": {ec}.");
                ReleaseClaim(holder, slots.Keys.Where(s => !lockedNow.Contains(s) && !alreadyClaimed.Contains(s)).ToHashSet());
                Persist();
                return false;
            }
            if (locks.TryGetValue(slot, out var previous) && previous.Owner != owner)
                suspended[slot] = (previous.Owner, previous.Value, true);
            locks[slot] = (owner, value);
            lockedNow.Add(slot);
        }

        Persist();
        return true;
    }

    /// Records slots as locked without applying anything. Check WouldOverlap and Claim before applying the design.
    public bool TryRegisterAlreadyApplied(string owner, IReadOnlyDictionary<ApiEquipSlot, SlotLockValue> slots)
    {
        if (slots.Count == 0 || WouldOverlap(slots.Keys, owner))
            return false;

        foreach (var (slot, value) in slots)
            locks[slot] = (owner, value);

        Persist();
        return true;
    }

    /// Ends the owner's locks without changing how anything looks; its slots stay held for a later release.
    public bool DropLock(string owner)
    {
        var dropped = false;
        isEnforcing = true;
        try
        {
            foreach (var slot in locks.Where(kv => kv.Value.Owner == owner).Select(kv => kv.Key).ToList())
            {
                locks.Remove(slot);
                dropped = true;
                if (suspended.Remove(slot, out var underneath))
                    Promote(slot, underneath, owner);
            }
        }
        finally
        {
            isEnforcing = false;
        }
        foreach (var slot in suspended.Where(kv => kv.Value.Owner == owner && kv.Value.Relock).Select(kv => kv.Key).ToList())
        {
            suspended[slot] = suspended[slot] with { Relock = false };
            dropped = true;
        }
        if (dropped)
            Persist();
        return dropped;
    }

    /// Releases every slot the owner locks or any of its holders has taken.
    public bool Release(string owner) => ReleaseSlots(owner, null);

    /// `only` limits the release to those slots; null releases every slot the owner holds.
    public bool ReleaseSlots(string owner, IReadOnlyCollection<ApiEquipSlot>? only) =>
        ReleaseCore(owner, h => IsOwnersHolder(h, owner), only);

    /// Lets go of one holder's slots without touching any lock.
    public bool ReleaseClaim(string holder, IReadOnlyCollection<ApiEquipSlot>? only = null) =>
        ReleaseCore(null, h => h == holder, only);

    public bool ReleaseClaimsStartingWith(string prefix) =>
        ReleaseCore(null, h => h.StartsWith(prefix, StringComparison.Ordinal), null);

    /// Any lock or holder on the slot, including one set aside under another.
    public bool IsHeld(ApiEquipSlot slot) => locks.ContainsKey(slot) || suspended.ContainsKey(slot) || layers.ContainsKey(slot);

    /// Per slot: a piece set aside underneath shows if there is one; a lock still on the slot keeps showing; otherwise
    /// the slot gets what it showed when the lowest released holder at the top took it. A released holder further down
    /// hands what it recorded to the holder above it. No other slot is touched.
    private bool ReleaseCore(string? owner, Func<string, bool> holderMatches, IReadOnlyCollection<ApiEquipSlot>? only)
    {
        bool Selected(ApiEquipSlot slot) => only is null || only.Contains(slot);

        var slots = locks.Where(kv => kv.Value.Owner == owner).Select(kv => kv.Key)
            .Concat(suspended.Where(kv => kv.Value.Owner == owner).Select(kv => kv.Key))
            .Concat(layers.Where(kv => kv.Value.Any(l => holderMatches(l.Holder))).Select(kv => kv.Key))
            .Where(Selected)
            .ToHashSet();
        if (slots.Count == 0)
            return true;

        isEnforcing = true;
        try
        {
            var restored = true;
            foreach (var slot in slots)
            {
                Layer? uncovered = null;
                if (layers.TryGetValue(slot, out var stack))
                {
                    for (var i = stack.Count - 1; i >= 0; i--)
                    {
                        if (!holderMatches(stack[i].Holder))
                            continue;
                        if (i == stack.Count - 1)
                            uncovered = stack[i];
                        else
                        {
                            stack[i + 1].Below = stack[i].Below;
                            stack[i + 1].Unknown = stack[i].Unknown;
                        }
                        stack.RemoveAt(i);
                    }
                    if (stack.Count == 0)
                        layers.Remove(slot);
                }

                // Set aside under someone else: just drop it, the slot keeps showing the other owner's piece.
                if (suspended.TryGetValue(slot, out var setAside) && setAside.Owner == owner)
                    suspended.Remove(slot);

                if (locks.TryGetValue(slot, out var held) && held.Owner == owner)
                {
                    locks.Remove(slot);
                    if (suspended.Remove(slot, out var underneath))
                    {
                        restored &= Promote(slot, underneath, owner!);
                        continue;
                    }
                }

                if (locks.ContainsKey(slot) || uncovered is null || uncovered.Unknown)
                    continue;
                var ec = glamourer.SetItemOnce(slot, uncovered.Below.ItemId, new List<byte> { uncovered.Below.Stain, uncovered.Below.Stain2 });
                if (ec == GlamourerApiEc.Success)
                    continue;
                restored = false;
                Plugin.Log.Warning($"SlotLockManager: released {owner ?? uncovered.Holder} on {slot}, but Glamourer failed to put the earlier piece back: {ec}.");
            }

            Persist();
            return restored;
        }
        finally
        {
            isEnforcing = false;
        }
    }

    /// Shows what an owner had set aside under a released lock, locking it again if that owner still locks it.
    private bool Promote(ApiEquipSlot slot, (string Owner, SlotLockValue Value, bool Relock) underneath, string releasedOwner)
    {
        if (underneath.Relock)
            locks[slot] = (underneath.Owner, underneath.Value);
        var ec = glamourer.SetItemOnce(slot, underneath.Value.ItemId, new List<byte> { underneath.Value.Stain, underneath.Value.Stain2 });
        if (ec == GlamourerApiEc.Success)
            return true;
        Plugin.Log.Warning($"SlotLockManager: released {releasedOwner}, but Glamourer failed to restore {underneath.Owner}'s {slot}: {ec}.");
        return false;
    }

    /// Drops locks without touching Glamourer (panic does its own full revert). `keepOwner`'s locks survive with
    /// what their slots showed before, and VerifySoon puts their piece back after the revert.
    public void ReleaseAllForPanic(string? keepOwner = null)
    {
        bool Kept(string holder) => keepOwner is not null && IsOwnersHolder(holder, keepOwner);

        var kept = locks.Where(kv => kv.Value.Owner == keepOwner).ToList();
        var keptSuspended = suspended.Where(kv => kv.Value.Owner == keepOwner && kv.Value.Relock).ToList();
        locks.Clear();
        suspended.Clear();
        foreach (var (slot, entry) in kept)
            locks[slot] = entry;
        foreach (var (slot, entry) in keptSuspended)
            locks[slot] = (entry.Owner, entry.Value);

        foreach (var (slot, stack) in layers.ToList())
        {
            for (var i = stack.Count - 1; i >= 0; i--)
            {
                if (Kept(stack[i].Holder))
                    continue;
                if (i < stack.Count - 1)
                {
                    stack[i + 1].Below = stack[i].Below;
                    stack[i + 1].Unknown = stack[i].Unknown;
                }
                stack.RemoveAt(i);
            }
            if (stack.Count == 0)
                layers.Remove(slot);
        }

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
            Unlocked = !kv.Value.Relock,
        }).ToList();
        config.SlotEarlierPieces = layers.SelectMany(kv => kv.Value.Select(l => new SlotEarlierPieceEntry
        {
            Slot = kv.Key,
            Holder = l.Holder,
            ItemId = l.Below.ItemId,
            Stain = l.Below.Stain,
            Stain2 = l.Below.Stain2,
            Unknown = l.Unknown,
        })).ToList();
        config.Save();
    }
}
