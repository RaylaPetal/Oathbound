using System.Collections.Generic;
using Glamourer.Api.Enums;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.Games;

/// Takes pieces off the local player for a lost round, as a `game:<session>` holder in SlotLockManager, so releasing it
/// puts back exactly what was there. Never strips a slot another feature holds.
public sealed class StripService
{
    public const string HolderPrefix = "game:";

    /// Glamourer's own "nothing" item per slot (`ItemManager.NothingId`). Both rings share one.
    private static readonly Dictionary<ApiEquipSlot, ulong> NothingIds = new()
    {
        [ApiEquipSlot.Head] = 4294967164,
        [ApiEquipSlot.Body] = 4294967163,
        [ApiEquipSlot.Hands] = 4294967162,
        [ApiEquipSlot.Legs] = 4294967160,
        [ApiEquipSlot.Feet] = 4294967159,
        [ApiEquipSlot.Ears] = 4294967158,
        [ApiEquipSlot.Neck] = 4294967157,
        [ApiEquipSlot.Wrists] = 4294967156,
        [ApiEquipSlot.RFinger] = 4294967155,
        [ApiEquipSlot.LFinger] = 4294967155,
    };

    private readonly PluginConfig config;
    private readonly GlamourerIpc glamourer;
    private readonly SlotLockManager slotLocks;

    public StripService(PluginConfig config, GlamourerIpc glamourer, SlotLockManager slotLocks)
    {
        this.config = config;
        this.glamourer = glamourer;
        this.slotLocks = slotLocks;
    }

    public bool IsAvailable => glamourer.IsAvailable;

    public static string Holder(string sessionId) => HolderPrefix + sessionId;

    /// The slot taken off, or null when nothing is left to take or Glamourer can't do it.
    public ApiEquipSlot? StripNext(string sessionId)
    {
        if (!glamourer.IsAvailable)
            return null;
        var holder = Holder(sessionId);
        var stripped = slotLocks.ClaimedSlots(holder);
        foreach (var slot in config.Games.StripOrder)
        {
            if (stripped.Contains(slot) || !NothingIds.TryGetValue(slot, out var nothing) || slotLocks.IsHeld(slot))
                continue;
            if (glamourer.GetEquipSlotValue(slot) is not { } worn || worn.ItemId == 0 || worn.ItemId == nothing)
                continue;
            if (!slotLocks.Claim(holder, [slot]))
                continue;
            if (glamourer.SetItemOnce(slot, nothing, [0, 0]) == Glamourer.Api.Enums.GlamourerApiEc.Success)
                return slot;
            slotLocks.ReleaseClaim(holder, [slot]);
        }
        return null;
    }

    public void RestoreAll(string sessionId) => slotLocks.ReleaseClaim(Holder(sessionId));

    /// A session that ended without restoring (crash, reload): put its pieces back.
    public void RestoreStale() => slotLocks.ReleaseClaimsStartingWith(HolderPrefix);

    public static string SlotName(ApiEquipSlot slot) => slot switch
    {
        ApiEquipSlot.Head => "head",
        ApiEquipSlot.Body => "body",
        ApiEquipSlot.Hands => "hands",
        ApiEquipSlot.Legs => "legs",
        ApiEquipSlot.Feet => "feet",
        ApiEquipSlot.Ears => "earrings",
        ApiEquipSlot.Neck => "necklace",
        ApiEquipSlot.Wrists => "bracelets",
        ApiEquipSlot.RFinger => "right ring",
        ApiEquipSlot.LFinger => "left ring",
        _ => slot.ToString().ToLowerInvariant(),
    };
}
