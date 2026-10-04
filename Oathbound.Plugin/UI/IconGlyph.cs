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
