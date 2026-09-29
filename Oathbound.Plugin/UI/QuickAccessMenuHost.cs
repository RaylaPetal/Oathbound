using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Oathbound.Plugin.UI;

/// Invisible, always-open host for QuickAccessMenu's popup. The menu is opened from the server info bar entry
/// (whose click callback can run outside any ImGui frame), so every real popup call has to happen from one
/// consistent, always-valid window context every frame - this window is that context and draws nothing of its
/// own. It used to also be the on-screen quick-access button, which was removed in favor of the server info
/// bar entry.
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

    public override void Draw() => QuickAccessMenu.Draw(plugin);
}
