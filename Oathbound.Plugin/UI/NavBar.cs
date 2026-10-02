using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace Oathbound.Plugin.UI;

/// The icon+label grid of modules, three per row.
public static class NavBar
{
    private const int Columns = 3;
    private const float ButtonHeight = 36f;

    /// Computed from the same style values Draw uses, so callers never guess a constant.
    public static float RequiredHeight(int itemCount)
    {
        var rows = (itemCount + Columns - 1) / Columns;
        var spacing = ImGui.GetStyle().ItemSpacing;
        var padding = ImGui.GetStyle().WindowPadding;
        return rows * ButtonHeight + (rows - 1) * spacing.Y + padding.Y * 2;
    }

    /// Non-null for an id draws that tile dimmed and unclickable, with the reason as its tooltip.
    public static string? Draw((string Id, FontAwesomeIcon Icon, string Tooltip)[] items, System.Func<string, string?>? disabledReason = null)
    {
        string? clicked = null;
        // The card's inner margin comes from WindowPadding; using ItemSpacing clipped the last row.
        var spacing = ImGui.GetStyle().ItemSpacing;
        var cardHeight = RequiredHeight(items.Length);
        using var card = Card.Begin("navBar", new Vector2(0, cardHeight), noScroll: true);

        var available = ImGui.GetContentRegionAvail().X;
        var buttonWidth = (available - spacing.X * (Columns - 1)) / Columns;
        var buttonSize = new Vector2(buttonWidth, ButtonHeight);

        for (var i = 0; i < items.Length; i++)
        {
            var itemClicked = DrawItem(items[i], buttonSize, disabledReason?.Invoke(items[i].Id));
            if (clicked is null && itemClicked is not null)
                clicked = itemClicked;

            if (i < items.Length - 1 && (i + 1) % Columns != 0)
                ImGui.SameLine();
        }

        return clicked;
    }

    /// Grouped so ImGui treats the cell as one item; without it, the next SameLine anchors to the label and cells drift.
    private static string? DrawItem((string Id, FontAwesomeIcon Icon, string Tooltip) item, Vector2 size, string? disabledReason)
    {
        string? result = null;
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.TileBg))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Theme.TileBgHover))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Theme.TileRounding))
        {
            // Wraps the whole cell so icon and label dim with the button.
            using (ImRaii.Disabled(disabledReason is not null))
            {
                ImGui.BeginGroup();
                var start = ImGui.GetCursorPos();
                if (ImGui.Button($"##{item.Id}", size))
                    result = item.Id;

                ImGui.SetCursorPos(start + new Vector2(8f, size.Y / 2 - 8f));
                using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
                    ImGui.TextUnformatted(item.Icon.ToIconString());
                ImGui.SameLine();
                ImGui.TextUnformatted(item.Tooltip);
                ImGui.EndGroup();
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(disabledReason is null ? item.Tooltip : $"{item.Tooltip} - unavailable. {disabledReason}");
        }

        return result;
    }
}
