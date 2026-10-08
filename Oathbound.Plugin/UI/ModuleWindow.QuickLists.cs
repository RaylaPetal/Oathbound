using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Oathbound.Plugin.Config;
using static Oathbound.Plugin.UI.Layout;

namespace Oathbound.Plugin.UI;

/// The Owner's saved commands of one category as a list beside one command's send row and editor.
public sealed partial class ModuleWindow
{
    private readonly Dictionary<string, ListDetail> quickLists = new();

    private ListDetail QuickList(string id)
    {
        if (!quickLists.TryGetValue(id, out var list))
            quickLists[id] = list = new ListDetail();
        return list;
    }

    /// Keys are list positions, so a rename keeps the selection. `newKey`/`drawNew` add a "+ New" draft entry;
    /// `info` draws extra details above the send row.
    private void DrawSavedQuickListDetail(string id, List<QuickCommand> list, bool canSend, Func<QuickCommand, ListDetailItem> toItem,
        string emptyText, string? newLabel = null, Action? drawNew = null, Func<bool>? newDirty = null, Action<QuickCommand>? info = null)
    {
        var listDetail = QuickList(id);
        var newKey = $"{id}:new";
        var items = list.Select((cmd, i) => toItem(cmd) with { Key = $"{id}:{i}" }).ToList();
        if (listDetail.Selected == newKey && newLabel is not null)
            items.Add(new ListDetailItem(newKey, newLabel));

        Action? toolbar = drawNew is null ? null : () =>
        {
            if (ImGui.Button($"+ New##{id}"))
            {
                CancelQuickCommandEdit();
                listDetail.Select(newKey);
            }
        };

        QuickCommand? Find(string? key) =>
            key is not null && key.StartsWith(id + ":") && int.TryParse(key[(id.Length + 1)..], out var index) && index < list.Count ? list[index] : null;

        listDetail.Draw(id, items, toolbar, item =>
            {
                if (item is null)
                {
                    IconGlyph.WrappedDisabled(drawNew is null ? "Choose an entry." : "Choose an entry, or use + New to add one.");
                    return;
                }
                if (item.Key == newKey)
                {
                    drawNew?.Invoke();
                    return;
                }
                if (Find(item.Key) is { } cmd)
                    DrawSavedQuickDetail(cmd, list, canSend, listDetail, info);
            },
            () => listDetail.Selected == newKey ? newDirty?.Invoke() == true : Find(listDetail.Selected) is { } cmd && QuickEditDirty(cmd),
            item =>
            {
                CancelQuickCommandEdit();
                if (Find(item?.Key) is { } cmd)
                    BeginQuickCommandEdit(cmd, list);
            },
            emptyText);
    }

    private void DrawSavedQuickDetail(QuickCommand cmd, List<QuickCommand> list, bool canSend, ListDetail listDetail, Action<QuickCommand>? info)
    {
        ImGui.PushID($"quickDetail_{cmd.Label}_{cmd.Command}");
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.AccentHover, cmd.Label);
        ContinueRowOrWrap(StarWidth);
        DrawFavoriteToggle(cmd, $"{cmd.Label}_{cmd.Command}");
        ContinueRowOrWrap(ButtonWidth("Delete"));
        if (ImGui.Button("Delete"))
        {
            list.Remove(cmd);
            CancelQuickCommandEdit();
            plugin.Configuration.Save();
            listDetail.Select(null);
            ImGui.PopID();
            return;
        }
        if (cmd.Command.StartsWith("outfit wear ", StringComparison.OrdinalIgnoreCase))
            ImGui.TextDisabled("Not locked");
        if (cmd.MoodleOverride is { } moodle && OwnerMoodleOverride.Accepts(cmd.Command))
            ImGui.TextDisabled($"Moodle: {moodle}");
        info?.Invoke(cmd);

        Section.SubHeading("Send");
        ImGui.BeginGroup();
        DrawSendCopyButtons(OwnerMoodleOverride.ForSend(plugin.Configuration, cmd), canSend, $"{cmd.Label}_{cmd.Command}");
        if (OwnerLockOption.Accepts(cmd.Command))
            OwnerLockOption.DrawInline($"{cmd.Label}_{cmd.Command}", cmd, plugin.Configuration);
        ImGui.EndGroup();
        TutorialService.Anchor(TutorialAnchors.QuickRow);

        Section.SubHeading("Edit");
        // The editor's Save and Cancel end the edit; reopen it so the detail pane always shows it.
        if (!ReferenceEquals(editingQuickCommand, cmd))
            BeginQuickCommandEdit(cmd, list);
        DrawQuickCommandEditor();
        ImGui.PopID();
    }

    private bool QuickEditDirty(QuickCommand cmd) =>
        ReferenceEquals(editingQuickCommand, cmd)
        && (editingQuickLabel.Trim() != cmd.Label
            || BuildQuickEditPayload(cmd).Command != cmd.Command
            || (editingQuickCategory == QuickEditCategory.Outfit && editingQuickMoodle != cmd.MoodleOverride));
}
