using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Oathbound.Plugin.UI;

/// Sections are child windows without scrollbars, so anything past the right edge is clipped unreachably.
public static class Layout
{
    private const float MinTextWidth = 120f;

    /// Dalamud scales Window Size/SizeConstraints itself, but not pixel values passed to ImGui directly.
    public static float Scaled(float px) => px * ImGuiHelpers.GlobalScale;

    /// Never wider than the space left, so a fixed width can't push past a narrow window's edge.
    public static void ItemWidth(float px) =>
        ImGui.SetNextItemWidth(Math.Max(1f, Math.Min(Scaled(px), ImGui.GetContentRegionAvail().X)));

    /// Ignores any "##id" suffix.
    public static float ButtonWidth(string visibleLabel) =>
        ImGui.CalcTextSize(visibleLabel).X + ImGui.GetStyle().FramePadding.X * 2f;

    public static float ActionsWidth(params string[] visibleLabels)
    {
        var width = 0f;
        foreach (var label in visibleLabels)
            width += ButtonWidth(label);
        return width + ImGui.GetStyle().ItemSpacing.X * Math.Max(0, visibleLabels.Length - 1);
    }

    /// Continues the row with SameLine() if the next control fits, otherwise wraps.
    public static void ContinueRowOrWrap(float nextControlWidth)
    {
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < nextControlWidth)
            ImGui.NewLine();
    }

    /// Draws `text` wrapped within the space left of `actionsWidth`, then leaves the cursor where the row's buttons
    /// go: beside the text, or on the next line once the text column would be too narrow to read.
    /// `draw` must use a wrap-honoring call (TextUnformatted/TextColored/TextDisabled/TextWrapped), not BulletText.
    public static void TextWithActions(string text, float actionsWidth, Action<string>? draw = null)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var startX = ImGui.GetCursorPosX();
        var textWidth = ImGui.GetContentRegionAvail().X - actionsWidth - spacing;
        var narrow = textWidth < Scaled(MinTextWidth);
        var fits = !narrow && ImGui.CalcTextSize(text).X <= textWidth;

        // One line beside the buttons: centre it on their height.
        if (fits)
            ImGui.AlignTextToFramePadding();
        ImGui.PushTextWrapPos(narrow ? 0f : startX + textWidth);
        if (draw is null)
            ImGui.TextUnformatted(text);
        else
            draw(text);
        ImGui.PopTextWrapPos();

        if (narrow)
            return;
        ImGui.SameLine();
        if (!fits)
            ImGui.SetCursorPosX(startX + textWidth + spacing);
    }
}
