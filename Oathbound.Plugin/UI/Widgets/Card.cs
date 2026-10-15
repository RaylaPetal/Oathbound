using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Oathbound.Plugin.UI;

/// Raw ImGui calls rather than ImRaii, whose ref-struct scopes can't be boxed into one IDisposable.
public sealed class CardScope : IDisposable
{
    private bool disposed;

    internal CardScope(string id, Vector2 size, ImGuiWindowFlags flags)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.CardRounding);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.CardBg);
        ImGui.BeginChild(id, size, true, flags);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        ImGui.EndChild();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }
}

public static class Card
{
    /// `noScroll` guarantees no scrollbar on pure-chrome cards even if the fixed height is a few pixels tight.
    public static CardScope Begin(string id, Vector2 size = default, bool noScroll = false)
    {
        var flags = noScroll ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None;
        return new CardScope(id, size, flags);
    }
}
