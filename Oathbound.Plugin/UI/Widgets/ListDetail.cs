using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace Oathbound.Plugin.UI;

/// `Icon` draws an icon tile where a picture would be; `Marker` is a short state like "on" or "locked". Consecutive
/// items with the same `Group` get one header row.
public sealed record ListDetailItem(string Key, string Label, string? Badge = null, string? Marker = null, string? ImageFile = null,
    FontAwesomeIcon? Icon = null, string? Group = null, string? Tooltip = null);

/// A searchable list beside the selected item's details, or a dropdown above them when the window is narrow. A table
/// rather than two fixed panes, so the details grow with their content and the module card scrolls as before.
public sealed class ListDetail
{
    private string search = "";
    private string? pendingKey;
    private bool openDiscardPopup;

    public string? Selected { get; private set; }

    private static float TwoPaneMinWidth => Layout.Scaled(600);
    private static float ThumbWidth => Layout.Scaled(22);

    /// Selects without the unsaved-edit check, e.g. right after creating an item.
    public void Select(string? key) => Selected = key;

    /// `isDirty` makes leaving the selected item ask first. `onSelected` loads drafts for the newly selected item.
    public void Draw(string id, IReadOnlyList<ListDetailItem> items, Action? toolbar, Action<ListDetailItem?> drawDetail,
        Func<bool>? isDirty = null, Action<ListDetailItem?>? onSelected = null, string emptyText = "Nothing here yet.")
    {
        ImGui.PushID(id);
        if (Selected is not null && items.All(i => i.Key != Selected))
            Selected = null;
        if (Selected is null && items.Count > 0)
        {
            Selected = items[0].Key;
            onSelected?.Invoke(items[0]);
        }

        var filtered = search.Trim().Length == 0
            ? items.ToList()
            : items.Where(i => i.Label.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                || i.Group?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) == true).ToList();
        var selectedItem = items.FirstOrDefault(i => i.Key == Selected);

        if (ImGui.GetContentRegionAvail().X >= TwoPaneMinWidth)
        {
            if (ImGui.BeginTable("listDetail", 2, ImGuiTableFlags.BordersInnerV))
            {
                var listWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.38f, Layout.Scaled(180), Layout.Scaled(280));
                ImGui.TableSetupColumn("list", ImGuiTableColumnFlags.WidthFixed, listWidth);
                ImGui.TableSetupColumn("detail", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawSearchAndToolbar(toolbar);
                DrawList(filtered, isDirty, onSelected, emptyText, items.Count);
                ImGui.TableNextColumn();
                drawDetail(selectedItem);
                ImGui.EndTable();
            }
        }
        else
        {
            DrawSearchAndToolbar(toolbar);
            DrawCombo(filtered, selectedItem, isDirty, onSelected, emptyText, items.Count);
            ImGui.Separator();
            drawDetail(selectedItem);
        }

        DrawDiscardPopup(items, onSelected);
        ImGui.PopID();
    }

    private void DrawSearchAndToolbar(Action? toolbar)
    {
        toolbar?.Invoke();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search...", ref search, 64);
    }

    private void DrawList(List<ListDetailItem> filtered, Func<bool>? isDirty, Action<ListDetailItem?>? onSelected, string emptyText, int total)
    {
        var rowHeight = MathF.Max(ImGui.GetFrameHeight(), ThumbWidth / ImageTile.Aspect) + ImGui.GetStyle().ItemSpacing.Y;
        var groups = filtered.Select(i => i.Group).Where(g => g is not null).Distinct().Count();
        var height = Math.Clamp(filtered.Count * rowHeight + groups * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2,
            Layout.Scaled(60), Layout.Scaled(420));
        if (!ImGui.BeginChild("list", new Vector2(0, height), false))
        {
            ImGui.EndChild();
            return;
        }
        if (filtered.Count == 0)
            IconGlyph.WrappedDisabled(total == 0 ? emptyText : "Nothing matches.");
        string? group = null;
        foreach (var item in filtered)
        {
            if (item.Group is not null && item.Group != group)
            {
                group = item.Group;
                ImGui.TextColored(Theme.AccentHover, group);
            }
            var hasTile = item.ImageFile is not null || item.Icon is not null;
            if (hasTile)
            {
                ImageTile.Draw(item.ImageFile, ThumbWidth, item.ImageFile is null ? item.Icon : null);
                ImGui.SameLine();
            }
            var thumbHeight = hasTile ? ThumbWidth / ImageTile.Aspect : 0;
            if (DrawRow(item, thumbHeight, out var truncated))
                RequestSelect(item, isDirty, onSelected);
            var tooltip = truncated ? item.Label + (item.Tooltip is { } extra ? "\n" + extra : "") : item.Tooltip;
            if (tooltip is not null && ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
        }
        ImGui.EndChild();
    }

    private void DrawCombo(List<ListDetailItem> filtered, ListDetailItem? selectedItem, Func<bool>? isDirty, Action<ListDetailItem?>? onSelected, string emptyText, int total)
    {
        if (total == 0)
        {
            IconGlyph.WrappedDisabled(emptyText);
            return;
        }
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("##pick", selectedItem is null ? "Choose..." : RowText(selectedItem)))
            return;
        string? group = null;
        foreach (var item in filtered)
        {
            if (item.Group is not null && item.Group != group)
            {
                group = item.Group;
                ImGui.TextColored(Theme.AccentHover, group);
            }
            if (ImGui.Selectable($"{RowText(item)}##{item.Key}", item.Key == Selected))
                RequestSelect(item, isDirty, onSelected);
        }
        ImGui.EndCombo();
    }

    /// The label is cut short with an ellipsis rather than running under the column's edge; the badge and marker stay
    /// visible at the right.
    private bool DrawRow(ListDetailItem item, float minHeight, out bool truncated)
    {
        var start = ImGui.GetCursorScreenPos();
        var width = MathF.Max(1, ImGui.GetContentRegionAvail().X);
        var lineHeight = ImGui.GetTextLineHeight();
        var height = MathF.Max(minHeight, lineHeight);
        var clicked = ImGui.Selectable($"##{item.Key}", item.Key == Selected, ImGuiSelectableFlags.None, new Vector2(width, height));

        var draw = ImGui.GetWindowDrawList();
        var y = start.Y + (height - lineHeight) / 2f;
        var suffix = string.Join("  · ", new[] { item.Badge, item.Marker }.Where(x => x is not null));
        var suffixWidth = suffix.Length > 0 ? ImGui.CalcTextSize(suffix).X + ImGui.GetStyle().ItemSpacing.X : 0;
        var label = Ellipsize(item.Label, width - suffixWidth, out truncated);
        draw.AddText(new Vector2(start.X, y), ImGui.GetColorU32(ImGuiCol.Text), label);
        if (suffix.Length > 0)
            draw.AddText(new Vector2(start.X + width - suffixWidth + ImGui.GetStyle().ItemSpacing.X, y), ImGui.GetColorU32(Theme.TextMuted), suffix);
        return clicked;
    }

    private static string Ellipsize(string text, float maxWidth, out bool truncated)
    {
        truncated = ImGui.CalcTextSize(text).X > maxWidth;
        if (!truncated)
            return text;
        var length = text.Length;
        while (length > 0 && ImGui.CalcTextSize(text[..length].TrimEnd() + "…").X > maxWidth)
            length--;
        return text[..length].TrimEnd() + "…";
    }

    private static string RowText(ListDetailItem item) =>
        item.Label + (item.Badge is { } badge ? $"  · {badge}" : "") + (item.Marker is { } marker ? $"  · {marker}" : "");

    private void RequestSelect(ListDetailItem item, Func<bool>? isDirty, Action<ListDetailItem?>? onSelected)
    {
        if (item.Key == Selected)
            return;
        if (isDirty?.Invoke() == true)
        {
            pendingKey = item.Key;
            openDiscardPopup = true;
            return;
        }
        Selected = item.Key;
        onSelected?.Invoke(item);
    }

    private void DrawDiscardPopup(IReadOnlyList<ListDetailItem> items, Action<ListDetailItem?>? onSelected)
    {
        if (openDiscardPopup)
        {
            ImGui.OpenPopup("discard");
            openDiscardPopup = false;
        }
        if (!ImGui.BeginPopup("discard"))
            return;
        ImGui.TextUnformatted("Discard your unsaved changes?");
        if (ImGui.Button("Discard"))
        {
            if (items.FirstOrDefault(i => i.Key == pendingKey) is { } next)
            {
                Selected = next.Key;
                onSelected?.Invoke(next);
            }
            pendingKey = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Keep editing"))
        {
            pendingKey = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }
}
