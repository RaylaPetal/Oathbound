using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace Oathbound.Plugin.UI;

/// The default UI font has no FontAwesome glyphs merged in, so icons must be drawn in UiBuilder.FontIcon separately.
public static class IconGlyph
{
    public static bool Button(FontAwesomeIcon icon, Vector2 size)
    {
        using var font = ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
        return ImGui.Button(icon.ToIconString(), size);
    }

    public static void Text(FontAwesomeIcon icon, string label)
    {
        using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
            ImGui.TextUnformatted(icon.ToIconString());
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
    }

    /// A favorite star the size of a square button. The icon font is taller than the text font, so the glyph is drawn
    /// shrunk inside the box instead of as a button label.
    public static bool Star(string id, bool on, string tooltip)
    {
        var height = ImGui.GetFrameHeight();
        var size = new Vector2(height, height);
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, ImGui.GetColorU32(hovered ? ImGuiCol.ButtonHovered : ImGuiCol.Button), ImGui.GetStyle().FrameRounding);

        var font = Plugin.PluginInterface.UiBuilder.FontIcon;
        var glyph = FontAwesomeIcon.Star.ToIconString();
        var fontSize = ImGui.GetTextLineHeight() * 0.8f;
        Vector2 glyphSize;
        using (ImRaii.PushFont(font))
            glyphSize = ImGui.CalcTextSize(glyph) * (fontSize / ImGui.GetFontSize());
        var color = on ? Theme.Warning : hovered ? Theme.TextMuted : Theme.TextMuted * new Vector4(1, 1, 1, 0.6f);
        draw.AddText(font, fontSize, pos + (size - glyphSize) / 2f, ImGui.GetColorU32(color), glyph);
        if (hovered)
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    public static void HelpMarker(string tooltip)
    {
        Layout.ContinueRowOrWrap(ImGui.CalcTextSize("(?)").X);
        ImGui.TextDisabled("(?)");
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 24f);
        ImGui.TextUnformatted(tooltip);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// TextColored doesn't wrap on its own.
    public static void WrappedColored(Vector4 color, string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    /// TextDisabled doesn't wrap on its own either.
    public static void WrappedDisabled(string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(text);
        ImGui.PopTextWrapPos();
    }
}
