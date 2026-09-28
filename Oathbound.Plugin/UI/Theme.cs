using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Oathbound.Plugin.UI;

/// One place to tune the plugin's look - every window/widget reads colors and rounding from here instead
/// of hardcoding its own, so the dark/card-based style (loosely inspired by GagSpeak's layout: a status
/// bar, a status card, a grid of module tiles) stays consistent as more UI gets added later.
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
    /// Settings dependency header: a clearly-red "not detected" status dot (Danger is too muted to read as red).
    public static readonly Vector4 StatusMissing = new(0.92f, 0.32f, 0.32f, 1f);

    public const float CardRounding = 8f;
    public const float TileRounding = 6f;

    /// Tint for a Section block - a step lighter than CardBg so sections read as raised inside a card.
    public static readonly Vector4 SectionBg = new(0.16f, 0.15f, 0.20f, 1f);

    /// Purple chrome shared by every Oathbound window: window/child/card borders, table borders, separators
    /// and the title bar, so every border in the plugin reads as the same accent instead of Dalamud's default
    /// grey/red. Pushed in each window's PreDraw and popped in PostDraw - windows opened while it's pushed
    /// (combos, popups) inherit it too.
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
