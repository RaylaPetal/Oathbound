using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Glamourer.Api.Enums;

namespace Oathbound.Plugin.UI;

/// Pick any equippable item for a slot, owned or not - SetItemOnce applies any valid item id.
public sealed class ItemPickerWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string search = "";
    private Action<uint, string>? onChosen;
    private ApiEquipSlot slot;
    private List<(uint ItemId, string Name)> slotItems = [];
    private string? modLabel;

    public ItemPickerWindow(Plugin plugin) : base("Choose item###CollarItemPicker")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(560, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 360), MaximumSize = new Vector2(900, 1000) };
    }

    /// Filtered once per Open: the Item sheet has tens of thousands of rows, only a few hundred per slot.
    public void Open(ApiEquipSlot slot, Action<uint, string> chosen)
    {
        this.slot = slot;
        modLabel = null;
        onChosen = chosen;
        search = "";
        slotItems = EnumerateSlotItems(slot);
        IsOpen = true;
    }

    public void Open(Action<uint, string> chosen)
    {
        this.slot = default;
        modLabel = null;
        onChosen = chosen;
        search = "";
        slotItems = EnumerateAllItems();
        IsOpen = true;
    }

    public void OpenForPenumbraMod(string directory, string name, Action<uint, string> chosen)
    {
        this.slot = default;
        modLabel = name;
        onChosen = chosen;
        search = "";
        var ids = plugin.PenumbraIpc.TryGetChangedItemIds(directory, name);
        OpenForItemIds(name, ids, chosen);
    }

    public void OpenForItemIds(string name, IReadOnlySet<uint> ids, Action<uint, string> chosen)
    {
        this.slot = default;
        modLabel = name;
        onChosen = chosen;
        search = "";
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        slotItems = sheet.Where(item => ids.Contains(item.RowId) && item.EquipSlotCategory.ValueNullable is not null)
            .Select(item => (ItemId: item.RowId, Name: item.Name.ExtractText()))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        IsOpen = true;
    }

    public void Dispose() { }

    private static List<(uint ItemId, string Name)> EnumerateSlotItems(ApiEquipSlot slot)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var results = new List<(uint, string)>();
        foreach (var item in sheet)
        {
            if (!MatchesSlot(item, slot))
                continue;
            var name = item.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            results.Add((item.RowId, name));
        }
        return results.OrderBy(i => i.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<(uint ItemId, string Name)> EnumerateAllItems()
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        return sheet.Where(item => item.EquipSlotCategory.ValueNullable is not null)
            .Select(item => (ItemId: item.RowId, Name: item.Name.ExtractText()))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// Each lockable slot maps to one non-zero EquipSlotCategory field.
    private static bool MatchesSlot(Lumina.Excel.Sheets.Item item, ApiEquipSlot slot)
    {
        var category = item.EquipSlotCategory.ValueNullable;
        if (category is null)
            return false;

        return slot switch
        {
            ApiEquipSlot.Head => category.Value.Head != 0,
            ApiEquipSlot.Body => category.Value.Body != 0,
            ApiEquipSlot.Hands => category.Value.Gloves != 0,
            ApiEquipSlot.Legs => category.Value.Legs != 0,
            ApiEquipSlot.Feet => category.Value.Feet != 0,
            ApiEquipSlot.Ears => category.Value.Ears != 0,
            ApiEquipSlot.Neck => category.Value.Neck != 0,
            ApiEquipSlot.Wrists => category.Value.Wrists != 0,
            ApiEquipSlot.RFinger => category.Value.FingerR != 0,
            ApiEquipSlot.LFinger => category.Value.FingerL != 0,
            _ => false,
        };
    }

    public override void PreDraw() => Theme.PushWindowStyle();
    public override void PostDraw() => Theme.PopWindowStyle();

    public override void Draw()
    {
        IconGlyph.Text(FontAwesomeIcon.Tshirt, modLabel is not null ? $"Changed items - {modLabel}" : slot == default ? "Item Library" : $"Item Library - {slot}");
        ImGui.SameLine();
        IconGlyph.WrappedDisabled("Choose any item valid for this slot - it does not need to be equipped or owned.");
        ImGui.Separator();

        ImGui.SetNextItemWidth(Math.Max(180, ImGui.GetContentRegionAvail().X));
        ImGui.InputTextWithHint("##itemPickerSearch", "Search item name...", ref search, 128);

        var filter = search.Trim();
        var visible = filter.Length == 0
            ? slotItems
            : slotItems.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        IconGlyph.WrappedDisabled(modLabel is not null ? $"{visible.Count} shown / {slotItems.Count} equipment items changed by this mod" : slot == default ? $"{visible.Count} shown / {slotItems.Count} equipment items" : $"{visible.Count} shown / {slotItems.Count} valid for {slot}");
        ImGui.Separator();
        using var child = ImRaii.Child("itemPickerResults", Vector2.Zero, false);
        if (visible.Count == 0) { IconGlyph.WrappedDisabled("No items match this search."); return; }

        foreach (var (itemId, name) in visible)
        {
            ImGui.TextUnformatted(name);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Choose##itemPicker_{itemId}"))
            {
                onChosen?.Invoke(itemId, name);
                IsOpen = false;
            }
        }
    }
}
