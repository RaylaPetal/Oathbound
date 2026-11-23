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

        ImGui.TextWrapped("Welcome! Before you get started, choose your role and the trigger phrase that starts your chat Reactions - you can change either later in Settings.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var roleIndex = config.Role switch { PluginRole.Owner => 1, PluginRole.Switch => 2, _ => 0 };
        if (ImGui.Combo("Role", ref roleIndex, RoleNames, RoleNames.Length))
            config.Role = roleIndex switch { 1 => PluginRole.Owner, 2 => PluginRole.Switch, _ => PluginRole.Sub };
        IconGlyph.HelpMarker("Sub receives commands, Owner sends them, Switch can do both. You can change this later.");

        ImGui.Spacing();
        ImGui.InputTextWithHint("Trigger phrase", "e.g. command", ref triggerPhraseInput, 32);
        IconGlyph.HelpMarker("Starts a chat phrase for your Reactions, e.g. \"collar kneel\". Commands from an Owner don't use it.");

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
