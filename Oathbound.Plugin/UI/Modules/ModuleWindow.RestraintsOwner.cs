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
    /// The new rules-only restraint being built: sent once as it is, or saved to the list.
    private RestraintDraft? adHocDraft;
    private LockSettings adHocLock;
    private string ownerRestraintSearch = "";

    /// The selected saved restraint's settings until Save. Its lock, key and struggle are send options and save at once.
    private RestraintDraft? ownerRestraintDraft;

    private const string SaveFirstReason = "Save or discard your changes first.";

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

        using (Section.Begin("restraintQuickCommands", "Commands"))
        {
            DrawFixedQuickRow("Unlock restraints", "restraint unlock", canSend, FixedActionIds.RestraintUnlock);
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
            ? r.ExpiresAtUtc is { } until ? $"sent, {TimeText.Left(until)}" : "sent"
            : null;

        var items = new List<ListDetailItem>();
        foreach (var cmd in quick.Where(x => x.RestraintCatalogId is not null))
        {
            var picture = cmd.ImageFile ?? cmd.SharedImageFile;
            items.Add(new ListDetailItem($"mod:{cmd.Label}", cmd.Label, "mod", Marker(cmd.Label), picture, picture is null ? FontAwesomeIcon.Image : null,
                ListGroups.Of(cmd)));
        }
        // The Sub's rules-only restraints arrive as self-contained `restraint wear` commands.
        foreach (var cmd in quick.Where(x => x.RestraintCatalogId is null))
            items.Add(new ListDetailItem($"shared:{cmd.Label}", cmd.Label, "rules-only", Marker(cmd.Label), null, FontAwesomeIcon.Handcuffs, ListGroups.Of(cmd)));
        if (creatingOwnerAdHoc)
            items.Add(new ListDetailItem(NewAdHocKey, "New rules-only restraint", "rules-only", null, null, FontAwesomeIcon.Handcuffs, ListGroups.WrittenByYou));
        return ListGroups.Finish(items);
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

        if (ImGui.Selectable("Rules-only restraint (no gear)"))
        {
            ResetAdHocDraft();
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
                creatingOwnerAdHoc = false;
                ownerRestraintDraft = RestraintDraft.Load(this, $"mod:{label}", quick[^1]);
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

    /// The Owner edits their own restraints; the Sub's shared rules-only ones are sent as they came.
    private static bool OwnerEditable(QuickCommand cmd) => cmd.RestraintCatalogId is not null || cmd.Source != ImportSource.Imported;

    private void OnOwnerRestraintSelected(ListDetailItem? item, List<QuickCommand> quick)
    {
        if (item?.Key == NewAdHocKey)
            return;
        creatingOwnerAdHoc = false;
        ownerRestraintDraft = FindOwnerRestraint(quick, item?.Key) is { } cmd && OwnerEditable(cmd) ? RestraintDraft.Load(this, item!.Key, cmd) : null;
    }

    private RestraintDraft OwnerDraftFor(string key, QuickCommand cmd)
    {
        if (ownerRestraintDraft?.Key != key)
            ownerRestraintDraft = RestraintDraft.Load(this, key, cmd);
        return ownerRestraintDraft;
    }

    private bool OwnerRestraintDirty(List<QuickCommand> quick)
    {
        var key = ownerRestraintList.Selected;
        if (key == NewAdHocKey)
            return adHocDraft is { } draft && (draft.Name.Trim().Length > 0 || HasAnyRule(draft.Rules));
        return ownerRestraintDraft is { } d && d.Key == key && FindOwnerRestraint(quick, key) is { } cmd && d.IsDirty(cmd);
    }

    private void ResetAdHocDraft()
    {
        adHocDraft = new RestraintDraft(this, NewAdHocKey, rulesOnly: true);
        adHocLock = new LockSettings(null);
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
            var cancelled = false;
            DrawDetailHeader("New rules-only restraint", "Send it once, or save it to your restraints", null, FontAwesomeIcon.Handcuffs, null, () =>
            {
                if (ImGui.Button("Cancel##adHocRestraint"))
                    cancelled = true;
            });
            if (cancelled)
            {
                creatingOwnerAdHoc = false;
                ownerRestraintList.Deleted();
                return;
            }
            DrawAdHocRestraintSection(quick);
            return;
        }
        if (FindOwnerRestraint(quick, item.Key) is not { } cmd)
            return;
        if (cmd.RestraintCatalogId is null && !OwnerEditable(cmd))
        {
            DrawDetailHeader(cmd.Label, "Rules-only restraint from your Sub", null, FontAwesomeIcon.Handcuffs, null, () => { });
            DrawSavedQuickRow(cmd, quick, canSend, listDetail: ownerRestraintList);
            if (cmd.RestraintRules is { Count: > 0 })
                DrawOwnerRedraw(cmd);
            return;
        }
        DrawOwnerRestraintEditor(item.Key, cmd, quick);
    }

    /// A mod restraint, or a rules-only one the Owner saved: one draft, saved or discarded together.
    private void DrawOwnerRestraintEditor(string key, QuickCommand cmd, List<QuickCommand> list)
    {
        var draft = OwnerDraftFor(key, cmd);
        var mod = cmd.RestraintCatalogId is not null;
        ImGui.PushID($"restraintQuick_{key}");
        var hasRules = cmd.RestraintRules is { Count: > 0 };
        var hasEquipment = !mod || cmd.RestraintItemId > 0 && GlamourerIpc.GetItemSlot((uint)cmd.RestraintItemId.Value) is not null;
        var catalogAvailable = cmd.RestraintCatalogId is not { } availableCatalogId ||
            plugin.Configuration.RestraintMapping.ImportedPeerCatalog.ContainsKey(availableCatalogId) ||
            (plugin.Configuration.Role == PluginRole.Sub && plugin.Configuration.RestraintMapping.LocalCatalog.ContainsKey(availableCatalogId));
        var dirty = draft.IsDirty(cmd);

        var deleted = false;
        var kind = !mod ? "Your rules-only restraint"
            : cmd.ImageFile is null && cmd.SharedImageFile is not null ? "Mod restraint · your Sub's picture" : "Mod restraint";
        DrawDetailHeader(cmd.Label, kind, mod ? cmd.ImageFile ?? cmd.SharedImageFile : null, mod ? FontAwesomeIcon.Image : FontAwesomeIcon.Handcuffs,
            !mod ? null : () => ImageTile.DrawPicker("restraintQuickPicture", cmd.ImageFile, plugin.FileDialogManager, plugin.Snapshot, file =>
            {
                cmd.ImageFile = file;
                plugin.Configuration.Save();
            }),
            () =>
            {
                DrawFavoriteToggle(cmd, cmd.Label);
                ImGui.SameLine();
                // "Delete", not "Remove": on the Owner's side "Remove" reads as "take this restraint off my Sub".
                if (ConfirmDelete.Button("delete", cmd.Label))
                {
                    ImageTile.Delete(cmd.ImageFile);
                    list.Remove(cmd);
                    ImageTile.DeleteUnusedShared(list.Select(c => c.SharedImageFile));
                    plugin.Configuration.Save();
                    deleted = true;
                }
            });
        if (deleted)
        {
            ownerRestraintList.Deleted();
            ImGui.PopID();
            return;
        }
        if (mod && cmd.SubHasPicture && cmd.SharedImageFile is null && cmd.ImageFile is null)
            IconGlyph.WrappedDisabled("Your Sub has a picture for this restraint, but it wasn't shared with you.");
        else if (mod && cmd.ImageFile is null && cmd.SharedPictureRef is not null && !ImageTile.SharedExists(cmd.SharedImageFile))
            IconGlyph.WrappedDisabled("Your Sub's picture for this restraint hasn't downloaded yet - Get latest on the Sync tab tries again.");

        Section.SubHeading("Send");
        SendButton.Draw(plugin, new SendSpec($"enable_{cmd.Label}", mod ? "Enable & lock" : "Send")
        {
            Command = OwnerMoodleOverride.ForSend(plugin.Configuration, cmd),
            BlockedReason = dirty ? SaveFirstReason
                : !hasRules ? "Set and save its rules first."
                : !hasEquipment ? "Choose the Glamourer item this mod should equip, and save."
                : !catalogAvailable ? "This restraint mod is no longer in your Sub's shared catalog."
                : null,
        });
        OwnerLockOption.DrawStacked($"restraintQuick_{cmd.Label}", cmd, plugin.Configuration);
        if (!hasRules)
            Status.Line(StatusKind.Caution, "No rules assigned yet - set them below and save before enabling it.");
        if (!hasEquipment)
            Status.Line(StatusKind.Caution, "Choose the Glamourer item this mod should equip and lock.");
        if (!catalogAvailable)
            Status.Line(StatusKind.Caution, "This restraint mod is no longer present in the latest shared catalog. Choose it again after the Sub shares it.");

        Section.SubHeading(mod ? "Name & item" : "Name");
        ItemWidth(220);
        ImGui.InputText("Name##restraintQuickLabel", ref draft.Name, 80);
        IconGlyph.HelpMarker(mod
            ? "Your own name for this restraint - rename it so you can tell entries for the same mod apart."
            : "Your own name for this restraint - never matched against anything on your Sub's side.");
        if (mod)
        {
            TextWithActions($"Glamourer item: {(draft.ItemId is { } equippedItem ? GetItemName(equippedItem) : "(none chosen)")}", ButtonWidth("Choose item..."));
            if (ImGui.Button("Choose item...##restraintQuick"))
            {
                var catalogEntry = plugin.Configuration.RestraintMapping.ImportedPeerCatalog.GetValueOrDefault(cmd.RestraintCatalogId ?? "");
                plugin.ItemPickerWindow.OpenForItemIds(draft.Name, catalogEntry?.ChangedItemIds.ToHashSet() ?? [], (chosenId, _) => draft.ItemId = chosenId);
            }
        }

        Section.SubHeading("Restrictions");
        DrawRestraintRuleCheckboxes(draft.Rules, $"restraintQuickRule_{key}", forOwner: true, rulesOnly: !mod);

        Section.SubHeading("Attached moodle");
        OwnerMoodleOverride.Draw($"restraintQuick_{key}", plugin.Configuration, ref draft.MoodleOverride);
        ImGui.Checkbox("Redraw on apply##ownerRedrawDraft", ref draft.Redraw);
        IconGlyph.HelpMarker(OwnerRedrawHelp);

        var name = draft.Name.Trim();
        var draftEquipment = !mod || draft.ItemId > 0 && GlamourerIpc.GetItemSlot((uint)draft.ItemId.Value) is not null;
        var invalid = name.Length == 0 ? "Give it a name."
            : list.Any(x => x != cmd && string.Equals(x.Label, name, StringComparison.OrdinalIgnoreCase)) ? "You already have a restraint with this name."
            : !HasAnyRule(draft.Rules) ? "Choose at least one rule."
            : !BoundAnimationsConfigured(draft.Rules, rulesOnly: !mod) ? "Choose an animation for every checked Arms/Legs/Full Body Cuffed rule before saving."
            : !draftEquipment ? "Choose the Glamourer item this mod should equip and lock."
            : null;
        DrawSaveBar(key, dirty, invalid,
            () =>
            {
                draft.Apply(cmd);
                plugin.Configuration.Save();
                // The list is keyed by name, so a rename moves the selection with it in the same frame.
                var newKey = $"{(mod ? "mod" : "shared")}:{cmd.Label}";
                ownerRestraintDraft = RestraintDraft.Load(this, newKey, cmd);
                ownerRestraintList.Select(newKey);
            },
            () => ownerRestraintDraft = RestraintDraft.Load(this, key, cmd));

        ImGui.PopID();
    }

    /// Built with its full definition in the command, so it can be sent once or saved to the list.
    private void DrawAdHocRestraintSection(List<QuickCommand> quick)
    {
        if (adHocDraft is null)
            ResetAdHocDraft();
        var draft = adHocDraft!;
        IconGlyph.WrappedDisabled("Send rules like Forced Pose, Walk Only, Gagged or Action Block without any gear.");

        ItemWidth(220);
        ImGui.InputText("Name##adHocRestraint", ref draft.Name, 32);
        IconGlyph.HelpMarker("Your own name for this restraint - never matched against anything on your Sub's side.");
        OwnerMoodleOverride.Draw("adHocRestraint", plugin.Configuration, ref draft.MoodleOverride);
        ImGui.Checkbox("Redraw on apply##adHocRedraw", ref draft.Redraw);
        IconGlyph.HelpMarker(OwnerRedrawHelp);

        Section.SubHeading("Restrictions");
        DrawRestraintRuleCheckboxes(draft.Rules, "adHocRestraint", forOwner: true, rulesOnly: true);

        var name = draft.Name.Trim();
        var hasAnyRule = HasAnyRule(draft.Rules);
        var boundAnimationsConfigured = BoundAnimationsConfigured(draft.Rules, rulesOnly: true);
        if (hasAnyRule && !boundAnimationsConfigured)
            Status.Line(StatusKind.Caution, "A chosen animation is missing or stale. Choose it again, or clear it.");
        var unfinished = name.Length == 0 || !hasAnyRule || !boundAnimationsConfigured
            ? "Choose a label and at least one rule before this can be sent."
            : null;

        Section.SubHeading("Send once");
        var command = unfinished is null ? RestraintCommand.BuildWearCommand(null, null, name, draft.BuildRules(), draft.Redraw) : "";
        SendButton.Draw(plugin, new SendSpec("adHocRestraint", "Send")
        {
            Command = unfinished is null
                ? OwnerLockOption.Apply(OwnerMoodleOverride.Apply(
                    RestraintStruggle.Insert(command, new StruggleSetting(adHocLock.Struggle, adHocLock.Seconds is null ? 0 : adHocLock.PenaltyMinutes)),
                    draft.MoodleOverride), adHocLock.Seconds, adHocLock.Key)
                : null,
            BlockedReason = unfinished,
            Help = "Sends it as it is, without adding it to your list.",
            AnchorQuickSend = true,
        });
        OwnerLockOption.Draw("adHocRestraint", ref adHocLock, LockKind.Restraint, LockLayout.Stacked);

        var taken = quick.Any(x => string.Equals(x.Label, name, StringComparison.OrdinalIgnoreCase));
        DrawSaveBar(NewAdHocKey, name.Length > 0 || hasAnyRule, unfinished ?? (taken ? "You already have a restraint with this label - pick another one to save it." : null),
            () => SaveAdHocRestraint(quick, command),
            ResetAdHocDraft, "Save to my restraints");
    }

    /// An Owner-made entry: catalog sync only replaces entries it imported, so this one stays.
    private void SaveAdHocRestraint(List<QuickCommand> quick, string command)
    {
        var draft = adHocDraft!;
        var label = draft.Name.Trim();
        quick.Add(new QuickCommand
        {
            Label = label,
            Command = command,
            Source = ImportSource.Manual,
            RestraintRules = draft.BuildRules(),
            RestraintRedraw = draft.Redraw,
            MoodleOverride = draft.MoodleOverride,
            LockSeconds = adHocLock.Seconds,
            LockKey = string.IsNullOrEmpty(adHocLock.Key) ? null : adHocLock.Key,
            Struggle = adHocLock.Struggle,
            StrugglePenaltyMinutes = adHocLock.Seconds is null ? 0 : adHocLock.PenaltyMinutes,
        });
        plugin.Configuration.Save();
        creatingOwnerAdHoc = false;
        ResetAdHocDraft();
        ownerRestraintDraft = RestraintDraft.Load(this, $"shared:{label}", quick[^1]);
        ownerRestraintList.Select($"shared:{label}");
    }
}
