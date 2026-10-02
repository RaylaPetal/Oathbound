using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Oathbound.Plugin.UI;

/// Every window and widget reads colors and rounding from here.
public static class Theme
{
    public static readonly Vector4 Accent = new(0.62f, 0.38f, 0.85f, 1f);
    public static readonly Vector4 AccentHover = new(0.72f, 0.48f, 0.95f, 1f);

    public static readonly Vector4 CardBg = new(0.13f, 0.13f, 0.17f, 1f);
    public static readonly Vector4 TileBg = new(0.18f, 0.18f, 0.23f, 1f);
    public static readonly Vector4 TileBgHover = new(0.27f, 0.21f, 0.34f, 1f);

    public static readonly Vector4 TextMuted = new(0.62f, 0.62f, 0.68f, 1f);
    public static readonly Vector4 Success = new(0.35f, 0.85f, 0.35f, 1f);
    public static readonly Vector4 Warning = new(0.9f, 0.72f, 0.25f, 1f);
    public static readonly Vector4 Danger = new(0.65f, 0.42f, 0.42f, 1f);
    /// Danger is too muted to read as red.
    public static readonly Vector4 StatusMissing = new(0.92f, 0.32f, 0.32f, 1f);

    public const float CardRounding = 8f;
    public const float TileRounding = 6f;

    /// A step lighter than CardBg so sections read as raised inside a card.
    public static readonly Vector4 SectionBg = new(0.16f, 0.15f, 0.20f, 1f);

    /// Pushed in each window's PreDraw and popped in PostDraw; combos and popups opened meanwhile inherit it.
    private static readonly (ImGuiCol Col, Vector4 Color)[] WindowColors =
    [
        (ImGuiCol.Border, Accent with { W = 0.55f }),
        (ImGuiCol.Separator, Accent with { W = 0.35f }),
        (ImGuiCol.TableBorderStrong, Accent with { W = 0.55f }),
        (ImGuiCol.TableBorderLight, Accent with { W = 0.25f }),
        (ImGuiCol.TitleBg, new Vector4(0.17f, 0.11f, 0.24f, 1f)),
        (ImGuiCol.TitleBgActive, new Vector4(0.30f, 0.17f, 0.43f, 1f)),
        (ImGuiCol.TitleBgCollapsed, new Vector4(0.17f, 0.11f, 0.24f, 1f)),
    ];

    public static void PushWindowStyle()
    {
        foreach (var (col, color) in WindowColors)
            ImGui.PushStyleColor(col, color);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
    }

    public static void PopWindowStyle()
    {
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(WindowColors.Length);
    }
}
