using System.Collections.Generic;
using System.Linq;
using Glamourer.Api.Enums;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.Games;

/// Takes pieces off the local player for a lost round, as a `game:<session>` holder in SlotLockManager, so releasing it
/// puts back exactly what was there. Never strips a slot another feature holds.
public sealed class StripService
{
    /// Owns a lost game's penalty locks; each session's claims are its holders, so a release puts back what was worn
    /// before that game.
    public const string PenaltyOwner = "game";
    public const string HolderPrefix = PenaltyOwner + ":";

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

    /// The slot taken off, or null with `skipped` saying why each slot in the order was passed over.
    public ApiEquipSlot? StripNext(string sessionId, out string skipped)
    {
        skipped = "";
        if (!glamourer.IsAvailable)
        {
            skipped = "Glamourer isn't available";
            return null;
        }
        var holder = Holder(sessionId);
        var stripped = slotLocks.ClaimedSlots(holder);
        var reasons = new List<string>();
        foreach (var slot in config.Games.StripOrder)
        {
            var name = SlotName(slot);
            if (stripped.Contains(slot) || !NothingIds.TryGetValue(slot, out var nothing))
                continue;
            if (slotLocks.IsLocked(slot))
            {
                reasons.Add($"{name} is locked");
                continue;
            }
            if (glamourer.GetEquipSlotValue(slot) is not { } worn)
            {
                reasons.Add($"{name} couldn't be read");
                continue;
            }
            if (worn.ItemId == 0 || worn.ItemId == nothing)
                continue;
            if (!slotLocks.Claim(holder, [slot]))
            {
                reasons.Add($"{name} couldn't be read");
                continue;
            }
            var ec = glamourer.SetItemOnce(slot, nothing, [0, 0]);
            if (ec == GlamourerApiEc.Success)
                return slot;
            slotLocks.ReleaseClaim(holder, [slot]);
            reasons.Add($"Glamourer refused {name} ({ec})");
        }
        skipped = string.Join(", ", reasons);
        if (skipped.Length > 0)
            Plugin.Log.Information($"Game strip found nothing to take off: {skipped}.");
        return null;
    }

    public void RestoreAll(string sessionId) => slotLocks.ReleaseClaim(Holder(sessionId));

    /// Takes off everything left in the order, then locks every piece this session took off as nothing. A slot another
    /// feature locked since it came off stays theirs, and is nothing again once they let go of it.
    public IReadOnlyList<ApiEquipSlot> LockNaked(string sessionId)
    {
        while (StripNext(sessionId, out _) is not null)
        {
        }
        var holder = Holder(sessionId);
        var toLock = new Dictionary<ApiEquipSlot, SlotLockValue>();
        foreach (var slot in slotLocks.ClaimedSlots(holder))
        {
            if (!NothingIds.TryGetValue(slot, out var nothing))
                continue;
            var value = new SlotLockValue(nothing, 0, 0);
            if (slotLocks.IsLocked(slot))
                slotLocks.SetAsideUnder(slot, PenaltyOwner, value);
            else
                toLock[slot] = value;
        }
        if (toLock.Count > 0 && !slotLocks.TryLock(PenaltyOwner, toLock, holder: holder))
            Plugin.Log.Warning($"Game penalty couldn't lock every slot ({string.Join(", ", toLock.Keys)}).");
        return slotLocks.ClaimedSlots(holder).ToList();
    }

    /// Ends the penalty and puts back what those sessions' pieces were before their games.
    public void ReleasePenalty(IEnumerable<string> sessionIds) =>
        slotLocks.ReleaseHolders(PenaltyOwner, sessionIds.Select(Holder).ToHashSet());

    /// Sessions that ended without restoring (crash, reload): put their pieces back, except those a running penalty
    /// still holds.
    public void RestoreStale(IReadOnlyCollection<string> penaltySessionIds)
    {
        if (penaltySessionIds.Count == 0)
        {
            slotLocks.Release(PenaltyOwner);
            return;
        }
        var kept = penaltySessionIds.Select(Holder).ToHashSet();
        slotLocks.ReleaseClaimsWhere(h => h.StartsWith(HolderPrefix, System.StringComparison.Ordinal) && !kept.Contains(h));
    }

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
