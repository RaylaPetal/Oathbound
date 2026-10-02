using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Oathbound.Plugin.UI;

/// A labeled, bordered group inside a tab. A real child window, sized from the content height measured last frame
/// (these bindings predate AutoResizeY), so its very first frame may look clipped.
public sealed class SectionScope : IDisposable
{
    private const float FirstFrameHeight = 40f;
    private static readonly Dictionary<uint, float> MeasuredHeights = new();

    private readonly uint key;
    private readonly string id;
    private readonly bool contentsDrawn;
    private bool disposed;

    internal SectionScope(string id, string? heading)
    {
        this.id = id;
        var childId = $"##section_{id}";
        key = ImGui.GetID(childId);
        var height = MeasuredHeights.TryGetValue(key, out var measured) ? measured : FirstFrameHeight;

        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.TileRounding);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.SectionBg);
        contentsDrawn = ImGui.BeginChild(childId, new Vector2(0, height), true,
            ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (heading is not null)
            Section.Heading(heading);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        // A section scrolled out of view gets its items skipped; measuring then would shrink it and cause scroll jitter.
        if (contentsDrawn)
        {
            // Swap the last item's trailing ItemSpacing for the bottom WindowPadding.
            var style = ImGui.GetStyle();
            MeasuredHeights[key] = ImGui.GetCursorPosY() - style.ItemSpacing.Y + style.WindowPadding.Y;
        }

        ImGui.EndChild();
        // The child is now the last item, so a tour step can outline the whole section.
        TutorialService.Anchor(TutorialAnchors.Section(id));
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        ImGui.Spacing();
    }
}

/// A scrolling list sized to its content, capped at `maxHeight` or the room left in the window.
public sealed class ListScope : IDisposable
{
    private const float MinHeight = 60f;
    private static readonly Dictionary<uint, float> MeasuredHeights = new();

    private readonly uint key;
    private readonly bool contentsDrawn;
    private bool disposed;

    internal ListScope(string id, float? maxHeight)
    {
        var childId = $"##list_{id}";
        key = ImGui.GetID(childId);
        // Inside a Section, "room left" is the list's own last height and the two would shrink each other.
        // Otherwise leave room for the spacing after the child, or the parent gains a scrollbar.
        var cap = Math.Max(MinHeight, maxHeight ?? ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().ItemSpacing.Y);
        var height = MeasuredHeights.TryGetValue(key, out var measured) ? Math.Min(measured, cap) : cap;
        contentsDrawn = ImGui.BeginChild(childId, new Vector2(0, Math.Max(MinHeight, height)), true);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        // Content space, so this is the full list height.
        if (contentsDrawn)
        {
            var style = ImGui.GetStyle();
            MeasuredHeights[key] = ImGui.GetCursorPosY() - style.ItemSpacing.Y + style.WindowPadding.Y;
        }
        ImGui.EndChild();
    }
}

public static class Section
{
    public static SectionScope Begin(string id, string? heading = null) => new(id, heading);

    public static ListScope List(string id, float? maxHeight = null) => new(id, maxHeight);

    public static void Heading(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.AccentHover);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
        ImGui.Separator();
    }

    public static void SubHeading(string text)
    {
        ImGui.Spacing();
        Heading(text);
    }
}
