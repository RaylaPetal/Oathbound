using System;
using System.Collections.Generic;
using System.Linq;
using Glamourer.Api.Enums;
using Glamourer.Api.Helpers;
using Glamourer.Api.IpcSubscribers;
using Newtonsoft.Json.Linq;

namespace Oathbound.Plugin.Ipc;

public readonly record struct GlamourerDesign(System.Guid Id, string DisplayName, string FullPath);

public readonly record struct GlamourerEquippedItem(ulong ItemId, byte Stain, byte Stain2);

/// The 10 gear slots this plugin's per-slot locking (collar/slot-locking) operates on - matches
/// `SetItem`'s own `ApiEquipSlot` parameter and `Glamourer.Designs.DesignData.NumEquipment`. Deliberately
/// excludes MainHand/OffHand (weapons) and customization/bonus items - locking has never covered those here.
public static class LockableEquipSlots
{
    public static readonly IReadOnlyList<ApiEquipSlot> All =
    [
        ApiEquipSlot.Head, ApiEquipSlot.Body, ApiEquipSlot.Hands, ApiEquipSlot.Legs, ApiEquipSlot.Feet,
        ApiEquipSlot.Ears, ApiEquipSlot.Neck, ApiEquipSlot.Wrists, ApiEquipSlot.RFinger, ApiEquipSlot.LFinger,
    ];
}

/// Thin wrapper around the Glamourer.Api calls collar/outfit needs, always targeting the local player
/// (objectIndex 0) - see design.md's Context: only the local client's own state change reaches anyone else.
/// Per-slot locking (collar/slot-locking, see SlotLockManager) never uses Glamourer's own actor-wide
/// `Combination` lock - every apply here goes through `ApplyFlag.Once` only.
public sealed class GlamourerIpc : IDisposable
{
    private const int LocalPlayerObjectIndex = 0;

    private readonly SetItem setItem;
    private readonly RevertToAutomation revertToAutomation;
    private readonly GetDesignListExtended getDesignListExtended;
    private readonly GetDesignJObject getDesignJObject;
    private readonly ApplyDesign applyDesign;
    private readonly GetState getState;
    private readonly ApiVersion apiVersion = new(Plugin.PluginInterface);

    /// collar/ui-organization dependency status: cheapest read-only gate; any exception (not installed,
    /// disabled, incompatible API) means unavailable.
    public bool IsAvailable { get { try { apiVersion.Invoke(); return true; } catch { return false; } } }
    private readonly EventSubscriber<nint, StateFinalizationType> stateFinalized;
    private readonly EventSubscriber<nint, StateChangeType> stateChanged;

    /// Fires whenever the local player's own tracked Glamourer state changes, from any source (manual
    /// edit through Glamourer's own UI, another IPC caller, automation, gearset change) - what
    /// SlotLockManager's enforcement loop reacts to instead of polling every frame. Subscribed on both
    /// `StateChangedWithType` (fires per individual edit - e.g. unequipping one piece in Glamourer's own
    /// UI) and `StateFinalized` (fires once a grouped change, e.g. a full design apply, completes) -
    /// mirroring GagSpeak's own `GlamourListener`, since a single manual slot edit only ever raises
    /// `StateChangedWithType`, never `StateFinalized` on its own.
    public event Action? LocalPlayerStateChanged;

    public GlamourerIpc()
    {
        setItem = new SetItem(Plugin.PluginInterface);
        revertToAutomation = new RevertToAutomation(Plugin.PluginInterface);
        getDesignListExtended = new GetDesignListExtended(Plugin.PluginInterface);
        getDesignJObject = new GetDesignJObject(Plugin.PluginInterface);
        applyDesign = new ApplyDesign(Plugin.PluginInterface);
        getState = new GetState(Plugin.PluginInterface);
        stateFinalized = StateFinalized.Subscriber(Plugin.PluginInterface, OnStateFinalized);
        stateChanged = StateChangedWithType.Subscriber(Plugin.PluginInterface, OnStateChanged);
    }

    public void Dispose()
    {
        stateFinalized.Dispose();
        stateChanged.Dispose();
    }

    private void OnStateFinalized(nint actor, StateFinalizationType type)
    {
        if (actor == Plugin.ObjectTable.LocalPlayer?.Address)
            LocalPlayerStateChanged?.Invoke();
    }

    private void OnStateChanged(nint actor, StateChangeType type)
    {
        if (actor == Plugin.ObjectTable.LocalPlayer?.Address)
            LocalPlayerStateChanged?.Invoke();
    }

    /// The Sub's own saved Glamourer designs, with the folder path shown in Glamourer's design browser -
    /// mirrors PenumbraIpc's sort-path use for the gesture folder allowlist.
    public IReadOnlyList<GlamourerDesign> GetDesigns() =>
        getDesignListExtended.Invoke()
            .Select(kv => new GlamourerDesign(kv.Key, kv.Value.DisplayName, kv.Value.FullPath))
            .ToList();

    /// Applies one of the Sub's own saved designs by id - the primary Wardrobe flow (collar/outfit).
    /// Never locks through Glamourer's own state; SlotLockManager tracks and enforces whichever of the
    /// design's own equipment slots (see GetDesignEquipSlots) need to stay in place afterward.
    public GlamourerApiEc ApplyDesign(System.Guid designId) =>
        applyDesign.Invoke(designId, LocalPlayerObjectIndex, 0, ApplyFlagEx.DesignDefault);

    /// Reads which of the 10 lockable gear slots a design is itself configured to change
    /// (`Equipment.<Slot>.Apply`, confirmed via decompiling `DesignBase.SerializeEquipment()`) - what
    /// "the slots the design itself changes" means for collar/outfit's per-slot lock, independent of
    /// whatever happens to already be equipped. Defensive against a missing/malformed shape: a slot whose
    /// `Apply` flag can't be read is treated as not applied, never thrown.
    public IReadOnlySet<ApiEquipSlot> GetDesignEquipSlots(System.Guid designId)
    {
        var design = getDesignJObject.Invoke(designId);
        var equipment = design?["Equipment"];
        if (equipment is null)
            return new HashSet<ApiEquipSlot>();

        var slots = new HashSet<ApiEquipSlot>();
        foreach (var slot in LockableEquipSlots.All)
        {
            var apply = equipment[slot.ToString()]?["Apply"]?.Value<bool>() ?? false;
            if (apply)
                slots.Add(slot);
        }
        return slots;
    }

    /// Applies a single equipment slot with `ApplyFlag.Once` only - never locks through Glamourer's own
    /// state. The one write path SlotLockManager uses both to establish a lock and to re-assert one that
    /// drifted.
    public GlamourerApiEc SetItemOnce(ApiEquipSlot slot, ulong itemId, IReadOnlyList<byte> stains) =>
        setItem.Invoke(LocalPlayerObjectIndex, slot, itemId, stains, 0, ApplyFlag.Once);

    /// Reverts the local player to Glamourer's automation-managed state for equipment only (deliberately
    /// excluding `Customization`, so this never touches face/body data) - what SlotLockManager.Release
    /// uses to make a released slot pick up automation's value, immediately followed by re-asserting every
    /// slot that wasn't part of the release (see SlotLockManager's snapshot/restore sequence).
    public GlamourerApiEc RevertToAutomationEquipmentOnly() =>
        revertToAutomation.Invoke(LocalPlayerObjectIndex, 0, ApplyFlag.Equipment);

    /// Reverts the local player fully (equipment and customization) to Glamourer's automation-managed
    /// state - PanicHandler's own unconditional whole-actor revert (design.md: "Panic keeps a single,
    /// unconditional whole-actor revert"), unlike SlotLockManager's equipment-only, snapshot/restore
    /// release path.
    public GlamourerApiEc RevertToAutomationFull() => revertToAutomation.Invoke(LocalPlayerObjectIndex);

    /// Reads a single equipment slot out of the local player's current Glamourer state - generalizes
    /// GetCurrentNeckItem's Neck-only lookup to any of the 10 lockable slots. Returns null on any failure
    /// (wrong ec, unexpected JSON shape, no state available) rather than throwing.
    public GlamourerEquippedItem? GetEquipSlotValue(ApiEquipSlot slot)
    {
        var (ec, state) = getState.Invoke(LocalPlayerObjectIndex, 0);
        if (ec != GlamourerApiEc.Success || state is null)
            return null;

        var slotState = state["Equipment"]?[slot.ToString()];
        if (slotState is null)
            return null;

        var itemId = slotState["ItemId"]?.Value<ulong>();
        if (itemId is null)
            return null;

        var stain = slotState["Stain"]?.Value<byte>() ?? 0;
        var stain2 = slotState["Stain2"]?.Value<byte>() ?? 0;
        return new GlamourerEquippedItem(itemId.Value, stain, stain2);
    }

    public static ApiEquipSlot? GetItemSlot(uint itemId)
    {
        var item = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(itemId);
        var category = item?.EquipSlotCategory.ValueNullable;
        if (category is null) return null;
        if (category.Value.Head != 0) return ApiEquipSlot.Head;
        if (category.Value.Body != 0) return ApiEquipSlot.Body;
        if (category.Value.Gloves != 0) return ApiEquipSlot.Hands;
        if (category.Value.Legs != 0) return ApiEquipSlot.Legs;
        if (category.Value.Feet != 0) return ApiEquipSlot.Feet;
        if (category.Value.Ears != 0) return ApiEquipSlot.Ears;
        if (category.Value.Neck != 0) return ApiEquipSlot.Neck;
        if (category.Value.Wrists != 0) return ApiEquipSlot.Wrists;
        if (category.Value.FingerR != 0) return ApiEquipSlot.RFinger;
        if (category.Value.FingerL != 0) return ApiEquipSlot.LFinger;
        return null;
    }
}
