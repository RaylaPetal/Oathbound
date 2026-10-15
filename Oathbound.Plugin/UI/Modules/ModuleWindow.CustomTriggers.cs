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
    private int? ctqLockSeconds;
    private string ctqLockKey = "";
    private StruggleLevel ctqStruggle;

    /// -1 = a moodle from the Sub's library by name.
    private int ctqCustomMoodleIndex = -1;
    private int ctqStrugglePenalty;
    private string ctNewAlias = "";

    private readonly List<CustomTriggerAction> ctDraftActions = new();
    private int ctNewActionKindIndex;
    private string ctTitleText = "";
    private bool ctTitleIsPrefix;

    private Vector3 ctTitleColor = new(1, 1, 1);
    private bool ctTitleHasGlow;

    private Vector3 ctTitleGlow = new(1, 1, 1);
    private int ctOutfitDesignIndex;
    private GestureCatalogEntry? ctSelectedGesture;
    private int ctMoodleStatusIndex;
    private int ctRestraintDeviceIndex;
    private string ctChatText = "";
    private int? editingCustomTriggerIndex;
    private int? editingCustomTriggerActionIndex;

    /// Owner-side ad-hoc Custom Trigger draft. Actions are typed by name, since the Owner can't see the Sub's catalogs.
    private string ctqLabel = "";

    private readonly List<CustomTriggerAction> ctqDraftActions = new();
    private int ctqKindIndex;
    private string ctqTitleText = "";
    private bool ctqTitleIsPrefix;

    private Vector3 ctqTitleColor = new(1, 1, 1);
    private bool ctqTitleHasGlow;

    private Vector3 ctqTitleGlow = new(1, 1, 1);
    private string ctqOutfitName = "";
    private string ctqGestureName = "";
    private string ctqMoodleName = "";
    private string ctqRestraintName = "";
    private string ctqChatText = "";
    private int? editingOwnerActionIndex;
    private QuickCommand? editingOwnerBundle;

    private string commandInput = "";

    /// Each bundled action checks its own permission at apply time; this UI only builds the definition.
    private readonly ListDetail customTriggerListDetail = new();
    private bool creatingCustomTrigger;
    private const string NewCustomTriggerKey = "new:customtrigger";

    private void DrawCustomTriggersModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.BoltLightning, "Custom Triggers");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Run several actions from one alias. Each action still needs its own permission.");

        var triggers = config.Aliases.CustomTriggers;
        var items = triggers.Select((t, i) => new ListDetailItem($"ct:{i}", t.Alias, t.Actions.Count == 1 ? "1 action" : $"{t.Actions.Count} actions")).ToList();
        if (creatingCustomTrigger)
            items.Add(new ListDetailItem(NewCustomTriggerKey, "New trigger"));

        using (Section.Begin("customTriggerList", "Your custom triggers"))
        {
            customTriggerListDetail.Draw("customTriggers", items, DrawCustomTriggerToolbar,
                item => DrawCustomTriggerDetail(config, triggers, item), () => CustomTriggerDirty(triggers),
                item => LoadCustomTriggerDraft(triggers, item), "No custom triggers yet. Use + New to build one.");
        }
    }

    private void DrawCustomTriggerToolbar()
    {
        if (!ImGui.Button("+ New##customTrigger"))
            return;
        ResetCustomTriggerDraft();
        creatingCustomTrigger = true;
        customTriggerListDetail.Select(NewCustomTriggerKey);
    }

    private void ResetCustomTriggerDraft()
    {
        ctNewAlias = "";
        ctDraftActions.Clear();
        editingCustomTriggerIndex = null;
        editingCustomTriggerActionIndex = null;
    }

    private void LoadCustomTriggerDraft(List<CustomTriggerDefinition> triggers, ListDetailItem? item)
    {
        if (item?.Key == NewCustomTriggerKey)
            return;
        creatingCustomTrigger = false;
        ResetCustomTriggerDraft();
        if (item is null || !int.TryParse(item.Key["ct:".Length..], out var index) || index >= triggers.Count)
            return;
        editingCustomTriggerIndex = index;
        ctNewAlias = triggers[index].Alias;
        ctDraftActions.AddRange(triggers[index].Actions.Select(CloneAction));
    }

    private bool CustomTriggerDirty(List<CustomTriggerDefinition> triggers)
    {
        if (customTriggerListDetail.Selected == NewCustomTriggerKey)
            return ctNewAlias.Trim().Length > 0 || ctDraftActions.Count > 0;
        return editingCustomTriggerIndex is { } index && index < triggers.Count
            && CustomTriggerCommand.BuildCastCommand(ctNewAlias.Trim(), ctDraftActions) != CustomTriggerCommand.BuildCastCommand(triggers[index].Alias, triggers[index].Actions);
    }

    private void DrawCustomTriggerDetail(PluginConfig config, List<CustomTriggerDefinition> triggers, ListDetailItem? item)
    {
        if (item is null)
        {
            IconGlyph.WrappedDisabled("Choose a custom trigger, or use + New to build one.");
            return;
        }
        if (item.Key != NewCustomTriggerKey && editingCustomTriggerIndex is { } index && index < triggers.Count)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.AccentHover, triggers[index].Alias);
            ImGui.SameLine();
            if (ImGui.Button("Delete##customTrigger"))
            {
                triggers.RemoveAt(index);
                config.Save();
                ResetCustomTriggerDraft();
                customTriggerListDetail.Select(null);
                return;
            }
        }
        DrawCustomTriggerEditor(config, triggers);
    }

    private void DrawCustomTriggerEditor(PluginConfig config, List<CustomTriggerDefinition> triggers)
    {
        using var editorBox = Section.Begin("customTriggerEditor", editingCustomTriggerIndex is null ? "New trigger" : "Edit trigger");
        ImGui.InputText("Alias##newCustomTrigger", ref ctNewAlias, 32);
        IconGlyph.HelpMarker("Short word your Owner sends. Runs every allowed action below in order.");
        DrawReservedWordWarning(ctNewAlias);

        if (ctDraftActions.Count > 0)
        {
            Section.SubHeading("Actions in this trigger");
            for (var i = 0; i < ctDraftActions.Count; i++)
            {
                ImGui.PushID($"ctDraftAction_{i}");
                // A restraint action only references a restraint; its rules/moodle are edited in the Restraints tab.
                var editable = ctDraftActions[i].Kind != CustomTriggerActionKind.Restraint;
                DrawActionSummary(ctDraftActions[i], DraftActionButtonsWidth(editable));
                using (ImRaii.Disabled(i == 0))
                    if (ImGui.Button("↑"))
                        MoveDraftAction(ctDraftActions, i, i - 1, ref editingCustomTriggerActionIndex);
                ContinueRowOrWrap(ButtonWidth("↓"));
                using (ImRaii.Disabled(i == ctDraftActions.Count - 1))
                    if (ImGui.Button("↓"))
                        MoveDraftAction(ctDraftActions, i, i + 1, ref editingCustomTriggerActionIndex);
                if (editable)
                {
                    ContinueRowOrWrap(ButtonWidth("Edit"));
                    if (ImGui.Button("Edit"))
                    {
                        LoadSubActionDraft(ctDraftActions[i]);
                        editingCustomTriggerActionIndex = i;
                    }
                }
                ContinueRowOrWrap(ButtonWidth("Remove"));
                if (ImGui.Button("Remove"))
                {
                    RemoveDraftAction(ctDraftActions, i, ref editingCustomTriggerActionIndex);
                    ImGui.PopID();
                    break;
                }
                ImGui.PopID();
            }
        }

        Section.SubHeading(editingCustomTriggerActionIndex is null ? "Add an action" : "Edit action");
        var kindNames = Enum.GetNames<CustomTriggerActionKind>();
        ctNewActionKindIndex = Math.Clamp(ctNewActionKindIndex, 0, kindNames.Length - 1);
        ImGui.Combo("Action type##newCtKind", ref ctNewActionKindIndex, kindNames, kindNames.Length);
        var kind = Enum.Parse<CustomTriggerActionKind>(kindNames[ctNewActionKindIndex]);
        if (editingCustomTriggerActionIndex is not null)
            IconGlyph.WrappedColored(Theme.Accent, "Editing this action. Change its values below, then choose Save action.");

        var kindBlocked = plugin.DependencyStatus.MissingReason(DependencyGates.CustomTriggerPart(kind));
        if (kindBlocked is not null)
            IconGlyph.WrappedColored(Theme.StatusMissing, kindBlocked);
        var kindDisabled = ImRaii.Disabled(kindBlocked is not null);

        switch (kind)
        {
            case CustomTriggerActionKind.Title:
                ImGui.InputText("Text##newCtTitle", ref ctTitleText, 64);
                ImGui.Checkbox("Prefix##newCtTitle", ref ctTitleIsPrefix);
                ImGui.ColorEdit3("Color##newCtTitle", ref ctTitleColor);
                DrawGlowPicker("newCtTitle", ref ctTitleHasGlow, ref ctTitleGlow);
                using (ImRaii.Disabled(ctTitleText.Length == 0))
                {
                    if (ImGui.Button($"{(editingCustomTriggerActionIndex is null ? "Add action" : "Save action")}##newCtTitleBtn"))
                    {
                        CommitSubAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Title, TitleText = ctTitleText, TitleIsPrefix = ctTitleIsPrefix, TitleColor = ctTitleColor, TitleGlow = ctTitleHasGlow ? ctTitleGlow : null });
                        ctTitleText = "";
                        ctTitleIsPrefix = false;
                        ctTitleColor = new Vector3(1, 1, 1);
                        ctTitleHasGlow = false;
                        ctTitleGlow = new Vector3(1, 1, 1);
                    }
                }
                break;

            case CustomTriggerActionKind.Outfit:
                var designs = config.WardrobeMapping.LocalDesigns.Values.ToList();
                if (designs.Count == 0)
                {
                    IconGlyph.WrappedDisabled("No scanned designs yet - rescan in Settings (gear icon) first.");
                    break;
                }
                var designNames = designs.Select(d => d.Name).ToArray();
                ctOutfitDesignIndex = Math.Clamp(ctOutfitDesignIndex, 0, designNames.Length - 1);
                ImGui.Combo("Design##newCtOutfit", ref ctOutfitDesignIndex, designNames, designNames.Length);
                if (ImGui.Button($"{(editingCustomTriggerActionIndex is null ? "Add action" : "Save action")}##newCtOutfitBtn"))
                {
                    var design = designs[ctOutfitDesignIndex];
                    CommitSubAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Outfit, OutfitDesignId = design.DesignId, OutfitDesignName = design.Name });
                }
                break;

            case CustomTriggerActionKind.Gesture:
                if (ImGui.Button(ctSelectedGesture is null ? "Choose animation...##newCtGesture" : $"Change animation... ({ctSelectedGesture.DisplayLabel})##newCtGesture"))
                    plugin.AnimationPickerWindow.Open(entry => ctSelectedGesture = entry);
                using (ImRaii.Disabled(ctSelectedGesture is null))
                {
                    if (ImGui.Button($"{(editingCustomTriggerActionIndex is null ? "Add action" : "Save action")}##newCtGestureBtn") && ctSelectedGesture is { } chosenGesture)
                    {
                        CommitSubAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Gesture, GestureId = chosenGesture.Id, GestureAnimationName = chosenGesture.AnimationName });
                        ctSelectedGesture = null;
                    }
                }
                break;

            case CustomTriggerActionKind.Moodle:
                var statuses = config.MoodlesMapping.LocalCatalog.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
                if (statuses.Count == 0)
                {
                    IconGlyph.WrappedDisabled("No scanned Moodles statuses yet - rescan in Settings (gear icon) first.");
                    break;
                }
                var statusNames = statuses.Select(s => MoodlesTextFormat.StripMarkup(s.Name)).ToArray();
                ctMoodleStatusIndex = Math.Clamp(ctMoodleStatusIndex, 0, statusNames.Length - 1);
                ImGui.Combo("Status##newCtMoodle", ref ctMoodleStatusIndex, statusNames, statusNames.Length);
                if (ImGui.Button($"{(editingCustomTriggerActionIndex is null ? "Add action" : "Save action")}##newCtMoodleBtn"))
                {
                    var status = statuses[ctMoodleStatusIndex];
                    CommitSubAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Moodle, MoodleStatusId = status.StatusId, MoodleStatusName = status.Name });
                }
                break;

            case CustomTriggerActionKind.Restraint:
                var restraintChoices = config.RestraintMapping.Devices.Values.Select(d => (Id: d.Id, Name: d.Name, CatalogId: "", ItemId: 0UL))
                    .Concat(config.RestraintMapping.ConfiguredMods.Where(x => x.ItemId > 0 && x.Rules.Count > 0)
                        .Select(x => (Id: x.Id, Name: $"{x.Name} (Penumbra)", CatalogId: x.CatalogId, ItemId: x.ItemId!.Value))).ToList();
                if (restraintChoices.Count == 0)
                {
                    IconGlyph.WrappedDisabled("No configured restraint devices yet - configure one in the Restraints tab first.");
                    break;
                }
                var deviceNames = restraintChoices.Select(d => d.Name).ToArray();
                ctRestraintDeviceIndex = Math.Clamp(ctRestraintDeviceIndex, 0, deviceNames.Length - 1);
                ImGui.Combo("Device##newCtRestraint", ref ctRestraintDeviceIndex, deviceNames, deviceNames.Length);
                IconGlyph.HelpMarker("Toggles this restraint when the trigger fires.");
                if (ImGui.Button($"{(editingCustomTriggerActionIndex is null ? "Add action" : "Save action")}##newCtRestraintBtn"))
                {
                    var device = restraintChoices[ctRestraintDeviceIndex];
                    CommitSubAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Restraint, RestraintDeviceId = device.CatalogId.Length == 0 ? device.Id : "", RestraintCatalogId = device.CatalogId, RestraintItemId = device.ItemId, RestraintDeviceName = device.Name });
                }
                break;

            case CustomTriggerActionKind.Chat:
                ImGui.InputText("Message##newCtChat", ref ctChatText, 400);
                IconGlyph.HelpMarker("Sent exactly as typed, unmodified - start it with a slash command (e.g. /sit) or a channel prefix (e.g. /p) to use those instead of your default chat channel. Needs the Custom chat messages permission and its own acknowledgement in Settings - see the README's Automation risk section.");
                using (ImRaii.Disabled(ctChatText.Trim().Length == 0))
                {
                    if (ImGui.Button($"{(editingCustomTriggerActionIndex is null ? "Add action" : "Save action")}##newCtChatBtn"))
                    {
                        CommitSubAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Chat, ChatText = ctChatText });
                        ctChatText = "";
                    }
                }
                break;
        }
        kindDisabled.Dispose();

        ImGui.Spacing();
        ImGui.Separator();
        var duplicateAlias = triggers.Where((_, i) => i != editingCustomTriggerIndex)
            .Any(t => string.Equals(t.Alias, ctNewAlias.Trim(), StringComparison.OrdinalIgnoreCase));
        if (duplicateAlias)
            IconGlyph.WrappedColored(Theme.Warning, "A custom trigger already uses this alias.");
        using (ImRaii.Disabled(ctNewAlias.Trim().Length == 0 || IsReserved(ctNewAlias) || ctDraftActions.Count == 0 || duplicateAlias))
        {
            if (ImGui.Button(editingCustomTriggerIndex is null ? "Save trigger" : "Save changes"))
            {
                var replacement = new CustomTriggerDefinition { Alias = ctNewAlias.Trim(), Actions = ctDraftActions.Select(CloneAction).ToList() };
                int savedIndex;
                if (editingCustomTriggerIndex is { } editIndex && editIndex >= 0 && editIndex < triggers.Count)
                {
                    triggers[editIndex] = replacement;
                    savedIndex = editIndex;
                }
                else
                {
                    triggers.Add(replacement);
                    savedIndex = triggers.Count - 1;
                }
                config.Save();
                creatingCustomTrigger = false;
                customTriggerListDetail.Select($"ct:{savedIndex}");
                LoadCustomTriggerDraft(triggers, new ListDetailItem($"ct:{savedIndex}", replacement.Alias));
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##editCustomTrigger"))
        {
            if (editingCustomTriggerIndex is { } cancelIndex)
                LoadCustomTriggerDraft(triggers, new ListDetailItem($"ct:{cancelIndex}", ""));
            else
            {
                creatingCustomTrigger = false;
                ResetCustomTriggerDraft();
                customTriggerListDetail.Select(null);
            }
        }
    }

    private static string SummarizeCustomTriggerAction(CustomTriggerAction a) => CommandPresentation.Action(a);

    /// With `actionsWidth`, leaves the cursor where that row's buttons go.
    private static void DrawActionSummary(CustomTriggerAction action, float actionsWidth = 0f)
    {
        var summary = SummarizeCustomTriggerAction(action);
        ImGui.Bullet();
        ImGui.SameLine();
        if (actionsWidth > 0f)
            TextWithActions(summary, actionsWidth);
        else
            ImGui.TextWrapped(summary);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(summary);
    }

    private static float DraftActionButtonsWidth(bool editable) =>
        editable ? ActionsWidth("↑", "↓", "Edit", "Remove") : ActionsWidth("↑", "↓", "Remove");

    private static CustomTriggerAction CloneAction(CustomTriggerAction action) => new()
    {
        Kind = action.Kind,
        TitleText = action.TitleText,
        TitleIsPrefix = action.TitleIsPrefix,
        TitleColor = action.TitleColor,
        OutfitDesignId = action.OutfitDesignId,
        OutfitDesignName = action.OutfitDesignName,
        GestureId = action.GestureId,
        GestureAnimationName = action.GestureAnimationName,
        MoodleStatusId = action.MoodleStatusId,
        MoodleStatusName = action.MoodleStatusName,
        CustomMoodle = action.CustomMoodle?.Clone(),
        RestraintDeviceId = action.RestraintDeviceId,
        RestraintDeviceName = action.RestraintDeviceName,
        RestraintCatalogId = action.RestraintCatalogId,
        RestraintItemId = action.RestraintItemId,
        ChatText = action.ChatText,
    };

    private void CommitSubAction(CustomTriggerAction action)
    {
        if (editingCustomTriggerActionIndex is { } index && index >= 0 && index < ctDraftActions.Count)
            ctDraftActions[index] = action;
        else
            ctDraftActions.Add(action);
        editingCustomTriggerActionIndex = null;
    }

    private void CommitOwnerAction(CustomTriggerAction action)
    {
        if (editingOwnerActionIndex is { } index && index >= 0 && index < ctqDraftActions.Count)
            ctqDraftActions[index] = action;
        else
            ctqDraftActions.Add(action);
        editingOwnerActionIndex = null;
    }

    private static void MoveDraftAction(List<CustomTriggerAction> actions, int from, int to, ref int? editingIndex)
    {
        (actions[from], actions[to]) = (actions[to], actions[from]);
        if (editingIndex == from) editingIndex = to;
        else if (editingIndex == to) editingIndex = from;
    }

    private static void RemoveDraftAction(List<CustomTriggerAction> actions, int index, ref int? editingIndex)
    {
        actions.RemoveAt(index);
        if (editingIndex == index) editingIndex = null;
        else if (editingIndex > index) editingIndex--;
    }

    private void LoadSubActionDraft(CustomTriggerAction action)
    {
        ctNewActionKindIndex = (int)action.Kind;
        ctTitleText = action.TitleText;
        ctTitleIsPrefix = action.TitleIsPrefix;
        ctTitleColor = action.TitleColor;
        ctTitleHasGlow = action.TitleGlow is not null;
        ctTitleGlow = action.TitleGlow ?? new Vector3(1, 1, 1);
        var designs = plugin.Configuration.WardrobeMapping.LocalDesigns.Values.ToList();
        ctOutfitDesignIndex = Math.Max(0, designs.FindIndex(d => d.DesignId == action.OutfitDesignId));
        ctSelectedGesture = plugin.Configuration.GestureMapping.LocalCatalog.Values
            .FirstOrDefault(g => string.Equals(g.Id, action.GestureId, StringComparison.OrdinalIgnoreCase));
        var statuses = plugin.Configuration.MoodlesMapping.LocalCatalog.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ctMoodleStatusIndex = Math.Max(0, statuses.FindIndex(s => s.StatusId == action.MoodleStatusId));
        var devices = plugin.Configuration.RestraintMapping.Devices.Values.ToList();
        ctRestraintDeviceIndex = Math.Max(0, devices.FindIndex(d => d.Id == action.RestraintDeviceId));
        ctChatText = action.ChatText;
    }

    private void LoadOwnerActionDraft(CustomTriggerAction action)
    {
        ctqKindIndex = (int)action.Kind;
        ctqTitleText = action.TitleText;
        ctqTitleIsPrefix = action.TitleIsPrefix;
        ctqTitleColor = action.TitleColor;
        ctqTitleHasGlow = action.TitleGlow is not null;
        ctqTitleGlow = action.TitleGlow ?? new Vector3(1, 1, 1);
        ctqOutfitName = action.OutfitDesignName;
        ctqGestureName = action.GestureAnimationName;
        ctqMoodleName = MoodlesTextFormat.StripMarkup(action.MoodleStatusName);
        ctqRestraintName = action.RestraintDeviceName;
        ctqChatText = action.ChatText;
    }

    /// Actions are typed by name only: the Owner's install can't see the Sub's catalogs.
    private void DrawCustomTriggerQuickSection(bool canSend)
    {
        IconGlyph.WrappedDisabled("Bundle actions under one name, using the names your Sub gave you.");

        ItemWidth(220);
        ImGui.InputText("Label##ctqLabel", ref ctqLabel, 32);
        IconGlyph.HelpMarker("Your own reference name for this bundle - never matched against anything on your Sub's side.");

        if (ctqDraftActions.Count > 0)
        {
            Section.SubHeading("Actions in this bundle");
            for (var i = 0; i < ctqDraftActions.Count; i++)
            {
                ImGui.PushID($"ctqDraftAction_{i}");
                // A restraint is edited in the Restraints tab, not from a bundle.
                var editable = ctqDraftActions[i].Kind != CustomTriggerActionKind.Restraint;
                DrawActionSummary(ctqDraftActions[i], DraftActionButtonsWidth(editable));
                using (ImRaii.Disabled(i == 0))
                    if (ImGui.Button("↑"))
                        MoveDraftAction(ctqDraftActions, i, i - 1, ref editingOwnerActionIndex);
                ContinueRowOrWrap(ButtonWidth("↓"));
                using (ImRaii.Disabled(i == ctqDraftActions.Count - 1))
                    if (ImGui.Button("↓"))
                        MoveDraftAction(ctqDraftActions, i, i + 1, ref editingOwnerActionIndex);
                if (editable)
                {
                    ContinueRowOrWrap(ButtonWidth("Edit"));
                    if (ImGui.Button("Edit"))
                    {
                        LoadOwnerActionDraft(ctqDraftActions[i]);
                        editingOwnerActionIndex = i;
                    }
                }
                ContinueRowOrWrap(ButtonWidth("Remove"));
                if (ImGui.Button("Remove"))
                {
                    RemoveDraftAction(ctqDraftActions, i, ref editingOwnerActionIndex);
                    ImGui.PopID();
                    break;
                }
                ImGui.PopID();
            }
        }

        Section.SubHeading(editingOwnerActionIndex is null ? "Add an action" : "Edit action");
        var kindNames = Enum.GetNames<CustomTriggerActionKind>();
        ctqKindIndex = Math.Clamp(ctqKindIndex, 0, kindNames.Length - 1);
        ItemWidth(160);
        ImGui.Combo("Action type##ctqKind", ref ctqKindIndex, kindNames, kindNames.Length);
        var kind = Enum.Parse<CustomTriggerActionKind>(kindNames[ctqKindIndex]);
        if (editingOwnerActionIndex is not null)
            IconGlyph.WrappedColored(Theme.Accent, "Editing this action. Change its values below, then choose Save.");

        switch (kind)
        {
            case CustomTriggerActionKind.Title:
                ItemWidth(220);
                ImGui.InputText("Text##ctqTitle", ref ctqTitleText, 64);
                ImGui.Checkbox("Prefix##ctqTitle", ref ctqTitleIsPrefix);
                ImGui.ColorEdit3("Color##ctqTitle", ref ctqTitleColor);
                DrawGlowPicker("ctqTitle", ref ctqTitleHasGlow, ref ctqTitleGlow);
                using (ImRaii.Disabled(ctqTitleText.Trim().Length == 0))
                {
                    if (ImGui.Button($"{(editingOwnerActionIndex is null ? "Add" : "Save")}##ctqTitleBtn"))
                    {
                        CommitOwnerAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Title, TitleText = ctqTitleText.Trim(), TitleIsPrefix = ctqTitleIsPrefix, TitleColor = ctqTitleColor, TitleGlow = ctqTitleHasGlow ? ctqTitleGlow : null });
                        ctqTitleText = "";
                        ctqTitleIsPrefix = false;
                        ctqTitleColor = new Vector3(1, 1, 1);
                        ctqTitleHasGlow = false;
                        ctqTitleGlow = new Vector3(1, 1, 1);
                    }
                }
                break;

            case CustomTriggerActionKind.Outfit:
                ItemWidth(220);
                ImGui.InputText("Design name##ctqOutfit", ref ctqOutfitName, 32);
                IconGlyph.HelpMarker("Type the exact wardrobe design name your Sub told you.");
                using (ImRaii.Disabled(ctqOutfitName.Trim().Length == 0))
                {
                    if (ImGui.Button($"{(editingOwnerActionIndex is null ? "Add" : "Save")}##ctqOutfitBtn"))
                    {
                        CommitOwnerAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Outfit, OutfitDesignName = ctqOutfitName.Trim() });
                        ctqOutfitName = "";
                    }
                }
                break;

            case CustomTriggerActionKind.Gesture:
                ItemWidth(220);
                ImGui.InputText("Animation name##ctqGesture", ref ctqGestureName, 32);
                IconGlyph.HelpMarker("Type the exact animation name your Sub told you.");
                using (ImRaii.Disabled(ctqGestureName.Trim().Length == 0))
                {
                    if (ImGui.Button($"{(editingOwnerActionIndex is null ? "Add" : "Save")}##ctqGestureBtn"))
                    {
                        CommitOwnerAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Gesture, GestureAnimationName = ctqGestureName.Trim() });
                        ctqGestureName = "";
                    }
                }
                break;

            case CustomTriggerActionKind.Moodle:
            {
                // The bundle carries a custom moodle whole, so it keeps working if the saved one is edited or deleted.
                var customs = plugin.Configuration.QuickCommands.CustomMoodles.Where(c => c.CustomMoodle is not null).ToList();
                if (customs.Count > 0)
                {
                    var names = new[] { "One of your Sub's moodles" }.Concat(customs.Select(c => $"Yours: {c.Label}")).ToArray();
                    ctqCustomMoodleIndex = Math.Clamp(ctqCustomMoodleIndex, -1, customs.Count - 1);
                    var pick = ctqCustomMoodleIndex + 1;
                    ItemWidth(220);
                    if (ImGui.Combo("Moodle##ctqMoodleSource", ref pick, names, names.Length))
                        ctqCustomMoodleIndex = pick - 1;
                    if (ctqCustomMoodleIndex >= 0)
                    {
                        IconGlyph.HelpMarker("Your Sub has to allow moodles you write.");
                        if (ImGui.Button($"{(editingOwnerActionIndex is null ? "Add" : "Save")}##ctqCustomMoodleBtn"))
                        {
                            var custom = customs[ctqCustomMoodleIndex].CustomMoodle!.Clone();
                            CommitOwnerAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Moodle, MoodleStatusName = custom.Title, CustomMoodle = custom });
                            ctqCustomMoodleIndex = -1;
                        }
                        break;
                    }
                }
                ItemWidth(220);
                ImGui.InputText("Status name##ctqMoodle", ref ctqMoodleName, 32);
                IconGlyph.HelpMarker("Type the exact Moodles status name your Sub told you.");
                using (ImRaii.Disabled(ctqMoodleName.Trim().Length == 0))
                {
                    if (ImGui.Button($"{(editingOwnerActionIndex is null ? "Add" : "Save")}##ctqMoodleBtn"))
                    {
                        CommitOwnerAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Moodle, MoodleStatusName = ctqMoodleName.Trim() });
                        ctqMoodleName = "";
                    }
                }
                break;
            }

            case CustomTriggerActionKind.Restraint:
                ItemWidth(220);
                ImGui.InputText("Device name##ctqRestraint", ref ctqRestraintName, 32);
                IconGlyph.HelpMarker("The restraint name your Sub gave you. Always applies; release it from Restraints.");
                using (ImRaii.Disabled(ctqRestraintName.Trim().Length == 0))
                {
                    if (ImGui.Button($"{(editingOwnerActionIndex is null ? "Add" : "Save")}##ctqRestraintBtn"))
                    {
                        CommitOwnerAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Restraint, RestraintDeviceName = ctqRestraintName.Trim() });
                        ctqRestraintName = "";
                    }
                }
                break;

            case CustomTriggerActionKind.Chat:
                ItemWidth(320);
                ImGui.InputText("Message##ctqChat", ref ctqChatText, 400);
                IconGlyph.HelpMarker("Sent exactly as typed, unmodified - start it with a slash command (e.g. /sit) or a channel prefix (e.g. /p) to use those instead of the default chat channel. Needs your Sub's Custom chat messages permission and its own acknowledgement (see the README's Automation risk section).");
                using (ImRaii.Disabled(ctqChatText.Trim().Length == 0))
                {
                    if (ImGui.Button($"{(editingOwnerActionIndex is null ? "Add" : "Save")}##ctqChatBtn"))
                    {
                        CommitOwnerAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Chat, ChatText = ctqChatText });
                        ctqChatText = "";
                    }
                }
                break;
        }

        // Only a bundle with a restraint in it has anything to lock.
        var bundleHasRestraint = ctqDraftActions.Any(a => a.Kind == CustomTriggerActionKind.Restraint);
        if (bundleHasRestraint)
        {
            Section.SubHeading("Restraint lock");
            OwnerLockOption.Draw("ctqCustomTrigger", ref ctqLockSeconds);
            OwnerLockOption.DrawKey("ctqCustomTrigger", ref ctqLockKey);
            // Only a restraint whose rules travel in the bundle has somewhere to carry the setting.
            if (ctqDraftActions.Any(a => a.Kind == CustomTriggerActionKind.Restraint && a.RestraintRules is not null))
                OwnerLockOption.DrawStruggle("ctqCustomTrigger", ref ctqStruggle, ref ctqStrugglePenalty, ctqLockSeconds is not null);
            else
                IconGlyph.WrappedDisabled("Struggling can only be allowed for a restraint picked from your Sub's synced list, which carries its rules.");
        }
        var bundleLockSeconds = bundleHasRestraint ? ctqLockSeconds : null;
        var bundleStruggle = bundleHasRestraint ? ctqStruggle : StruggleLevel.None;
        var bundlePenalty = bundleLockSeconds is null ? 0 : ctqStrugglePenalty;
        var bundleKey = bundleHasRestraint && ctqLockKey.Trim().Length > 0 ? ctqLockKey : null;

        ImGui.Spacing();
        ImGui.Separator();
        if (ctqLabel.Trim().Length > 0 && ctqDraftActions.Count > 0)
        {
            var command = CustomTriggerCommand.BuildCastCommand(ctqLabel.Trim(), ctqDraftActions);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(editingOwnerBundle is null ? "Send or save this bundle:" : "Update this saved bundle:");
            ContinueRowOrWrap(ButtonWidth("Send"));
            DrawSendCopyButtons(OwnerLockOption.Apply(RestraintStruggle.Insert(command, new StruggleSetting(bundleStruggle, bundlePenalty)), bundleLockSeconds, bundleKey), canSend, "ctqCustomTrigger");
            ContinueRowOrWrap(ButtonWidth("Save bundle"));
            var aliases = plugin.Configuration.QuickCommands.Aliases;
            var stale = editingOwnerBundle is not null && !aliases.Contains(editingOwnerBundle);
            var duplicate = aliases.Any(q => !ReferenceEquals(q, editingOwnerBundle) &&
                (string.Equals(q.Label, ctqLabel.Trim(), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(q.Command, command, StringComparison.OrdinalIgnoreCase)));
            var safe = ChatComposer.AllFit(plugin.ChatComposer.ComposeAll(command));
            using (ImRaii.Disabled(stale || duplicate || !safe))
            {
                if (ImGui.Button($"{(editingOwnerBundle is null ? "Save bundle" : "Save changes")}##ctqSave"))
                {
                    if (editingOwnerBundle is null)
                        aliases.Add(new QuickCommand { Label = ctqLabel.Trim(), Command = command, LockSeconds = bundleLockSeconds, LockKey = bundleKey, Struggle = bundleStruggle, StrugglePenaltyMinutes = bundlePenalty });
                    else
                    {
                        editingOwnerBundle.Label = ctqLabel.Trim();
                        editingOwnerBundle.Command = command;
                        editingOwnerBundle.LockSeconds = bundleLockSeconds;
                        editingOwnerBundle.LockKey = bundleKey;
                        editingOwnerBundle.Struggle = bundleStruggle;
                        editingOwnerBundle.StrugglePenaltyMinutes = bundlePenalty;
                    }
                    plugin.Configuration.Save();
                    ClearOwnerBundleDraft();
                }
            }
            if (stale) IconGlyph.WrappedColored(Theme.Warning, "This saved bundle was removed while it was being edited.");
            else if (duplicate) IconGlyph.WrappedColored(Theme.Warning, "Another saved bundle already uses this label or command.");
            else if (!safe) IconGlyph.WrappedColored(Theme.Warning, "This bundle is too long for a safe chat payload.");
            ContinueRowOrWrap(ButtonWidth("Cancel"));
            if (ImGui.Button($"{(editingOwnerBundle is null ? "Clear bundle" : "Cancel")}##ctq"))
                ClearOwnerBundleDraft();
        }
        else
        {
            IconGlyph.WrappedColored(Theme.Warning, "Give this bundle a label and at least one action before it can be sent.");
        }
    }

    private void ClearOwnerBundleDraft()
    {
        ctqDraftActions.Clear();
        ctqLabel = "";
        ctqLockSeconds = null;
        ctqLockKey = "";
        ctqStruggle = StruggleLevel.None;
        ctqStrugglePenalty = 0;
        editingOwnerActionIndex = null;
        editingOwnerBundle = null;
    }

    private void BeginOwnerBundleEdit(QuickCommand command)
    {
        const string prefix = "customtrigger cast ";
        if (!command.Command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !CustomTriggerCommand.TryParseCastCommand(command.Command[prefix.Length..], out var label, out var actions))
            return;

        editingOwnerBundle = command;
        ctqLabel = label;
        ctqLockSeconds = command.LockSeconds;
        ctqLockKey = command.LockKey ?? "";
        ctqStruggle = command.Struggle;
        ctqStrugglePenalty = command.StrugglePenaltyMinutes;
        ctqDraftActions.Clear();
        ctqDraftActions.AddRange(actions.Select(CloneAction));
        editingOwnerActionIndex = null;
    }

    private void DrawFreeformComposer(bool canSend)
    {
        IconGlyph.WrappedDisabled("Type an alias your Sub gave you, or a direct command. Add Command saves it as a button.");

        ImGui.InputText("Command", ref commandInput, 96);
        IconGlyph.HelpMarker("Either a short alias name your Sub defined, or a direct override: \"title create <text>\" / \"title clear\", \"outfit lock <name>\" / \"outfit unlock\", \"gesture <name>\".");

        var composed = plugin.ChatComposer.Compose(commandInput.Trim());
        ImGui.TextUnformatted("Preview:");
        ImGui.TextWrapped(composed);

        var hasCommand = commandInput.Trim().Length > 0;
        using (ImRaii.Disabled(!canSend || !hasCommand))
        {
            if (ImGui.Button("Send"))
                plugin.ChatSender.Send(composed);
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!hasCommand))
        {
            if (ImGui.Button("Copy to clipboard"))
                ImGui.SetClipboardText(composed);
        }
        ImGui.SameLine();
        var aliasQuick = plugin.Configuration.QuickCommands.Aliases;
        using (ImRaii.Disabled(!hasCommand))
        {
            if (ImGui.Button("Add Command##alias"))
            {
                var text = commandInput.Trim();
                aliasQuick.Add(new QuickCommand { Label = text, Command = text });
                plugin.Configuration.Save();
                commandInput = "";
            }
        }
        IconGlyph.HelpMarker("Send sends it as one /tell now. Copy only copies it. Add Command saves it as a button.");
    }

    private readonly ListDetail ownerBundleList = new();
    private const string NewBundleKey = "new:bundle";

    private static bool IsBundle(QuickCommand cmd) => cmd.Command.StartsWith("customtrigger cast ", StringComparison.OrdinalIgnoreCase);

    /// Saved bundles open in the bundle builder; your Sub's own trigger words use the plain command editor.
    private void DrawOwnerBundles(bool canSend)
    {
        var aliases = plugin.Configuration.QuickCommands.Aliases;
        var items = aliases.Select((cmd, i) => new ListDetailItem($"alias:{i}", cmd.Label, IsBundle(cmd) ? null : "your Sub's word")).ToList();
        if (ownerBundleList.Selected == NewBundleKey)
            items.Add(new ListDetailItem(NewBundleKey, "New bundle"));
        QuickCommand? Find(string? key) => key is not null && key.StartsWith("alias:") && int.TryParse(key[6..], out var i) && i < aliases.Count ? aliases[i] : null;

        ownerBundleList.Draw("ownerBundles", items,
            () =>
            {
                if (!ImGui.Button("+ New##ownerBundle"))
                    return;
                CancelQuickCommandEdit();
                ClearOwnerBundleDraft();
                ownerBundleList.Select(NewBundleKey);
            },
            item =>
            {
                if (item is null)
                {
                    IconGlyph.WrappedDisabled("Choose a bundle, or use + New to build one.");
                    return;
                }
                if (item.Key == NewBundleKey)
                {
                    var before = aliases.Count;
                    using (Section.Begin("ctqBuilder", "Build a bundle"))
                        DrawCustomTriggerQuickSection(canSend);
                    if (aliases.Count > before)
                        ownerBundleList.Select($"alias:{aliases.Count - 1}");
                    return;
                }
                if (Find(item.Key) is not { } cmd)
                    return;
                if (!IsBundle(cmd))
                {
                    DrawSavedQuickDetail(cmd, aliases, canSend, ownerBundleList, null);
                    return;
                }
                ImGui.PushID($"bundle_{cmd.Label}");
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.AccentHover, cmd.Label);
                ContinueRowOrWrap(StarWidth);
                DrawFavoriteToggle(cmd, $"{cmd.Label}_{cmd.Command}");
                ContinueRowOrWrap(ButtonWidth("Delete"));
                if (ImGui.Button("Delete"))
                {
                    aliases.Remove(cmd);
                    ClearOwnerBundleDraft();
                    plugin.Configuration.Save();
                    ownerBundleList.Select(null);
                    ImGui.PopID();
                    return;
                }
                // The builder's Save ends the edit; reopen it so the detail pane always shows it.
                if (!ReferenceEquals(editingOwnerBundle, cmd))
                    BeginOwnerBundleEdit(cmd);
                using (Section.Begin("ctqBuilder"))
                    DrawCustomTriggerQuickSection(canSend);
                ImGui.PopID();
            },
            () => ownerBundleList.Selected == NewBundleKey ? ctqLabel.Trim().Length > 0 || ctqDraftActions.Count > 0
                : Find(ownerBundleList.Selected) is { } cmd && (IsBundle(cmd)
                    ? ReferenceEquals(editingOwnerBundle, cmd)
                      && (CustomTriggerCommand.BuildCastCommand(ctqLabel.Trim(), ctqDraftActions) != cmd.Command
                          || ctqLockSeconds != cmd.LockSeconds || ctqStruggle != cmd.Struggle || (ctqLockKey.Trim().Length > 0 ? ctqLockKey : null) != cmd.LockKey)
                    : QuickEditDirty(cmd)),
            item =>
            {
                CancelQuickCommandEdit();
                ClearOwnerBundleDraft();
                if (Find(item?.Key) is { } cmd)
                {
                    if (IsBundle(cmd))
                        BeginOwnerBundleEdit(cmd);
                    else
                        BeginQuickCommandEdit(cmd, aliases);
                }
            },
            "Nothing saved yet - bundles your Sub shares, and ones you build with + New, show up here.");
    }
}
