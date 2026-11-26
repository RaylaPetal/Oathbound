using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Oathbound.Plugin.UI;

/// Invisible, always-open host for QuickAccessMenu's popup: its opener can run outside any ImGui frame, so every
/// popup call has to come from this one consistent window context.
public sealed class QuickAccessMenuHost : Window, IDisposable
{
    private readonly Plugin plugin;

    public QuickAccessMenuHost(Plugin plugin) : base("###CollarQuickAccessMenuHost")
    {
        this.plugin = plugin;
        IsOpen = true;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoInputs |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
                ImGuiWindowFlags.NoBringToFrontOnFocus;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().Pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(Vector2.One, ImGuiCond.Always);
    }

    /// Pushed inside Draw rather than PreDraw so the invisible host itself never gets a border; the popup begun here
    /// reads the style at its own Begin.
    public override void Draw()
    {
        Theme.PushWindowStyle();
        QuickAccessMenu.Draw(plugin);
        Theme.PopWindowStyle();
    }
}
