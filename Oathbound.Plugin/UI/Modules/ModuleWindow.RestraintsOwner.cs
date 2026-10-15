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
    /// Null = no moodle; an ad-hoc device has no Sub-side default.
    private string? adHocMoodleOverride;

    /// Null = Permanent.
    private int? adHocLockSeconds;
    private string adHocLockKey = "";
    private StruggleLevel adHocStruggle;
    private int adHocStrugglePenalty;
    private bool adHocRedraw;
    private string ownerRestraintSearch = "";

    /// Gear is optional; a rules-only ad-hoc device leaves mod/slot/item unset.
    private string newAdHocLabel = "";

    private readonly RestraintRuleEditState newAdHocRuleEdit = new();

    private void DrawRestraintQuickSection(bool canSend)
    {
        DrawRestraintQuickSectionBody(canSend);
    }

    private readonly ListDetail ownerRestraintList = new();
    private bool creatingOwnerAdHoc;
    private const string NewAdHocKey = "new:adhoc";

    private void DrawRestraintQuickSectionBody(bool canSend)
    {
        var quick = plugin.Configuration.QuickCommands.Restraints;
        DrawSectionTitleRow(FontAwesomeIcon.Handcuffs, "Restraints", quick.Count > 0, "restraintsQuick", () =>
        {
            foreach (var cmd in quick)
                ImageTile.Delete(cmd.ImageFile);
            quick.Clear();
            ImageTile.DeleteUnusedShared([]);
            plugin.Configuration.Save();
        });

        using (Section.Begin("restraintQuickCommands", "Commands"))
        {
            DrawFixedQuickRow("Restraint unlock", "restraint unlock", canSend, FixedActionIds.RestraintUnlock);
            IconGlyph.HelpMarker("Releases every restraint on your Sub.");
        }

        using (Section.Begin("restraintQuickConfigured", "Your restraints"))
        {
            ownerRestraintList.Draw("ownerRestraints", BuildOwnerRestraintItems(quick), () => DrawOwnerRestraintToolbar(quick),
                item => DrawOwnerRestraintDetail(item, quick, canSend), () => OwnerRestraintDirty(quick), item => OnOwnerRestraintSelected(item, quick),
                "Nothing yet. Use + New to add one of your Sub's restraint mods or a rules-only restraint.");
        }

        DrawDrawnRestraintsSection(plugin.Configuration);
    }

    private List<ListDetailItem> BuildOwnerRestraintItems(List<QuickCommand> quick)
    {
        var sent = plugin.OwnerStatusEstimates.ForActivePairing?.Restraints;
        string? Marker(string label) => sent?.FirstOrDefault(r => string.Equals(r.Reference, label, StringComparison.OrdinalIgnoreCase)) is { } r
            ? r.ExpiresAtUtc is { } until ? $"sent, {RestraintLock.Format(until - DateTime.UtcNow)} left" : "sent"
            : null;

        var items = new List<ListDetailItem>();
        foreach (var cmd in quick.Where(x => x.RestraintCatalogId is not null))
        {
            var picture = cmd.ImageFile ?? cmd.SharedImageFile;
            items.Add(new ListDetailItem($"mod:{cmd.Label}", cmd.Label, "mod", Marker(cmd.Label), picture, picture is null ? FontAwesomeIcon.Image : null));
        }
        // The Sub's rules-only restraints arrive as self-contained `restraint wear` commands.
        foreach (var cmd in quick.Where(x => x.RestraintCatalogId is null))
            items.Add(new ListDetailItem($"shared:{cmd.Label}", cmd.Label, "rules-only", Marker(cmd.Label), null, FontAwesomeIcon.Handcuffs));
        if (creatingOwnerAdHoc)
            items.Add(new ListDetailItem(NewAdHocKey, "One-off rules-only restraint", "rules-only", null, null, FontAwesomeIcon.Handcuffs));
        return items;
    }

    private void DrawOwnerRestraintToolbar(List<QuickCommand> quick)
    {
        if (ImGui.Button("+ New##ownerRestraint"))
        {
            ownerRestraintSearch = "";
            ImGui.OpenPopup("newOwnerRestraint");
        }
        if (!ImGui.BeginPopup("newOwnerRestraint"))
            return;

        if (ImGui.Selectable("One-off rules-only restraint (no gear)"))
        {
            creatingOwnerAdHoc = true;
            ownerRestraintList.Select(NewAdHocKey);
        }
        ImGui.Separator();
        ImGui.TextDisabled("Or one of your Sub's restraint mods:");
        ImGui.SetNextItemWidth(Scaled(260));
        ImGui.InputTextWithHint("##ownerRestraintSearch", "Search restraint mods...", ref ownerRestraintSearch, 128);
        var catalog = plugin.Configuration.RestraintMapping.ImportedPeerCatalog.Values;
        using (ImRaii.Child("restraintModBrowser", new Vector2(Scaled(260), Scaled(200)), true))
        {
            var mods = catalog.Where(x => string.IsNullOrWhiteSpace(ownerRestraintSearch) || x.ModName.Contains(ownerRestraintSearch.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.ModName).ToList();
            if (mods.Count == 0)
                IconGlyph.WrappedDisabled(catalog.Count == 0 ? "Your Sub hasn't shared any restraint mods yet." : "Nothing matches.");
            foreach (var entry in mods)
            {
                if (!ImGui.Selectable($"{entry.ModName}##restraintMod_{entry.Id}"))
                    continue;
                // Rows are keyed by Label, so a repeated mod gets a distinct one.
                var count = quick.Count(x => x.RestraintCatalogId == entry.Id);
                var label = count == 0 ? entry.ModName : $"{entry.ModName} ({count + 1})";
                quick.Add(new QuickCommand
                {
                    Label = label,
                    Command = "",
                    Source = ImportSource.Manual,
                    Target = entry.Id,
                    RestraintCatalogId = entry.Id,
                });
                plugin.Configuration.Save();
                restraintRuleEdits[label] = new RestraintRuleEditState();
                creatingOwnerAdHoc = false;
                ownerRestraintList.Select($"mod:{label}");
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.EndPopup();
    }

    private static QuickCommand? FindOwnerRestraint(List<QuickCommand> quick, string? key) =>
        key is null ? null
        : key.StartsWith("mod:") ? quick.FirstOrDefault(x => x.RestraintCatalogId is not null && $"mod:{x.Label}" == key)
        : key.StartsWith("shared:") ? quick.FirstOrDefault(x => x.RestraintCatalogId is null && $"shared:{x.Label}" == key)
        : null;

    private void OnOwnerRestraintSelected(ListDetailItem? item, List<QuickCommand> quick)
    {
        if (item?.Key != NewAdHocKey)
            creatingOwnerAdHoc = false;
        if (FindOwnerRestraint(quick, item?.Key) is { RestraintCatalogId: not null } cmd)
            restraintRuleEdits[cmd.Label] = FromRules(cmd.RestraintRules);
    }

    private bool OwnerRestraintDirty(List<QuickCommand> quick)
    {
        var key = ownerRestraintList.Selected;
        if (key == NewAdHocKey)
            return newAdHocLabel.Trim().Length > 0 || HasAnyRule(newAdHocRuleEdit);
        return FindOwnerRestraint(quick, key) is { RestraintCatalogId: not null } cmd
            && restraintRuleEdits.TryGetValue(cmd.Label, out var edit)
            && RestraintCommand.EncodeRuleTokens(ToRules(edit)) != RestraintCommand.EncodeRuleTokens(cmd.RestraintRules ?? []);
    }

    private void DrawOwnerRestraintDetail(ListDetailItem? item, List<QuickCommand> quick, bool canSend)
    {
        if (item is null)
        {
            IconGlyph.WrappedDisabled("Choose a restraint, or use + New to add one.");
            return;
        }
        if (item.Key == NewAdHocKey)
        {
            DrawDetailHeader("One-off rules-only restraint", "Sent with its rules, not saved", null, FontAwesomeIcon.Handcuffs, null, () =>
            {
                if (ImGui.Button("Cancel##adHocRestraint"))
                {
                    creatingOwnerAdHoc = false;
                    ownerRestraintList.Select(null);
                }
            });
            DrawAdHocRestraintSection(canSend);
            return;
        }
        if (FindOwnerRestraint(quick, item.Key) is not { } cmd)
            return;
        if (cmd.RestraintCatalogId is null)
        {
            DrawDetailHeader(cmd.Label, "Rules-only restraint from your Sub", null, FontAwesomeIcon.Handcuffs, null, () => { });
            DrawSavedQuickRow(cmd, quick, canSend);
            if (cmd.RestraintRules is { Count: > 0 })
                DrawOwnerRedraw(cmd);
            return;
        }
        DrawOwnerModRestraintDetail(cmd, quick, canSend);
    }

    private void DrawOwnerModRestraintDetail(QuickCommand cmd, List<QuickCommand> list, bool canSend)
    {
        ImGui.PushID($"restraintQuick_{cmd.Label}");
        var hasRules = cmd.RestraintRules is { Count: > 0 };
        var hasEquipment = cmd.RestraintItemId > 0 && GlamourerIpc.GetItemSlot((uint)cmd.RestraintItemId.Value) is not null;
        var catalogAvailable = cmd.RestraintCatalogId is not { } availableCatalogId ||
            plugin.Configuration.RestraintMapping.ImportedPeerCatalog.ContainsKey(availableCatalogId) ||
            (plugin.Configuration.Role == PluginRole.Sub && plugin.Configuration.RestraintMapping.LocalCatalog.ContainsKey(availableCatalogId));
        if (!restraintRuleEdits.TryGetValue(cmd.Label, out var edit))
            restraintRuleEdits[cmd.Label] = edit = FromRules(cmd.RestraintRules);

        var deleted = false;
        DrawDetailHeader(cmd.Label, cmd.ImageFile is null && cmd.SharedImageFile is not null ? "Mod restraint · your Sub's picture" : "Mod restraint",
            cmd.ImageFile ?? cmd.SharedImageFile, FontAwesomeIcon.Image,
            () => ImageTile.DrawPicker("restraintQuickPicture", cmd.ImageFile, plugin.FileDialogManager, plugin.Snapshot, file =>
            {
                cmd.ImageFile = file;
                plugin.Configuration.Save();
            }),
            () =>
            {
                DrawFavoriteToggle(cmd, cmd.Label);
                ImGui.SameLine();
                // "Delete", not "Remove": on the Owner's side "Remove" reads as "take this restraint off my Sub".
                if (ImGui.Button("Delete"))
                {
                    ImageTile.Delete(cmd.ImageFile);
                    list.Remove(cmd);
                    ImageTile.DeleteUnusedShared(list.Select(c => c.SharedImageFile));
                    restraintRuleEdits.Remove(cmd.Label);
                    plugin.Configuration.Save();
                    deleted = true;
                }
            });
        if (deleted)
        {
            ImGui.PopID();
            return;
        }
        if (cmd.SubHasPicture && cmd.SharedImageFile is null && cmd.ImageFile is null)
            IconGlyph.WrappedDisabled("Your Sub has a picture for this restraint, but it wasn't shared with you.");
        else if (cmd.ImageFile is null && cmd.SharedPictureRef is not null && !ImageTile.SharedExists(cmd.SharedImageFile))
            IconGlyph.WrappedDisabled("Your Sub's picture for this restraint hasn't downloaded yet - Check now on the Sync tab tries again.");

        Section.SubHeading("Send");
        using (ImRaii.Disabled(!hasRules || !hasEquipment || !catalogAvailable))
            DrawSendOnly(OwnerMoodleOverride.ForSend(plugin.Configuration, cmd), canSend, $"enable_{cmd.Label}", "Enable & lock");
        OwnerLockOption.DrawInline($"restraintQuick_{cmd.Label}", cmd, plugin.Configuration);
        if (!hasRules)
            IconGlyph.WrappedColored(Theme.Warning, "No rules assigned yet - set them below and save before enabling it.");
        if (!hasEquipment)
            IconGlyph.WrappedColored(Theme.Warning, "Choose the Glamourer item this mod should equip and lock.");
        if (!catalogAvailable)
            IconGlyph.WrappedColored(Theme.Warning, "This restraint mod is no longer present in the latest shared catalog. Choose it again after the Sub shares it.");

        Section.SubHeading("Name & item");
        // Committed only when editing ends: the row's ImGui ID is keyed by the label.
        if (DrawDeferredTextInput("Name##restraintQuickLabel", cmd, cmd.Label, 80, out var labelBuffer))
        {
            var trimmedLabel = labelBuffer.Trim();
            var oldLabel = cmd.Label;
            if (trimmedLabel.Length > 0 && !list.Any(x => x != cmd && string.Equals(x.Label, trimmedLabel, StringComparison.OrdinalIgnoreCase)))
            {
                cmd.Label = trimmedLabel;
                if (restraintRuleEdits.Remove(oldLabel, out var editState))
                    restraintRuleEdits[trimmedLabel] = editState;
                plugin.Configuration.Save();
                ownerRestraintList.Select($"mod:{trimmedLabel}");
            }
        }
        IconGlyph.HelpMarker("Your own label for this restraint - rename it so you can tell entries for the same mod apart.");
        TextWithActions($"Glamourer item: {(cmd.RestraintItemId is { } equippedItem ? GetItemName(equippedItem) : "(none chosen)")}", ButtonWidth("Choose item..."));
        if (ImGui.Button("Choose item...##restraintQuick"))
        {
            var catalogEntry = plugin.Configuration.RestraintMapping.ImportedPeerCatalog.GetValueOrDefault(cmd.RestraintCatalogId ?? "");
            plugin.ItemPickerWindow.OpenForItemIds(cmd.Label, catalogEntry?.ChangedItemIds.ToHashSet() ?? [], (chosenId, _) =>
            {
                cmd.RestraintItemId = chosenId;
                plugin.Configuration.Save();
            });
        }

        Section.SubHeading("Restrictions");
        DrawRestraintRuleCheckboxes(edit, $"restraintQuickRule_{cmd.Label}");

        Section.SubHeading("Attached moodle");
        var restraintMoodle = cmd.MoodleOverride;
        OwnerMoodleOverride.Draw($"restraintQuick_{cmd.Label}", plugin.Configuration, ref restraintMoodle);
        if (restraintMoodle != cmd.MoodleOverride)
        {
            cmd.MoodleOverride = restraintMoodle;
            plugin.Configuration.Save();
        }
        DrawOwnerRedraw(cmd);

        var hasAnyRule = HasAnyRule(edit);
        var boundAnimationsConfigured = BoundAnimationsConfigured(edit);
        if (hasAnyRule && !boundAnimationsConfigured)
            IconGlyph.WrappedColored(Theme.Warning, "Choose an animation for every checked Arms/Legs/Full Body Cuffed rule before saving.");

        ImGui.Spacing();
        ImGui.Separator();
        using (ImRaii.Disabled(!hasAnyRule || !boundAnimationsConfigured || !hasEquipment))
        {
            if (ImGui.Button("Save rules##restraintQuickRule"))
            {
                var rules = ToRules(edit);
                cmd.RestraintRules = rules;
                cmd.Command = cmd.RestraintCatalogId is { } catalogId && cmd.RestraintItemId is { } itemId
                    ? RestraintCommand.BuildCatalogLockCommand(catalogId, cmd.Label, itemId, rules, cmd.RestraintRedraw)
                    : RestraintCommand.BuildLockCommand(cmd.Label, rules, cmd.RestraintRedraw);
                plugin.Configuration.Save();
                restraintRuleEdits[cmd.Label] = FromRules(rules);
            }
        }

        ImGui.PopID();
    }

    /// Rules-only and sent with its full definition in the command, so it isn't added to the quick list.
    private void DrawAdHocRestraintSection(bool canSend)
    {
        IconGlyph.WrappedDisabled("Send rules like forced pose, walk-only, gag or action block without any gear.");

        ItemWidth(220);
        ImGui.InputText("Label##adHocRestraint", ref newAdHocLabel, 32);
        IconGlyph.HelpMarker("Your own reference name for this restraint - never matched against anything on your Sub's side.");
        OwnerMoodleOverride.Draw("adHocRestraint", plugin.Configuration, ref adHocMoodleOverride);
        OwnerLockOption.Draw("adHocRestraint", ref adHocLockSeconds);
        OwnerLockOption.DrawKey("adHocRestraint", ref adHocLockKey);
        OwnerLockOption.DrawStruggle("adHocRestraint", ref adHocStruggle, ref adHocStrugglePenalty, adHocLockSeconds is not null);
        ImGui.Checkbox("Redraw on apply##adHocRedraw", ref adHocRedraw);
        IconGlyph.HelpMarker(OwnerRedrawHelp);

        Section.SubHeading("Restrictions");
        DrawRestraintRuleCheckboxes(newAdHocRuleEdit, "adHocRestraint", rulesOnly: true);

        var hasAnyRule = HasAnyRule(newAdHocRuleEdit);
        var boundAnimationsConfigured = BoundAnimationsConfigured(newAdHocRuleEdit, rulesOnly: true);
        if (hasAnyRule && !boundAnimationsConfigured)
            IconGlyph.WrappedColored(Theme.Warning, "A chosen animation is missing or stale. Choose it again, or clear it.");

        ImGui.Spacing();
        ImGui.Separator();
        if (newAdHocLabel.Trim().Length > 0 && hasAnyRule && boundAnimationsConfigured)
        {
            var command = RestraintCommand.BuildWearCommand(null, null, newAdHocLabel.Trim(), ToRules(newAdHocRuleEdit, rulesOnly: true), adHocRedraw);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Send this restraint:");
            ContinueRowOrWrap(ButtonWidth("Send"));
            var withStruggle = RestraintStruggle.Insert(command, new StruggleSetting(adHocStruggle, adHocLockSeconds is null ? 0 : adHocStrugglePenalty));
            DrawSendCopyButtons(OwnerLockOption.Apply(OwnerMoodleOverride.Apply(withStruggle, adHocMoodleOverride), adHocLockSeconds, adHocLockKey), canSend, "adHocRestraint");
        }
        else
        {
            IconGlyph.WrappedColored(Theme.Warning, "Choose a label and at least one rule before this can be sent.");
        }
    }
}
