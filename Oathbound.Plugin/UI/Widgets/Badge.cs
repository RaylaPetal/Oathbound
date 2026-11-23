using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Oathbound.Plugin.UI;

/// The small round attention mark on the top-right corner of the last drawn item (Chat's unread count, the Rulebook
/// tile's "waiting for you").
public static class Badge
{
    public static void OnLastItem(string text)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var textSize = ImGui.CalcTextSize(text);
        var radius = MathF.Max(textSize.Y * 0.5f, textSize.X * 0.5f + Layout.Scaled(2f));
        var center = new Vector2(max.X - Layout.Scaled(2f), min.Y + Layout.Scaled(2f));
        var draw = ImGui.GetWindowDrawList();
        draw.AddCircleFilled(center, radius, ImGui.GetColorU32(Theme.Notify));
        draw.AddText(center - textSize / 2, ImGui.GetColorU32(new Vector4(1, 1, 1, 1)), text);
    }
}
