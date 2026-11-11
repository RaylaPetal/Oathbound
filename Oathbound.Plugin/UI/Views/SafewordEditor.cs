using System;
using System.Numerics;
using Oathbound.Plugin.Config;
using Dalamud.Bindings.ImGui;

namespace Oathbound.Plugin.UI;

internal static class SafewordEditor
{
    /// In SafewordMood order.
    public static readonly string[] MoodNames = ["I'm okay, just stop", "Please check on me"];

    public static void Draw(PluginConfig config, string id, ref bool reveal, string? help = null)
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
        if (help is not null)
            IconGlyph.HelpMarker(help);

        // Sub view only: an Owner has no Owners to tell. A safety setting, not an escape, so it stays editable while caged.
        if (config.ResolveActiveDirection() == PairingDirection.SubSide)
        {
            var notify = config.NotifyOwnersOnSafeword;
            if (ImGui.Checkbox("Tell my Owners when I use my safeword", ref notify))
            {
                config.NotifyOwnersOnSafeword = notify;
                config.Save();
            }
            IconGlyph.HelpMarker("Sends each of your Owners a short tell, and leaves a note on the relay for an Owner who's offline. It carries only the choice below - never any other text.");
            if (notify)
            {
                var mood = (int)config.SafewordMood;
                ImGui.SetNextItemWidth(Math.Max(1f, Math.Min(260f, ImGui.GetContentRegionAvail().X)));
                if (ImGui.Combo("##safewordMood", ref mood, MoodNames, MoodNames.Length))
                {
                    config.SafewordMood = (SafewordMood)mood;
                    config.Save();
                }
            }
        }
        ImGui.PopID();
    }
}
