using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace Oathbound.Plugin.UI;

/// A "Support me" label with small Ko-fi, Patreon and Discord icon buttons, on one line in the header.
public static class SupportLinks
{
    private const string KofiUrl = "https://ko-fi.com/raylapetal";
    private const string PatreonUrl = "https://www.patreon.com/RaylaPetal";
    public const string DiscordUrl = "https://discord.gg/87eMbV4FU9";
    private const string Label = "Support me";

    private static readonly Vector4 White = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 KofiBlue = new(0.161f, 0.671f, 0.878f, 1f);
    private static readonly Vector4 KofiBlueHovered = new(0.29f, 0.75f, 0.93f, 1f);
    private static readonly Vector4 PatreonCoral = new(1f, 0.259f, 0.302f, 1f);
    private static readonly Vector4 PatreonCoralHovered = new(1f, 0.4f, 0.44f, 1f);
    private static readonly Vector4 DiscordBlurple = new(0.345f, 0.396f, 0.949f, 1f);
    private static readonly Vector4 DiscordBlurpleHovered = new(0.45f, 0.49f, 0.97f, 1f);

    private static float ButtonSize => ImGui.GetFrameHeight();
    private static float IconsWidth => ButtonSize * 3 + ImGui.GetStyle().ItemSpacing.X * 2;

    public static float Width => ImGui.CalcTextSize(Label).X + ImGui.GetStyle().ItemSpacing.X + IconsWidth;
    public static float Height => ButtonSize;

    /// Drawn at the cursor, label then icons on one line.
    public static void Draw()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(Label);
        ImGui.SameLine();
        if (IconButton("kofi", FontAwesomeIcon.MugHot, KofiBlue, KofiBlueHovered))
            Dalamud.Utility.Util.OpenLink(KofiUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Support Oathbound on Ko-fi\nko-fi.com/raylapetal");

        ImGui.SameLine();
        if (IconButton("patreon", FontAwesomeIcon.HandHoldingHeart, PatreonCoral, PatreonCoralHovered))
            Dalamud.Utility.Util.OpenLink(PatreonUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Support Oathbound on Patreon\npatreon.com/RaylaPetal");

        ImGui.SameLine();
        // Dalamud's FontAwesome set has no brand icons, so Discord gets a chat bubble in its own color.
        if (IconButton("discord", FontAwesomeIcon.Comments, DiscordBlurple, DiscordBlurpleHovered))
            Dalamud.Utility.Util.OpenLink(DiscordUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Join the Oathbound Discord - updates and help\ndiscord.gg/87eMbV4FU9");
    }

    private static bool IconButton(string id, FontAwesomeIcon icon, Vector4 bg, Vector4 bgHovered)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, bg).Push(ImGuiCol.ButtonHovered, bgHovered).Push(ImGuiCol.ButtonActive, bgHovered);
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Theme.TileRounding);
        return IconGlyph.CenteredButton($"support_{id}", icon, ButtonSize, White);
    }
}
