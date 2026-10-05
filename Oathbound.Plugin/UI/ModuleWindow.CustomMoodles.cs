using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using static Oathbound.Plugin.UI.Layout;

namespace Oathbound.Plugin.UI;

/// The Owner's custom moodle builder. The Sub has none: they make their own in Moodles.
public sealed partial class ModuleWindow
{
    private static readonly string[] CustomMoodleKindNames = ["Positive", "Negative", "Special"];
    private const int IconResultLimit = 120;

    private CustomMoodle cmDraft = NewCustomMoodleDraft();
    /// The saved entry the builder is editing; null builds a new one.
    private QuickCommand? cmEditing;
    private string cmIconSearch = "";

    private static List<(uint Icon, string Name)>? statusIcons;

    /// Same set as Moodles' own picker: every status icon that has a name, once each.
    private static List<(uint Icon, string Name)> StatusIcons => statusIcons ??=
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>()
            .Where(s => s.Icon != 0 && !string.IsNullOrEmpty(s.Name.ExtractText()))
            .Select(s => (s.Icon, Name: s.Name.ExtractText()))
            .GroupBy(s => s.Icon)
            .Select(g => g.First())
            // Some sheet rows point at icons the game files don't have.
            .Where(s => Plugin.TextureProvider.TryGetIconPath(new GameIconLookup(s.Icon), out _))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static CustomMoodle NewCustomMoodleDraft() => new() { Kind = 0, Seconds = 0 };

    private static Vector2 MoodleIconSize => new(Scaled(24), Scaled(32));

    private static void DrawMoodleIcon(uint icon)
    {
        // GetFromGameIcon throws for an icon the game files don't have; keep the slot so layouts don't shift.
        if (Plugin.TextureProvider.TryGetFromGameIcon(new GameIconLookup(icon), out var texture))
            ImGui.Image(texture.GetWrapOrEmpty().Handle, MoodleIconSize);
        else
            ImGui.Dummy(MoodleIconSize);
    }

    private void DrawCustomMoodlesSection(bool canSend)
    {
        var config = plugin.Configuration;
        var list = config.QuickCommands.CustomMoodles;
        using (Section.Begin("customMoodles", "Your custom moodles"))
        {
            IconGlyph.WrappedDisabled("Moodles you write yourself. Your Sub doesn't need them in their Moodles library, but they have to allow \"moodles my Owner writes\" in their permissions.");
            if (list.Count == 0)
                IconGlyph.WrappedDisabled("None yet - build one below.");
            foreach (var cmd in list.ToArray())
                DrawCustomMoodleRow(cmd, list, canSend);
        }

        using (Section.Begin("customMoodleBuilder", cmEditing is null ? "Build a moodle" : "Edit moodle"))
            DrawCustomMoodleBuilder(list);
    }

    private void DrawCustomMoodleRow(QuickCommand cmd, List<QuickCommand> list, bool canSend)
    {
        if (cmd.CustomMoodle is not { } moodle)
            return;
        var id = moodle.Id.ToString("N");
        ImGui.PushID(id);
        DrawMoodleIcon((uint)moodle.IconId);
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextUnformatted(cmd.Label);
        if (moodle.Description.Length > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(moodle.Description);
        DrawFavoriteToggle(cmd, id);
        ContinueRowOrWrap(ButtonWidth("Send"));
        DrawSendCopyButtons(cmd.Command, canSend, id);
        ContinueRowOrWrap(ButtonWidth("Take off"));
        DrawSendCopyButtons(moodle.RemoveCommand(), canSend, id + "_remove", "Take off");
        ContinueRowOrWrap(ButtonWidth("Edit"));
        if (ImGui.SmallButton(ReferenceEquals(cmEditing, cmd) ? "Close" : "Edit"))
        {
            if (ReferenceEquals(cmEditing, cmd))
                ResetCustomMoodleBuilder();
            else
            {
                cmEditing = cmd;
                cmDraft = moodle.Clone();
            }
        }
        ContinueRowOrWrap(ButtonWidth("Delete"));
        if (ImGui.SmallButton("Delete"))
        {
            list.Remove(cmd);
            if (ReferenceEquals(cmEditing, cmd))
                ResetCustomMoodleBuilder();
            plugin.Configuration.Save();
        }
        ImGui.EndGroup();
        ImGui.Separator();
        ImGui.PopID();
    }

    private void ResetCustomMoodleBuilder()
    {
        cmEditing = null;
        cmDraft = NewCustomMoodleDraft();
    }

    private void DrawCustomMoodleBuilder(List<QuickCommand> list)
    {
        var d = cmDraft;
        var title = d.Title;
        ItemWidth(280);
        if (ImGui.InputText("Title##cmTitle", ref title, CustomMoodle.MaxTitleLength))
            d.Title = title.Replace("\n", "").Replace("\r", "");
        IconGlyph.HelpMarker("Moodles' color tags work here, e.g. [color=red]Owned[/color].");
        var description = d.Description;
        ItemWidth(280);
        if (ImGui.InputTextWithHint("Description##cmDescription", "optional", ref description, CustomMoodle.MaxDescriptionLength))
            d.Description = description.Replace("\n", "").Replace("\r", "");

        var kind = d.Kind;
        ItemWidth(160);
        if (ImGui.Combo("Kind##cmKind", ref kind, CustomMoodleKindNames, CustomMoodleKindNames.Length))
            d.Kind = kind;

        var untilRemoved = d.Seconds == 0;
        if (ImGui.Checkbox("Until removed##cmUntilRemoved", ref untilRemoved))
            d.Seconds = untilRemoved ? 0 : 3600;
        if (!untilRemoved)
        {
            ImGui.SameLine();
            var minutes = Math.Max(1, d.Seconds / 60);
            ItemWidth(110);
            if (ImGui.InputInt("minutes##cmMinutes", ref minutes, 15, 60))
                d.Seconds = Math.Clamp(minutes, CustomMoodle.MinSeconds / 60, CustomMoodle.MaxSeconds / 60) * 60;
            ImGui.SameLine();
            ImGui.TextDisabled($"= {RestraintLock.Format(TimeSpan.FromSeconds(d.Seconds))}");
        }

        Section.SubHeading("Icon");
        DrawStatusIconPicker(d);

        Section.SubHeading("Preview");
        if (d.IconId > 0)
        {
            DrawMoodleIcon((uint)d.IconId);
            ImGui.SameLine();
        }
        ImGui.BeginGroup();
        ImGui.TextColored(d.Kind switch { 1 => Theme.Warning, 2 => Theme.AccentHover, _ => Theme.Success },
            d.Title.Length == 0 ? "(no title)" : MoodlesTextFormat.StripMarkup(d.Title));
        if (d.Description.Length > 0)
            IconGlyph.WrappedDisabled(d.Description);
        ImGui.TextDisabled(d.Seconds == 0 ? "Until removed" : $"Lasts {RestraintLock.Format(TimeSpan.FromSeconds(d.Seconds))}");
        ImGui.EndGroup();

        var problem = d.Problem() ?? (CommandSelector.Fits(d.ApplyCommand()) ? null : "Too long to send in one tell - shorten the title, description or color tags.");
        if (problem is not null)
            IconGlyph.WrappedColored(Theme.Warning, problem);
        ImGui.Spacing();
        using (ImRaii.Disabled(problem is not null))
        {
            if (ImGui.Button(cmEditing is null ? "Save moodle" : "Save changes"))
            {
                // Same id on an edit, so sending it again updates the copy the Sub already has.
                var saved = d.Clone();
                var label = MoodlesTextFormat.StripMarkup(saved.Title);
                if (cmEditing is { } editing && list.Contains(editing))
                {
                    editing.Label = label;
                    editing.Command = saved.ApplyCommand();
                    editing.CustomMoodle = saved;
                }
                else
                    list.Add(new QuickCommand { Label = label, Command = saved.ApplyCommand(), Source = ImportSource.Manual, CustomMoodle = saved });
                plugin.Configuration.Save();
                ResetCustomMoodleBuilder();
            }
        }
        if (cmEditing is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel##cmCancel"))
                ResetCustomMoodleBuilder();
        }
    }

    private void DrawStatusIconPicker(CustomMoodle d)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##cmIconSearch", "Search status icons by name (e.g. Heart, Bind)", ref cmIconSearch, 40);
        var filter = cmIconSearch.Trim();
        var rows = (filter.Length == 0 ? StatusIcons : StatusIcons.Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            .Take(IconResultLimit).ToList();
        var size = MoodleIconSize;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        using (Section.List("cmIcons", Scaled(150)))
        {
            var perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (size.X + spacing)));
            for (var i = 0; i < rows.Count; i++)
            {
                var (icon, name) = rows[i];
                if (i % perRow != 0)
                    ImGui.SameLine();
                var picked = d.IconId == (int)icon;
                var start = ImGui.GetCursorScreenPos();
                DrawMoodleIcon(icon);
                if (picked)
                    ImGui.GetWindowDrawList().AddRect(start, start + size, ImGui.GetColorU32(Theme.AccentHover), 0, ImDrawFlags.None, Scaled(2));
                if (ImGui.IsItemClicked())
                    d.IconId = (int)icon;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(name);
            }
            if (rows.Count == 0)
                ImGui.TextDisabled("Nothing matches.");
        }
        if (filter.Length == 0)
            ImGui.TextDisabled($"Showing {rows.Count} of {StatusIcons.Count} - search to find more.");
    }
}
