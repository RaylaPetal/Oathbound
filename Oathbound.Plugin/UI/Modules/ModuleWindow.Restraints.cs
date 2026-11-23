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
    private string subRestraintSearch = "";

    /// The selected restraint's settings until Save.
    private RestraintDraft? subRestraintDraft;
    private StatusMessage? restraintActionResult;

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
        if (any)
            Status.Line(restraintActionResult);
    }

    private void DrawStruggleButton(WornRestraint worn, StruggleSetting setting)
    {
        var wait = RestraintCommand.StruggleWait(worn);
        var caged = plugin.CageService.IsCaged;
        using (ImRaii.Disabled(wait is not null || caged))
        {
            if (ImGui.Button(wait is { } w ? $"Struggle: {worn.Reference} (again {TimeText.Until(w)})" : $"Struggle: {worn.Reference}"))
                restraintActionResult = ResultMessage(plugin.RestraintCommand.Struggle(worn.RuntimeId));
        }
        IconGlyph.HelpMarker(caged
            ? $"You can't struggle while you're caged. {RestraintStruggle.Describe(setting)} once the cage opens."
            : $"Your Owner lets you try to get free: {RestraintStruggle.Describe(setting)}. Getting free takes this restraint off, and your Owner is told.");
    }

    private static StatusMessage ResultMessage(LocalTestResult result) =>
        new(result.Success ? StatusKind.Ok : StatusKind.Error, result.Message);

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
            if (ImGui.Button(wait is { } w ? $"Unlock (again {TimeText.Until(w)})" : "Unlock"))
            {
                restraintActionResult = ResultMessage(plugin.RestraintCommand.TryKey(worn.RuntimeId, guess));
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
        IconGlyph.WrappedDisabled("Set up the restraints your Owner can use. Each alias toggles its restraint; your Owner's \"Unlock restraints\" releases everything.");

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
            subRestraintDraft = new RestraintDraft(this, NewRulesOnlyKey, rulesOnly: true);
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
                subRestraintDraft = RestraintDraft.Load(this, $"mod:{created.Id}", created);
                subRestraintList.Select($"mod:{created.Id}");
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.EndPopup();
    }

    private void OnSubRestraintSelected(ListDetailItem? item)
    {
        if (item?.Key == NewRulesOnlyKey)
            return;
        creatingRulesOnly = false;
        subRestraintDraft = item is null ? null : LoadSubDraft(item.Key);
    }

    private RestraintDraft? LoadSubDraft(string key)
    {
        var mapping = plugin.Configuration.RestraintMapping;
        if (key.StartsWith("mod:") && mapping.ConfiguredMods.FirstOrDefault(m => $"mod:{m.Id}" == key) is { } mod)
            return RestraintDraft.Load(this, key, mod);
        if (key.StartsWith("dev:") && mapping.Devices.TryGetValue(key[4..], out var device))
            return RestraintDraft.Load(this, key, device);
        return null;
    }

    /// The draft for `key`, loaded fresh when the selection moved without onSelected (e.g. the first auto-pick).
    private RestraintDraft? SubDraftFor(string key)
    {
        if (subRestraintDraft?.Key != key)
            subRestraintDraft = key == NewRulesOnlyKey ? new RestraintDraft(this, key, rulesOnly: true) : LoadSubDraft(key);
        return subRestraintDraft;
    }

    private bool SubRestraintDirty()
    {
        var mapping = plugin.Configuration.RestraintMapping;
        if (subRestraintList.Selected is not { } key || subRestraintDraft is not { } draft || draft.Key != key)
            return false;
        if (key == NewRulesOnlyKey)
            return draft.Alias.Trim().Length > 0 || HasAnyRule(draft.Rules);
        if (mapping.ConfiguredMods.FirstOrDefault(m => $"mod:{m.Id}" == key) is { } mod)
            return draft.IsDirty(mod);
        return key.StartsWith("dev:") && mapping.Devices.TryGetValue(key[4..], out var device) && draft.IsDirty(device);
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
        var key = $"mod:{created.Id}";
        if (SubDraftFor(key) is not { } draft)
            return;
        var missing = !config.RestraintMapping.LocalCatalog.ContainsKey(created.CatalogId);

        var deleted = false;
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
                    Status.Line(StatusKind.Caution, "This mod is outside the latest scan and will not be exported.");
                // "Delete", not "Remove": this deletes the saved restraint, it doesn't take it off anyone.
                if (ConfirmDelete.Button($"delete_{key}", created.Name))
                {
                    ImageTile.Delete(created.ImageFile);
                    ImageTile.Delete(created.ThumbnailFile);
                    config.RestraintMapping.ConfiguredMods.Remove(created);
                    config.Save();
                    deleted = true;
                }
            });
        if (deleted)
        {
            subRestraintList.Deleted();
            return;
        }

        Section.SubHeading("Name, alias & item");
        ImGui.InputText($"Name##{key}", ref draft.Name, 80);
        IconGlyph.HelpMarker("Your own name for this restraint - rename it so you can tell entries for the same mod apart. Your Owner doesn't send it; they send the alias below.");
        ImGui.InputTextWithHint($"Alias##{key}", "optional", ref draft.Alias, 32);
        IconGlyph.HelpMarker("Optional word your Owner sends to toggle this restraint.");
        var alias = draft.Alias.Trim();
        DrawReservedWordWarning(alias);
        if (alias.Length > 0
            && (config.RestraintMapping.ConfiguredMods.Any(m => m != created && string.Equals(m.Alias.Trim(), alias, StringComparison.OrdinalIgnoreCase))
                || config.RestraintMapping.Devices.Values.Any(d => string.Equals(d.Name.Trim(), alias, StringComparison.OrdinalIgnoreCase))))
            Status.Line(StatusKind.Caution, "Another restraint already uses this alias - only one of them will respond to it.");
        TextWithActions($"Glamourer item: {(draft.ItemId is { } equippedItem ? GetItemName(equippedItem) : "(none chosen)")}", ButtonWidth("Choose item..."));
        if (ImGui.Button($"Choose item...##{key}"))
        {
            var catalogEntry = config.RestraintMapping.LocalCatalog.GetValueOrDefault(created.CatalogId);
            plugin.ItemPickerWindow.OpenForItemIds(draft.Name, catalogEntry?.ChangedItemIds.ToHashSet() ?? [], (chosenId, _) => draft.ItemId = chosenId);
        }

        Section.SubHeading("Restrictions");
        DrawRestraintRuleCheckboxes(draft.Rules, key, forOwner: false, allowCustomizePreset: true);

        Section.SubHeading("Attached moodle");
        if (DrawAttachedMoodlePicker(key, draft.Moodle, config, out var modMoodle))
            draft.Moodle = modMoodle;
        DrawRedrawOnApply(key, ref draft.Redraw);

        var invalid = missing ? "This mod is outside the latest scan - rescan it before saving."
            : draft.Name.Trim().Length == 0 ? "Give it a name."
            : IsReserved(alias) ? "This alias is reserved for a fixed Owner command - pick a different one."
            : !(draft.ItemId > 0 && GlamourerIpc.GetItemSlot((uint)draft.ItemId.Value) is not null) || !HasAnyRule(draft.Rules) ? "Choose an item and at least one rule before saving."
            : !BoundAnimationsConfigured(draft.Rules) ? "Choose an animation for every checked Arms/Legs/Full Body Cuffed rule before saving."
            : null;
        DrawSaveBar(key, draft.IsDirty(created), invalid,
            () =>
            {
                draft.Apply(created);
                config.Save();
                subRestraintDraft = RestraintDraft.Load(this, key, created);
            },
            () => subRestraintDraft = RestraintDraft.Load(this, key, created));
    }

    /// `device` null = the new rules-only restraint being drafted.
    private void DrawRulesOnlyDetail(PluginConfig config, RestraintDeviceDefinition? device)
    {
        var key = device is null ? NewRulesOnlyKey : $"dev:{device.Id}";
        if (SubDraftFor(key) is not { } draft)
            return;
        var deleted = false;
        DrawDetailHeader(device?.Name ?? "New rules-only restraint", "Rules-only restraint", null, FontAwesomeIcon.Handcuffs, null, () =>
        {
            if (device is null)
            {
                if (ImGui.Button("Cancel##newDevice"))
                {
                    creatingRulesOnly = false;
                    deleted = true;
                }
                return;
            }
            if (ConfirmDelete.Button($"delete_dev_{device.Id}", device.Name))
            {
                plugin.RestraintCommand.RemoveDevice(device.Id);
                deleted = true;
            }
        });
        if (deleted)
        {
            subRestraintList.Deleted();
            return;
        }

        if (device?.ItemId is { } deviceItemId)
            IconGlyph.WrappedDisabled($"Gear (from an older version): {device.Slot} · {GetItemName(deviceItemId)}");
        if (device is not null && device.Rules.Any(r =>
                CuffSets.IsCuff(r.Kind) && !string.IsNullOrWhiteSpace(r.AnimationId) && !config.GestureMapping.LocalCatalog.ContainsKey(r.AnimationId)))
            Status.Line(StatusKind.Caution, "A cuff animation is stale. Select the animation again before using this restraint.");

        ImGui.InputText("Alias##newDevice", ref draft.Alias, 32);
        IconGlyph.HelpMarker("The word your Owner sends to toggle this restraint.");
        var alias = draft.Alias.Trim();
        DrawReservedWordWarning(alias);
        if (DrawAttachedMoodlePicker("newDevice", draft.Moodle, config, out var pickedDeviceMoodle, width: null))
            draft.Moodle = pickedDeviceMoodle;
        DrawRedrawOnApply("newDevice", ref draft.Redraw);

        Section.SubHeading("Restrictions");
        DrawRestraintRuleCheckboxes(draft.Rules, "newDevice", forOwner: false, allowCustomizePreset: true, rulesOnly: true);

        var hasAnyRule = HasAnyRule(draft.Rules);
        var boundAnimationsConfigured = BoundAnimationsConfigured(draft.Rules, rulesOnly: true);
        var duplicateDeviceName = config.RestraintMapping.Devices.Values.Any(d => d.Id != device?.Id && string.Equals(d.Name, alias, StringComparison.OrdinalIgnoreCase))
            || config.RestraintMapping.ConfiguredMods.Any(m => string.Equals(m.Alias.Trim(), alias, StringComparison.OrdinalIgnoreCase));
        // Only measured once the draft is complete; Save is disabled for an unfinished rule anyway.
        var safeDeviceCommand = !hasAnyRule || !boundAnimationsConfigured || CommandSelector.Fits(RestraintCommand.BuildLockCommand(alias, draft.BuildRules()));
        var invalid = alias.Length == 0 ? "Give it an alias first."
            : IsReserved(alias) ? "This alias is reserved for a fixed Owner command - pick a different one."
            : !hasAnyRule ? "Choose at least one rule."
            : !boundAnimationsConfigured ? "A chosen animation is missing or stale. Choose it again, or clear it."
            : duplicateDeviceName ? "Another restraint already uses this alias."
            : !safeDeviceCommand ? "This restraint name and rule set are too long for a safe command."
            : null;
        var dirty = device is null ? alias.Length > 0 || hasAnyRule : draft.IsDirty(device);
        DrawSaveBar(key, dirty, invalid,
            () =>
            {
                if (device is not null)
                {
                    draft.Apply(device);
                    config.Save();
                    subRestraintDraft = RestraintDraft.Load(this, key, device);
                    return;
                }
                if (plugin.RestraintCommand.CaptureDeviceFromItem(null, null, alias, draft.BuildRules(), draft.Moodle)
                    && config.RestraintMapping.Devices.Values.FirstOrDefault(d => d.Name == alias) is { } added)
                {
                    added.RedrawOnApply = draft.Redraw;
                    config.Save();
                    creatingRulesOnly = false;
                    subRestraintDraft = RestraintDraft.Load(this, $"dev:{added.Id}", added);
                    subRestraintList.Select($"dev:{added.Id}");
                }
            },
            () => subRestraintDraft = device is null ? new RestraintDraft(this, key, rulesOnly: true) : RestraintDraft.Load(this, key, device),
            device is null ? "Add restraint" : "Save");
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
        IconGlyph.HelpMarker("Cuffs and chains drawn for Arms Cuffed, Legs Cuffed and Full Body Cuffed. Only changes what you see. Hiding them doesn't release the restraint.");

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
