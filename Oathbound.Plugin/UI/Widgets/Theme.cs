using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Oathbound.Plugin.UI;

/// Every window and widget reads colors and rounding from here.
public static class Theme
{
    public static readonly Vector4 Accent = new(0.62f, 0.38f, 0.85f, 1f);
    public static readonly Vector4 AccentHover = new(0.72f, 0.48f, 0.95f, 1f);

    public static readonly Vector4 CardBg = new(0.13f, 0.13f, 0.17f, 1f);
    public static readonly Vector4 TileBg = new(0.18f, 0.18f, 0.23f, 1f);
    public static readonly Vector4 TileBgHover = new(0.27f, 0.21f, 0.34f, 1f);

    /// Status colours, one meaning each (see Status): something failed or is missing.
    public static readonly Vector4 Error = new(0.92f, 0.32f, 0.32f, 1f);
    /// Needs the user's attention or a wait on someone else, but nothing failed.
    public static readonly Vector4 Caution = new(0.9f, 0.72f, 0.25f, 1f);
    /// A normal state or something in progress. Kept apart from the purple accent.
    public static readonly Vector4 Info = new(0.48f, 0.72f, 0.95f, 1f);
    /// Secondary explanation.
    public static readonly Vector4 Muted = new(0.62f, 0.62f, 0.68f, 1f);
    /// Done or connected.
    public static readonly Vector4 Ok = new(0.35f, 0.85f, 0.35f, 1f);
    /// Fill for a button that is asking to confirm an end, revert or delete. Not a status colour.
    public static readonly Vector4 Destructive = new(0.65f, 0.42f, 0.42f, 1f);
    /// Unread/attention dots.
    public static readonly Vector4 Notify = Error;

    public static readonly Vector4 TextMuted = Muted;

    public static readonly Vector4 Bronze = new(0.80f, 0.52f, 0.30f, 1f);
    public static readonly Vector4 Silver = new(0.78f, 0.80f, 0.86f, 1f);
    public static readonly Vector4 Gold = new(0.98f, 0.80f, 0.30f, 1f);

    public static Vector4 Tier(int tier) => tier switch { 0 => Bronze, 1 => Silver, _ => Gold };

    public const float CardRounding = 8f;
    public const float TileRounding = 6f;

    /// A step lighter than CardBg so sections read as raised inside a card.
    public static readonly Vector4 SectionBg = new(0.16f, 0.15f, 0.20f, 1f);

    /// A step darker than CardBg, with the title bar's purple tint, so cards read as raised on it.
    public static readonly Vector4 WindowBg = new(0.08f, 0.07f, 0.11f, 1f);

    /// Pushed in each window's PreDraw and popped in PostDraw; combos and popups opened meanwhile inherit it.
    private static readonly (ImGuiCol Col, Vector4 Color)[] WindowColors =
    [
        (ImGuiCol.WindowBg, WindowBg),
        (ImGuiCol.PopupBg, CardBg),
        (ImGuiCol.Button, TileBg),
        (ImGuiCol.ButtonHovered, TileBgHover),
        (ImGuiCol.ButtonActive, Accent with { W = 0.6f }),
        (ImGuiCol.FrameBg, TileBg),
        (ImGuiCol.FrameBgHovered, TileBgHover),
        (ImGuiCol.FrameBgActive, TileBgHover),
        (ImGuiCol.Header, TileBg),
        (ImGuiCol.HeaderHovered, TileBgHover),
        (ImGuiCol.HeaderActive, Accent with { W = 0.6f }),
        (ImGuiCol.CheckMark, AccentHover),
        (ImGuiCol.SliderGrab, Accent),
        (ImGuiCol.SliderGrabActive, AccentHover),
        (ImGuiCol.ScrollbarBg, WindowBg with { W = 0.6f }),
        (ImGuiCol.ScrollbarGrab, Accent with { W = 0.35f }),
        (ImGuiCol.ScrollbarGrabHovered, Accent with { W = 0.55f }),
        (ImGuiCol.ScrollbarGrabActive, Accent with { W = 0.75f }),
        (ImGuiCol.Tab, TileBg),
        (ImGuiCol.TabHovered, TileBgHover),
        (ImGuiCol.TabActive, Accent with { W = 0.6f }),
        (ImGuiCol.TabUnfocused, TileBg),
        (ImGuiCol.TabUnfocusedActive, Accent with { W = 0.4f }),
        (ImGuiCol.ResizeGrip, Accent with { W = 0.2f }),
        (ImGuiCol.ResizeGripHovered, Accent with { W = 0.5f }),
        (ImGuiCol.ResizeGripActive, Accent with { W = 0.75f }),
        (ImGuiCol.TextSelectedBg, Accent with { W = 0.4f }),
        (ImGuiCol.Border, Accent with { W = 0.55f }),
        (ImGuiCol.Separator, Accent with { W = 0.35f }),
        (ImGuiCol.TableBorderStrong, Accent with { W = 0.55f }),
        (ImGuiCol.TableBorderLight, Accent with { W = 0.25f }),
        (ImGuiCol.TitleBg, new Vector4(0.17f, 0.11f, 0.24f, 1f)),
        (ImGuiCol.TitleBgActive, new Vector4(0.30f, 0.17f, 0.43f, 1f)),
        (ImGuiCol.TitleBgCollapsed, new Vector4(0.17f, 0.11f, 0.24f, 1f)),
    ];

    private static readonly (ImGuiStyleVar Var, float Value)[] WindowVars =
    [
        (ImGuiStyleVar.WindowBorderSize, 1f),
        (ImGuiStyleVar.ChildBorderSize, 1f),
        (ImGuiStyleVar.FrameRounding, TileRounding),
        (ImGuiStyleVar.PopupRounding, TileRounding),
        (ImGuiStyleVar.ScrollbarRounding, TileRounding),
        (ImGuiStyleVar.GrabRounding, TileRounding),
    ];

    public static void PushWindowStyle()
    {
        foreach (var (col, color) in WindowColors)
            ImGui.PushStyleColor(col, color);
        foreach (var (var, value) in WindowVars)
            ImGui.PushStyleVar(var, value);
    }

    public static void PopWindowStyle()
    {
        ImGui.PopStyleVar(WindowVars.Length);
        ImGui.PopStyleColor(WindowColors.Length);
    }

    /// For a window's one confirm/close action. Same fill as Sub Control's send tiles.
    public static IDisposable PushPrimaryButton() => ImRaii.PushColor(ImGuiCol.Button, Accent with { W = 0.8f })
        .Push(ImGuiCol.ButtonHovered, AccentHover)
        .Push(ImGuiCol.ButtonActive, Accent);
}
