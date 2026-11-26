using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Oathbound.Plugin.UI;

/// What changed in the current release. Opens by itself once per update to a version with notes.
public sealed class ChangelogWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public ChangelogWindow(Plugin plugin) : base("What's new in Oathbound###OathboundChangelog")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(520, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(360, 300), MaximumSize = new Vector2(900, 1200) };
    }

    public static string CurrentVersion => Plugin.PluginInterface.Manifest.AssemblyVersion.ToString();

    /// Called once at load: a fresh install gets the welcome instead, and counts this version as seen.
    public void ShowIfUpdated()
    {
        var config = plugin.Configuration;
        var current = CurrentVersion;
        if (config.LastSeenChangelog == current)
            return;
        var isUpdate = config.HasCompletedWelcome;
        config.LastSeenChangelog = current;
        config.Save();
        if (isUpdate && Changelog.Latest.Version == current)
            IsOpen = true;
    }

    public void Dispose() { }

    public override void PreDraw() => Theme.PushWindowStyle();
    public override void PostDraw() => Theme.PopWindowStyle();

    public override void Draw()
    {
        var entry = Changelog.Latest;
        var footer = ImGui.GetFrameHeightWithSpacing();
        using (Card.Begin("changelogCard", new Vector2(0, -footer)))
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Theme.AccentHover))
                IconGlyph.Text(FontAwesomeIcon.Gift, $"{entry.Version}  -  {entry.Title}");
            ImGui.SameLine();
            ImGui.TextDisabled(entry.Date);
            if (entry.Version != CurrentVersion)
                IconGlyph.WrappedDisabled($"You're on {CurrentVersion}.");
            ImGui.Spacing();

            for (var i = 0; i < entry.Sections.Length; i++)
            {
                var section = entry.Sections[i];
                using (Section.Begin($"changelog{i}", section.Heading))
                {
                    foreach (var item in section.Items)
                    {
                        ImGui.Bullet();
                        ImGui.SameLine();
                        ImGui.TextWrapped(item);
                    }
                }
            }
        }

        using (Theme.PushPrimaryButton())
        {
            if (ImGui.Button("Got it"))
                IsOpen = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Join the Oathbound Discord"))
            Dalamud.Utility.Util.OpenLink(SupportLinks.DiscordUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Updates and help\ndiscord.gg/87eMbV4FU9");
    }
}
