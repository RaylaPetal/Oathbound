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
    private string newDeviceName = "";
    private AttachedMoodleRef? newDeviceMoodle;
    private bool newDeviceRedraw;
    private ApiEquipSlot? newDeviceSlot;
    private ulong? newDeviceItemId;

    private readonly RestraintRuleEditState newDeviceRuleEdit = new();
    private string? editingDeviceId;

    private string subRestraintSearch = "";

    /// Keyed by the quick command's Label; transient.
    private string? expandedSubModKey;
    private string? restraintActionResult;

    private readonly Dictionary<string, string> restraintKeyGuesses = new();

    /// One Struggle button per lock that allows it. Also drawn in the main window's header.
    public void DrawStruggleRow(string id)
    {
        var any = false;
        foreach (var worn in plugin.RestraintCommand.Worn.ToList())
        {
            if (RestraintCommand.StruggleAvailable(worn) is not { } setting)
                continue;
            any = true;
            ImGui.PushID($"struggle{id}_{worn.RuntimeId}");
            DrawStruggleButton(worn, setting);
            ImGui.PopID();
        }
        if (!any && plugin.RestraintCommand.Worn.Count == 0)
            restraintActionResult = null;
        if (any && restraintActionResult is not null)
            IconGlyph.WrappedDisabled(restraintActionResult);
    }

    private void DrawStruggleButton(WornRestraint worn, StruggleSetting setting)
    {
        var wait = RestraintCommand.StruggleWait(worn);
        var caged = plugin.CageService.IsCaged;
        using (ImRaii.Disabled(wait is not null || caged))
        {
            if (ImGui.Button(wait is { } w ? $"Struggle: {worn.Reference} (again in {RestraintLock.Format(w)})" : $"Struggle: {worn.Reference}"))
                restraintActionResult = plugin.RestraintCommand.Struggle(worn.RuntimeId).Message;
        }
        IconGlyph.HelpMarker(caged
            ? $"You can't struggle while you're caged. {RestraintStruggle.Describe(setting)} once the cage opens."
            : $"Your Owner lets you try to get free: {RestraintStruggle.Describe(setting)}. Getting free takes this restraint off, and your Owner is told.");
    }

    private void DrawRestraintKeyRow(WornRestraint worn)
    {
        var guess = restraintKeyGuesses.GetValueOrDefault(worn.RuntimeId, "");
        ItemWidth(140);
        if (ImGui.InputTextWithHint("##key", "Key...", ref guess, RestraintKey.MaxLength, ImGuiInputTextFlags.Password))
            restraintKeyGuesses[worn.RuntimeId] = guess;
        var wait = RestraintCommand.KeyWait(worn);
        var caged = plugin.CageService.IsCaged;
        ImGui.SameLine();
        using (ImRaii.Disabled(wait is not null || guess.Trim().Length == 0 || caged))
        {
            if (ImGui.Button(wait is { } w ? $"Unlock (again in {RestraintLock.Format(w)})" : "Unlock"))
            {
                restraintActionResult = plugin.RestraintCommand.TryKey(worn.RuntimeId, guess).Message;
                restraintKeyGuesses.Remove(worn.RuntimeId);
            }
        }
        IconGlyph.HelpMarker(caged
            ? "You can't use a key while you're caged."
            : "If your Owner gave you the key, type it here to take this restraint off. Your Owner is told.");
    }

    private readonly ListDetail subRestraintList = new();
    private bool creatingRulesOnly;
    private const string NewRulesOnlyKey = "new:rulesonly";

    private void DrawRestraintsModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.Handcuffs, "Restraints");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Set up the restraints your Owner can use. Each alias toggles its restraint; `restraint unlock` releases everything.");

        using (Section.Begin("configuredRestraintMods", "Your restraints"))
        {
            subRestraintList.Draw("subRestraints", BuildSubRestraintItems(config), DrawSubRestraintToolbar,
                item => DrawSubRestraintDetail(config, item), SubRestraintDirty, OnSubRestraintSelected,
                "No restraints yet. Use + New to make one.");
        }

        DrawGagSpeechSection(config);
        DrawDrawnRestraintsSection(config);
    }

    private string? gagSoundsBuffer;
    private const string GagPreviewText = "Hello there, could you let me go please?";

    /// The Sub's own setting only: it never reaches the Owner, who just sees the gag level they sent.
    private void DrawGagSpeechSection(PluginConfig config)
    {
        using var box = Section.Begin("gagSpeech", "Gag speech");
        IconGlyph.WrappedDisabled("How your chat sounds while a Gagged restraint is on.");

        gagSoundsBuffer ??= string.Join(", ", config.GagSounds);
        ItemWidth(320);
        ImGui.InputTextWithHint("Your gag sounds##gagSounds", "mmph, nngh, hrrmf", ref gagSoundsBuffer, 400);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            config.GagSounds = ChatGagService.ParseGagSounds(gagSoundsBuffer);
            gagSoundsBuffer = string.Join(", ", config.GagSounds);
            config.Save();
        }
        IconGlyph.HelpMarker($"Separate them with commas, up to {ChatGagService.MaxGagSounds}. They mix into your gag speech: about half your muffled words become one of these, the rest are sounded out through the gag. Letters only, plus ' - and ~.");

        foreach (var level in new[] { GagLevel.Medium, GagLevel.Heavy })
            IconGlyph.WrappedDisabled($"{level}: {ChatGagService.Garble(GagPreviewText, level, config.GagSounds, new Random(1))}");
    }

    private List<ListDetailItem> BuildSubRestraintItems(PluginConfig config)
    {
        string? Marker(string runtimeId) => plugin.RestraintCommand.Worn.FirstOrDefault(w => w.RuntimeId == runtimeId) is { } worn
            ? worn.Lock is null ? "on" : "locked"
            : null;

        var items = new List<ListDetailItem>();
        foreach (var mod in config.RestraintMapping.ConfiguredMods)
            items.Add(new ListDetailItem($"mod:{mod.Id}", mod.Name, "mod", Marker($"catalog:{mod.CatalogId}"), mod.ImageFile,
                mod.ImageFile is null ? FontAwesomeIcon.Image : null));
        foreach (var device in config.RestraintMapping.Devices.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            items.Add(new ListDetailItem($"dev:{device.Id}", device.Name, "rules-only", Marker(device.Id), null, FontAwesomeIcon.Handcuffs));
        if (creatingRulesOnly)
            items.Add(new ListDetailItem(NewRulesOnlyKey, "New rules-only restraint", "rules-only", null, null, FontAwesomeIcon.Handcuffs));
        return items;
    }

    private void DrawSubRestraintToolbar()
    {
        if (ImGui.Button("+ New##subRestraint"))
        {
            subRestraintSearch = "";
            ImGui.OpenPopup("newSubRestraint");
        }
        if (!ImGui.BeginPopup("newSubRestraint"))
            return;

        if (ImGui.Selectable("Rules-only restraint (no gear)"))
        {
            CloseSubModEditor();
            ResetDeviceDraft();
            creatingRulesOnly = true;
            subRestraintList.Select(NewRulesOnlyKey);
        }
        ImGui.Separator();
        ImGui.TextDisabled("Or a restraint mod found in Penumbra:");
        ImGui.SetNextItemWidth(Scaled(260));
        ImGui.InputTextWithHint("##subRestraintSearch", "Search restraint mods...", ref subRestraintSearch, 128);
        var config = plugin.Configuration;
        var configured = config.RestraintMapping.ConfiguredMods;
        using (ImRaii.Child("subRestraintModBrowser", new Vector2(Scaled(260), Scaled(200)), true))
        {
            var mods = config.RestraintMapping.LocalCatalog.Values
                .Where(x => string.IsNullOrWhiteSpace(subRestraintSearch) || x.ModName.Contains(subRestraintSearch.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.ModName).ToList();
            if (mods.Count == 0)
                IconGlyph.WrappedDisabled(config.RestraintMapping.LocalCatalog.Count == 0 ? "No restraint mods found. Rescan in the scan settings." : "Nothing matches.");
            foreach (var entry in mods)
            {
                if (!ImGui.Selectable($"{entry.ModName}##subRestraint_{entry.Id}"))
                    continue;
                // The same mod can be configured more than once, so each gets a distinct name.
                var count = configured.Count(x => x.CatalogId == entry.Id);
                var created = new ConfiguredModRestraint { CatalogId = entry.Id, Name = count == 0 ? entry.ModName : $"{entry.ModName} ({count + 1})" };
                configured.Add(created);
                config.Save();
                creatingRulesOnly = false;
                OpenSubModEditor($"submod:{created.Id}", new RestraintRuleEditState());
                subRestraintList.Select($"mod:{created.Id}");
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.EndPopup();
    }

    private void OnSubRestraintSelected(ListDetailItem? item)
    {
        var config = plugin.Configuration;
        if (item?.Key is not { } key || key == NewRulesOnlyKey)
            return;
        creatingRulesOnly = false;
        if (key.StartsWith("mod:") && config.RestraintMapping.ConfiguredMods.FirstOrDefault(m => $"mod:{m.Id}" == key) is { } mod)
        {
            ResetDeviceDraft();
            OpenSubModEditor($"submod:{mod.Id}", FromRules(mod.Rules));
        }
        else if (key.StartsWith("dev:") && config.RestraintMapping.Devices.TryGetValue(key[4..], out var device))
        {
            CloseSubModEditor();
            LoadDeviceDraft(device);
        }
    }

    private bool SubRestraintDirty()
    {
        var config = plugin.Configuration;
        var key = subRestraintList.Selected;
        if (key == NewRulesOnlyKey)
            return newDeviceName.Trim().Length > 0 || HasAnyRule(newDeviceRuleEdit);
        if (key?.StartsWith("mod:") == true && config.RestraintMapping.ConfiguredMods.FirstOrDefault(m => $"mod:{m.Id}" == key) is { } mod
            && restraintRuleEdits.TryGetValue($"submod:{mod.Id}", out var edit))
            return RestraintCommand.EncodeRuleTokens(ToRules(edit)) != RestraintCommand.EncodeRuleTokens(mod.Rules);
        if (key?.StartsWith("dev:") == true && editingDeviceId is { } id && config.RestraintMapping.Devices.TryGetValue(id, out var device))
            return newDeviceName.Trim() != device.Name
                || newDeviceMoodle?.StatusId != device.AttachedMoodle?.StatusId
                || RestraintCommand.EncodeRuleTokens(ToRules(newDeviceRuleEdit, rulesOnly: true)) != RestraintCommand.EncodeRuleTokens(device.Rules);
        return false;
    }

    private void DrawSubRestraintDetail(PluginConfig config, ListDetailItem? item)
    {
        if (item is null)
        {
            IconGlyph.WrappedDisabled("Choose a restraint, or use + New to make one.");
            return;
        }
        if (item.Key == NewRulesOnlyKey)
            DrawRulesOnlyDetail(config, null);
        else if (item.Key.StartsWith("dev:") && config.RestraintMapping.Devices.TryGetValue(item.Key[4..], out var device))
            DrawRulesOnlyDetail(config, device);
        else if (config.RestraintMapping.ConfiguredMods.FirstOrDefault(m => $"mod:{m.Id}" == item.Key) is { } mod)
            DrawSubModDetail(config, mod);
    }

    private static float DetailTileWidth => Scaled(110);

    /// Picture on the left; name, actions and the picture's buttons beside it. The picture buttons stay out of the
    /// picture's column, which would otherwise grow as wide as their row and push everything beside it away.
    private void DrawDetailHeader(string name, string kind, string? imageFile, FontAwesomeIcon? icon, Action? pictureActions, Action headerActions)
    {
        if (!ImGui.BeginTable("##restraintDetailHeader", 2, ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("picture", ImGuiTableColumnFlags.WidthFixed, DetailTileWidth);
        ImGui.TableSetupColumn("actions", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextColumn();
        ImageTile.Draw(imageFile, DetailTileWidth, imageFile is null ? icon : null, "No picture");
        ImGui.TableNextColumn();
        ImGui.TextColored(Theme.AccentHover, name);
        ImGui.TextDisabled(kind);
        headerActions();
        if (pictureActions is not null)
        {
            Section.SubHeading("Picture");
            pictureActions();
        }
        ImGui.EndTable();
        ImGui.Spacing();
    }

    private void DrawSubModDetail(PluginConfig config, ConfiguredModRestraint created)
    {
        var key = $"submod:{created.Id}";
        var missing = !config.RestraintMapping.LocalCatalog.ContainsKey(created.CatalogId);
        if (!restraintRuleEdits.TryGetValue(key, out var edit))
        {
            OpenSubModEditor(key, FromRules(created.Rules));
            edit = restraintRuleEdits[key];
        }

        DrawDetailHeader(created.Name, "Mod restraint", created.ImageFile, FontAwesomeIcon.Image,
            () => ImageTile.DrawPicker(key, created.ImageFile, plugin.FileDialogManager, plugin.Snapshot, file =>
            {
                ImageTile.Delete(created.ThumbnailFile);
                created.ThumbnailFile = null;
                created.ThumbnailTargetBytes = 0;
                created.ImageFile = file;
                config.Save();
            }),
            () =>
            {
                if (missing)
                    IconGlyph.WrappedColored(Theme.Warning, "This mod is outside the latest scan and will not be exported.");
                // "Delete", not "Remove": this deletes the saved restraint, it doesn't take it off anyone.
                if (ImGui.Button($"Delete##{key}"))
                {
                    ImageTile.Delete(created.ImageFile);
                    ImageTile.Delete(created.ThumbnailFile);
                    config.RestraintMapping.ConfiguredMods.Remove(created);
                    CloseSubModEditor();
                    config.Save();
                }
            });

        Section.SubHeading("Name, alias & item");
        if (DrawDeferredTextInput($"Name##{key}", (created, "name"), created.Name, 80, out var nameBuffer) && nameBuffer.Trim().Length > 0)
        {
            created.Name = nameBuffer;
            config.Save();
        }
        IconGlyph.HelpMarker("Your own label for this restraint - rename it so you can tell entries for the same mod apart.");
        if (DrawDeferredTextInput($"Alias##{key}", (created, "alias"), created.Alias, 32, out var aliasBuffer, "optional"))
        {
            created.Alias = aliasBuffer.Trim();
            config.Save();
        }
        IconGlyph.HelpMarker("Optional word your Owner sends to toggle this restraint.");
        DrawReservedWordWarning(created.Alias);
        if (created.Alias.Length > 0
            && (config.RestraintMapping.ConfiguredMods.Any(m => m != created && string.Equals(m.Alias.Trim(), created.Alias, StringComparison.OrdinalIgnoreCase))
                || config.RestraintMapping.Devices.Values.Any(d => string.Equals(d.Name.Trim(), created.Alias, StringComparison.OrdinalIgnoreCase))))
            IconGlyph.WrappedColored(Theme.Warning, "Another restraint already uses this alias - only one of them will respond to it.");
        TextWithActions($"Glamourer item: {(created.ItemId is { } equippedItem ? GetItemName(equippedItem) : "(none chosen)")}", ButtonWidth("Choose item..."));
        if (ImGui.Button($"Choose item...##{key}"))
        {
            var catalogEntry = config.RestraintMapping.LocalCatalog.GetValueOrDefault(created.CatalogId);
            plugin.ItemPickerWindow.OpenForItemIds(created.Name, catalogEntry?.ChangedItemIds.ToHashSet() ?? [], (chosenId, _) =>
            {
                created.ItemId = chosenId;
                config.Save();
            });
        }

        Section.SubHeading("Restrictions");
        DrawRestraintRuleCheckboxes(edit, key, allowCustomizePreset: true);

        Section.SubHeading("Attached moodle");
        if (DrawAttachedMoodlePicker(key, created.AttachedMoodle, config, out var modMoodle))
        {
            created.AttachedMoodle = modMoodle;
            config.Save();
        }
        var modRedraw = created.RedrawOnApply;
        if (DrawRedrawOnApply(key, ref modRedraw))
        {
            created.RedrawOnApply = modRedraw;
            config.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        var valid = created.ItemId > 0 && GlamourerIpc.GetItemSlot((uint)created.ItemId.Value) is not null && HasAnyRule(edit) && BoundAnimationsConfigured(edit);
        using (ImRaii.Disabled(!valid || missing))
        {
            if (ImGui.Button($"Save restraint##{key}"))
            {
                created.Rules = ToRules(edit);
                config.Save();
                OpenSubModEditor(key, FromRules(created.Rules));
            }
        }
        if (!valid)
            IconGlyph.WrappedDisabled("Choose an item and at least one rule before saving.");
    }

    /// `device` null = the new rules-only restraint being drafted.
    private void DrawRulesOnlyDetail(PluginConfig config, RestraintDeviceDefinition? device)
    {
        var devices = config.RestraintMapping.Devices.Values.ToList();
        DrawDetailHeader(device?.Name ?? "New rules-only restraint", "Rules-only restraint", null, FontAwesomeIcon.Handcuffs, null, () =>
        {
            if (device is null)
            {
                if (ImGui.Button("Cancel##newDevice"))
                {
                    creatingRulesOnly = false;
                    ResetDeviceDraft();
                    subRestraintList.Select(null);
                }
                return;
            }
            if (ImGui.Button($"Delete##dev_{device.Id}"))
            {
                plugin.RestraintCommand.RemoveDevice(device.Id);
                ResetDeviceDraft();
            }
        });

        if (device?.ItemId is { } deviceItemId)
            IconGlyph.WrappedDisabled($"Gear (from an older version): {device.Slot} · {GetItemName(deviceItemId)}");
        if (device is not null && device.Rules.Any(r =>
                CuffSets.IsCuff(r.Kind) && !string.IsNullOrWhiteSpace(r.AnimationId) && !config.GestureMapping.LocalCatalog.ContainsKey(r.AnimationId)))
            IconGlyph.WrappedColored(Theme.Warning, "A cuff animation is stale. Select the animation again before using this restraint.");

        ImGui.InputText("Alias##newDevice", ref newDeviceName, 32);
        IconGlyph.HelpMarker("The word your Owner sends to toggle this restraint.");
        DrawReservedWordWarning(newDeviceName);
        if (DrawAttachedMoodlePicker("newDevice", newDeviceMoodle, config, out var pickedDeviceMoodle, width: null))
            newDeviceMoodle = pickedDeviceMoodle;
        // Saved at once on an existing restraint, like its moodle; a new one takes it when added.
        var deviceRedraw = device?.RedrawOnApply ?? newDeviceRedraw;
        if (DrawRedrawOnApply("newDevice", ref deviceRedraw))
        {
            if (device is null)
                newDeviceRedraw = deviceRedraw;
            else
            {
                device.RedrawOnApply = deviceRedraw;
                config.Save();
            }
        }

        Section.SubHeading("Restrictions");
        DrawRestraintRuleCheckboxes(newDeviceRuleEdit, "newDevice", allowCustomizePreset: true, rulesOnly: true);

        var hasAnyRule = HasAnyRule(newDeviceRuleEdit);
        var boundAnimationsConfigured = BoundAnimationsConfigured(newDeviceRuleEdit, rulesOnly: true);
        if (hasAnyRule && !boundAnimationsConfigured)
            IconGlyph.WrappedColored(Theme.Warning, "A chosen animation is missing or stale. Choose it again, or clear it.");

        var duplicateDeviceName = devices.Any(d => d.Id != editingDeviceId &&
            string.Equals(d.Name, newDeviceName.Trim(), StringComparison.OrdinalIgnoreCase))
            || config.RestraintMapping.ConfiguredMods.Any(m => string.Equals(m.Alias.Trim(), newDeviceName.Trim(), StringComparison.OrdinalIgnoreCase));
        // Only measured once the draft is complete; Save is disabled for an unfinished rule anyway.
        var safeDeviceCommand = !hasAnyRule || !boundAnimationsConfigured
            || CommandSelector.Fits(RestraintCommand.BuildLockCommand(newDeviceName.Trim(), ToRules(newDeviceRuleEdit, rulesOnly: true)));
        if (duplicateDeviceName)
            IconGlyph.WrappedColored(Theme.Warning, "Another restraint already uses this alias.");
        if (!safeDeviceCommand)
            IconGlyph.WrappedColored(Theme.Warning, "This restraint name and rule set are too long for a safe command.");
        ImGui.Spacing();
        ImGui.Separator();
        using (ImRaii.Disabled(newDeviceName.Trim().Length == 0 || IsReserved(newDeviceName) || !hasAnyRule || !boundAnimationsConfigured || duplicateDeviceName || !safeDeviceCommand))
        {
            if (ImGui.Button(device is null ? "Add restraint" : "Save restraint"))
            {
                var rules = ToRules(newDeviceRuleEdit, rulesOnly: true);
                if (device is null)
                {
                    var name = newDeviceName.Trim();
                    if (plugin.RestraintCommand.CaptureDeviceFromItem(null, null, name, rules, newDeviceMoodle)
                        && config.RestraintMapping.Devices.Values.FirstOrDefault(d => d.Name == name) is { } added)
                    {
                        added.RedrawOnApply = newDeviceRedraw;
                        config.Save();
                        creatingRulesOnly = false;
                        LoadDeviceDraft(added);
                        subRestraintList.Select($"dev:{added.Id}");
                    }
                }
                // An edited older device keeps its gear.
                else if (SaveDeviceDraft(newDeviceSlot, newDeviceItemId, rules))
                    LoadDeviceDraft(device);
            }
        }
    }

    /// Opening another discards the current one's unsaved draft.
    private void OpenSubModEditor(string key, RestraintRuleEditState draft)
    {
        CloseSubModEditor();
        expandedSubModKey = key;
        restraintRuleEdits[key] = draft;
    }

    private void CloseSubModEditor()
    {
        if (expandedSubModKey is { } open)
            restraintRuleEdits.Remove(open);
        expandedSubModKey = null;
    }

    private void LoadDeviceDraft(RestraintDeviceDefinition device)
    {
        editingDeviceId = device.Id;
        newDeviceName = device.Name;
        newDeviceMoodle = device.AttachedMoodle;
        newDeviceSlot = device.Slot;
        newDeviceItemId = device.ItemId;
        CopyRuleEdit(FromRules(device.Rules), newDeviceRuleEdit);
    }

    private bool SaveDeviceDraft(ApiEquipSlot? slot, ulong? itemId, List<RestraintRuleAssignment> rules)
    {
        if (editingDeviceId is not { } id || !plugin.Configuration.RestraintMapping.Devices.TryGetValue(id, out var device))
            return false;

        var oldName = device.Name;
        device.Name = newDeviceName.Trim();
        device.Slot = slot;
        device.ItemId = itemId;
        device.Rules = rules;
        device.AttachedMoodle = newDeviceMoodle;
        plugin.Configuration.Save();
        Plugin.Log.Debug($"Edited restraint device '{oldName}' while preserving id {id}.");
        return true;
    }

    private void ResetDeviceDraft()
    {
        newDeviceRedraw = false;
        editingDeviceId = null;
        newDeviceName = "";
        newDeviceMoodle = null;
        newDeviceSlot = null;
        newDeviceItemId = null;
        CopyRuleEdit(new RestraintRuleEditState(), newDeviceRuleEdit);
    }

    /// Only changes what this client draws; the restraint's rules keep applying with its cuffs hidden.
    private static void DrawDrawnRestraintsSection(PluginConfig config)
    {
        using var box = Section.Begin("restraintAppearance", "Drawn restraints");
        var show = config.ShowDrawnRestraints;
        if (ImGui.Checkbox("Show drawn restraints", ref show))
        {
            config.ShowDrawnRestraints = show;
            config.Save();
        }
        IconGlyph.HelpMarker("Cuffs and chains drawn for Arms Cuffed, Legs Cuffed and Fully Restrain. Only changes what you see. Hiding them doesn't release the restraint.");

        using var disabled = ImRaii.Disabled(!config.ShowDrawnRestraints);
        var color = config.RestraintColor;
        if (ImGui.ColorEdit3("Color##restraintAppearance", ref color, ImGuiColorEditFlags.NoInputs))
        {
            config.RestraintColor = color;
            config.Save();
        }
        ContinueRowOrWrap(ButtonWidth("Reset"));
        if (ImGui.Button("Reset##restraintAppearance"))
        {
            config.RestraintColor = PluginConfig.DefaultRestraintColor;
            config.RestraintBrightness = 1f;
            config.RestraintOpacity = 1f;
            config.Save();
        }

        var brightness = (int)MathF.Round(config.RestraintBrightness * 100f);
        ItemWidth(200);
        if (ImGui.SliderInt("Brightness##restraintAppearance", ref brightness, (int)(PluginConfig.MinLeashBrightness * 100f), 100, "%d%%"))
        {
            config.RestraintBrightness = brightness / 100f;
            config.Save();
        }

        var opacity = (int)MathF.Round(config.RestraintOpacity * 100f);
        ItemWidth(200);
        if (ImGui.SliderInt("Opacity##restraintAppearance", ref opacity, (int)(PluginConfig.MinLeashOpacity * 100f), 100, "%d%%"))
        {
            config.RestraintOpacity = opacity / 100f;
            config.Save();
        }
    }
}
