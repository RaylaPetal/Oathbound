using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace Oathbound.Plugin.UI;

/// The nav grid's last row: a "Support" label cell, then Ko-fi and Patreon tiles.
public static class SupportLinks
{
    private const string KofiUrl = "https://ko-fi.com/raylapetal";
    private const string PatreonUrl = "https://www.patreon.com/RaylaPetal";

    private static readonly Vector4 White = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 KofiRed = new(1f, 0.369f, 0.357f, 1f);
    private static readonly Vector4 KofiRedHovered = new(1f, 0.47f, 0.46f, 1f);
    private static readonly Vector4 PatreonDark = new(0.08f, 0.08f, 0.09f, 1f);
    private static readonly Vector4 PatreonDarkHovered = new(0.2f, 0.2f, 0.22f, 1f);

    public static void DrawRow(Vector2 cellSize)
    {
        DrawLabelCell(cellSize);

        ImGui.SameLine();
        if (NavBar.DrawTile(("kofi", FontAwesomeIcon.MugHot, "Ko-fi"), cellSize, null, KofiRed, KofiRedHovered, White) is not null)
            Dalamud.Utility.Util.OpenLink(KofiUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Support Oathbound on Ko-fi\nko-fi.com/raylapetal");

        ImGui.SameLine();
        if (NavBar.DrawTile(("patreon", FontAwesomeIcon.Heart, "Patreon"), cellSize, null, PatreonDark, PatreonDarkHovered, White) is not null)
            Dalamud.Utility.Util.OpenLink(PatreonUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Support Oathbound on Patreon\npatreon.com/RaylaPetal");
    }

    /// Not a button. Drawn straight to the draw list so only the Dummy counts as the item: text wider than the cell
    /// would otherwise widen it and push the tiles off the grid's columns.
    private static void DrawLabelCell(Vector2 size)
    {
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(size);
        var drawList = ImGui.GetWindowDrawList();
        var pos = origin + new Vector2(Layout.Scaled(8f), size.Y / 2 - Layout.Scaled(8f));
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
        {
            var icon = FontAwesomeIcon.HandHoldingHeart.ToIconString();
            drawList.AddText(pos, ImGui.GetColorU32(KofiRed), icon);
            pos.X += ImGui.CalcTextSize(icon).X + spacing;
        }
        drawList.AddText(pos, ImGui.GetColorU32(ImGuiCol.Text), "Support me");
    }
}
