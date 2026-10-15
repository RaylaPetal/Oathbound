using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Oathbound.Plugin.UI;

/// The "what's active" strip at the top of a module window. The background is drawn behind the content afterwards
/// (draw-list channels), so the banner is exactly as tall as what's in it.
public static class ActiveBanner
{
    /// `body` returns false when nothing is active. `caption` is the Owner's "last sent by you" note.
    /// `anchorId` lets a guided tour point at the banner like a section.
    public static void Draw(string anchorId, string heading, Func<bool> body, string? caption = null, Action? clear = null)
    {
        var draw = ImGui.GetWindowDrawList();
        var width = ImGui.GetContentRegionAvail().X;
        var pad = Layout.Scaled(6);
        var start = ImGui.GetCursorScreenPos();

        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.SetCursorScreenPos(start + new Vector2(pad, pad));
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - pad * 2);
        ImGui.TextColored(Theme.AccentHover, heading);
        ImGui.Indent(pad);
        if (!body())
            ImGui.TextDisabled("Nothing active.");
        ImGui.Unindent(pad);
        if (caption is not null)
            ImGui.TextDisabled(caption);
        if (clear is not null)
        {
            if (ImGui.Button("Clear estimate##banner"))
                clear();
        }
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        var bottom = ImGui.GetItemRectMax().Y + pad;
        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(start, new Vector2(start.X + width, bottom), ImGui.GetColorU32(Theme.SectionBg), Layout.Scaled(6));
        draw.ChannelsMerge();
        TutorialService.AnchorRect(TutorialAnchors.Section(anchorId), start, new Vector2(start.X + width, bottom));
        ImGui.SetCursorScreenPos(new Vector2(start.X, bottom));
        ImGui.Dummy(new Vector2(0, Layout.Scaled(4)));
    }

    /// A small rounded label on the current line, e.g. "locked 42m".
    public static void Pill(string text, Vector4? color = null)
    {
        ImGui.SameLine();
        var pos = ImGui.GetCursorScreenPos();
        var size = ImGui.CalcTextSize(text);
        var padX = Layout.Scaled(5);
        var box = new Vector2(size.X + padX * 2, ImGui.GetTextLineHeight());
        ImGui.GetWindowDrawList().AddRectFilled(pos, pos + box, ImGui.GetColorU32(Theme.TileBg), box.Y / 2);
        ImGui.GetWindowDrawList().AddText(pos + new Vector2(padX, 0), ImGui.GetColorU32(color ?? Theme.TextMuted), text);
        ImGui.Dummy(box);
    }
}
