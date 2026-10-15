using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Glamourer.Api.Enums;
using static Oathbound.Plugin.UI.Layout;

namespace Oathbound.Plugin.UI;

public sealed partial class ModuleWindow
{
    private string newMoodleAlias = "";

    private readonly ListDetail moodleList = new();

    private void DrawMoodlesModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.Smile, "Moodles Aliases");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Scan your own registered Moodles statuses in Settings, then pick one to give it an alias.");
        DrawFixedWordRow("moodleFixed", new FixedWordInfo("Clear moodles", ControlWords.ClearMoodle,
            "Clears your Moodles, except ones attached to something active. It can't be renamed."));

        var moodleAliases = config.Aliases.Moodles;
        var statuses = config.MoodlesMapping.LocalCatalog.Values.OrderBy(s => MoodlesTextFormat.StripMarkup(s.Name), StringComparer.OrdinalIgnoreCase).ToList();
        var items = statuses.Select(st =>
        {
            var aliases = moodleAliases.Where(m => m.StatusId == st.StatusId).Select(m => m.Alias).ToList();
            return new ListDetailItem($"status:{st.StatusId}", MoodlesTextFormat.StripMarkup(st.Name), aliases.Count > 0 ? string.Join(", ", aliases) : null);
        }).ToList();
        items.AddRange(moodleAliases.Select((m, i) => (m, i))
            .Where(x => string.IsNullOrEmpty(x.m.StatusId) || !config.MoodlesMapping.LocalCatalog.ContainsKey(x.m.StatusId))
            .Select(x => new ListDetailItem($"stale:{x.i}", x.m.Alias, "needs rescan", Group: "Needs rescan")));

        using (Section.Begin("moodleAliases", "Your moodles"))
        {
            moodleList.Draw("moodles", items, null, item => DrawMoodleStatusDetail(config, moodleAliases, item),
                () => newMoodleAlias.Trim().Length > 0, _ => newMoodleAlias = "",
                "No scanned Moodles statuses yet - rescan in Settings (gear icon) first.");
        }
    }

    private void DrawMoodleStatusDetail(PluginConfig config, List<MoodlesAliasDefinition> moodleAliases, ListDetailItem? item)
    {
        if (item is null)
        {
            IconGlyph.WrappedDisabled("Choose a status.");
            return;
        }
        if (item.Key.StartsWith("stale:") && int.TryParse(item.Key["stale:".Length..], out var staleIndex) && staleIndex < moodleAliases.Count)
        {
            var stale = moodleAliases[staleIndex];
            ImGui.TextColored(Theme.AccentHover, stale.Alias);
            IconGlyph.WrappedColored(Theme.Warning, $"{MoodlesTextFormat.StripMarkup(stale.StatusName)} isn't in the latest scan. Rescan, or delete this alias and make it again.");
            if (ImGui.Button("Delete##staleMoodle"))
            {
                moodleAliases.RemoveAt(staleIndex);
                config.Save();
                moodleList.Select(null);
            }
            return;
        }
        if (!config.MoodlesMapping.LocalCatalog.TryGetValue(item.Key["status:".Length..], out var status))
            return;

        ImGui.PushID($"moodleStatus_{status.StatusId}");
        ImGui.TextColored(Theme.AccentHover, MoodlesTextFormat.StripMarkup(status.Name));

        Section.SubHeading("Aliases");
        var bound = moodleAliases.Select((m, i) => (m, i)).Where(x => x.m.StatusId == status.StatusId).ToList();
        if (bound.Count == 0)
            IconGlyph.WrappedDisabled("No alias applies this yet.");
        foreach (var (alias, index) in bound)
        {
            ImGui.PushID($"moodleAlias_{index}");
            if (DrawDeferredTextInput("##aliasEdit", (alias, "alias"), alias.Alias, 32, out var renamed) && renamed.Trim().Length > 0 && !IsReserved(renamed))
            {
                alias.Alias = renamed.Trim();
                config.Save();
            }
            ImGui.SameLine();
            if (ImGui.Button("Delete"))
            {
                moodleAliases.RemoveAt(index);
                config.Save();
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }

        Section.SubHeading("Add an alias");
        ItemWidth(160);
        ImGui.InputTextWithHint("##newMoodle", "word", ref newMoodleAlias, 32);
        IconGlyph.HelpMarker("Short word the Owner types. With Moodles permission enabled, this immediately applies the status.");
        DrawReservedWordWarning(newMoodleAlias);
        using (ImRaii.Disabled(newMoodleAlias.Trim().Length == 0 || IsReserved(newMoodleAlias)))
        {
            if (ImGui.Button("Add alias##newMoodle"))
            {
                moodleAliases.Add(new MoodlesAliasDefinition { Alias = newMoodleAlias.Trim(), StatusId = status.StatusId, StatusName = status.Name });
                config.Save();
                newMoodleAlias = "";
            }
        }
        ImGui.PopID();
    }

    private readonly ListDetail ownerMoodleList = new();
    private const string NewOwnMoodleKey = "own:new";

    private void DrawMoodlesQuickSection(bool canSend)
    {
        var quick = plugin.Configuration.QuickCommands.Moodles;
        var own = plugin.Configuration.QuickCommands.CustomMoodles;
        DrawSectionTitleRow(FontAwesomeIcon.Smile, "Moodles", quick.Count > 0, "moodlesQuick", () =>
        {
            quick.Clear();
            plugin.Configuration.Save();
        });
        DrawCommandRow("moodlesQuickFixed", canSend, new RowCommand("Clear moodle", "moodle clear", FixedActionIds.ClearMoodle,
            "Clears your Sub's moodles, except ones attached to something active."));
        if (quick.Count == 0)
            DrawGoToSyncTabPrompt("No Moodles statuses imported yet.");

        var items = quick.Select((c, i) => new ListDetailItem($"sub:{i}", MoodlesTextFormat.StripMarkup(c.Label), Group: "From your Sub")).ToList();
        items.AddRange(own.Select((c, i) => new ListDetailItem($"own:{i}", c.Label, Group: "Written by you",
            Tooltip: c.CustomMoodle?.Description is { Length: > 0 } description ? description : null)));
        if (ownerMoodleList.Selected == NewOwnMoodleKey)
            items.Add(new ListDetailItem(NewOwnMoodleKey, "New moodle", Group: "Written by you"));

        QuickCommand? FindSub(string? key) => key is not null && key.StartsWith("sub:") && int.TryParse(key[4..], out var i) && i < quick.Count ? quick[i] : null;
        QuickCommand? FindOwn(string? key) => key is not null && key != NewOwnMoodleKey && key.StartsWith("own:") && int.TryParse(key[4..], out var i) && i < own.Count ? own[i] : null;

        using var section = Section.Begin("customMoodles", "Moodles");
        IconGlyph.WrappedDisabled("Your Sub's moodles, and moodles you write yourself. Your Sub has to allow \"moodles my Owner writes\" for yours.");
        ownerMoodleList.Draw("ownerMoodles", items,
            () =>
            {
                if (!ImGui.Button("+ New moodle"))
                    return;
                CancelQuickCommandEdit();
                ResetCustomMoodleBuilder();
                ownerMoodleList.Select(NewOwnMoodleKey);
            },
            item =>
            {
                if (item is null)
                {
                    IconGlyph.WrappedDisabled("Choose a moodle, or use + New moodle to write one.");
                    return;
                }
                if (item.Key == NewOwnMoodleKey)
                {
                    var before = own.Count;
                    using (Section.Begin("customMoodleBuilder", "Write a moodle"))
                        DrawCustomMoodleBuilder(own);
                    if (own.Count > before)
                        ownerMoodleList.Select($"own:{own.Count - 1}");
                    return;
                }
                if (FindSub(item.Key) is { } subMoodle)
                    DrawSavedQuickDetail(subMoodle, quick, canSend, ownerMoodleList, null);
                else if (FindOwn(item.Key) is { } ownMoodle)
                    DrawOwnMoodleDetail(ownMoodle, own, canSend);
            },
            () => ownerMoodleList.Selected == NewOwnMoodleKey ? cmDraft.Title.Length > 0
                : FindSub(ownerMoodleList.Selected) is { } s ? QuickEditDirty(s)
                : FindOwn(ownerMoodleList.Selected) is { } o && ReferenceEquals(cmEditing, o) && cmDraft.ApplyCommand() != o.Command,
            item =>
            {
                CancelQuickCommandEdit();
                ResetCustomMoodleBuilder();
                if (FindSub(item?.Key) is { } subMoodle)
                    BeginQuickCommandEdit(subMoodle, quick);
            },
            "No moodles yet. Your Sub's arrive when their catalog syncs; use + New moodle to write your own.");
    }

    private void DrawOwnMoodleDetail(QuickCommand cmd, List<QuickCommand> own, bool canSend)
    {
        if (cmd.CustomMoodle is not { } moodle)
            return;
        var id = moodle.Id.ToString("N");
        ImGui.PushID(id);
        DrawMoodleIcon((uint)moodle.IconId);
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.AccentHover, cmd.Label);
        ContinueRowOrWrap(StarWidth);
        DrawFavoriteToggle(cmd, id);
        ContinueRowOrWrap(ButtonWidth("Delete"));
        if (ImGui.Button("Delete"))
        {
            own.Remove(cmd);
            ResetCustomMoodleBuilder();
            plugin.Configuration.Save();
            ownerMoodleList.Select(null);
            ImGui.EndGroup();
            ImGui.PopID();
            return;
        }
        if (moodle.Description.Length > 0)
            IconGlyph.WrappedDisabled(moodle.Description);
        ImGui.EndGroup();

        Section.SubHeading("Send");
        DrawSendCopyButtons(OwnerMoodleOverride.ForSend(plugin.Configuration, cmd), canSend, id);
        OwnerLockOption.DrawInline(id, cmd, plugin.Configuration);
        ContinueRowOrWrap(ButtonWidth("Take off"));
        DrawSendCopyButtons(moodle.RemoveCommand(), canSend, id + "_remove", "Take off");

        Section.SubHeading("Edit");
        // The builder's Save and Cancel end the edit; reopen it so the detail pane always shows it.
        if (!ReferenceEquals(cmEditing, cmd))
        {
            cmEditing = cmd;
            cmDraft = moodle.Clone();
        }
        using (Section.Begin("customMoodleBuilder"))
            DrawCustomMoodleBuilder(own);
        ImGui.PopID();
    }
}
