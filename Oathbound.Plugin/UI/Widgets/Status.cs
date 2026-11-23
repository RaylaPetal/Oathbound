using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Oathbound.Plugin.UI;

/// Error: something failed or is missing. Caution: needs the user's attention, nothing failed. Info: a normal state or
/// something in progress. Muted: secondary explanation. Ok: done or connected.
public enum StatusKind
{
    Error,
    Caution,
    Info,
    Muted,
    Ok,
}

/// A result whose kind is decided where it's known, never guessed from its text.
public sealed record StatusMessage(StatusKind Kind, string Text)
{
    public static StatusMessage Error(string text) => new(StatusKind.Error, text);
    public static StatusMessage Ok(string text) => new(StatusKind.Ok, text);
    public static StatusMessage Info(string text) => new(StatusKind.Info, text);
}

/// Every status line goes through here, so one kind looks the same in every window.
public static class Status
{
    public static Vector4 Color(StatusKind kind) => kind switch
    {
        StatusKind.Error => Theme.Error,
        StatusKind.Caution => Theme.Caution,
        StatusKind.Info => Theme.Info,
        StatusKind.Ok => Theme.Ok,
        _ => Theme.Muted,
    };

    /// Wrapped to the window. Muted uses the disabled-text style, like the rest of the plugin's secondary text.
    public static void Line(StatusKind kind, string text)
    {
        if (kind == StatusKind.Muted)
        {
            IconGlyph.WrappedDisabled(text);
            return;
        }
        IconGlyph.WrappedColored(Color(kind), text);
    }

    public static void Line(StatusMessage? message)
    {
        if (message is not null)
            Line(message.Kind, message.Text);
    }

    /// Unwrapped, for text that continues a row.
    public static void Inline(StatusKind kind, string text)
    {
        if (kind == StatusKind.Muted)
            ImGui.TextDisabled(text);
        else
            ImGui.TextColored(Color(kind), text);
    }

    /// A small rounded label on the current line, e.g. "locked 42m".
    public static void Pill(StatusKind kind, string text)
    {
        ImGui.SameLine();
        var pos = ImGui.GetCursorScreenPos();
        var size = ImGui.CalcTextSize(text);
        var padX = Layout.Scaled(5);
        var box = new Vector2(size.X + padX * 2, ImGui.GetTextLineHeight());
        ImGui.GetWindowDrawList().AddRectFilled(pos, pos + box, ImGui.GetColorU32(Theme.TileBg), box.Y / 2);
        ImGui.GetWindowDrawList().AddText(pos + new Vector2(padX, 0), ImGui.GetColorU32(Color(kind)), text);
        ImGui.Dummy(box);
    }
}
