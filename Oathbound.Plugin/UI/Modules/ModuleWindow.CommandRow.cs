using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Commands;
using static Oathbound.Plugin.UI.Layout;

namespace Oathbound.Plugin.UI;

/// Module-wide commands and fixed words as one compact row at the top of a module.
public sealed partial class ModuleWindow
{
    private readonly record struct RowCommand(string Label, string Command, string FavoriteId, string? Help = null);

    private readonly record struct FixedWordInfo(string Label, string Word, string Help);

    /// Each command is one send button with a favorite star; right-click a button to copy its command.
    private void DrawCommandRow(string sectionId, bool canSend, params RowCommand[] commands)
    {
        using var box = Section.Begin(sectionId);
        DrawCommandButtons(canSend, commands);
    }

    private void DrawCommandButtons(bool canSend, params RowCommand[] commands)
    {
        var first = true;
        foreach (var c in commands)
        {
            var messages = plugin.ChatComposer.ComposeAll(OwnerMoodleOverride.ForSend(plugin.Configuration, c.Command, null));
            var fits = ChatComposer.AllFit(messages);
            if (!first)
            {
                ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X * 2);
                if (ImGui.GetContentRegionAvail().X < ButtonWidth(c.Label) + StarWidth + ImGui.GetStyle().ItemInnerSpacing.X)
                    ImGui.NewLine();
            }
            first = false;

            ImGui.BeginGroup();
            using (ImRaii.Disabled(!canSend || !fits))
            {
                if (ImGui.Button($"{c.Label}##cmdrow_{c.FavoriteId}"))
                    plugin.ChatSender.SendAll(messages);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip((c.Help is null ? "" : c.Help + "\n") + (canSend ? string.Join("\n", messages) : "No /tell target yet - pairing hasn't captured your Sub's name.")
                    + "\nRight-click to copy.");
            if (ImGui.BeginPopupContextItem($"cmdrowCopy_{c.FavoriteId}"))
            {
                using (ImRaii.Disabled(!fits || messages.Count > 1))
                    if (ImGui.Selectable("Copy command"))
                        ImGui.SetClipboardText(messages[0]);
                ImGui.EndPopup();
            }
            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
            DrawFavoriteStar(c.FavoriteId);
            ImGui.EndGroup();
            TutorialService.Anchor(TutorialAnchors.Fixed(c.FavoriteId));
        }
    }

    private void DrawFavoriteStar(string favoriteId)
    {
        var favorites = plugin.Configuration.QuickCommands.FavoriteFixedActions;
        var isFavorite = favorites.Contains(favoriteId);
        if (IconGlyph.Star($"##fav_{favoriteId}", isFavorite, isFavorite ? "Remove from favorites" : "Add to favorites"))
        {
            if (isFavorite) favorites.Remove(favoriteId);
            else favorites.Add(favoriteId);
            plugin.Configuration.Save();
        }
    }

    /// The Sub's fixed words, read-only, inline with their help.
    private static void DrawFixedWordRow(string sectionId, params FixedWordInfo[] words)
    {
        using var box = Section.Begin(sectionId);
        ImGui.TextDisabled("Fixed words:");
        foreach (var w in words)
        {
            ContinueRowOrWrap(ImGui.CalcTextSize($"{w.Label} {w.Word} (?)").X + ImGui.GetStyle().ItemSpacing.X * 2);
            ImGui.TextUnformatted(w.Label);
            ImGui.SameLine();
            ImGui.TextColored(Theme.AccentHover, w.Word);
            IconGlyph.HelpMarker(w.Help);
        }
    }

    private readonly record struct OptionRow(string Label, string Help, Action<float> Control);

    /// Label beside control, so options line up whatever their label lengths. Each control gets the width left
    /// after its help marker.
    private static void DrawOptionRows(string id, params OptionRow[] rows)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingFixedFit))
            return;
        ImGui.TableSetupColumn("label", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("control", ImGuiTableColumnFlags.WidthStretch);
        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(row.Label);
            ImGui.TableNextColumn();
            ImGui.PushID(row.Label);
            row.Control(Math.Max(1f, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("(?)").X - ImGui.GetStyle().ItemSpacing.X));
            IconGlyph.HelpMarker(row.Help);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    /// Side by side when the window is wide enough, stacked otherwise. A table, never legacy Columns, which break
    /// inside table cells.
    private static void TwoColumns(string id, Action left, Action right)
    {
        if (ImGui.GetContentRegionAvail().X >= Scaled(600) && ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            left();
            ImGui.TableNextColumn();
            right();
            ImGui.EndTable();
            return;
        }
        left();
        right();
    }
}
