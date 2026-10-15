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
    private string? editingQuickMoodle;
    private QuickCommand? editingQuickCommand;
    private List<QuickCommand>? editingQuickList;
    private string editingQuickLabel = "";
    private string editingQuickPayload = "";
    private string editingQuickTarget = "";
    private string editingQuickOriginalTarget = "";
    private bool editingQuickTitleIsPrefix;
    private bool editingQuickOriginalTitleIsPrefix;
    private bool editingQuickOutfitLocked = true;
    private bool editingQuickOriginalOutfitLocked = true;
    private static readonly string[] OutfitLockModeNames = ["Locked", "Not locked"];

    private Vector3 editingQuickTitleColor = new(1, 1, 1);

    private Vector3 editingQuickOriginalTitleColor = new(1, 1, 1);
    private bool editingQuickTitleHasGlow;
    private bool editingQuickOriginalTitleHasGlow;

    private Vector3 editingQuickTitleGlow = new(1, 1, 1);

    private Vector3 editingQuickOriginalTitleGlow = new(1, 1, 1);

    private enum QuickEditCategory { Raw, Title, Outfit, Gesture, Follow, Moodle }
    private QuickEditCategory editingQuickCategory;

    /// A built-in action that can't be removed.
    private void DrawFixedQuickRow(string label, string command, bool canSend, string favoriteId)
    {
        ImGui.BeginGroup();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(label);
        ContinueRowOrWrap(StarWidth);
        DrawFavoriteStar(favoriteId);
        ContinueRowOrWrap(ButtonWidth("Send"));
        DrawSendCopyButtons(OwnerMoodleOverride.ForSend(plugin.Configuration, command, null), canSend, $"fixed_{label}");
        ImGui.EndGroup();
        TutorialService.Anchor(TutorialAnchors.Fixed(favoriteId));
    }

    /// `displayLabel` changes display only; IDs and the command keep the raw label.
    private void DrawSavedQuickRow(QuickCommand cmd, List<QuickCommand> list, bool canSend, Func<string, string>? displayLabel = null)
    {
        var shownLabel = displayLabel?.Invoke(cmd.Label) ?? cmd.Label;
        if (cmd.Command.StartsWith("outfit wear ", StringComparison.OrdinalIgnoreCase))
            shownLabel += "  · not locked";
        if (cmd.MoodleOverride is { } rowMoodle && OwnerMoodleOverride.Accepts(cmd.Command))
            shownLabel += $"  · moodle: {rowMoodle}";
        ImGui.BeginGroup();
        ImGui.TextUnformatted(shownLabel);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(shownLabel);
        ContinueRowOrWrap(StarWidth);
        DrawFavoriteToggle(cmd, $"{cmd.Label}_{cmd.Command}");
        ContinueRowOrWrap(ButtonWidth("Send"));
        DrawSendCopyButtons(OwnerMoodleOverride.ForSend(plugin.Configuration, cmd), canSend, $"{cmd.Label}_{cmd.Command}");
        if (OwnerLockOption.Accepts(cmd.Command))
            OwnerLockOption.DrawInline($"{cmd.Label}_{cmd.Command}", cmd, plugin.Configuration);
        ImGui.EndGroup();
        TutorialService.Anchor(TutorialAnchors.QuickRow);
        // A bundle loads into the bundle builder instead of expanding.
        var expanded = ReferenceEquals(editingQuickCommand, cmd);
        var editLabel = expanded ? "Close" : "Edit";
        ContinueRowOrWrap(ButtonWidth(editLabel));
        if (ImGui.Button($"{editLabel}##{cmd.Label}_{cmd.Command}"))
        {
            if (expanded)
                CancelQuickCommandEdit();
            else if (ReferenceEquals(list, plugin.Configuration.QuickCommands.Aliases) &&
                cmd.Command.StartsWith("customtrigger cast ", StringComparison.OrdinalIgnoreCase))
                BeginOwnerBundleEdit(cmd);
            else
                BeginQuickCommandEdit(cmd, list);
        }
        ContinueRowOrWrap(ButtonWidth("Remove"));
        if (ImGui.Button($"Remove##{cmd.Label}_{cmd.Command}"))
        {
            list.Remove(cmd);
            if (ReferenceEquals(editingQuickCommand, cmd))
                CancelQuickCommandEdit();
            plugin.Configuration.Save();
            return;
        }

        if (ReferenceEquals(editingQuickCommand, cmd))
        {
            ImGui.Indent();
            DrawQuickCommandEditor();
            ImGui.Unindent();
        }
    }

    private void BeginQuickCommandEdit(QuickCommand command, List<QuickCommand> list)
    {
        editingQuickCommand = command;
        editingQuickList = list;
        editingQuickLabel = command.Label;
        editingQuickPayload = command.Command;
        editingQuickCategory = CategoryFor(list);
        editingQuickTarget = command.Target ?? ExtractQuickTarget(command, editingQuickCategory);
        editingQuickOriginalTarget = editingQuickTarget;
        editingQuickTitleIsPrefix = command.TitleIsPrefix;
        editingQuickTitleColor = command.TitleColor ?? new Vector3(1, 1, 1);
        editingQuickTitleHasGlow = command.TitleGlow is not null;
        editingQuickTitleGlow = command.TitleGlow ?? new Vector3(1, 1, 1);
        editingQuickMoodle = command.MoodleOverride;

        if (editingQuickCategory == QuickEditCategory.Title &&
            command.Command.StartsWith("title style ", StringComparison.OrdinalIgnoreCase) &&
            TitleCommand.TryParseStyleCommand(command.Command["title style ".Length..], out var title, out var prefix, out var color, out var glow))
        {
            editingQuickTarget = title;
            editingQuickTitleIsPrefix = prefix;
            editingQuickTitleColor = color;
            editingQuickTitleHasGlow = glow is not null;
            editingQuickTitleGlow = glow ?? new Vector3(1, 1, 1);
        }
        editingQuickOriginalTarget = editingQuickTarget;
        editingQuickOriginalTitleIsPrefix = editingQuickTitleIsPrefix;
        editingQuickOutfitLocked = !command.Command.StartsWith("outfit wear ", StringComparison.OrdinalIgnoreCase);
        editingQuickOriginalOutfitLocked = editingQuickOutfitLocked;
        editingQuickOriginalTitleColor = editingQuickTitleColor;
        editingQuickOriginalTitleHasGlow = editingQuickTitleHasGlow;
        editingQuickOriginalTitleGlow = editingQuickTitleGlow;
    }

    private QuickEditCategory CategoryFor(List<QuickCommand> list)
    {
        var quick = plugin.Configuration.QuickCommands;
        if (ReferenceEquals(list, quick.Titles)) return QuickEditCategory.Title;
        if (ReferenceEquals(list, quick.Outfits)) return QuickEditCategory.Outfit;
        if (ReferenceEquals(list, quick.Gestures)) return QuickEditCategory.Gesture;
        if (ReferenceEquals(list, quick.Follow)) return QuickEditCategory.Follow;
        if (ReferenceEquals(list, quick.Moodles)) return QuickEditCategory.Moodle;
        return QuickEditCategory.Raw;
    }

    private static string ExtractQuickTarget(QuickCommand command, QuickEditCategory category)
    {
        var prefixes = category switch
        {
            QuickEditCategory.Outfit => new[] { "outfit lock ", "outfit wear " },
            QuickEditCategory.Gesture => new[] { "gesture " },
            QuickEditCategory.Moodle => new[] { "moodle apply " },
            _ => Array.Empty<string>(),
        };
        foreach (var prefix in prefixes)
            if (command.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return command.Command[prefix.Length..].Trim().Trim('"');
        return category == QuickEditCategory.Follow ? command.Command : command.Label;
    }

    /// Nothing touches the stored object until Save, so Cancel is lossless.
    private void DrawQuickCommandEditor()
    {
        if (editingQuickCommand is not { } source || editingQuickList is not { } list)
            return;

        using var editorBox = Section.Begin("quickCommandEditor");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##quickEditLabel", "Label", ref editingQuickLabel, 80);

        switch (editingQuickCategory)
        {
            case QuickEditCategory.Title:
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("Title text##quickEdit", ref editingQuickTarget, 64);
                ImGui.Checkbox("Prefix (not suffix)##quickEdit", ref editingQuickTitleIsPrefix);
                ImGui.ColorEdit3("Color##quickEdit", ref editingQuickTitleColor);
                DrawGlowPicker("quickEdit", ref editingQuickTitleHasGlow, ref editingQuickTitleGlow);
                break;
            case QuickEditCategory.Outfit:
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("Outfit name##quickEdit", ref editingQuickTarget, 96);
                var lockMode = editingQuickOutfitLocked ? 0 : 1;
                if (ImGui.Combo("Lock##quickEdit", ref lockMode, OutfitLockModeNames, OutfitLockModeNames.Length))
                    editingQuickOutfitLocked = lockMode == 0;
                IconGlyph.HelpMarker("Locked: your Sub can't change it until you unlock it. Not locked: they can change it freely.");
                OwnerMoodleOverride.Draw("quickEditOutfit", plugin.Configuration, ref editingQuickMoodle);
                break;
            case QuickEditCategory.Gesture:
                DrawQuickGestureTargetPicker();
                break;
            case QuickEditCategory.Follow:
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("Sub alias##quickEdit", ref editingQuickTarget, 32);
                break;
            case QuickEditCategory.Moodle:
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("Moodle status##quickEdit", ref editingQuickTarget, 96);
                break;
            default:
                ImGui.SetNextItemWidth(-1);
                ImGui.InputText("Command##quickEdit", ref editingQuickPayload, 400);
                break;
        }

        var (draftCommand, draftTarget) = BuildQuickEditPayload(source);

        var stale = !list.Contains(source);
        var duplicate = list.Any(q => !ReferenceEquals(q, source) &&
            (string.Equals(q.Label, editingQuickLabel.Trim(), StringComparison.OrdinalIgnoreCase) ||
             string.Equals(q.Command, draftCommand, StringComparison.OrdinalIgnoreCase)));
        var aliasList = ReferenceEquals(list, plugin.Configuration.QuickCommands.Aliases) ||
            ReferenceEquals(list, plugin.Configuration.QuickCommands.Follow);
        var reserved = aliasList && IsReserved(draftCommand);
        var safe = ChatComposer.AllFit(plugin.ChatComposer.ComposeAll(draftCommand));
        var validTarget = draftCommand.Length > 0 && (editingQuickCategory != QuickEditCategory.Gesture || draftTarget is not null);
        if (stale) IconGlyph.WrappedColored(Theme.Warning, "This entry was removed while it was being edited.");
        else if (duplicate) IconGlyph.WrappedColored(Theme.Warning, "Another saved entry already uses this label or command.");
        else if (reserved) IconGlyph.WrappedColored(Theme.Warning, "This alias is reserved for a direct Owner command.");
        else if (!safe) IconGlyph.WrappedColored(Theme.Warning, "This command is too long for a safe chat payload.");

        using (ImRaii.Disabled(stale || duplicate || reserved || !safe || !validTarget || editingQuickLabel.Trim().Length == 0))
        {
            if (ImGui.Button("Save##quickEdit"))
            {
                source.Label = editingQuickLabel.Trim();
                source.Command = draftCommand;
                source.Target = draftTarget;
                if (editingQuickCategory == QuickEditCategory.Outfit)
                    source.MoodleOverride = editingQuickMoodle;                if (editingQuickCategory == QuickEditCategory.Title)
                {
                    source.TitleIsPrefix = editingQuickTitleIsPrefix;
                    source.TitleColor = editingQuickTitleColor;
                    source.TitleGlow = editingQuickTitleHasGlow ? editingQuickTitleGlow : null;
                }
                if (editingQuickCategory == QuickEditCategory.Gesture && draftTarget is not null &&
                    plugin.Configuration.GestureMapping.ImportedPeerCatalog.TryGetValue(draftTarget, out var gesture))
                {
                    source.GestureModName = gesture.ModName;
                    source.GestureGroupName = gesture.GroupName;
                    source.GestureGroupOrder = gesture.GroupOrder;
                    source.GestureOptionOrder = gesture.OptionOrder;
                }
                plugin.Configuration.Save();
                CancelQuickCommandEdit();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##quickEdit"))
            CancelQuickCommandEdit();
    }

    private void DrawQuickGestureTargetPicker()
    {
        var catalog = plugin.Configuration.GestureMapping.ImportedPeerCatalog.Values
            .OrderBy(g => g.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.GroupOrder).ThenBy(g => g.OptionOrder).ToList();
        var preview = plugin.Configuration.GestureMapping.ImportedPeerCatalog.TryGetValue(editingQuickTarget, out var selected)
            ? CommandSelector.GestureSelector(selected, catalog)
            : "Choose an imported animation...";
        ImGui.SetNextItemWidth(-1);
        if (!ImGui.BeginCombo("Animation##quickEdit", preview)) return;
        foreach (var entry in catalog)
        {
            var label = CommandSelector.GestureSelector(entry, catalog);
            if (ImGui.Selectable($"{label}##{entry.Id}", string.Equals(entry.Id, editingQuickTarget, StringComparison.OrdinalIgnoreCase)))
                editingQuickTarget = entry.Id;
        }
        ImGui.EndCombo();
    }

    private (string Command, string? Target) BuildQuickEditPayload(QuickCommand source)
    {
        var target = editingQuickTarget.Trim();
        var targetChanged = !string.Equals(target, editingQuickOriginalTarget.Trim(), StringComparison.Ordinal) ||
            (editingQuickCategory == QuickEditCategory.Title &&
             (editingQuickTitleIsPrefix != editingQuickOriginalTitleIsPrefix || editingQuickTitleColor != editingQuickOriginalTitleColor ||
              editingQuickTitleHasGlow != editingQuickOriginalTitleHasGlow || editingQuickTitleGlow != editingQuickOriginalTitleGlow)) ||
            (editingQuickCategory == QuickEditCategory.Outfit && editingQuickOutfitLocked != editingQuickOriginalOutfitLocked);
        if (!targetChanged && editingQuickCategory != QuickEditCategory.Raw)
            return (source.Command, source.Target);
        return editingQuickCategory switch
        {
            QuickEditCategory.Title when target.Length > 0 =>
                (TitleCommand.BuildStyleCommand(target, editingQuickTitleIsPrefix, editingQuickTitleColor, editingQuickTitleHasGlow ? editingQuickTitleGlow : null), null),
            QuickEditCategory.Outfit when target.Length > 0 => ($"outfit {(editingQuickOutfitLocked ? "lock" : "wear")} {target}", target),
            QuickEditCategory.Gesture when plugin.Configuration.GestureMapping.ImportedPeerCatalog.TryGetValue(target, out var entry) =>
                ($"gesture {CommandSelector.Quote(CommandSelector.GestureSelector(entry, plugin.Configuration.GestureMapping.ImportedPeerCatalog.Values))}", entry.Id),
            QuickEditCategory.Follow when target.Length > 0 => (target, null),
            QuickEditCategory.Moodle when target.Length > 0 =>
                ($"moodle apply {CommandSelector.Quote(target)}", target),
            QuickEditCategory.Raw => (editingQuickPayload.Trim(), source.Target),
            _ => ("", null),
        };
    }

    private void CancelQuickCommandEdit()
    {
        editingQuickCommand = null;
        editingQuickList = null;
        editingQuickLabel = "";
        editingQuickPayload = "";
        editingQuickTarget = "";
        editingQuickOriginalTarget = "";
        editingQuickCategory = QuickEditCategory.Raw;
    }

    private void DrawFavoriteToggle(QuickCommand cmd, string idSuffix)
    {
        var tooltip = cmd.IsFavorite
            ? "Remove from favorites"
            : OwnerLockOption.Accepts(cmd.Command)
                ? "Add to favorites - the favorite keeps the lock timer set right now"
                : "Add to favorites";
        if (IconGlyph.Star($"##fav_{idSuffix}", cmd.IsFavorite, tooltip))
        {
            cmd.IsFavorite = !cmd.IsFavorite;
            // A favorite keeps the timer and key this command had when it was starred.
            cmd.FavoriteLockSeconds = cmd.IsFavorite ? cmd.LockSeconds : null;
            cmd.FavoriteLockKey = cmd.IsFavorite ? cmd.LockKey : null;
            plugin.Configuration.Save();
        }
        TutorialService.Anchor(TutorialAnchors.QuickStar);
    }

    private static float StarWidth => ImGui.GetFrameHeight();
}
