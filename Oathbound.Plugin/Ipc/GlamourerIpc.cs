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

/// Weapons and customization are deliberately excluded.
public static class LockableEquipSlots
{
    public static readonly IReadOnlyList<ApiEquipSlot> All =
    [
        ApiEquipSlot.Head, ApiEquipSlot.Body, ApiEquipSlot.Hands, ApiEquipSlot.Legs, ApiEquipSlot.Feet,
        ApiEquipSlot.Ears, ApiEquipSlot.Neck, ApiEquipSlot.Wrists, ApiEquipSlot.RFinger, ApiEquipSlot.LFinger,
    ];
}

/// Always targets the local player (objectIndex 0). Never uses Glamourer's actor-wide lock - every apply is ApplyFlag.Once.
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

    /// Any exception means unavailable.
    public bool IsAvailable { get { try { apiVersion.Invoke(); return true; } catch { return false; } } }
    private readonly EventSubscriber<nint, StateFinalizationType> stateFinalized;
    private readonly EventSubscriber<nint, StateChangeType> stateChanged;

    /// Fires on any change to the local player's Glamourer state. Subscribed to both StateChangedWithType and
    /// StateFinalized, since a single manual slot edit only raises the former.
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

    /// Includes the design-browser folder path.
    public IReadOnlyList<GlamourerDesign> GetDesigns() =>
        getDesignListExtended.Invoke()
            .Select(kv => new GlamourerDesign(kv.Key, kv.Value.DisplayName, kv.Value.FullPath))
            .ToList();

    /// Never locks through Glamourer; SlotLockManager enforces the design's slots afterwards.
    public GlamourerApiEc ApplyDesign(System.Guid designId) =>
        applyDesign.Invoke(designId, LocalPlayerObjectIndex, 0, ApplyFlagEx.DesignDefault);

    /// The slots a design is configured to change (`Equipment.<Slot>.Apply`). An unreadable flag counts as not applied.
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

    /// ApplyFlag.Once only - the one write path SlotLockManager uses.
    public GlamourerApiEc SetItemOnce(ApiEquipSlot slot, ulong itemId, IReadOnlyList<byte> stains) =>
        setItem.Invoke(LocalPlayerObjectIndex, slot, itemId, stains, 0, ApplyFlag.Once);

    /// Equipment only, never customization.
    public GlamourerApiEc RevertToAutomationEquipmentOnly() =>
        revertToAutomation.Invoke(LocalPlayerObjectIndex, 0, ApplyFlag.Equipment);

    /// Equipment and customization.
    public GlamourerApiEc RevertToAutomationFull() => revertToAutomation.Invoke(LocalPlayerObjectIndex);

    /// Null on any failure rather than throwing.
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
