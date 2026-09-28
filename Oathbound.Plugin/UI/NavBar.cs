using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace Oathbound.Plugin.UI;

/// The persistent icon+label grid used to switch between modules (Title/Wardrobe/Gesture/Follow/...) - the
/// "scalable" navigation surface: a new module is one more entry in the caller's array, not a wider grid or
/// another accordion buried in the window body. collar/ui-organization "Navigation shows an icon and a
/// visible label, wrapped into rows" (redesign-nav-and-modules): three buttons per row, wrapping to a new
/// row past that - replaces the old single-row, icon-only, tooltip-labeled strip. No "active" highlight -
/// every destination opens its own window or popup now rather than swapping inline content, so there's no
/// longer a single "current" entry to distinguish from the rest.
public static class NavBar
{
    private const int Columns = 3;
    private const float ButtonHeight = 36f;

    /// The exact height `Draw` will reserve for `itemCount` items, computed from the same live style values
    /// `Draw` itself uses - callers (`CollarWindow`'s minimum-size calculation) can use this instead of a
    /// separately-guessed constant that could silently drift out of sync with this file's own layout.
    public static float RequiredHeight(int itemCount)
    {
        var rows = (itemCount + Columns - 1) / Columns;
        var spacing = ImGui.GetStyle().ItemSpacing;
        var padding = ImGui.GetStyle().WindowPadding;
        return rows * ButtonHeight + (rows - 1) * spacing.Y + padding.Y * 2;
    }

    /// `disabledReason` (collar/ui-organization dependency gating): when it returns non-null for an item's id, that
    /// tile is drawn dimmed, can't be clicked, and shows the reason as its tooltip.
    public static string? Draw((string Id, FontAwesomeIcon Icon, string Tooltip)[] items, System.Func<string, string?>? disabledReason = null)
    {
        string? clicked = null;
        // The top/bottom margin inside the card's child region comes from WindowPadding, not ItemSpacing -
        // using ItemSpacing here under-reserved that margin and clipped the last row's bottom edge.
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

    /// Composes an icon (icon font) and a label (default font) into one clickable cell: a full-size button
    /// first for the click target/background/hover styling, then the icon+label drawn on top - all inside
    /// one `BeginGroup`/`EndGroup` pair, which is what makes Dear ImGui treat the whole composite as a
    /// single item afterward. Without the group, `SameLine()`/layout for the *next* button ends up anchored
    /// to whatever the last widget drawn here was (the small label text), not the full button cell - that
    /// mismatch is what caused every button after the first to drift diagonally off its actual cell.
    /// IconGlyph's icon+text helpers can't be reused directly here since neither draws a clickable
    /// background sized to a fixed grid cell in the first place.
    private static string? DrawItem((string Id, FontAwesomeIcon Icon, string Tooltip) item, Vector2 size, string? disabledReason)
    {
        string? result = null;
        using (ImRaii.PushColor(ImGuiCol.Button, Theme.TileBg))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, Theme.TileBgHover))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Theme.TileRounding))
        {
            // Disabled wraps the whole cell so the icon and label dim along with the button.
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
