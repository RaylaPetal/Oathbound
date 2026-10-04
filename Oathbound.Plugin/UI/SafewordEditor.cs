using System;
using System.Numerics;
using Oathbound.Plugin.Config;
using Dalamud.Bindings.ImGui;

namespace Oathbound.Plugin.UI;

internal static class SafewordEditor
{
    public static void Draw(PluginConfig config, string id, ref bool reveal)
    {
        ImGui.PushID(id);
        var value = config.PanicSafeword ?? "";
        var revealWidth = ImGui.CalcTextSize(reveal ? "Hide" : "Show").X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SetNextItemWidth(Math.Max(1f, ImGui.GetContentRegionAvail().X - revealWidth - ImGui.GetStyle().ItemSpacing.X));
        var flags = reveal ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        if (ImGui.InputTextWithHint("##safeword", "Optional safeword", ref value, 32, flags))
        {
            config.PanicSafeword = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            config.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button(reveal ? "Hide" : "Show", new Vector2(revealWidth, 0)))
            reveal = !reveal;

        IconGlyph.WrappedColored(config.PanicSafeword is null ? Theme.TextMuted : Theme.Success,
            config.PanicSafeword is null ? "No safeword set — plain /obpanic remains available." : "Safeword configured for /obpanic.");
        ImGui.PopID();
    }
}
