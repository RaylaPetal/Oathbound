using System;
using System.Numerics;
using Oathbound.Plugin.Config;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Oathbound.Plugin.UI;

/// First-run window, shown before the main window needs Role/pairing state. Writes the same config fields Settings uses.
public sealed class WelcomeWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string triggerPhraseInput = "";
    private static readonly string[] RoleNames = ["Sub", "Owner", "Switch"];

    public WelcomeWindow(Plugin plugin) : base("Welcome to Oathbound###OathboundWelcome")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse;
        Size = new Vector2(480, 320);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 280), MaximumSize = new Vector2(700, 500) };
    }

    public void Dispose() { }

    public override void OnOpen() => triggerPhraseInput = plugin.Configuration.TriggerPhrase;

    public override void PreDraw() => Theme.PushWindowStyle();
    public override void PostDraw() => Theme.PopWindowStyle();

    public override void Draw()
    {
        var config = plugin.Configuration;

        ImGui.TextWrapped("Welcome! Before you get started, choose your role and the trigger phrase your commands will use - you can change either later in Settings.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var roleIndex = config.Role switch { PluginRole.Owner => 1, PluginRole.Switch => 2, _ => 0 };
        if (ImGui.Combo("Role", ref roleIndex, RoleNames, RoleNames.Length))
            config.Role = roleIndex switch { 1 => PluginRole.Owner, 2 => PluginRole.Switch, _ => PluginRole.Sub };
        IconGlyph.HelpMarker("Sub reacts to trigger tells and applies commands locally - only a Sub-side pairing actually gates anything. Owner is mostly informational. Switch can be both at once. You can change this later in Settings.");

        ImGui.Spacing();
        ImGui.InputTextWithHint("Trigger phrase", "e.g. command", ref triggerPhraseInput, 32);
        IconGlyph.HelpMarker("The word that must start every ongoing command tell, e.g. \"command strip\".");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        IconGlyph.WrappedDisabled("After you continue, a short guided tour shows you around. Every module also has its own tour behind the ? in its title bar.");

        if (ImGui.Button("Continue"))
        {
            config.TriggerPhrase = triggerPhraseInput;
            config.HasCompletedWelcome = true;
            config.Save();
            IsOpen = false;
            plugin.Tutorial.StartOverviewIfUnseen(config.Role);
        }
    }
}
