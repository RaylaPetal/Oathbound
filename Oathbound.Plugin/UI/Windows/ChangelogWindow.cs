using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Oathbound.Plugin.UI;

/// What changed, newest first. Opens by itself once per update to a version with notes.
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
        if (isUpdate && Changelog.Entries.Any(e => e.Version == current))
            IsOpen = true;
    }

    public void Dispose() { }

    public override void PreDraw() => Theme.PushWindowStyle();
    public override void PostDraw() => Theme.PopWindowStyle();

    public override void Draw()
    {
        IconGlyph.Text(FontAwesomeIcon.Gift, $"You're on {CurrentVersion}");
        ImGui.Separator();

        var footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        using (var list = ImRaii.Child("##changelogList", new Vector2(-1, -footer)))
        {
            if (list)
            {
                var first = true;
                foreach (var entry in Changelog.Entries)
                {
                    if (first)
                        ImGui.SetNextItemOpen(true, ImGuiCond.Appearing);
                    first = false;
                    if (!ImGui.CollapsingHeader($"{entry.Version}  -  {entry.Title}  ({entry.Date})##changelog{entry.Version}"))
                        continue;
                    foreach (var section in entry.Sections)
                    {
                        Section.SubHeading(section.Heading);
                        foreach (var item in section.Items)
                        {
                            ImGui.Bullet();
                            ImGui.SameLine();
                            ImGui.TextWrapped(item);
                        }
                    }
                    ImGui.Spacing();
                }
            }
        }

        if (ImGui.Button("Got it"))
            IsOpen = false;
    }
}
