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
    private string? importResult;
    private string? resetImportsResult;
    private string? subExportResult;
    private string penumbraFolderSearch = "";
    private string newWardrobeAllowlistFolder = "";

    /// A gap means the tab was just (re)opened.
    private int lastSyncTabFrame = -10;

    private void DrawPicturesNotShared()
    {
        var failed = plugin.RestraintThumbnailWorker.Failed(plugin.RestraintCommand.ThumbnailTargetBytes());
        if (failed.Count > 0)
            IconGlyph.WrappedColored(Theme.Warning, $"Shared without their picture (couldn't be made small enough): {string.Join(", ", failed)}.");
        var pending = plugin.RestraintCommand.PicturesLeftOut.Except(failed).ToList();
        if (pending.Count > 0)
            IconGlyph.WrappedDisabled($"Picture still being prepared for sharing: {string.Join(", ", pending)}.");
    }

    private void DrawSyncTab(bool ownerMode)
    {
        if (!ownerMode)
        {
            DrawSubExportSection();
            DrawPicturesNotShared();
            return;
        }

        // Checks once when the tab becomes visible, skipped if the last success was under a minute ago.
        var frame = ImGui.GetFrameCount();
        if (frame - lastSyncTabFrame > 1 && plugin.Configuration.ActivePairing is { Direction: PairingDirection.OwnerSide } openedPairing)
            plugin.CatalogAutoSync.RequestOwnerCheck(openedPairing, force: false);
        lastSyncTabFrame = frame;

        TwoColumns("syncColumns", () =>
            {
                using (Section.Begin("catalogRelay"))
                    DrawCatalogRelaySection();
            },
            () =>
            {
                using (Section.Begin("catalogFileFallback"))
                {
                    if (ImGui.CollapsingHeader("Offline / legacy file fallback##catalogFileFallback"))
                    {
                        IconGlyph.WrappedDisabled("Only for importing a catalog file by hand. Sync normally happens automatically.");
                        DrawImportCommandsButton();
                    }
                }
            });
    }

    private void DrawSubExportSection()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.CloudDownloadAlt, "Catalog sync");
        ImGui.Separator();

        using (Section.Begin("subRelayInfo", "Cloud catalog sync"))
            DrawSubAutoSyncInfo(config);

        var scanBox = Section.Begin("subScan", "Scan");
        IconGlyph.WrappedDisabled("Scan every catalog at once below, then export your resulting catalog for your Owner.");

        if (ImGui.Button("Scan all"))
        {
            plugin.OutfitCommand.Rescan();
            plugin.GestureCommand.Rescan();
            plugin.MoodlesCommand.Rescan();
            plugin.RestraintCommand.RescanCatalog();
        }
        IconGlyph.HelpMarker("Rescans your designs, animations, moodles and shared restraint folders.");
        scanBox.Dispose();

        using (Section.Begin("scanWardrobe"))
            DrawGatedScan(DependencyId.Glamourer, () => DrawWardrobeScanBody(config));
        using (Section.Begin("scanGesture"))
            DrawGatedScan(DependencyId.Penumbra, () => DrawGestureScanBody(config));
        using (Section.Begin("scanRestraint"))
            DrawGatedScan(DependencyId.Penumbra, () => DrawRestraintScanBody(config));
        using (Section.Begin("scanMoodles"))
            DrawGatedScan(DependencyId.Moodles, () => DrawMoodlesScanBody(config));

        // A trigger with an action too long for a single message can't be shared.
        var tooLong = config.Aliases.CustomTriggers.Where(t => SharedPresetCommands.IsTooLongToShare(t, config)).Select(t => t.Alias).ToList();
        if (tooLong.Count > 0)
        {
            using (Section.Begin("subShareLimits", "Not shared"))
                IconGlyph.WrappedColored(Theme.Warning, $"{tooLong.Count} custom trigger(s) too long to share: {string.Join(", ", tooLong)}. Each action has to fit in one chat message - shorten the long ones (e.g. shorter restraint, animation or preset names, or chat text) to share them with your Owner.");
        }

        using var exportBox = Section.Begin("subExport", "Offline / manual export");
        IconGlyph.WrappedDisabled("Only needed if automatic sync isn't working.");

        var hasAnythingToExport = plugin.OutfitCommand.LastScanTotalDesigns is not null || plugin.GestureCommand.LastScanTotalMods is not null ||
            plugin.MoodlesCommand.LastScanTotalStatuses is not null || config.RestraintMapping.Devices.Count > 0;
        using (ImRaii.Disabled(!hasAnythingToExport))
        {
            if (ImGui.Button("Export..."))
            {
                plugin.FileDialogManager.SaveFileDialog("Export Collar catalog", ".txt", "collar-export", ".txt", (ok, path) =>
                {
                    if (!ok)
                        return;
                    try
                    {
                        System.IO.File.WriteAllText(path, plugin.CatalogSyncService.BuildExport());
                        subExportResult = $"Exported to {path} - send this file to your Owner.";
                    }
                    catch (Exception ex)
                    {
                        subExportResult = $"Export failed: {ex.Message}";
                    }
                });
            }
        }
        if (!hasAnythingToExport)
            IconGlyph.HelpMarker("Scan at least one category above, or tag a Restraints device, before exporting.");
        if (subExportResult is not null)
            IconGlyph.WrappedColored(subExportResult.StartsWith("Export failed", StringComparison.Ordinal) ? Theme.Danger : Theme.Success, subExportResult);
    }

    private void DrawSubAutoSyncInfo(PluginConfig config)
    {
        IconGlyph.WrappedDisabled("Your catalog is shared with your Owner automatically whenever it changes.");

        if (ImGuiCheckbox("Automatically rescan every 15 minutes", config.AutoRescanCatalogs, out var autoRescan))
        {
            config.AutoRescanCatalogs = autoRescan;
            config.Save();
        }
        IconGlyph.HelpMarker("Rescans at login and every 15 minutes, so new additions reach your Owner on their own.");

        if (!config.Permissions.RelayCatalogSync)
        {
            IconGlyph.WrappedColored(Theme.Warning, "\"Catalog sync\" is off in Permissions, so nothing is shared.");
            return;
        }

        foreach (var pairing in config.Pairings.Where(p => p is { Direction: PairingDirection.SubSide, IsPaired: true }))
        {
            var name = pairing.PeerName ?? "your Owner";
            if (pairing.LastPublishedCatalogUnixSeconds > 0)
                IconGlyph.WrappedDisabled($"Last shared with {name}: {DateTimeOffset.FromUnixTimeSeconds(pairing.LastPublishedCatalogUnixSeconds).LocalDateTime:g}.");
            else
                IconGlyph.WrappedDisabled($"Not shared with {name} yet - it goes out once their plugin checks in.");
            DrawSubPublishStatus(pairing, name);
        }
    }

    private void DrawSubPublishStatus(PairingState pairing, string name)
    {
        var retry = pairing.NextPublishRetryUnixSeconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(pairing.NextPublishRetryUnixSeconds).LocalDateTime : (DateTime?)null;
        switch (pairing.LastPublishOutcome)
        {
            case Relay.MailboxPublishOutcome.Failed:
                IconGlyph.WrappedColored(Theme.Warning, $"Last automatic share with {name} failed: {pairing.LastPublishError ?? "unknown error"}{(retry is { } failRetry ? $" Trying again around {failRetry:t}." : "")}");
                return;
            case Relay.MailboxPublishOutcome.NotReady:
                IconGlyph.WrappedDisabled(pairing.LastPublishError ?? $"Waiting for {name}'s plugin to set up automatic sync.");
                return;
            case Relay.MailboxPublishOutcome.RateLimited when retry is { } r:
                IconGlyph.WrappedDisabled($"A change is waiting for the relay's upload interval - it goes out around {r:t}.");
                return;
        }
        var (pending, opensAtUtc) = plugin.CatalogAutoSync.PendingChange(pairing);
        if (pending && opensAtUtc > DateTime.UtcNow)
            IconGlyph.WrappedDisabled($"A change is waiting for the relay's upload interval - it goes out around {opensAtUtc.ToLocalTime():t}.");
        else if (pending)
            IconGlyph.WrappedDisabled("A change goes out in a few minutes.");
    }

    /// One missing plugin disables only its own source's section.
    private void DrawGatedScan(DependencyId required, Action draw)
    {
        var blocked = DependencyGates.FeatureBlockedReason(plugin, required);
        using (ImRaii.Disabled(blocked is not null))
            draw();
        if (blocked is not null)
            IconGlyph.WrappedColored(Theme.StatusMissing, blocked);
    }

    private void DrawWardrobeScanBody(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.Tshirt, "Wardrobe design allowlist & scan");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("No folders = all designs. Add folders to limit what's shared.");
        IconGlyph.HelpMarker("Only designs in these Glamourer folders are shared.");

        DrawAllowlistBody(config.WardrobeFolderAllowlist, ref newWardrobeAllowlistFolder, "wardrobe");

        ImGui.Spacing();
        if (ImGui.Button("Rescan wardrobe"))
            plugin.OutfitCommand.Rescan();
        IconGlyph.HelpMarker("Re-reads your Glamourer designs.");

        DrawWardrobeScanFeedback();
    }

    private void DrawWardrobeScanFeedback()
    {
        var wardrobe = plugin.Configuration.WardrobeMapping;
        var lastScanTotal = plugin.OutfitCommand.LastScanTotalDesigns;

        if (plugin.OutfitCommand.LastScanError is { } scanError)
            IconGlyph.WrappedColored(Theme.Danger, scanError);

        if (lastScanTotal is null)
        {
            IconGlyph.WrappedDisabled("Not scanned yet this session.");
            return;
        }

        var matched = wardrobe.LocalDesigns.Count;
        var color = matched > 0 ? Theme.Success : Theme.Warning;
        var scope = plugin.Configuration.WardrobeFolderAllowlist.Count == 0 ? "all-design mode" : "folder-filtered mode";
        IconGlyph.WrappedColored(color, $"Found {lastScanTotal} saved design(s); {matched} available ({scope}).");

        if (matched == 0)
            return;

        if (ImGui.Button("Copy names##wardrobe"))
            ImGui.SetClipboardText(string.Join("\n", wardrobe.LocalDesigns.Values.Select(d => d.Name)));
        IconGlyph.HelpMarker("Copies the list as text so you can send your Owner the names.");

        using var _ = ImRaii.Child("wardrobeCatalog", new Vector2(0, Scaled(80)), true);
        foreach (var entry in wardrobe.LocalDesigns.Values)
            ImGui.BulletText(entry.Name);
    }

    private void DrawGestureScanBody(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.TheaterMasks, "Animation mods to scan");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("With no folders or mods selected, everything is scanned.");
        DrawPenumbraFolderPicker("Animation folders", config.SelectedGestureFolders, config);
        ImGui.InputTextWithHint("##gestureModSearch", "Search mod names...", ref gestureModSearch, 128);
        var installed = plugin.GestureCommand.GetInstalledMods();
        using (ImRaii.Child("gestureModPicker", new Vector2(0, Scaled(180)), true))
        {
            foreach (var mod in installed.Where(m =>
                         (config.SelectedGestureFolders.Count == 0 || (m.SortPath is { } path && config.SelectedGestureFolders.Any(f =>
                             path.Equals(f, StringComparison.OrdinalIgnoreCase) || path.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase)))) &&
                         (string.IsNullOrWhiteSpace(gestureModSearch) || m.Name.Contains(gestureModSearch.Trim(), StringComparison.OrdinalIgnoreCase))))
            {
                var selected = config.SelectedGestureMods.Contains(mod.Directory);
                if (ImGui.Checkbox($"{mod.Name}##gestureMod_{mod.Directory}", ref selected))
                {
                    if (selected) config.SelectedGestureMods.Add(mod.Directory); else config.SelectedGestureMods.Remove(mod.Directory);
                    config.Save();
                }
                if (mod.SortPath != null) { ImGui.SameLine(); IconGlyph.WrappedDisabled(mod.SortPath); }
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Rescan animations"))
            plugin.GestureCommand.Rescan();
        IconGlyph.HelpMarker("Reads every installed mod, or only the ones you select.");

        DrawGestureScanFeedback();
    }

    private void DrawRestraintScanBody(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.Lock, "Shared Penumbra restraints");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Only restraint mods in these folders are shared with your Owner.");
        DrawPenumbraFolderPicker("Restraint folders", config.SelectedRestraintFolders, config);
        if (ImGui.Button("Rescan restraints")) plugin.RestraintCommand.RescanCatalog();
        var command = plugin.RestraintCommand;
        if (command.LastScanError is { } error) IconGlyph.WrappedColored(Theme.Danger, error);
        else if (command.LastScanTotalMods is not null)
            IconGlyph.WrappedColored(config.RestraintMapping.LocalCatalog.Count > 0 ? Theme.Success : Theme.Warning,
                $"Matched {command.LastScanMatchedMods} mod(s); found {config.RestraintMapping.LocalCatalog.Count} restraint option(s).");
        if (config.SelectedRestraintFolders.Count == 0)
            IconGlyph.WrappedColored(Theme.Warning, "No folders selected: the shared Penumbra restraint catalog is empty.");
        using var child = ImRaii.Child("restraintCatalogPreview", new Vector2(0, Scaled(100)), true);
        foreach (var entry in config.RestraintMapping.LocalCatalog.Values.OrderBy(x => x.ModName))
            ImGui.BulletText(entry.ModName);
    }

    private void DrawPenumbraFolderPicker(string label, List<string> selected, PluginConfig config)
    {
        var installed = plugin.GestureCommand.GetInstalledMods();
        var folders = installed.Select(x => x.SortPath).Where(x => !string.IsNullOrWhiteSpace(x))
            .SelectMany(x => ParentFolders(x!)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var preview = selected.Count == 0 ? "None" : $"{selected.Count} selected";
        if (ImGui.BeginCombo($"{label}##{label}", preview))
        {
            ImGui.InputTextWithHint($"##folderSearch{label}", "Search folders...", ref penumbraFolderSearch, 128);
            foreach (var folder in folders.Where(x => string.IsNullOrWhiteSpace(penumbraFolderSearch) || x.Contains(penumbraFolderSearch, StringComparison.OrdinalIgnoreCase)))
            {
                var chosen = selected.Contains(folder, StringComparer.OrdinalIgnoreCase);
                if (ImGui.Selectable(folder, chosen, ImGuiSelectableFlags.DontClosePopups))
                {
                    if (chosen) selected.RemoveAll(x => x.Equals(folder, StringComparison.OrdinalIgnoreCase)); else selected.Add(folder);
                    config.Save();
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(folder);
            }
            ImGui.EndCombo();
        }
        foreach (var folder in selected.ToList())
        {
            var missing = !folders.Contains(folder, StringComparer.OrdinalIgnoreCase);
            TextWithActions(missing ? $"{folder} (missing)" : folder, ButtonWidth("Remove"));
            if (ImGui.Button($"Remove##{label}{folder}")) { selected.Remove(folder); config.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(folder);
        }
    }

    private static IEnumerable<string> ParentFolders(string path)
    {
        var normalized = path.Replace('\\', '/').Trim('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < parts.Length; i++) yield return string.Join('/', parts.Take(i));
    }

    private void DrawGestureScanFeedback()
    {
        var gestureMapping = plugin.Configuration.GestureMapping;
        var lastScanTotal = plugin.GestureCommand.LastScanTotalMods;

        if (lastScanTotal is null)
        {
            IconGlyph.WrappedDisabled("Not scanned yet this session.");
            return;
        }

        if (plugin.GestureCommand.LastScanError is { } error)
        {
            IconGlyph.WrappedColored(Theme.Danger, error);
            return;
        }
        var matched = gestureMapping.LocalCatalog.Count;
        var color = matched > 0 ? Theme.Success : Theme.Warning;
        var selectionCount = plugin.Configuration.SelectedGestureMods.Count;
        var scope = selectionCount == 0 ? "all-mod mode" : $"{selectionCount} explicitly selected";
        IconGlyph.WrappedColored(color, $"Found {lastScanTotal} installed mod(s); {scope}; {matched} animation trigger(s) discovered.");

        if (matched == 0 && lastScanTotal > 0)
        {
            ImGui.TextWrapped("No playable animation options were found in the current scan scope.");
            return;
        }

        if (matched == 0)
            return;

        if (ImGui.Button("Copy names##gesture"))
        {
            ImGui.SetClipboardText(plugin.GestureCommand.ExportCatalog());
        }
        IconGlyph.HelpMarker("Copies these animations for your Owner's Add from clipboard.");

        using var _ = ImRaii.Child("gestureCatalog", new Vector2(0, Scaled(80)), true);
        foreach (var entry in gestureMapping.LocalCatalog.Values)
        {
            ImGui.BulletText(entry.DisplayLabel);
        }
    }

    /// Reads individual statuses rather than presets, so the Owner can command a single status.
    private void DrawMoodlesScanBody(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.TheaterMasks, "Moodles status scan");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Your Moodles statuses are read directly - nothing to set up.");

        if (ImGui.Button("Rescan Moodles statuses"))
            plugin.MoodlesCommand.Rescan();
        IconGlyph.HelpMarker("Re-reads your Moodles statuses. Run it after adding a new one.");

        DrawMoodlesScanFeedback();
    }

    private void DrawMoodlesScanFeedback()
    {
        var moodlesMapping = plugin.Configuration.MoodlesMapping;
        var lastScanTotal = plugin.MoodlesCommand.LastScanTotalStatuses;

        if (plugin.MoodlesCommand.LastScanStatus is MoodlesScanStatus.Unavailable or MoodlesScanStatus.Failed)
        {
            IconGlyph.WrappedColored(Theme.Danger, plugin.MoodlesCommand.LastScanError ?? "Moodles status scan failed.");
            if (moodlesMapping.LocalCatalog.Count > 0)
                IconGlyph.WrappedDisabled($"Keeping {moodlesMapping.LocalCatalog.Count} status(es) from the last successful scan.");
            return;
        }

        if (lastScanTotal is null)
        {
            IconGlyph.WrappedDisabled("Not scanned yet this session.");
            return;
        }

        IconGlyph.WrappedColored(Theme.Success, $"Scan succeeded: found {lastScanTotal} registered status(es).");

        if (lastScanTotal == 0)
            return;

        if (ImGui.Button("Copy names##moodles"))
            ImGui.SetClipboardText(string.Join("\n", moodlesMapping.LocalCatalog.Values.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x)));
        IconGlyph.HelpMarker("Copies the list as text so you can send your Owner the names.");

        using var _ = ImRaii.Child("moodlesCatalog", new Vector2(0, Scaled(80)), true);
        foreach (var entry in moodlesMapping.LocalCatalog.Values)
        {
            ImGui.PushID(entry.StatusId);
            ImGui.BulletText(entry.Name);
            ImGui.PopID();
        }
    }

    /// "Empty = all"; prefix matching when narrowed.
    private void DrawAllowlistBody(List<string> allowlist, ref string newFolderInput, string idSuffix)
    {
        for (var i = 0; i < allowlist.Count; i++)
        {
            ImGui.PushID(i);
            ImGui.Bullet();
            ImGui.SameLine();
            TextWithActions(allowlist[i], ButtonWidth("Remove"));
            if (ImGui.Button("Remove"))
            {
                allowlist.RemoveAt(i);
                plugin.Configuration.Save();
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }

        ImGui.InputText($"##newFolder_{idSuffix}", ref newFolderInput, 128);
        ContinueRowOrWrap(ButtonWidth("Add folder"));
        if (ImGui.Button($"Add folder##{idSuffix}") && newFolderInput.Length > 0)
        {
            allowlist.Add(newFolderInput);
            plugin.Configuration.Save();
            newFolderInput = "";
        }
    }

    private void DrawImportCommandsButton()
    {
        const string importLabel = "Import commands";
        const string resetLabel = "Reset imports";
        var importWidth = ImGui.CalcTextSize(importLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
        var resetWidth = ImGui.CalcTextSize(resetLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
        var totalWidth = importWidth + ImGui.GetStyle().ItemSpacing.X + resetWidth;
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail > totalWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - totalWidth) / 2f);

        if (ImGui.Button(importLabel))
        {
            plugin.FileDialogManager.OpenFileDialog("Import Collar catalog", ".txt", (ok, path) =>
            {
                if (!ok)
                    return;
                try
                {
                    var text = System.IO.File.ReadAllText(path);
                    // Paired with this Sub: re-importing their file replaces what they shared before, like a relay refresh.
                    if (plugin.CatalogSyncService.TryApplyFileAsPairSnapshot(text, plugin.Configuration.ActivePairing) is { } shared)
                    {
                        importResult = shared.Error ?? $"Updated from your Sub's file: {shared.Added} added, {shared.Updated} updated, {shared.Removed} removed. Your own commands were left as they were.";
                        resetImportsResult = null;
                        return;
                    }
                    var result = plugin.CatalogSyncService.ParseImport(text);
                    var duplicateNote = result.Duplicates > 0 ? $" {result.Duplicates} duplicate(s) skipped." : "";
                    importResult = result.Error ?? (result.TotalAdded == 0
                        ? $"Nothing new - everything in that file was already imported or a duplicate.{duplicateNote}"
                        : $"Imported {result.TotalAdded} new command(s): {result.Title} title, {result.Wardrobe} outfit, {result.Gesture} animation, {result.Moodles} moodles, {result.Restraints} restraint, {result.Bundles} bundle.{duplicateNote}");
                    resetImportsResult = null;
                }
                catch (Exception ex)
                {
                    importResult = $"Import failed: {ex.Message}";
                }
            });
        }

        ImGui.SameLine();

        /// Removes only Imported-sourced entries. The Custom Trigger list mixes imported and manual bundles, so it's cleared whole.
        if (ImGui.Button(resetLabel))
        {
            var quick = plugin.Configuration.QuickCommands;
            RemoveImportedEntries(quick.Titles);
            RemoveImportedEntries(quick.Outfits);
            RemoveImportedEntries(quick.Gestures);
            RemoveImportedEntries(quick.Moodles);
            RemoveImportedEntries(quick.Restraints);
            quick.Aliases.Clear();
            plugin.Configuration.GestureMapping.ImportedPeerCatalog.Clear();
            plugin.Configuration.RestraintMapping.ImportedPeerCatalog.Clear();
            plugin.CatalogStore.Save(plugin.Configuration);
            plugin.Configuration.Save();
            restraintRuleEdits.Clear();
            resetImportsResult = "All imports reset to a blank slate.";
            importResult = null;
        }
        IconGlyph.HelpMarker("Clears everything imported from your Sub, including every bundle. Commands you added yourself stay.");

        if (importResult is not null)
        {
            var isError = importResult.StartsWith("Import failed", StringComparison.Ordinal) || importResult.Contains("doesn't look like", StringComparison.Ordinal) || importResult.Contains("is empty", StringComparison.Ordinal);
            IconGlyph.WrappedColored(isError ? Theme.Danger : Theme.Success, importResult);
        }
        if (resetImportsResult is not null)
            IconGlyph.WrappedColored(Theme.Success, resetImportsResult);
    }

    private static void RemoveImportedEntries(List<QuickCommand> list) =>
        list.RemoveAll(cmd => cmd.Source == ImportSource.Imported);

    private void DrawCatalogRelaySection()
    {
        var relayService = plugin.CatalogSyncRelayService;
        var mailbox = plugin.CatalogMailboxService;
        var pairing = plugin.Configuration.ActivePairing;
        IconGlyph.Text(FontAwesomeIcon.CloudDownloadAlt, "Cloud catalog sync");
        IconGlyph.WrappedDisabled("Your Sub's catalog arrives here automatically when it changes.");
        if (pairing is not { Direction: PairingDirection.OwnerSide })
        {
            IconGlyph.WrappedDisabled("No Owner-side pairing is active - pick one above or pair in Settings.");
            return;
        }

        var (stateColor, stateText) = DescribeCatalogSyncState(pairing, mailbox);
        IconGlyph.WrappedColored(stateColor, stateText);

        using (ImRaii.Disabled(mailbox.IsChecking(pairing.Id)))
        {
            if (ImGui.Button(mailbox.IsChecking(pairing.Id) ? "Checking..." : "Check now"))
                plugin.CatalogAutoSync.RequestOwnerCheck(pairing, force: true);
        }
        IconGlyph.HelpMarker("Checks for a newer catalog from your Sub now.");

        ImGui.SameLine();
        using (ImRaii.Disabled(relayService.RequestInFlight))
        {
            if (ImGui.Button(relayService.RequestInFlight ? "Requesting..." : "Request refresh"))
                Plugin.FireAndForget(relayService.RequestRefreshAsync(pairing, System.Threading.CancellationToken.None));
        }
        IconGlyph.HelpMarker("Asks your Sub's plugin to send its catalog by tell. They need to be online.");

        if (pairing.SubLastPublishedUnixSeconds is { } published)
            IconGlyph.WrappedDisabled($"Your Sub last shared changes: {DateTimeOffset.FromUnixTimeSeconds(published).LocalDateTime:g}.");
        if (pairing.LastAcceptedCatalogSyncUnixSeconds > 0)
            IconGlyph.WrappedDisabled($"Last imported: {DateTimeOffset.FromUnixTimeSeconds(pairing.LastAcceptedCatalogSyncUnixSeconds).LocalDateTime:g}.");
        if (pairing.LastMailboxCheckOkUnixSeconds > 0)
            IconGlyph.WrappedDisabled($"Last checked: {DateTimeOffset.FromUnixTimeSeconds(pairing.LastMailboxCheckOkUnixSeconds).LocalDateTime:g}.");

        if (relayService.RequestInFlight)
            IconGlyph.WrappedDisabled($"Manual refresh: {relayService.Phase}.");
        if (relayService.LastError is { Length: > 0 } lastError)
            IconGlyph.WrappedColored(Theme.Danger, $"Manual refresh: {lastError}");

        var lastResult = mailbox.LastAutoImport is { } auto && auto.PairingId == pairing.Id ? auto.Result : relayService.LastImportResult;
        if (lastResult is { Error: null } result && result.Added + result.Updated + result.Removed > 0)
            IconGlyph.WrappedColored(Theme.Success, $"Last sync: {result.Added} added, {result.Updated} updated, {result.Removed} removed.");
    }

    /// Priority order: Syncing > Last sync failed > Out of date (no successful check in ~2h) > No automatic updates > Up to date.
    private static (Vector4 Color, string Text) DescribeCatalogSyncState(PairingState pairing, Relay.CatalogMailboxService mailbox)
    {
        if (mailbox.IsSyncing(pairing.Id))
            return (Theme.Warning, "Syncing: your Sub's catalog changed - importing it now...");
        if (pairing.LastMailboxCheckError is { Length: > 0 } error)
            return (Theme.Warning, $"Last sync failed: {error}");

        var lastOk = pairing.LastMailboxCheckOkUnixSeconds;
        if (lastOk == 0)
            return (Theme.Warning, "Out of date: not checked yet since this update - checking shortly.");
        var sinceOk = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(lastOk);
        if (sinceOk > TimeSpan.FromHours(2))
            return (Theme.Warning, $"Out of date: couldn't check for updates since {DateTimeOffset.FromUnixTimeSeconds(lastOk).LocalDateTime:g}.");
        if (pairing.SubLastPublishedUnixSeconds is null)
            return (Theme.Warning, "No updates from your Sub yet - their \"Catalog sync\" permission may be off.");
        return (Theme.Success, "Up to date.");
    }
}
