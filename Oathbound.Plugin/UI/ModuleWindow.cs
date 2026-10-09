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

/// One reusable window whose content switches on the active module id.
public sealed partial class ModuleWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string activeModule = "title";

    public ModuleWindow(Plugin plugin) : base("Oathbound###CollarModuleWindow")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(465, 520), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.QuestionCircle,
            Click = _ => plugin.Tutorial.Start(activeModule, lastViewIsOwner),
            ShowTooltip = () => ImGui.SetTooltip("Tour of this module"),
        });
    }

    /// The view drawn last frame, so the title-bar ? starts the tour matching what's on screen.
    private bool lastViewIsOwner;

    public void Dispose() { }

    public void Show(string moduleId)
    {
        activeModule = moduleId;
        IsOpen = true;
    }

    private string newTitleAlias = "";
    private string newTitleText = "";
    private bool newTitleIsPrefix;
    private Vector3 newTitleColor = new(1, 1, 1);
    private bool newTitleHasGlow;
    private Vector3 newTitleGlow = new(1, 1, 1);

    private string newOutfitAlias = "";
    private int newOutfitDesignIndex;
    private AttachedMoodleRef? newOutfitMoodle;

    /// Null = no moodle; an ad-hoc device has no Sub-side default.
    private string? adHocMoodleOverride;
    /// Null = Permanent.
    private int? adHocLockSeconds;
    private int? ctqLockSeconds;
    private string adHocLockKey = "";
    private string ctqLockKey = "";
    private StruggleLevel adHocStruggle;
    private int adHocStrugglePenalty;
    private StruggleLevel ctqStruggle;
    /// -1 = a moodle from the Sub's library by name.
    private int ctqCustomMoodleIndex = -1;
    private int ctqStrugglePenalty;
    private string? editingQuickMoodle;
    private bool newOutfitLocked = true;

    private string newGestureAlias = "";

    private string newMoodleAlias = "";

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

    private string newDeviceName = "";
    private AttachedMoodleRef? newDeviceMoodle;
    private bool newDeviceRedraw;
    private ApiEquipSlot? newDeviceSlot;
    private ulong? newDeviceItemId;
    private readonly RestraintRuleEditState newDeviceRuleEdit = new();
    private string? editingDeviceId;

    private string ownerRestraintSearch = "";
    private string subRestraintSearch = "";

    /// Gear is optional; a rules-only ad-hoc device leaves mod/slot/item unset.
    private string newAdHocLabel = "";
    private readonly RestraintRuleEditState newAdHocRuleEdit = new();

    private static readonly string[] PoseNames = ["Ground Sit", "Sit", "Doze"];

    /// Order matches the ChatChannel enum; the header combo indexes into it.

    private string commandInput = "";
    private string newTitleQuickText = "";
    private bool newTitleQuickIsPrefix;
    private Vector3 newTitleQuickColor = new(1, 1, 1);
    private bool newTitleQuickHasGlow;
    private Vector3 newTitleQuickGlow = new(1, 1, 1);
    private string? importResult;
    private string? resetImportsResult;
    private string? subExportResult;
    private string gestureModSearch = "";
    private string penumbraFolderSearch = "";
    private string newWardrobeAllowlistFolder = "";
    private int newCollarMoodleStatusIndex;
    private int toyVibrateIntensity = 50;
    private bool toyVibrateHasDuration;
    private bool toyVibrateIsPermanent;
    private int toyVibrateDurationSeconds = 10;
    private string toyIntifaceAddress = "";

    /// Non-null while editing an existing pattern in place.
    private string toyPatternNameInput = "";
    private bool toyPatternLoopInput;
    private readonly List<PatternStep> toyPatternStepsInput = new();
    private string? toyPatternEditingId;
    private string? toyPatternError;

    private ToyTriggerKind toyTriggerKindInput = ToyTriggerKind.HealthPercent;
    private int toyTriggerHealthThresholdInput = 50;
    private RestraintRuleKind toyTriggerRestrictionKindInput = RestraintRuleKind.Gagged;
    private readonly HashSet<uint> toyTriggerSpellJobIdsInput = new();
    private readonly HashSet<uint> toyTriggerSpellActionIdsInput = new();
    private string toyTriggerSpellJobSearch = "";
    private string toyTriggerSpellActionSearch = "";
    private bool toyTriggerUsePatternInput;
    private int toyTriggerIntensityInput = 50;
    private bool toyTriggerHasDurationInput;
    private int toyTriggerDurationSecondsInput = 10;
    private string toyTriggerPatternNameInput = "";
    private int toyTriggerCooldownInput = 5;
    private string? editingToyTriggerId;
    private readonly HashSet<uint> toyTriggerEmoteIdsInput = new();
    private string toyTriggerEmoteSearch = "";
    private readonly List<string> toyTriggerSourcePlayersInput = new();
    private string toyTriggerSourcePlayerInput = "";

    /// A gap means the tab was just (re)opened.
    private int lastSyncTabFrame = -10;

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

    /// Keyed by the quick command's Label; transient.
    private string? expandedSubModKey;
    private readonly Dictionary<string, RestraintRuleEditState> restraintRuleEdits = new();

    private sealed class RestraintRuleEditState
    {
        public bool ForcedPose;
        public int PoseIndex;
        public bool ForcedPoseIsMod;
        public string? ForcedPoseAnimationId;
        public bool WalkOnly;
        public bool ActionBlock;
        public bool Gagged;
        public string? GagAnimationId;
        public string? GagCustomizePresetId;
        public string? GagCustomizePresetLabel;
        public GagLevel GagLevel;
        public bool ArmsCuffed;
        public string? ArmsCuffedAnimationId;
        public bool LegsCuffed;
        public string? LegsCuffedAnimationId;
        public bool FullBodyCuffed;
        public string? FullBodyCuffedAnimationId;
        /// Device and mod restraints only; a rules-only restraint's cuffs are always drawn.
        public bool ArmsCuffedDrawn;
        public bool LegsCuffedDrawn;
        public bool FullBodyCuffedDrawn;
    }

    public override void PreDraw() => Theme.PushWindowStyle();
    public override void PostDraw() => Theme.PopWindowStyle();

    public override void Draw()
    {
        // Title-bar buttons aren't ImGui items, so the ? button's area is reported by position.
        var windowPos = ImGui.GetWindowPos();
        var titleBarHeight = ImGui.GetFrameHeight();
        TutorialService.AnchorRect(TutorialAnchors.ModuleHelp, windowPos + new Vector2(ImGui.GetWindowWidth() - titleBarHeight * 3f, 0),
            windowPos + new Vector2(ImGui.GetWindowWidth(), titleBarHeight));

        using var card = Card.Begin("moduleCard");
        var isOwner = ResolveOwnerModeView();
        lastViewIsOwner = isOwner;

        if (DependencyGates.ModuleBlockedReason(plugin, activeModule) is { } blockedReason)
        {
            IconGlyph.WrappedColored(Theme.StatusMissing, blockedReason);
            IconGlyph.WrappedDisabled("Install or enable it from /xlplugins. This window updates once it's detected.");
            return;
        }

        DrawActiveBanner(isOwner);

        switch (activeModule)
        {
            case "title":
                if (isOwner) DrawTitleQuickSection(DrawOwnerCanSendBanner());
                else DrawTitleModule();
                break;
            case "outfit":
                if (isOwner) DrawOutfitQuickSection(DrawOwnerCanSendBanner());
                else DrawWardrobeModule();
                break;
            case "animation":
                if (isOwner) DrawGestureQuickSection(DrawOwnerCanSendBanner());
                else DrawGestureModule();
                break;
            case "moodles":
                if (isOwner) DrawMoodlesQuickSection(DrawOwnerCanSendBanner());
                else DrawMoodlesModule();
                break;
            case "restraints":
                if (isOwner) DrawRestraintQuickSection(DrawOwnerCanSendBanner());
                else DrawRestraintsModule();
                break;
            case "toycontrol":
                if (isOwner) DrawToyControlQuickSection(DrawOwnerCanSendBanner());
                else DrawToyControlModule();
                break;
            case "customtriggers":
                if (isOwner)
                {
                    var canSend = DrawOwnerCanSendBanner();
                    IconGlyph.Text(FontAwesomeIcon.BoltLightning, "Custom Triggers");
                    ImGui.Separator();
                    DrawCommandRow("ctqRevert", canSend, new RowCommand("Revert custom triggers", "customtrigger revert", FixedActionIds.CustomTriggerRevert,
                        "Undoes the title, outfit, animation, moodles and restraints your Custom Triggers applied. Sent chat can't be taken back."));
                    using (Section.Begin("ctqSaved", "Bundles"))
                        DrawOwnerBundles(canSend);
                    using (Section.Begin("freeformComposer", "One-off command"))
                        DrawFreeformComposer(canSend);
                }
                else
                    DrawCustomTriggersModule();
                break;
            case "collar":
                if (isOwner) DrawCollarQuickSection(DrawOwnerCanSendBanner());
                else DrawCollarModule();
                break;
            case "follow":
                // Heading and status span both columns, so the two columns' boxes start level.
                if (isOwner)
                {
                    var canSendLeash = DrawOwnerCanSendBanner();
                    IconGlyph.Text(FontAwesomeIcon.Link, "Follow / Leash");
                    ImGui.Separator();
                    OwnerStatusView.Draw(plugin);
                    TwoColumns("followColumns", () => DrawFollowQuickSectionBody(canSendLeash), () => DrawLeashLineSection(plugin.Configuration));
                }
                else
                {
                    IconGlyph.Text(FontAwesomeIcon.Link, "Follow / Leash");
                    ImGui.Separator();
                    TwoColumns("followColumns", DrawFollowLeashModule, () => DrawLeashLineSection(plugin.Configuration));
                }
                break;
            case "sync":
                DrawSyncTab(isOwner);
                break;
            case "reactions":
                DrawReactionsModule();
                break;
            case "rulebook":
                DrawRulebookModule(isOwner);
                break;
        }
    }

    /// Returns whether Send should be enabled on that tab.
    private bool DrawOwnerCanSendBanner()
    {
        var canSend = plugin.Configuration.ActivePairing is { Direction: PairingDirection.OwnerSide };
        if (!canSend)
            IconGlyph.WrappedColored(Theme.Warning, "No Sub to send to yet - pick an Owner-side pairing in the header, or pair in Settings. Copy still works.");
        return canSend;
    }

    private bool ResolveOwnerModeView()
    {
        var config = plugin.Configuration;
        var isOwner = config.ResolveActiveDirection() == PairingDirection.OwnerSide;

        // No Save here: it's only a fallback default, so it rides along on the next save.
        if (config.Role == PluginRole.Switch && config.SwitchLastUsedOwnerView != isOwner)
            config.SwitchLastUsedOwnerView = isOwner;
        return isOwner;
    }

    private void DrawGoToSyncTabPrompt(string message)
    {
        IconGlyph.WrappedDisabled(message);
        if (ImGui.Button("Go to Sync tab"))
            Show("sync");
    }
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
    /// A note only - the toggle stays usable so a permission can be set before installing the plugin.
    private void DrawPermissionDependencyNote(params DependencyId[] required)
    {
        if (DependencyGates.PermissionNote(plugin, required) is { } note)
            IconGlyph.WrappedColored(Theme.StatusMissing, note);
    }

    internal void DrawPermissionsCard()
    {
        var permissions = plugin.Configuration.Permissions;
        IconGlyph.Text(FontAwesomeIcon.ShieldAlt, "Permissions");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("What you'll accept from a paired Owner while you're set to Sub - each category is independent.");
        var group = Section.Begin("permBasic", "Everyday");

        if (ImGuiCheckbox("Title", permissions.Title, out var newTitle))
            SavePermission(() => permissions.Title = newTitle);
        IconGlyph.HelpMarker("Lets a paired Owner apply or clear your Honorific title via a trigger tell.");
        DrawPermissionDependencyNote(DependencyId.Honorific);

        if (ImGuiCheckbox("Outfit / Wardrobe", permissions.Outfit, out var newOutfit))
            SavePermission(() => permissions.Outfit = newOutfit);
        IconGlyph.HelpMarker("Lets a paired Owner apply or unlock a Glamourer design via a trigger tell.");
        DrawPermissionDependencyNote(DependencyId.Glamourer);

        group.Dispose();
        group = Section.Begin("permAutomation", "Automation (needs the ToS acknowledgement)");
        var config = plugin.Configuration;
        if (!config.TosAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Animation/Follow/Restraints/Teleport require the acknowledgement on the ToS tab first.");

        using (ImRaii.Disabled(!config.TosAcknowledged))
        {
            if (ImGuiCheckbox("Animation", permissions.Gesture, out var newGesture))
                SavePermission(() => permissions.Gesture = newGesture);
            IconGlyph.HelpMarker("Lets your Owner play your shared animations on you.");
            DrawPermissionDependencyNote(DependencyId.Penumbra);

            if (ImGuiCheckbox("Follow / Leash", permissions.Follow, out var newFollow))
                SavePermission(() => permissions.Follow = newFollow);
            IconGlyph.HelpMarker("Lets a paired Owner lock your movement to follow them, blocking your own WASD input until released. Heavier automation footprint than the other three - see the README's Automation risk section.");

            if (ImGuiCheckbox("Restraints", permissions.Restraints, out var newRestraints))
                SavePermission(() => permissions.Restraints = newRestraints);
            IconGlyph.HelpMarker("Lets a paired Owner apply or release a restraint device via a trigger tell. Restraint devices can suppress movement, force walking, block actions, garble your outgoing chat (Gagged), or hold you in a chosen animation (Arms/Legs/Full Body Cuffed) while active - Gagged rewrites content you actually typed, a heavier automation footprint than the others - see the Restraints tab and the README's Automation risk section.");
            DrawPermissionDependencyNote(DependencyId.Glamourer, DependencyId.Penumbra);

            if (ImGuiCheckbox("Teleport", permissions.Teleport, out var newTeleport))
                SavePermission(() => permissions.Teleport = newTeleport);
            IconGlyph.HelpMarker("Lets a paired Owner summon you to their side via a trigger tell: your client changes world, teleports to your nearest attuned aetheryte (or travels to their housing ward), then walks, rides or flies to them. Your movement is locked the whole way - press Stop teleport in the header (or panic) to end it. Refused automatically while you're bound by duty, in combat, or when they're inside a house. Requires Lifestream and vnavmesh.");
            DrawPermissionDependencyNote(DependencyGates.Teleport);
        }

        group.Dispose();
        group = Section.Begin("permCollarMoodles", "Collar & Moodles");
        if (ImGuiCheckbox("Collar", permissions.Collar, out var newCollar))
            SavePermission(() => permissions.Collar = newCollar);
        IconGlyph.HelpMarker("Locks your collar on automatically when you accept a pairing.");

        if (ImGuiCheckbox("Moodles", permissions.Moodles, out var newMoodles))
            SavePermission(() => permissions.Moodles = newMoodles);
        IconGlyph.HelpMarker("Lets your Owner add or clear your Moodles statuses.");
        DrawPermissionDependencyNote(DependencyId.Moodles);
        ImGui.Indent();
        using (ImRaii.Disabled(!permissions.Moodles))
        {
            if (ImGuiCheckbox("Allow moodles my Owner writes", permissions.OwnerWrittenMoodles, out var newOwnerWritten))
                SavePermission(() => permissions.OwnerWrittenMoodles = newOwnerWritten);
        }
        IconGlyph.HelpMarker("Your Owner can put moodles they wrote themselves on you - their own title, description and icon - without you making them first. Other players see moodles through sync tools. Moodles also has to allow it: in Moodles' settings, \"Allow other plugins apply Moodles.\" and allow your Owner (friends, party members or everyone).");
        ImGui.Unindent();

        group.Dispose();
        group = Section.Begin("permCatalog", "Catalog sync");
        if (ImGuiCheckbox("Catalog sync", permissions.RelayCatalogSync, out var newRelayCatalogSync))
            SavePermission(() => permissions.RelayCatalogSync = newRelayCatalogSync);
        IconGlyph.HelpMarker("Shares your catalog with your Owner automatically when it changes. Off by default.");

        group.Dispose();
        group = Section.Begin("permCustomChat", "Custom chat (own acknowledgement)");
        if (!config.CustomChatAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Needs its own acknowledgement on the ToS tab first.");

        using (ImRaii.Disabled(!config.CustomChatAcknowledged))
        {
            if (ImGuiCheckbox("Custom chat messages", permissions.CustomChatMessages, out var newCustomChat))
                SavePermission(() => permissions.CustomChatMessages = newCustomChat);
            IconGlyph.HelpMarker("Lets a Custom Trigger's chat action send arbitrary text to any channel (including public chat) as your own character. A materially broader automation surface than Animation's closed set of self-targeting commands - see the README's Automation risk section.");
        }

        group.Dispose();
        group = Section.Begin("permToyControl", "Toy control (own acknowledgement)");
        if (!config.ToyControlAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Needs its own acknowledgement on the ToS tab first.");

        using (ImRaii.Disabled(!config.ToyControlAcknowledged))
        {
            if (ImGuiCheckbox("Toy control", permissions.ToyControl, out var newToyControl))
                SavePermission(() => permissions.ToyControl = newToyControl);
            IconGlyph.HelpMarker("Lets a paired Owner remotely vibrate or run a pattern on a toy you have connected via Intiface Central, until it stops itself, you stop it, or a maximum duration elapses. Directly actuates a physical device, not just in-game state - the single heaviest automation footprint in this plugin - see the README's Automation risk section.");
        }

        group.Dispose();
        group = Section.Begin("permRulebook", "Rulebook (own acknowledgement)");
        if (!config.RulebookAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Needs its own acknowledgement on the ToS tab first.");

        using (ImRaii.Disabled(!config.RulebookAcknowledged))
        {
            if (ImGuiCheckbox("Rulebook", permissions.Rulebook, out var newRulebook))
                SavePermission(() => permissions.Rulebook = newRulebook);
            IconGlyph.HelpMarker("Lets a rulebook you accepted run by itself: oaths, card draws, place and presence rules and the ledger. Each consequence still needs its own permission above. Turning this off voids your open oaths.");
        }
        group.Dispose();
    }

    private readonly ListDetail outfitList = new();
    private bool creatingOutfitAlias;
    private const string NewOutfitKey = "new:outfit";

    private void DrawWardrobeModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.Tshirt, "Outfit");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Design folder allowlist and scanning live in Settings (gear icon). Define your outfit aliases below.");

        using (Section.Begin("outfitFixed", "Fixed words"))
        {
            DrawFixedWord("Release outfit", ControlWords.Unlock);
            IconGlyph.HelpMarker("For example: kae unlock. Releases the current outfit's locks and its attached moodle - your look stays as it is.");
        }

        var outfits = config.Aliases.Outfits;
        var items = outfits.Select((o, i) => new ListDetailItem($"alias:{i}", o.Alias, o.Locked ? "locked" : null)).ToList();
        if (creatingOutfitAlias)
            items.Add(new ListDetailItem(NewOutfitKey, "New outfit alias"));

        using (Section.Begin("outfitAliases", "Your outfit aliases"))
        {
            outfitList.Draw("outfits", items, DrawOutfitToolbar, item => DrawOutfitDetail(config, item),
                () => outfitList.Selected == NewOutfitKey && newOutfitAlias.Trim().Length > 0,
                item =>
                {
                    if (item?.Key != NewOutfitKey)
                        creatingOutfitAlias = false;
                },
                "No outfit aliases yet. Use + New to add one.");
        }
    }

    private void DrawOutfitToolbar()
    {
        if (!ImGui.Button("+ New##outfitAlias"))
            return;
        creatingOutfitAlias = true;
        newOutfitAlias = "";
        newOutfitMoodle = null;
        newOutfitLocked = true;
        outfitList.Select(NewOutfitKey);
    }

    private void DrawOutfitDetail(PluginConfig config, ListDetailItem? item)
    {
        var designs = config.WardrobeMapping.LocalDesigns.Values.ToList();
        var designNames = designs.Select(d => d.Name).ToArray();
        if (item is null)
        {
            IconGlyph.WrappedDisabled("Choose an outfit alias, or use + New to add one.");
            return;
        }

        if (item.Key == NewOutfitKey)
        {
            DrawOutfitAddForm(config, designs, designNames);
            return;
        }

        var outfits = config.Aliases.Outfits;
        if (!int.TryParse(item.Key["alias:".Length..], out var index) || index >= outfits.Count)
            return;
        var o = outfits[index];
        ImGui.PushID($"outfitAlias_{index}");
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.AccentHover, o.Alias);
        ImGui.SameLine();
        if (ImGui.Button("Delete"))
        {
            outfits.RemoveAt(index);
            config.Save();
            outfitList.Select(null);
            ImGui.PopID();
            return;
        }

        if (DrawDeferredTextInput("Alias##outfitAliasEdit", (o, "alias"), o.Alias, 32, out var aliasBuffer) && aliasBuffer.Trim().Length > 0 && !IsReserved(aliasBuffer))
        {
            o.Alias = aliasBuffer.Trim();
            config.Save();
        }
        IconGlyph.HelpMarker("Short word the Owner types after the trigger phrase to apply this outfit.");

        var designIndex = designs.FindIndex(d => d.DesignId == o.DesignId);
        if (designs.Count > 0 && ImGui.Combo("Design##outfitAliasEdit", ref designIndex, designNames, designNames.Length) && designIndex >= 0)
        {
            o.DesignId = designs[designIndex].DesignId;
            o.DesignName = designs[designIndex].Name;
            config.Save();
        }
        if (designIndex < 0)
            IconGlyph.WrappedDisabled($"Design: {o.DesignName} (not in the latest scan)");

        var locked = o.Locked;
        if (ImGui.Checkbox("Lock##outfitAliasEdit", ref locked))
        {
            o.Locked = locked;
            config.Save();
        }
        IconGlyph.HelpMarker("Locks the design's slots so only an unlock can change them.");

        if (DrawAttachedMoodlePicker($"outfitAlias_{index}", o.AttachedMoodle, config, out var aliasMoodle))
        {
            o.AttachedMoodle = aliasMoodle;
            config.Save();
        }
        ImGui.PopID();
    }

    private void DrawOutfitAddForm(PluginConfig config, List<WardrobeDesignEntry> designs, string[] designNames)
    {
        if (designs.Count == 0)
        {
            IconGlyph.WrappedDisabled("No scanned designs yet - rescan in Settings (gear icon) first.");
            return;
        }

        using (Section.Begin("outfitAdd", "Add an outfit alias"))
        {
            newOutfitDesignIndex = Math.Clamp(newOutfitDesignIndex, 0, designNames.Length - 1);
            ImGui.InputText("Alias##newOutfit", ref newOutfitAlias, 32);
            IconGlyph.HelpMarker("Short word the Owner types after the trigger phrase to apply this outfit.");
            ImGui.Combo("Design##newOutfit", ref newOutfitDesignIndex, designNames, designNames.Length);
            IconGlyph.HelpMarker("The Glamourer design this alias applies. Rescan if it's missing.");
            if (DrawAttachedMoodlePicker("newOutfit", newOutfitMoodle, config, out var pickedOutfitMoodle, width: null))
                newOutfitMoodle = pickedOutfitMoodle;
            ImGui.Checkbox("Lock##newOutfit", ref newOutfitLocked);
            IconGlyph.HelpMarker("Locks the design's slots so only an unlock can change them.");
            DrawReservedWordWarning(newOutfitAlias);
            if (ImGui.Button("Add outfit alias") && newOutfitAlias.Trim().Length > 0 && !IsReserved(newOutfitAlias))
            {
                var design = designs[newOutfitDesignIndex];
                config.Aliases.Outfits.Add(new OutfitAliasDefinition
                {
                    Alias = newOutfitAlias.Trim(),
                    DesignId = design.DesignId,
                    DesignName = design.Name,
                    Locked = newOutfitLocked,
                    AttachedMoodle = newOutfitMoodle,
                });
                config.Save();
                newOutfitAlias = "";
                newOutfitMoodle = null;
                creatingOutfitAlias = false;
                outfitList.Select($"alias:{config.Aliases.Outfits.Count - 1}");
            }
        }
    }

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
        using (ImRaii.Disabled(wait is not null))
        {
            if (ImGui.Button(wait is { } w ? $"Struggle: {worn.Reference} (again in {RestraintLock.Format(w)})" : $"Struggle: {worn.Reference}"))
                restraintActionResult = plugin.RestraintCommand.Struggle(worn.RuntimeId).Message;
        }
        IconGlyph.HelpMarker($"Your Owner lets you try to get free: {RestraintStruggle.Describe(setting)}. Getting free takes this restraint off, and your Owner is told.");
    }

    private void DrawRestraintKeyRow(WornRestraint worn)
    {
        var guess = restraintKeyGuesses.GetValueOrDefault(worn.RuntimeId, "");
        ItemWidth(140);
        if (ImGui.InputTextWithHint("##key", "Key...", ref guess, RestraintKey.MaxLength, ImGuiInputTextFlags.Password))
            restraintKeyGuesses[worn.RuntimeId] = guess;
        var wait = RestraintCommand.KeyWait(worn);
        ImGui.SameLine();
        using (ImRaii.Disabled(wait is not null || guess.Trim().Length == 0))
        {
            if (ImGui.Button(wait is { } w ? $"Unlock (again in {RestraintLock.Format(w)})" : "Unlock"))
            {
                restraintActionResult = plugin.RestraintCommand.TryKey(worn.RuntimeId, guess).Message;
                restraintKeyGuesses.Remove(worn.RuntimeId);
            }
        }
        IconGlyph.HelpMarker("If your Owner gave you the key, type it here to take this restraint off. Your Owner is told.");
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

        DrawDrawnRestraintsSection(config);
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

    private static bool DrawRedrawOnApply(string id, ref bool redraw)
    {
        var changed = ImGui.Checkbox($"Redraw on apply##redraw_{id}", ref redraw);
        IconGlyph.HelpMarker("Redraws your character when this restraint goes on, even with \"Redraw after a mod change\" off in Settings. Turn it on if the restraint's mod or animation doesn't show until you redraw. Sync plugins re-send your character on every redraw.");
        return changed;
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

    private static string PoseName(int poseModeId) => poseModeId is >= 1 and <= 3 ? PoseNames[poseModeId - 1] : "unknown";

    private static bool HasAnyRule(RestraintRuleEditState edit) =>
        edit.ForcedPose || edit.WalkOnly || edit.ActionBlock || edit.Gagged || edit.ArmsCuffed || edit.LegsCuffed || edit.FullBodyCuffed;

    /// Gagged's animation stays optional: its chat garble applies on its own. So do a rules-only restraint's cuff
    /// animations, since its cuffs are drawn.
    private bool BoundAnimationsConfigured(RestraintRuleEditState edit, bool rulesOnly = false)
    {
        bool Contains(string id) => ResolveOwnerModeView()
            ? plugin.Configuration.GestureMapping.ImportedPeerCatalog.ContainsKey(id)
            : plugin.Configuration.GestureMapping.LocalCatalog.ContainsKey(id);
        bool Valid(bool enabled, string? id) => !enabled || id is not null && Contains(id);
        bool ValidCuff(bool enabled, string? id) => rulesOnly && id is null || Valid(enabled, id);
        return ValidCuff(edit.ArmsCuffed, edit.ArmsCuffedAnimationId)
            && ValidCuff(edit.LegsCuffed, edit.LegsCuffedAnimationId)
            && ValidCuff(edit.FullBodyCuffed, edit.FullBodyCuffedAnimationId)
            && Valid(edit.ForcedPose && edit.ForcedPoseIsMod, edit.ForcedPoseAnimationId);
    }

    private static readonly string[] ForcedPoseSourceNames = ["Vanilla pose", "Animation mod"];

    /// `allowCustomizePreset` is only for the Sub's own editors - only the Sub's client can list their profiles.
    /// `rulesOnly` makes the cuffs drawn, with an optional animation.
    private void DrawRestraintRuleCheckboxes(RestraintRuleEditState edit, string idSuffix, bool allowCustomizePreset = false, bool rulesOnly = false)
    {
        void Cuff(string label, ref bool enabled, ref bool drawn, string? animationId, Action<string?> setAnimation, string cuffSuffix, string help)
        {
            if (rulesOnly)
            {
                ImGui.Checkbox($"{label} (drawn)##{idSuffix}{cuffSuffix}", ref enabled);
                IconGlyph.HelpMarker($"Draws cuffs on you until released. {help} Optionally also holds an animation.");
                if (enabled)
                {
                    ImGui.Indent();
                    DrawAnimationChooser(animationId, id => setAnimation(id), $"{idSuffix}{cuffSuffix}", () => setAnimation(null));
                    ImGui.Unindent();
                }
            }
            else
            {
                DrawBoundAnimationPicker(label, ref enabled, animationId, id => setAnimation(id), $"{idSuffix}{cuffSuffix}");
                IconGlyph.HelpMarker($"Holds you in the chosen animation until released. {help}");
                if (enabled)
                {
                    ImGui.Indent();
                    ImGui.Checkbox($"Draw cuffs##{idSuffix}{cuffSuffix}Drawn", ref drawn);
                    IconGlyph.HelpMarker("Also draws cuffs and chains for this rule. Leave it off if this restraint's own gear already shows them.");
                    ImGui.Unindent();
                }
            }
        }

        var cells = new List<Action>
        {
            () =>
            {
                ImGui.Checkbox($"Forced pose##{idSuffix}", ref edit.ForcedPose);
                IconGlyph.HelpMarker("Places you into the chosen pose (or holds a chosen animation) and fully blocks movement input until released.");
                if (edit.ForcedPose)
                {
                    ImGui.Indent();
                    var sourceIndex = edit.ForcedPoseIsMod ? 1 : 0;
                    if (ImGui.Combo($"Source##{idSuffix}ForcedPose", ref sourceIndex, ForcedPoseSourceNames, ForcedPoseSourceNames.Length))
                        edit.ForcedPoseIsMod = sourceIndex == 1;
                    if (edit.ForcedPoseIsMod)
                        DrawAnimationChooser(edit.ForcedPoseAnimationId, id => edit.ForcedPoseAnimationId = id, $"{idSuffix}ForcedPose");
                    else
                        ImGui.Combo($"Pose##{idSuffix}", ref edit.PoseIndex, PoseNames, PoseNames.Length);
                    ImGui.Unindent();
                }
            },
        };
        cells.Add(() => Cuff("Arms Cuffed", ref edit.ArmsCuffed, ref edit.ArmsCuffedDrawn, edit.ArmsCuffedAnimationId, id => edit.ArmsCuffedAnimationId = id,
            "Arms", "Cuffs your wrists together."));
        cells.Add(() => Cuff("Legs Cuffed", ref edit.LegsCuffed, ref edit.LegsCuffedDrawn, edit.LegsCuffedAnimationId, id => edit.LegsCuffedAnimationId = id,
            "Legs", "Cuffs your ankles together."));
        cells.Add(() =>
        {
            ImGui.Checkbox($"Walk-only##{idSuffix}", ref edit.WalkOnly);
            IconGlyph.HelpMarker("Forces walking and blocks running, without blocking directional movement input.");
        });
        cells.Add(() => DrawGaggedPicker(edit, idSuffix, allowCustomizePreset));
        cells.Add(() => Cuff("Fully Restrain", ref edit.FullBodyCuffed, ref edit.FullBodyCuffedDrawn, edit.FullBodyCuffedAnimationId, id => edit.FullBodyCuffedAnimationId = id,
            "FullBody", "Cuffs your wrists and ankles and chains them together, and blocks movement."));
        cells.Add(() =>
        {
            ImGui.Checkbox($"Action block##{idSuffix}", ref edit.ActionBlock);
            IconGlyph.HelpMarker("Blocks hotbar action/skill usage until released, without affecting movement.");
        });

        // A table, not legacy Columns: Columns break inside a ListDetail table cell and hide the left column. One row
        // per pair keeps each toggle's dependent controls under it.
        if (!ImGui.BeginTable($"restraintRules_{idSuffix}", 2, ImGuiTableFlags.SizingStretchSame))
            return;
        for (var i = 0; i < cells.Count; i += 2)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            cells[i]();
            ImGui.TableNextColumn();
            if (i + 1 < cells.Count)
                cells[i + 1]();
        }
        ImGui.EndTable();
    }

    private static readonly string[] GagLevelNames = ["Heavy", "Medium", "Light"];

    private void DrawGaggedPicker(RestraintRuleEditState edit, string idSuffix, bool allowCustomizePreset)
    {
        ImGui.Checkbox($"Gagged##{idSuffix}", ref edit.Gagged);
        IconGlyph.HelpMarker("Garbles your outgoing chat text - the actual transmitted message, not just your own display - until released. See the README's Automation risk section before enabling.");
        if (!edit.Gagged)
            return;

        ImGui.Indent();
        var level = (int)edit.GagLevel;
        ItemWidth(140);
        if (ImGui.Combo($"level##{idSuffix}GagLevel", ref level, GagLevelNames, GagLevelNames.Length))
            edit.GagLevel = (GagLevel)level;
        IconGlyph.HelpMarker("Heavy garbles every word. Medium muffles each word but keeps its first letter and length. Light garbles about one longer word in three. A Sub on an older Oathbound version is gagged at Heavy whatever you pick.");
        DrawAnimationChooser(edit.GagAnimationId, id => edit.GagAnimationId = id, $"{idSuffix}Gag", () => edit.GagAnimationId = null);
        if (allowCustomizePreset)
        {
            DrawCustomizePresetChooser(edit.GagCustomizePresetId, edit.GagCustomizePresetLabel, (id, label) =>
            {
                edit.GagCustomizePresetId = id;
                edit.GagCustomizePresetLabel = label;
            }, $"{idSuffix}GagCustomize");
        }
        ImGui.Unindent();
    }

    /// A delegate rather than `ref`, since the picker's callback fires on a later frame.
    private void DrawBoundAnimationPicker(string label, ref bool enabled, string? currentAnimationId, Action<string> onChosen, string idSuffix)
    {
        ImGui.Checkbox($"{label}##{idSuffix}", ref enabled);
        if (!enabled)
            return;

        ImGui.Indent();
        DrawAnimationChooser(currentAnimationId, onChosen, idSuffix);
        ImGui.Unindent();
    }

    private void DrawAnimationChooser(string? currentAnimationId, Action<string> onChosen, string idSuffix, Action? onCleared = null)
    {
        var ownerMode = ResolveOwnerModeView();
        var localCatalog = plugin.Configuration.GestureMapping.LocalCatalog;
        var peerCatalog = plugin.Configuration.GestureMapping.ImportedPeerCatalog;
        string? chosenLabel = null;
        var chosenMode = "";
        var stale = false;
        if (currentAnimationId is { } id)
        {
            if (ownerMode && peerCatalog.TryGetValue(id, out var peer))
            {
                chosenLabel = CommandPresentation.AnimationDisplayName(peer.GroupName, peer.AnimationName);
                chosenMode = $"{peer.ModName} — {(peer.Trigger is null ? "Enable option only" : peer.Trigger.Label)}";
            }
            else if (!ownerMode && localCatalog.TryGetValue(id, out var local))
            {
                chosenLabel = CommandPresentation.AnimationDisplayName(local.GroupName, local.AnimationName);
                chosenMode = $"{local.ModName} — {(local.Trigger is null ? "Enable option only" : local.Trigger.Label)}";
            }
            else
            {
                stale = true;
            }
        }

        var buttonText = stale ? "Missing - choose again..."
            : chosenLabel ?? "Choose animation...";
        if (DrawChoiceButton(buttonText, idSuffix, stale))
        {
            if (ownerMode) plugin.AnimationPickerWindow.OpenImportedForRestraint(chosen => onChosen(chosen.Id));
            else plugin.AnimationPickerWindow.OpenForRestraint(chosen => onChosen(chosen.Id));
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(stale ? "This animation is no longer in the scanned catalog - click to choose again."
                : chosenLabel is null ? "Click to choose an animation."
                : $"{chosenLabel}{(chosenMode.Length > 0 ? $"\n{chosenMode}" : "")}\n\nClick to change.");
        if (onCleared is not null && currentAnimationId is not null)
            DrawChoiceClearButton(idSuffix, onCleared);
    }

    private static bool DrawChoiceButton(string text, string idSuffix, bool warn = false)
    {
        var clearWidth = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;
        var width = Math.Max(120f, ImGui.GetContentRegionAvail().X - clearWidth);
        if (warn)
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warning);
        var clicked = ImGui.Button($"{text}##choice_{idSuffix}", new Vector2(width, 0));
        if (warn)
            ImGui.PopStyleColor();
        return clicked;
    }

    private static void DrawChoiceClearButton(string idSuffix, Action onCleared)
    {
        ImGui.SameLine();
        // IconGlyph.Button's label is only the glyph, so scope its ID - a gag row has two of these.
        ImGui.PushID($"choiceClear_{idSuffix}");
        if (IconGlyph.Button(FontAwesomeIcon.Times, new Vector2(ImGui.GetFrameHeight(), ImGui.GetFrameHeight())))
            onCleared();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clear");
        ImGui.PopID();
    }

    /// Customize+ profiles are flat and never shared via catalog sync, so there's no Owner mode here.
    private void DrawCustomizePresetChooser(string? currentPresetId, string? currentPresetLabel, Action<string?, string?> onChosen, string idSuffix)
    {
        var blocked = DependencyGates.FeatureBlockedReason(plugin, DependencyId.CustomizePlus);
        var buttonText = currentPresetId is null ? "Choose Customize+ preset..." : $"C+: {currentPresetLabel ?? currentPresetId}";
        using (ImRaii.Disabled(blocked is not null))
            if (DrawChoiceButton(buttonText, idSuffix))
                plugin.CustomizePresetPickerWindow.Open(profile => onChosen(profile.UniqueId.ToString(), profile.Name));
        if (blocked is not null)
        {
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(blocked);
            IconGlyph.WrappedColored(Theme.StatusMissing, blocked);
            return;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(currentPresetId is null ? "Optional - click to choose a Customize+ preset." : $"Customize+ preset: {currentPresetLabel ?? currentPresetId}\n\nClick to change.");
        if (currentPresetId is not null)
            DrawChoiceClearButton(idSuffix, () => onChosen(null, null));
    }

    private readonly ListDetail gestureList = new();

    private void DrawGestureModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.TheaterMasks, "Animation");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Select animation mods and scan them in Settings. Pick an animation to see it and give it an alias.");

        var held = plugin.GestureCommand.IsHeld;
        if (plugin.GestureCommand.HasActiveTemporary && !held)
        {
            if (ImGui.Button("Reset active animation"))
                plugin.GestureCommand.ResetActiveTemporary();
            IconGlyph.HelpMarker("Puts the animation mod back to its saved settings now, instead of after the ~30s idle timeout.");
        }

        var gestures = config.Aliases.Gestures;
        var catalog = config.GestureMapping.LocalCatalog.Values.Where(e => e.Trigger != null)
            .OrderBy(e => e.ModName, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.GroupOrder).ThenBy(e => e.OptionOrder).ThenBy(e => e.TriggerOrder)
            .ToList();
        var items = catalog.GroupBy(e => e.ModDirectory).Select(mod =>
        {
            var withAlias = mod.Count(e => gestures.Any(g => g.GestureId == e.Id));
            return new ListDetailItem($"mod:{mod.Key}", mod.First().ModName, $"{mod.Count()}",
                Tooltip: $"{mod.Count()} animation{(mod.Count() == 1 ? "" : "s")}, {withAlias} with an alias");
        }).ToList();
        // Aliases whose animation is gone from the latest scan, so they can still be found and deleted.
        var staleCount = gestures.Count(g => string.IsNullOrEmpty(g.GestureId) || !config.GestureMapping.LocalCatalog.ContainsKey(g.GestureId));
        if (staleCount > 0)
            items.Add(new ListDetailItem(StaleGesturesKey, "Missing from the scan", $"{staleCount}", Group: "Needs rescan"));

        using (Section.Begin("gestureAliases", "Your animations"))
        {
            gestureList.Draw("gestures", items, null, item => DrawGestureModDetail(config, gestures, catalog, item),
                () => newGestureAlias.Trim().Length > 0,
                _ =>
                {
                    newGestureAlias = "";
                    gestureAnimId = null;
                },
                "No scanned animations yet - rescan in Settings (gear icon) first.");
        }
    }

    private const string StaleGesturesKey = "stale";
    private string? gestureAnimId;

    private void DrawGestureModDetail(PluginConfig config, List<GestureAliasDefinition> gestures, List<GestureCatalogEntry> catalog, ListDetailItem? item)
    {
        if (item is null)
        {
            IconGlyph.WrappedDisabled("Choose an animation mod.");
            return;
        }

        if (item.Key == StaleGesturesKey)
        {
            IconGlyph.WrappedColored(Theme.Warning, "These aliases point at animations that aren't in the latest scan. Rescan, or delete them and make them again.");
            foreach (var (stale, index) in gestures.Select((g, i) => (g, i)).ToList())
            {
                if (!string.IsNullOrEmpty(stale.GestureId) && config.GestureMapping.LocalCatalog.ContainsKey(stale.GestureId))
                    continue;
                ImGui.PushID($"staleGesture_{index}");
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.AccentHover, stale.Alias);
                ImGui.SameLine();
                ImGui.TextDisabled($"{(stale.AnimationName.Length > 0 ? stale.AnimationName : stale.EmoteName)} ({stale.ModName})");
                ContinueRowOrWrap(ButtonWidth("Delete"));
                if (ImGui.Button("Delete"))
                {
                    gestures.RemoveAt(index);
                    config.Save();
                    ImGui.PopID();
                    break;
                }
                ImGui.PopID();
            }
            return;
        }

        var anims = catalog.Where(e => $"mod:{e.ModDirectory}" == item.Key).ToList();
        if (anims.Count == 0)
            return;
        var selected = anims.FirstOrDefault(e => e.Id == gestureAnimId) ?? anims[0];
        ImGui.TextColored(Theme.AccentHover, anims[0].ModName);
        ImGui.TextDisabled($"{anims.Count} animation{(anims.Count == 1 ? "" : "s")}");

        Section.SubHeading("Animations");
        DrawAnimationPicker(anims, selected.Id, e => gestures.Where(g => g.GestureId == e.Id).Select(g => g.Alias), e => e.GroupName,
            e => $"{e.AnimationName} - {e.Trigger!.Label}", e => e.Id, id =>
            {
                gestureAnimId = id;
                newGestureAlias = "";
            });

        DrawGestureDetail(config, gestures, selected);
    }

    /// The selected mod's animations as a short scrolling list, under their option group names.
    private static void DrawAnimationPicker<T>(List<T> anims, string selectedId, Func<T, IEnumerable<string>> badges, Func<T, string?> groupOf,
        Func<T, string> labelOf, Func<T, string> idOf, Action<string> onPick, Func<T, bool>? locked = null)
    {
        var rows = anims.Count + anims.Select(groupOf).Where(g => !string.IsNullOrEmpty(g)).Distinct().Count();
        var height = Math.Min(rows, 10) * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
        using var child = ImRaii.Child("animPicker", new Vector2(-1, height), true);
        if (!child)
            return;
        string? lastGroup = null;
        foreach (var anim in anims)
        {
            var group = groupOf(anim);
            if (!string.IsNullOrEmpty(group) && group != lastGroup)
                ImGui.TextDisabled(group);
            lastGroup = group;

            var id = idOf(anim);
            var badge = string.Join(", ", badges(anim));
            var startX = ImGui.GetCursorPosX();
            var avail = ImGui.GetContentRegionAvail().X;
            using (ImRaii.Disabled(locked?.Invoke(anim) == true))
            {
                if (ImGui.Selectable($"{labelOf(anim)}##anim_{id}", id == selectedId))
                    onPick(id);
            }
            if (locked?.Invoke(anim) == true && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Save or cancel your edit first.");
            if (id == selectedId && ImGui.IsWindowAppearing())
                ImGui.SetScrollHereY();
            if (badge.Length > 0)
            {
                var width = ImGui.CalcTextSize(badge).X;
                ImGui.SameLine(startX + Math.Max(0, avail - width));
                ImGui.TextColored(Theme.AccentHover, badge);
            }
        }
    }

    private void DrawGestureDetail(PluginConfig config, List<GestureAliasDefinition> gestures, GestureCatalogEntry entry)
    {
        if (entry.Trigger is null)
            return;
        ImGui.PushID($"gesture_{entry.Id}");
        ImGui.Spacing();
        ImGui.TextColored(Theme.AccentHover, entry.AnimationName);
        if (entry.GroupName.Length > 0)
            ImGui.TextDisabled(entry.GroupName);
        ImGui.TextUnformatted($"Plays: {entry.Trigger.DisplayName}");
        if (entry.Trigger.Label != entry.Trigger.DisplayName)
            ImGui.TextDisabled(entry.Trigger.Label);
        if (entry.GroupSelections.Count > 0)
        {
            Section.SubHeading("Mod options it turns on");
            foreach (var (group, options) in entry.GroupSelections)
                IconGlyph.WrappedDisabled($"{group}: {string.Join(", ", options)}");
        }

        Section.SubHeading("Aliases");
        var bound = gestures.Select((g, i) => (g, i)).Where(x => x.g.GestureId == entry.Id).ToList();
        if (bound.Count == 0)
            IconGlyph.WrappedDisabled("No alias plays this yet.");
        foreach (var (alias, index) in bound)
        {
            ImGui.PushID($"gestureAlias_{index}");
            if (DrawDeferredTextInput("##aliasEdit", (alias, "alias"), alias.Alias, 32, out var renamed) && renamed.Trim().Length > 0 && !IsReserved(renamed))
            {
                alias.Alias = renamed.Trim();
                config.Save();
            }
            ImGui.SameLine();
            if (ImGui.Button("Delete"))
            {
                gestures.RemoveAt(index);
                config.Save();
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }

        Section.SubHeading("Add an alias");
        ItemWidth(160);
        ImGui.InputTextWithHint("##newGesture", "word", ref newGestureAlias, 32);
        IconGlyph.HelpMarker("Short word your Owner sends to play this animation.");
        DrawReservedWordWarning(newGestureAlias);
        using (ImRaii.Disabled(newGestureAlias.Trim().Length == 0 || IsReserved(newGestureAlias)))
        {
            if (ImGui.Button("Add alias##newGesture"))
            {
                gestures.Add(new GestureAliasDefinition
                {
                    Alias = newGestureAlias.Trim(),
                    GestureId = entry.Id,
                    AnimationName = entry.AnimationName,
                    ModDirectory = entry.ModDirectory,
                    ModName = entry.ModName,
                    EmoteName = entry.Trigger.DisplayName.TrimStart('/'),
                });
                config.Save();
                newGestureAlias = "";
            }
        }
        ImGui.PopID();
    }

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

    private static void CopyRuleEdit(RestraintRuleEditState source, RestraintRuleEditState target)
    {
        target.ForcedPose = source.ForcedPose;
        target.PoseIndex = source.PoseIndex;
        target.ForcedPoseIsMod = source.ForcedPoseIsMod;
        target.ForcedPoseAnimationId = source.ForcedPoseAnimationId;
        target.WalkOnly = source.WalkOnly;
        target.ActionBlock = source.ActionBlock;
        target.Gagged = source.Gagged;
        target.GagAnimationId = source.GagAnimationId;
        target.GagCustomizePresetId = source.GagCustomizePresetId;
        target.GagCustomizePresetLabel = source.GagCustomizePresetLabel;
        target.GagLevel = source.GagLevel;
        target.ArmsCuffed = source.ArmsCuffed;
        target.ArmsCuffedAnimationId = source.ArmsCuffedAnimationId;
        target.LegsCuffed = source.LegsCuffed;
        target.LegsCuffedAnimationId = source.LegsCuffedAnimationId;
        target.FullBodyCuffed = source.FullBodyCuffed;
        target.FullBodyCuffedAnimationId = source.FullBodyCuffedAnimationId;
        target.ArmsCuffedDrawn = source.ArmsCuffedDrawn;
        target.LegsCuffedDrawn = source.LegsCuffedDrawn;
        target.FullBodyCuffedDrawn = source.FullBodyCuffedDrawn;
    }

    /// Capture-only, and editing is disabled while the collar is locked.
    private void DrawCollarModule()
    {
        var config = plugin.Configuration;
        var locked = plugin.RuntimeState.CollarForceLocked;
        IconGlyph.Text(FontAwesomeIcon.Lock, "Collar");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Choose a Neck collar, a left ring, or both. They lock on when you accept a pairing.");

        if (locked)
            IconGlyph.WrappedColored(Theme.Danger, "Locked - applied at pairing. Only your Owner's \"collar unlock\" or ending that pairing releases it. Your safeword leaves the collar on.");

        using var disabled = ImRaii.Disabled(locked);

        TwoColumns("collarColumns", () => DrawCollarGearSections(config), () => DrawCollarMoodleSection(config));
    }

    private void DrawCollarGearSections(PluginConfig config)
    {
        using (Section.Begin("collarItem", "Collar item"))
        {
            var collarChosenLabel = config.Collar.ItemId is { } collarItemId ? GetItemName(collarItemId) : "(none chosen)";
            TextWithActions($"Item: {collarChosenLabel}", ButtonWidth("Choose item..."));
            if (ImGui.Button("Choose item...##collar"))
                plugin.ItemPickerWindow.Open(ApiEquipSlot.Neck, (chosenId, _) => plugin.CollarCommand.ConfigureFromItem(chosenId));
            IconGlyph.HelpMarker("Any Neck item - it doesn't need to be owned.");

            if (config.Collar.HasNeckItem)
            {
                ContinueRowOrWrap(ButtonWidth("Clear"));
                if (ImGui.Button("Clear"))
                    plugin.CollarCommand.ClearConfiguredCollar();
            }
            else
            {
                IconGlyph.WrappedDisabled("No Neck item chosen.");
            }
        }

        using (Section.Begin("collarRing", "Ring"))
        {
            var ringChosenLabel = config.Collar.RingItemId is { } ringItemId ? GetItemName(ringItemId) : "(none chosen)";
            TextWithActions($"Ring: {ringChosenLabel}", ButtonWidth("Choose item..."));
            if (ImGui.Button("Choose item...##collarRing"))
                plugin.ItemPickerWindow.Open(ApiEquipSlot.LFinger, (chosenId, _) => plugin.CollarCommand.ConfigureRingFromItem(chosenId));
            IconGlyph.HelpMarker("Optional ring that locks with your collar. It doesn't need to be owned.");

            if (config.Collar.HasRing)
            {
                ContinueRowOrWrap(ButtonWidth("Clear"));
                if (ImGui.Button("Clear##collarRing"))
                    plugin.CollarCommand.ClearConfiguredRing();
            }
        }
    }

    private void DrawCollarMoodleSection(PluginConfig config)
    {
        using (Section.Begin("collarMoodle", "Collar moodle"))
        {
            var collarMoodleLabel = config.Collar.MoodleStatusName is { } assignedMoodleName ? MoodlesTextFormat.StripMarkup(assignedMoodleName) : "(none assigned)";
            TextWithActions($"Moodle: {collarMoodleLabel}", ImGui.CalcTextSize("(?)").X);
            IconGlyph.HelpMarker("Optional status shown while your collar is locked. Your safeword leaves it on.");

            var collarMoodleStatuses = config.MoodlesMapping.LocalCatalog.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            // Clearing an existing assignment stays available without Moodles.
            if (DependencyGates.FeatureBlockedReason(plugin, DependencyId.Moodles) is { } collarMoodleBlocked)
            {
                IconGlyph.WrappedColored(Theme.StatusMissing, collarMoodleBlocked);
            }
            else if (collarMoodleStatuses.Count == 0)
            {
                IconGlyph.WrappedDisabled("No scanned Moodles statuses yet - rescan in Settings (gear icon) first.");
            }
            else
            {
                var collarMoodleStatusNames = collarMoodleStatuses.Select(s => MoodlesTextFormat.StripMarkup(s.Name)).ToArray();
                newCollarMoodleStatusIndex = Math.Clamp(newCollarMoodleStatusIndex, 0, collarMoodleStatusNames.Length - 1);
                ItemWidth(220);
                ImGui.Combo("##newCollarMoodle", ref newCollarMoodleStatusIndex, collarMoodleStatusNames, collarMoodleStatusNames.Length);
                ContinueRowOrWrap(ButtonWidth("Assign"));
                if (ImGui.Button("Assign##collarMoodle"))
                {
                    var chosen = collarMoodleStatuses[newCollarMoodleStatusIndex];
                    config.Collar.MoodleStatusId = chosen.StatusId;
                    config.Collar.MoodleStatusName = chosen.Name;
                    config.Save();
                }
            }

            if (config.Collar.HasMoodleAssigned)
            {
                ContinueRowOrWrap(ButtonWidth("Clear"));
                if (ImGui.Button("Clear##collarMoodle"))
                {
                    config.Collar.MoodleStatusId = null;
                    config.Collar.MoodleStatusName = null;
                    config.Save();
                }
            }
        }
    }

    private void DrawFollowLeashModule()
    {
        var config = plugin.Configuration;
        DrawFixedWordRow("leashFixed",
            new FixedWordInfo("Leash", ControlWords.Leash, "Puts on a leash of your Owner's chosen length. You move freely inside it and get pulled along."),
            new FixedWordInfo("Unleash", ControlWords.Unleash, "Takes the leash off and gives you full control back."));

        using (Section.Begin("leashLimit", "Leash length"))
        {
            var follow = config.Aliases.Follow;
            var maxLength = follow.MaxLeashLengthYalms;
            ItemWidth(200);
            if (ImGui.SliderInt("Longest leash I'll accept (yalms)##subLeashMax", ref maxLength, LengthOption.MinYalms, LengthOption.MaxYalms))
            {
                follow.MaxLeashLengthYalms = Math.Clamp(maxLength, LengthOption.MinYalms, LengthOption.MaxYalms);
                config.Save();
            }
            IconGlyph.HelpMarker("A longer leash from your Owner is shortened to this.");
        }

        using (Section.Begin("leashMoodle", "Attached moodle"))
        {
            var follow = config.Aliases.Follow;
            if (DrawAttachedMoodlePicker("leash", follow.AttachedMoodle, config, out var leashMoodle))
            {
                follow.AttachedMoodle = leashMoodle;
                config.Save();
            }
        }
    }

    /// Only changes what this client draws; the leash keeps working with its line hidden.
    private static void DrawLeashLineSection(PluginConfig config)
    {
        using var box = Section.Begin("leashAppearance", "Leash line");
        var shown = config.ShowLeashLine;
        DrawOptionRows("leashLineOptions",
            new OptionRow("Show", "Only changes what you see. Hiding it doesn't release the leash.", _ =>
            {
                var showLeash = config.ShowLeashLine;
                if (ImGui.Checkbox("Show leash line", ref showLeash))
                {
                    config.ShowLeashLine = showLeash;
                    config.Save();
                }
            }),
            new OptionRow("Color", "The leash line's colour. Reset also puts brightness and opacity back.", _ =>
            {
                using var disabled = ImRaii.Disabled(!shown);
                var color = config.LeashColor;
                if (ImGui.ColorEdit3("##leashColor", ref color, ImGuiColorEditFlags.NoInputs))
                {
                    config.LeashColor = color;
                    config.Save();
                }
                ImGui.SameLine();
                if (ImGui.Button("Reset##leashAppearance"))
                {
                    config.LeashColor = PluginConfig.DefaultLeashColor;
                    config.LeashBrightness = 1f;
                    config.LeashOpacity = 1f;
                    config.Save();
                }
            }),
            new OptionRow("Brightness", "How bright the line is drawn.", width =>
            {
                using var disabled = ImRaii.Disabled(!shown);
                var brightness = (int)MathF.Round(config.LeashBrightness * 100f);
                ImGui.SetNextItemWidth(Math.Min(width, Scaled(280)));
                if (ImGui.SliderInt("##leashBrightness", ref brightness, (int)(PluginConfig.MinLeashBrightness * 100f), 100, "%d%%"))
                {
                    config.LeashBrightness = brightness / 100f;
                    config.Save();
                }
            }),
            new OptionRow("Opacity", "How see-through the line is.", width =>
            {
                using var disabled = ImRaii.Disabled(!shown);
                var opacity = (int)MathF.Round(config.LeashOpacity * 100f);
                ImGui.SetNextItemWidth(Math.Min(width, Scaled(280)));
                if (ImGui.SliderInt("##leashOpacity", ref opacity, (int)(PluginConfig.MinLeashOpacity * 100f), 100, "%d%%"))
                {
                    config.LeashOpacity = opacity / 100f;
                    config.Save();
                }
            }));
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

    /// Falls back to the numeric id for sentinel values that don't resolve to a row.
    private static string GetItemName(ulong itemId)
    {
        var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault((uint)itemId);
        var name = row?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? $"Item #{itemId}" : name;
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

    /// Wraps the button to its own line when the window is too narrow.
    private static void DrawSectionTitleRow(FontAwesomeIcon icon, string title, bool showClearAll, string idSuffix, Action onClearAll)
    {
        IconGlyph.Text(icon, title);
        if (showClearAll)
        {
            const string label = "Clear all";
            var buttonWidth = ButtonWidth(label);
            ImGui.SameLine();
            var avail = ImGui.GetContentRegionAvail().X;
            if (avail < buttonWidth)
                ImGui.NewLine();
            else if (avail > buttonWidth)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - buttonWidth);
            if (ImGui.Button($"{label}##{idSuffix}"))
                onClearAll();
        }
        ImGui.Separator();
    }

    /// The collar applies at pairing acceptance or via `collar lock`; only the fixed rows are needed.
    private void DrawCollarQuickSection(bool canSend)
    {
        IconGlyph.Text(FontAwesomeIcon.Lock, "Collar");
        ImGui.Separator();
        DrawCommandRow("collarQuick", canSend,
            new RowCommand("Collar lock", "collar lock", FixedActionIds.CollarLock, "Puts your Sub's collar on and locks it."),
            new RowCommand("Collar unlock", "collar unlock", FixedActionIds.CollarUnlock, "Unlocks your Sub's collar. It stays on, just unlocked."));
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
                    ? RestraintCommand.BuildCatalogLockCommand(catalogId, cmd.Label, itemId, rules)
                    : RestraintCommand.BuildLockCommand(cmd.Label, rules);
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
            var command = RestraintCommand.BuildWearCommand(null, null, newAdHocLabel.Trim(), ToRules(newAdHocRuleEdit, rulesOnly: true));
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

    private readonly Dictionary<object, string> textInputDrafts = new();

    /// Returns true once, when editing ends with a changed value, so callers save once per edit, not per keystroke.
    private bool DrawDeferredTextInput(string label, object draftKey, string current, int maxLength, out string committed, string? hint = null)
    {
        var draft = textInputDrafts.TryGetValue(draftKey, out var pending) ? pending : current;
        var edited = hint is null ? ImGui.InputText(label, ref draft, maxLength) : ImGui.InputTextWithHint(label, hint, ref draft, maxLength);
        if (edited)
            textInputDrafts[draftKey] = draft;
        committed = draft;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            textInputDrafts.Remove(draftKey);
            return draft != current;
        }
        if (!ImGui.IsItemActive())
            textInputDrafts.Remove(draftKey);
        return false;
    }

    private static RestraintRuleEditState FromRules(List<RestraintRuleAssignment>? rules)
    {
        var edit = new RestraintRuleEditState();
        foreach (var rule in rules ?? [])
        {
            switch (rule.Kind)
            {
                case RestraintRuleKind.ForcedPose:
                    edit.ForcedPose = true;
                    if (rule.PoseModeId == 0)
                    {
                        edit.ForcedPoseIsMod = true;
                        edit.ForcedPoseAnimationId = rule.AnimationId;
                    }
                    else
                    {
                        edit.PoseIndex = Math.Clamp(rule.PoseModeId - 1, 0, PoseNames.Length - 1);
                    }
                    break;
                case RestraintRuleKind.WalkOnly: edit.WalkOnly = true; break;
                case RestraintRuleKind.ActionBlock: edit.ActionBlock = true; break;
                case RestraintRuleKind.Gagged:
                    edit.Gagged = true;
                    edit.GagAnimationId = rule.AnimationId;
                    edit.GagCustomizePresetId = rule.CustomizePresetId;
                    edit.GagCustomizePresetLabel = rule.CustomizePresetLabel;
                    edit.GagLevel = rule.GagLevel;
                    break;
                case RestraintRuleKind.ArmsCuffed: edit.ArmsCuffed = true; edit.ArmsCuffedAnimationId = NullIfBlank(rule.AnimationId); edit.ArmsCuffedDrawn = rule.Drawn; break;
                case RestraintRuleKind.LegsCuffed: edit.LegsCuffed = true; edit.LegsCuffedAnimationId = NullIfBlank(rule.AnimationId); edit.LegsCuffedDrawn = rule.Drawn; break;
                case RestraintRuleKind.FullBodyCuffed: edit.FullBodyCuffed = true; edit.FullBodyCuffedAnimationId = NullIfBlank(rule.AnimationId); edit.FullBodyCuffedDrawn = rule.Drawn; break;
            }
        }
        return edit;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// A rules-only restraint's cuffs are always drawn.
    private List<RestraintRuleAssignment> ToRules(RestraintRuleEditState edit, bool rulesOnly = false)
    {
        string? LabelFor(string? id)
        {
            if (id is null) return null;
            if (ResolveOwnerModeView() && plugin.Configuration.GestureMapping.ImportedPeerCatalog.TryGetValue(id, out var peer))
                return CommandSelector.GestureSelector(peer, plugin.Configuration.GestureMapping.ImportedPeerCatalog.Values);
            return plugin.Configuration.GestureMapping.LocalCatalog.TryGetValue(id, out var local)
                ? CommandSelector.GestureLabel(local.ModName, local.GroupName, local.AnimationName, local.Trigger) : null;
        }
        var rules = new List<RestraintRuleAssignment>();
        if (edit.ForcedPose && edit.ForcedPoseIsMod)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ForcedPose, PoseModeId = 0, AnimationId = edit.ForcedPoseAnimationId, AnimationLabel = LabelFor(edit.ForcedPoseAnimationId) });
        else if (edit.ForcedPose)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ForcedPose, PoseModeId = edit.PoseIndex + 1 });
        if (edit.WalkOnly)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.WalkOnly });
        if (edit.ActionBlock)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ActionBlock });
        if (edit.Gagged)
        {
            rules.Add(new RestraintRuleAssignment
            {
                Kind = RestraintRuleKind.Gagged,
                AnimationId = edit.GagAnimationId,
                AnimationLabel = LabelFor(edit.GagAnimationId),
                CustomizePresetId = edit.GagCustomizePresetId,
                CustomizePresetLabel = edit.GagCustomizePresetLabel,
                GagLevel = edit.GagLevel,
            });
        }
        if (edit.ArmsCuffed)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ArmsCuffed, AnimationId = edit.ArmsCuffedAnimationId, AnimationLabel = LabelFor(edit.ArmsCuffedAnimationId), Drawn = rulesOnly || edit.ArmsCuffedDrawn });
        if (edit.LegsCuffed)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.LegsCuffed, AnimationId = edit.LegsCuffedAnimationId, AnimationLabel = LabelFor(edit.LegsCuffedAnimationId), Drawn = rulesOnly || edit.LegsCuffedDrawn });
        if (edit.FullBodyCuffed)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.FullBodyCuffed, AnimationId = edit.FullBodyCuffedAnimationId, AnimationLabel = LabelFor(edit.FullBodyCuffedAnimationId), Drawn = rulesOnly || edit.FullBodyCuffedDrawn });
        return rules;
    }

    private readonly ListDetail titleList = new();
    private const string NewTitleKey = "new:title";

    private void DrawTitleModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.Heading, "Title Aliases");
        ImGui.Separator();
        DrawFixedWordRow("titleFixed", new FixedWordInfo("Clear title", ControlWords.ClearTitle,
            "Removes your current title. It can't be renamed, so your Owner always knows it."));

        var titles = config.Aliases.Titles;
        var items = titles.Select((t, i) => new ListDetailItem($"title:{i}", t.Alias, t.IsPrefix ? "prefix" : null, Tooltip: t.Text)).ToList();
        if (titleList.Selected == NewTitleKey)
            items.Add(new ListDetailItem(NewTitleKey, "New title alias"));

        using (Section.Begin("titleAliases", "Your title aliases"))
        {
            titleList.Draw("titles", items, () =>
                {
                    if (!ImGui.Button("+ New##titleAlias"))
                        return;
                    newTitleAlias = "";
                    newTitleText = "";
                    titleList.Select(NewTitleKey);
                },
                item => DrawTitleAliasDetail(config, titles, item),
                () => titleList.Selected == NewTitleKey && (newTitleAlias.Trim().Length > 0 || newTitleText.Trim().Length > 0),
                null, "No title aliases yet. Use + New to add one.");
        }
    }

    /// How a title reads next to a name, in its own colour.
    private static void DrawTitlePreview(string text, bool isPrefix, Vector3 color)
    {
        ImGui.TextDisabled("Preview:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(color, 1f), isPrefix ? $"«{text}»" : "Your Name");
        ImGui.SameLine();
        ImGui.TextColored(isPrefix ? Theme.TextMuted : new Vector4(color, 1f), isPrefix ? "Your Name" : $"«{text}»");
    }

    private void DrawTitleAliasDetail(PluginConfig config, List<TitleAliasDefinition> titles, ListDetailItem? item)
    {
        if (item is null)
        {
            IconGlyph.WrappedDisabled("Choose a title alias, or use + New to add one.");
            return;
        }
        if (item.Key == NewTitleKey)
        {
            DrawTitleAddForm(config, titles);
            return;
        }
        if (!int.TryParse(item.Key["title:".Length..], out var index) || index >= titles.Count)
            return;

        var t = titles[index];
        ImGui.PushID($"titleAlias_{index}");
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.AccentHover, t.Alias);
        ImGui.SameLine();
        if (ImGui.Button("Delete"))
        {
            titles.RemoveAt(index);
            config.Save();
            titleList.Select(null);
            ImGui.PopID();
            return;
        }
        DrawTitlePreview(t.Text, t.IsPrefix, t.Color);

        if (DrawDeferredTextInput("Alias##titleEdit", (t, "alias"), t.Alias, 32, out var alias) && alias.Trim().Length > 0 && !IsReserved(alias))
        {
            t.Alias = alias.Trim();
            config.Save();
        }
        IconGlyph.HelpMarker("Short word the Owner types after the trigger phrase to apply this title.");
        if (DrawDeferredTextInput("Title text##titleEdit", (t, "text"), t.Text, 64, out var text) && text.Trim().Length > 0)
        {
            t.Text = text.Trim();
            config.Save();
        }
        var isPrefix = t.IsPrefix;
        if (ImGui.Checkbox("Prefix (not suffix)##titleEdit", ref isPrefix))
        {
            t.IsPrefix = isPrefix;
            config.Save();
        }
        var color = t.Color;
        if (ImGui.ColorEdit3("Color##titleEdit", ref color))
        {
            t.Color = color;
            config.Save();
        }
        var hasGlow = t.Glow is not null;
        var glow = t.Glow ?? new Vector3(1, 1, 1);
        var glowBefore = (hasGlow, glow);
        DrawGlowPicker("titleEdit", ref hasGlow, ref glow);
        if ((hasGlow, glow) != glowBefore)
        {
            t.Glow = hasGlow ? glow : null;
            config.Save();
        }
        ImGui.PopID();
    }

    private void DrawTitleAddForm(PluginConfig config, List<TitleAliasDefinition> titles)
    {
        using var box = Section.Begin("titleAdd", "Add a title alias");
        ImGui.InputText("Alias##newTitle", ref newTitleAlias, 32);
        IconGlyph.HelpMarker("Short word the Owner types after the trigger phrase to apply this title, e.g. \"command goodgirl\".");
        ImGui.InputText("Title text##newTitle", ref newTitleText, 64);
        IconGlyph.HelpMarker("The exact title text applied via Honorific.");
        ImGui.Checkbox("Prefix (not suffix)##newTitle", ref newTitleIsPrefix);
        IconGlyph.HelpMarker("Show the title before your name instead of after it.");
        ImGui.ColorEdit3("Color##newTitle", ref newTitleColor);
        IconGlyph.HelpMarker("Honorific title color.");
        DrawGlowPicker("newTitle", ref newTitleHasGlow, ref newTitleGlow);
        if (newTitleText.Trim().Length > 0)
            DrawTitlePreview(newTitleText.Trim(), newTitleIsPrefix, newTitleColor);
        DrawReservedWordWarning(newTitleAlias);
        if (ImGui.Button("Add title alias") && newTitleAlias.Trim().Length > 0 && newTitleText.Trim().Length > 0 && !IsReserved(newTitleAlias))
        {
            titles.Add(new TitleAliasDefinition { Alias = newTitleAlias.Trim(), Text = newTitleText.Trim(), IsPrefix = newTitleIsPrefix, Color = newTitleColor, Glow = newTitleHasGlow ? newTitleGlow : null });
            config.Save();
            newTitleAlias = "";
            newTitleText = "";
            titleList.Select($"title:{titles.Count - 1}");
        }
    }

    private void DrawTitleQuickSection(bool canSend)
    {
        IconGlyph.Text(FontAwesomeIcon.Heading, "Title");
        ImGui.Separator();
        var quick = plugin.Configuration.QuickCommands.Titles;
        DrawCommandRow("titleQuickFixed", canSend, new RowCommand("Clear title", "title clear", FixedActionIds.ClearTitle, "Removes your Sub's title."));

        using (Section.Begin("titleQuickSaved", "Saved titles"))
        {
            DrawSavedQuickListDetail("titleQuick", quick, canSend, cmd => new ListDetailItem("", cmd.Label, cmd.TitleIsPrefix ? "prefix" : null),
                "No saved titles yet. Use + New to make one.", "New title", () => DrawTitleQuickAddForm(quick), () => newTitleQuickText.Trim().Length > 0,
                cmd =>
                {
                    if (cmd.Command.StartsWith("title style ", StringComparison.OrdinalIgnoreCase)
                        && TitleCommand.TryParseStyleCommand(cmd.Command["title style ".Length..], out var text, out var prefix, out var color, out _))
                        DrawTitlePreview(text, prefix, color);
                });
        }
    }

    private int? newTitleQuickLock;

    private void DrawTitleQuickAddForm(List<QuickCommand> quick)
    {
        using var addBox = Section.Begin("titleQuickAdd", "Add a title command");
        ItemWidth(220);
        ImGui.InputText("##newQuickTitle", ref newTitleQuickText, 64);
        IconGlyph.HelpMarker("The exact title text applied via Honorific.");
        ImGui.Checkbox("Prefix (not suffix)##newQuickTitle", ref newTitleQuickIsPrefix);
        IconGlyph.HelpMarker("Show the title before your Sub's name instead of after it.");
        ImGui.ColorEdit3("Color##newQuickTitle", ref newTitleQuickColor);
        IconGlyph.HelpMarker("Honorific title color - matches the Sub's own Title alias color picker.");
        DrawGlowPicker("newQuickTitle", ref newTitleQuickHasGlow, ref newTitleQuickGlow);
        OwnerLockOption.Draw("newQuickTitle", ref newTitleQuickLock, gesture: false, ownerLock: true);
        if (newTitleQuickText.Trim().Length > 0)
            DrawTitlePreview(newTitleQuickText.Trim(), newTitleQuickIsPrefix, newTitleQuickColor);
        if (ImGui.Button("Save title##quickTitle") && newTitleQuickText.Trim().Length > 0)
        {
            var text = newTitleQuickText.Trim();
            var glow = newTitleQuickHasGlow ? newTitleQuickGlow : (Vector3?)null;
            quick.Add(new QuickCommand
            {
                Label = text,
                Command = TitleCommand.BuildStyleCommand(text, newTitleQuickIsPrefix, newTitleQuickColor, glow),
                TitleIsPrefix = newTitleQuickIsPrefix,
                TitleColor = newTitleQuickColor,
                TitleGlow = glow,
                LockSeconds = newTitleQuickLock,
            });
            plugin.Configuration.Save();
            newTitleQuickText = "";
            newTitleQuickLock = null;
            newTitleQuickIsPrefix = false;
            newTitleQuickColor = new Vector3(1, 1, 1);
            newTitleQuickHasGlow = false;
            newTitleQuickGlow = new Vector3(1, 1, 1);
            QuickList("titleQuick").Select($"titleQuick:{quick.Count - 1}");
        }
        IconGlyph.HelpMarker("Saves a title that applies and locks on your Sub, until you clear it or for the time you set. Your Sub's own titles can't replace it meanwhile.");
    }

    private void DrawOutfitQuickSection(bool canSend)
    {
        DrawOutfitQuickSectionBody(canSend);
    }

    private void DrawOutfitQuickSectionBody(bool canSend)
    {
        var quick = plugin.Configuration.QuickCommands.Outfits;
        DrawSectionTitleRow(FontAwesomeIcon.Tshirt, "Outfit", quick.Count > 0, "outfitQuick", () =>
        {
            quick.Clear();
            plugin.Configuration.Save();
        });

        DrawCommandRow("outfitQuickFixed", canSend, new RowCommand("Unlock outfit", "outfit unlock", FixedActionIds.UnlockOutfit, "Lets your Sub change outfits again."));

        if (quick.Count == 0)
        {
            DrawGoToSyncTabPrompt("No outfits imported yet.");
            return;
        }

        using var _ = Section.Begin("outfitQuickList", "Your Sub's outfits");
        DrawSavedQuickListDetail("outfitQuick", quick, canSend,
            cmd => new ListDetailItem("", cmd.Label, cmd.Command.StartsWith("outfit wear ", StringComparison.OrdinalIgnoreCase) ? "not locked" : null),
            "No outfits imported yet.");
    }

    private void DrawGestureQuickSection(bool canSend)
    {
        var quick = plugin.Configuration.QuickCommands.Gestures;
        // No clear-all here: wiping the imported list was too easy to hit while trying to stop an animation.
        IconGlyph.Text(FontAwesomeIcon.TheaterMasks, "Animation");
        ImGui.Separator();

        // Drawn even with nothing imported - the Sub may be held by an alias or a Custom Trigger.
        DrawCommandRow("gestureQuickFixed", canSend, new RowCommand("Stop animation", $"gesture {ChatComposer.StopGestureWord}", FixedActionIds.StopAnimation,
            "An animation you send holds your Sub in place. This stops it and gives them their movement back."));

        if (quick.Count == 0)
        {
            DrawGoToSyncTabPrompt("No animations imported yet.");
            return;
        }

        // Manifest order within a mod, not alphabetical - "10" would sort before "2". Sorting the saved list itself
        // keeps the list keys (positions) matching what's shown.
        var ordered = quick.OrderBy(c => c.GestureModName ?? "￿", StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.GestureGroupOrder).ThenBy(c => c.GestureOptionOrder).ToList();
        if (!ordered.SequenceEqual(quick))
        {
            quick.Clear();
            quick.AddRange(ordered);
        }

        using var _ = Section.Begin("gestureQuickList", "Your Sub's animations");
        static string ModOf(QuickCommand c) => c.GestureModName ?? "Other";
        var items = quick.GroupBy(ModOf).Select(mod => new ListDetailItem($"mod:{mod.Key}", mod.Key, $"{mod.Count()}",
            Tooltip: $"{mod.Count()} animation{(mod.Count() == 1 ? "" : "s")}")).ToList();
        var listDetail = QuickList("gestureQuick");
        listDetail.Draw("gestureQuick", items, null, item =>
            {
                if (item is null)
                {
                    IconGlyph.WrappedDisabled("Choose an animation mod.");
                    return;
                }
                var anims = quick.Where(c => $"mod:{ModOf(c)}" == item.Key).ToList();
                if (anims.Count == 0)
                    return;
                var selected = ownerGestureAnim is { } current && anims.Contains(current) ? current : anims[0];
                ownerGestureAnim = selected;
                ImGui.TextColored(Theme.AccentHover, item.Label);
                ImGui.TextDisabled($"{anims.Count} animation{(anims.Count == 1 ? "" : "s")}");

                Section.SubHeading("Animations");
                // Switching would drop an unsaved edit, so the others wait until it's saved or cancelled.
                var dirty = QuickEditDirty(selected);
                DrawAnimationPicker(anims, quick.IndexOf(selected).ToString(), _ => [], c => c.GestureGroupName,
                    c => AutoLabeledGesture(plugin.Configuration, c) is { } entry ? $"{entry.AnimationName} - {entry.Trigger!.Label}" : c.Label,
                    c => quick.IndexOf(c).ToString(),
                    id =>
                    {
                        CancelQuickCommandEdit();
                        ownerGestureAnim = quick[int.Parse(id)];
                    },
                    c => dirty && !ReferenceEquals(c, selected));

                ImGui.Spacing();
                DrawSavedQuickDetail(selected, quick, canSend, listDetail, cmd =>
                {
                    if (cmd.GestureGroupName is { Length: > 0 } group)
                        ImGui.TextDisabled(group);
                    if (AutoLabeledGesture(plugin.Configuration, cmd) is { Trigger: { } trigger })
                        ImGui.TextUnformatted($"Plays: {trigger.Label}");
                });
            },
            () => ownerGestureAnim is { } c && QuickEditDirty(c),
            _ =>
            {
                CancelQuickCommandEdit();
                ownerGestureAnim = null;
            },
            "No animations imported yet.");
    }

    private QuickCommand? ownerGestureAnim;

    /// Null for manual or renamed entries. Also used by SubControlWindow.
    internal static GestureExportEntry? AutoLabeledGesture(PluginConfig config, QuickCommand cmd) =>
        cmd.Target is { } target &&
        config.GestureMapping.ImportedPeerCatalog.TryGetValue(target, out var entry) &&
        entry.Trigger is not null && cmd.Label == entry.DisplayLabel
            ? entry
            : null;

    private void DrawFollowQuickSectionBody(bool canSend)
    {
        var quick = plugin.Configuration.QuickCommands.Follow;

        // Custom follow words saved by an older version are listed so they can be removed.
        using (Section.Begin("followQuickCommands", "Commands"))
        {
            var quickConfig = plugin.Configuration.QuickCommands;
            DrawCommandButtons(canSend,
                new RowCommand("Leash", ControlWords.Leash, FixedActionIds.LeashDefault, "Sent with the options below."),
                new RowCommand("Unleash", ControlWords.Unleash, FixedActionIds.UnleashDefault));

            Section.SubHeading("Leash options");
            // Used wherever the Leash command is sent.
            DrawOptionRows("ownerLeashOptions",
                new OptionRow("Length", "How far your Sub can move from you, in yalms. They may have set a shorter limit.", width =>
                {
                    var leashLength = quickConfig.LeashLengthYalms;
                    ImGui.SetNextItemWidth(Math.Min(width, Scaled(280)));
                    if (ImGui.SliderInt("##ownerLeashLength", ref leashLength, LengthOption.MinYalms, LengthOption.MaxYalms, "%d yalms"))
                    {
                        quickConfig.LeashLengthYalms = Math.Clamp(leashLength, LengthOption.MinYalms, LengthOption.MaxYalms);
                        plugin.Configuration.Save();
                    }
                }),
                new OptionRow("Moodle", "Which moodle goes on your Sub with the leash. \"Sub's default\" uses their own pick.", width =>
                {
                    var leashMoodle = quickConfig.LeashMoodleOverride;
                    ImGui.SetNextItemWidth(Math.Min(width, Scaled(280)));
                    OwnerMoodleOverride.Draw("leash", plugin.Configuration, ref leashMoodle, bare: true);
                    if (leashMoodle != quickConfig.LeashMoodleOverride)
                    {
                        quickConfig.LeashMoodleOverride = leashMoodle;
                        plugin.Configuration.Save();
                    }
                }),
                new OptionRow("In duties", "While your Sub is in a duty, their leash pauses as if you were apart, and picks back up once they leave. Applies from the next time you send Leash.", _ =>
                {
                    var pauseInDuties = quickConfig.PauseLeashInDuties;
                    if (ImGui.Checkbox("Pause the leash##ownerPauseLeashInDuties", ref pauseInDuties))
                    {
                        quickConfig.PauseLeashInDuties = pauseInDuties;
                        plugin.Configuration.Save();
                    }
                }));
        }

        if (quick.Count == 0)
            return;

        using var legacyBox = Section.Begin("followQuickLegacy", "Old custom follow words");
        IconGlyph.WrappedDisabled("Saved by an older version - your Sub's plugin now only understands leash / unleash, so these can be removed.");
        foreach (var cmd in quick.ToArray())
            DrawSavedQuickRow(cmd, quick, canSend);
    }

    private void DrawToyControlModule()
    {
        IconGlyph.Text(FontAwesomeIcon.Plug, "Toy Control (Intiface)");
        ImGui.Separator();
        var config = plugin.Configuration;
        if (!config.ToyControlAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Needs the Toy Control acknowledgement and permission in Settings first.");

        if (toyIntifaceAddress.Length == 0)
            toyIntifaceAddress = config.IntifaceAddress;

        var connectionBox = Section.Begin("toyConnection", "Connection");
        var intiface = plugin.IntifaceIpc;
        ItemWidth(260);
        using (ImRaii.Disabled(intiface.IsConnected || intiface.IsConnecting))
            ImGui.InputText("Intiface address##toyControl", ref toyIntifaceAddress, 128);
        IconGlyph.HelpMarker("Intiface Central's address. Only change it if you changed it in Intiface.");

        if (intiface.IsConnected)
        {
            ContinueRowOrWrap(ButtonWidth("Disconnect"));
            if (ImGui.Button("Disconnect##toyControl"))
                intiface.Disconnect();
        }
        else
        {
            var connectLabel = intiface.IsConnecting ? "Connecting..." : "Connect";
            ContinueRowOrWrap(ButtonWidth(connectLabel));
            using (ImRaii.Disabled(intiface.IsConnecting || toyIntifaceAddress.Trim().Length == 0))
            if (ImGui.Button($"{connectLabel}##toyControl"))
            {
                config.IntifaceAddress = toyIntifaceAddress.Trim();
                config.Save();
                intiface.Connect(config.IntifaceAddress);
            }
        }

        var status = intiface.IsConnected ? $"Connected - {intiface.ConnectedDeviceCount} device(s)" : intiface.IsConnecting ? "Connecting..." : "Not connected";
        IconGlyph.WrappedDisabled(status);
        if (intiface.LastError is { Length: > 0 } error)
            IconGlyph.WrappedColored(Theme.Warning, error);

        ImGui.Spacing();
        using (ImRaii.Disabled(!intiface.IsConnected))
        if (ImGui.Button("Stop now##toyControlLocal"))
            plugin.ToyControlCommand.ForceStop();
        IconGlyph.HelpMarker("Stops every device right away.");
        connectionBox.Dispose();

        using (Section.Begin("toyStatus", "Status"))
            ToyStatusView.DrawSub(plugin.ToyControlCommand, intiface.IsConnected);

        var limitsBox = Section.Begin("toyLimits", "Limits");
        var defaultMaxDuration = config.DefaultMaxDurationSeconds;
        ItemWidth(160);
        if (ImGui.SliderInt("Default max duration (s)##toyControlDefaultMax", ref defaultMaxDuration, 1, ToyControlCommand.MaxDurationSeconds))
        {
            config.DefaultMaxDurationSeconds = defaultMaxDuration;
            config.Save();
        }
        IconGlyph.HelpMarker($"How long a command runs if it has no duration. Never more than {ToyControlCommand.MaxDurationSeconds}s.");

        var permanentBackstop = config.PermanentBackstopSeconds;
        ItemWidth(160);
        if (ImGui.InputInt("Permanent-mode backstop (s)##toyControlPermanentBackstop", ref permanentBackstop))
        {
            config.PermanentBackstopSeconds = Math.Max(1, permanentBackstop);
            config.Save();
        }
        IconGlyph.HelpMarker("The limit for permanent-mode commands. Default 4 hours.");
        limitsBox.Dispose();

        using (Section.Begin("toyPatterns"))
            DrawToyPatternEditor();

        using (Section.Begin("toyTriggers"))
            DrawToyTriggerEditor();
    }

    private readonly ListDetail toyPatternList = new();
    private const string NewPatternKey = "new:pattern";

    /// The Owner's saved patterns are sent by value, since the Sub may not have them; `ownerCanSend` enables that.
    private void DrawToyPatternEditor(bool? ownerCanSend = null)
    {
        var config = plugin.Configuration;
        Section.SubHeading("Custom patterns");
        var items = config.ToyPatterns.Select(p => new ListDetailItem($"pattern:{p.Id}", p.Name,
            $"{p.Steps.Count} step{(p.Steps.Count == 1 ? "" : "s")}{(p.Loop ? ", loops" : "")}")).ToList();
        if (toyPatternList.Selected == NewPatternKey)
            items.Add(new ListDetailItem(NewPatternKey, "New pattern"));

        toyPatternList.Draw("toyPatterns", items,
            () =>
            {
                if (!ImGui.Button("+ New##toyPattern"))
                    return;
                ResetToyPatternInput();
                toyPatternList.Select(NewPatternKey);
            },
            item =>
            {
                if (item is null)
                {
                    IconGlyph.WrappedDisabled("Choose a pattern, or use + New to make one.");
                    return;
                }
                var pattern = config.ToyPatterns.FirstOrDefault(p => $"pattern:{p.Id}" == item.Key);
                if (pattern is not null)
                {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(Theme.AccentHover, pattern.Name);
                    ImGui.SameLine();
                    if (ImGui.Button($"Delete##toyPattern{pattern.Id}"))
                    {
                        config.ToyPatterns.Remove(pattern);
                        // Disable, rather than orphan, triggers that named this pattern.
                        foreach (var rule in config.ToyTriggerRules.Where(r => string.Equals(r.PatternName, pattern.Name, StringComparison.OrdinalIgnoreCase)))
                            rule.Enabled = false;
                        config.Save();
                        ResetToyPatternInput();
                        toyPatternList.Select(null);
                        return;
                    }
                    if (ownerCanSend is { } canSend)
                    {
                        ImGui.SameLine();
                        DrawSendOnly(ToyControlCommand.BuildCustomSequenceCommand(pattern.Steps, pattern.Loop), canSend, $"toyPatternSend{pattern.Id}", "Send");
                        IconGlyph.HelpMarker("Sends this pattern to your Sub. They don't need it saved.");
                    }
                }
                DrawToyPatternForm();
            },
            () => toyPatternList.Selected == NewPatternKey
                ? toyPatternNameInput.Trim().Length > 0 || toyPatternStepsInput.Count > 0
                : config.ToyPatterns.FirstOrDefault(p => p.Id == toyPatternEditingId) is { } edited
                  && (edited.Name != toyPatternNameInput.Trim() || edited.Loop != toyPatternLoopInput
                      || !edited.Steps.Select(x => (x.IntensityPercent, x.DurationMs)).SequenceEqual(toyPatternStepsInput.Select(x => (x.IntensityPercent, x.DurationMs)))),
            item =>
            {
                ResetToyPatternInput();
                if (config.ToyPatterns.FirstOrDefault(p => $"pattern:{p.Id}" == item?.Key) is { } pattern)
                    LoadToyPatternDraft(pattern);
            },
            "No custom patterns yet. Use + New to make one.");
    }

    private void LoadToyPatternDraft(ToyPattern pattern)
    {
        toyPatternEditingId = pattern.Id;
        toyPatternNameInput = pattern.Name;
        toyPatternLoopInput = pattern.Loop;
        toyPatternStepsInput.Clear();
        toyPatternStepsInput.AddRange(pattern.Steps.Select(s => new PatternStep { IntensityPercent = s.IntensityPercent, DurationMs = s.DurationMs }));
    }

    private void DrawToyPatternForm()
    {
        var config = plugin.Configuration;
        ImGui.Spacing();
        IconGlyph.WrappedDisabled(toyPatternEditingId is null ? "New pattern" : "Editing pattern");
        ItemWidth(200);
        ImGui.InputText("Name##toyPatternName", ref toyPatternNameInput, 64);
        IconGlyph.HelpMarker("Can't match a built-in or another saved pattern.");
        ImGui.Checkbox("Loop##toyPatternLoop", ref toyPatternLoopInput);
        IconGlyph.HelpMarker("Repeat the steps until stopped.");
        ImGui.Spacing();

        ImGui.Indent();
        for (var i = 0; i < toyPatternStepsInput.Count; i++)
        {
            var step = toyPatternStepsInput[i];
            IconGlyph.WrappedDisabled($"Step {i + 1}");
            ItemWidth(140);
            var intensity = step.IntensityPercent;
            if (ImGui.SliderInt($"Intensity##toyPatternStep{i}", ref intensity, 0, 100, "%d%%"))
                step.IntensityPercent = intensity;
            IconGlyph.HelpMarker("How strong the vibration is while this step is active.");

            ContinueRowOrWrap(160);
            ItemWidth(120);
            var durationMs = step.DurationMs;
            if (ImGui.InputInt($"ms##toyPatternStepDuration{i}", ref durationMs))
                step.DurationMs = Math.Max(0, durationMs);
            IconGlyph.HelpMarker("Step length in milliseconds. 0 holds until stopped.");

            ContinueRowOrWrap(ButtonWidth("Up"));
            using (ImRaii.Disabled(i == 0))
            if (ImGui.Button($"Up##toyPatternStep{i}"))
            {
                (toyPatternStepsInput[i - 1], toyPatternStepsInput[i]) = (toyPatternStepsInput[i], toyPatternStepsInput[i - 1]);
                break;
            }

            ContinueRowOrWrap(ButtonWidth("Down"));
            using (ImRaii.Disabled(i == toyPatternStepsInput.Count - 1))
            if (ImGui.Button($"Down##toyPatternStep{i}"))
            {
                (toyPatternStepsInput[i + 1], toyPatternStepsInput[i]) = (toyPatternStepsInput[i], toyPatternStepsInput[i + 1]);
                break;
            }

            ContinueRowOrWrap(ButtonWidth("Remove"));
            if (ImGui.Button($"Remove##toyPatternStep{i}"))
            {
                toyPatternStepsInput.RemoveAt(i);
                break;
            }

            if (i < toyPatternStepsInput.Count - 1)
                ImGui.Separator();
        }
        ImGui.Unindent();

        ImGui.Spacing();
        if (ImGui.Button("Add step##toyPattern"))
            toyPatternStepsInput.Add(new PatternStep { IntensityPercent = 50, DurationMs = 500 });

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Save pattern##toyPattern"))
        {
            var name = toyPatternNameInput.Trim();
            if (name.Length == 0)
                toyPatternError = "A pattern needs a name.";
            else if (toyPatternStepsInput.Count == 0)
                toyPatternError = "A pattern needs at least one step.";
            else if (ToyControlCommand.IsBuiltInPattern(name))
                toyPatternError = "That name is reserved for a built-in pattern.";
            else if (config.ToyPatterns.Any(p => p.Id != toyPatternEditingId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                toyPatternError = "Another custom pattern already has that name.";
            else
            {
                toyPatternError = null;
                var existing = toyPatternEditingId is { } id ? config.ToyPatterns.FirstOrDefault(p => p.Id == id) : null;
                var target = existing ?? new ToyPattern();
                var oldName = existing?.Name;
                target.Name = name;
                target.Loop = toyPatternLoopInput;
                target.Steps = toyPatternStepsInput.Select(s => new PatternStep { IntensityPercent = s.IntensityPercent, DurationMs = s.DurationMs }).ToList();
                if (existing is null)
                    config.ToyPatterns.Add(target);
                else if (!string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var rule in config.ToyTriggerRules.Where(r => string.Equals(r.PatternName, oldName, StringComparison.OrdinalIgnoreCase)))
                        rule.PatternName = name;
                }
                config.Save();
                LoadToyPatternDraft(target);
                toyPatternList.Select($"pattern:{target.Id}");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##toyPattern"))
        {
            if (config.ToyPatterns.FirstOrDefault(p => p.Id == toyPatternEditingId) is { } original)
                LoadToyPatternDraft(original);
            else
            {
                ResetToyPatternInput();
                toyPatternList.Select(null);
            }
        }
        if (toyPatternError is { Length: > 0 } error)
            IconGlyph.WrappedColored(Theme.Warning, error);
    }

    private void ResetToyPatternInput()
    {
        toyPatternEditingId = null;
        toyPatternNameInput = "";
        toyPatternLoopInput = false;
        toyPatternStepsInput.Clear();
        toyPatternError = null;
    }

    /// Gated on ToyTriggersAcknowledged only; ToyControl and its acknowledgement cover Owner commands.
    private void DrawToyTriggerEditor()
    {
        Section.SubHeading("Automatic triggers");

        var config = plugin.Configuration;

        if (!config.ToyTriggersAcknowledged)
        {
            IconGlyph.WrappedColored(Theme.Warning, "Needs the Automatic Triggers acknowledgement in Settings first.");
            return;
        }

        var runtimeState = plugin.RuntimeState;
        if (runtimeState.ToyTriggersSuspended)
        {
            IconGlyph.WrappedColored(Theme.Warning, "Triggers are suspended (panic was triggered). They will not fire again until you resume them.");
            if (ImGui.Button("Resume triggers##toyTriggers"))
                runtimeState.ToyTriggersSuspended = false;
            IconGlyph.HelpMarker("Turns triggers back on after a panic.");
            ImGui.Spacing();
        }

        var items = config.ToyTriggerRules.Select(r => new ListDetailItem($"trigger:{r.Id}", DescribeTrigger(r), r.Enabled ? null : "off",
            Tooltip: DescribeTrigger(r, full: true))).ToList();
        if (toyTriggerList.Selected == NewTriggerKey)
            items.Add(new ListDetailItem(NewTriggerKey, "New trigger"));

        toyTriggerList.Draw("toyTriggers", items,
            () =>
            {
                if (!ImGui.Button("+ New##toyTrigger"))
                    return;
                ResetToyTriggerDraft();
                toyTriggerList.Select(NewTriggerKey);
            },
            item =>
            {
                if (item is null)
                {
                    IconGlyph.WrappedDisabled("Choose a trigger, or use + New to make one.");
                    return;
                }
                if (config.ToyTriggerRules.FirstOrDefault(r => $"trigger:{r.Id}" == item.Key) is { } rule)
                {
                    var enabled = rule.Enabled;
                    if (ImGui.Checkbox($"On##toyTriggerEnabled{rule.Id}", ref enabled))
                    {
                        rule.Enabled = enabled;
                        config.Save();
                    }
                    ImGui.SameLine();
                    if (ImGui.Button($"Delete##toyTrigger{rule.Id}"))
                    {
                        config.ToyTriggerRules.Remove(rule);
                        config.Save();
                        ResetToyTriggerDraft();
                        toyTriggerList.Select(null);
                        return;
                    }
                    if (rule.PatternName is { Length: > 0 } targetPattern && !IsKnownPatternName(config, targetPattern))
                        IconGlyph.WrappedColored(Theme.Warning, $"\"{targetPattern}\" no longer exists - this trigger needs reconfiguration.");
                }
                DrawToyTriggerForm();
            },
            () => toyTriggerList.Selected != NewTriggerKey && ToyTriggerDraftDiffers(),
            item =>
            {
                ResetToyTriggerDraft();
                if (config.ToyTriggerRules.FirstOrDefault(r => $"trigger:{r.Id}" == item?.Key) is { } rule)
                    LoadToyTriggerDraft(rule);
            },
            "No triggers yet. Use + New to make one.");
    }

    private readonly ListDetail toyTriggerList = new();
    private const string NewTriggerKey = "new:trigger";

    /// Compares through a scratch rule built from the draft, so every field counts.
    private bool ToyTriggerDraftDiffers()
    {
        if (editingToyTriggerId is not { } id || plugin.Configuration.ToyTriggerRules.FirstOrDefault(r => r.Id == id) is not { } rule)
            return false;
        var draft = new ToyTriggerRule();
        ApplyToyTriggerDraft(draft);
        return DescribeTrigger(draft, full: true) != DescribeTrigger(rule, full: true) || draft.CooldownSeconds != rule.CooldownSeconds;
    }

    private void DrawToyTriggerForm()
    {
        var config = plugin.Configuration;
        ImGui.Spacing();
        var editingRule = editingToyTriggerId is { } editingId ? config.ToyTriggerRules.FirstOrDefault(r => r.Id == editingId) : null;
        if (editingToyTriggerId is not null && editingRule is null)
            ResetToyTriggerDraft();
        IconGlyph.WrappedDisabled(editingRule is null ? "New trigger" : "Edit trigger");
        ImGui.Spacing();
        var kindNames = new[] { "Health drops below %", "Hit (damage from anything)", "A restriction becomes active", "Spell cast on you", "Emote used on you" };
        var kindIndex = (int)toyTriggerKindInput;
        ItemWidth(220);
        if (ImGui.Combo("Condition##toyTriggerKind", ref kindIndex, kindNames, kindNames.Length))
            toyTriggerKindInput = (ToyTriggerKind)kindIndex;
        IconGlyph.HelpMarker("What fires this trigger. Runs only on your client.");

        if (toyTriggerKindInput == ToyTriggerKind.HealthPercent)
        {
            ItemWidth(120);
            ImGui.SliderInt("Threshold %##toyTriggerHealth", ref toyTriggerHealthThresholdInput, 1, 100);
            IconGlyph.HelpMarker("Fires when your health drops to this percent. It has to rise above it to fire again.");
        }
        else if (toyTriggerKindInput == ToyTriggerKind.RestrictionActive)
        {
            var restrictionNames = Enum.GetNames<RestraintRuleKind>();
            var restrictionIndex = (int)toyTriggerRestrictionKindInput;
            ItemWidth(200);
            if (ImGui.Combo("Restriction##toyTriggerRestriction", ref restrictionIndex, restrictionNames, restrictionNames.Length))
                toyTriggerRestrictionKindInput = (RestraintRuleKind)restrictionIndex;
            IconGlyph.HelpMarker("Fires when this restriction starts on you.");
        }
        else if (toyTriggerKindInput == ToyTriggerKind.PlayerDamage)
        {
            IconGlyph.WrappedDisabled("Fires when you take damage. Add players below to only react to them.");
            DrawToyTriggerSourcePlayers();
        }
        else if (toyTriggerKindInput == ToyTriggerKind.SpellCastOnYou)
        {
            IconGlyph.WrappedDisabled("Fires when a player uses an action on you. Narrow it by job or skill below.");
            DrawToySpellJobFilter();
            DrawToySpellActionFilter();
            DrawToyTriggerSourcePlayers();
        }
        else
        {
            IconGlyph.WrappedDisabled("Fires when a player emotes at you. Narrow it by emote or player below.");
            DrawToyEmoteFilter();
            DrawToyTriggerSourcePlayers();
        }

        ImGui.Checkbox("Named pattern (instead of a fixed intensity)##toyTriggerUsePattern", ref toyTriggerUsePatternInput);
        IconGlyph.HelpMarker("Off: a plain vibration. On: play a named pattern.");
        if (toyTriggerUsePatternInput)
        {
            ItemWidth(180);
            ImGui.InputText("Pattern name##toyTriggerPattern", ref toyTriggerPatternNameInput, 64);
            IconGlyph.HelpMarker("A built-in (weak/medium/strong/pulse) or saved pattern name. Unknown names never fire.");
        }
        else
        {
            ItemWidth(140);
            ImGui.SliderInt("Intensity##toyTriggerIntensity", ref toyTriggerIntensityInput, 0, 100, "%d%%");
            IconGlyph.HelpMarker("How strong the vibration is when this trigger fires.");

            ImGui.Checkbox("Duration##toyTriggerHasDuration", ref toyTriggerHasDurationInput);
            IconGlyph.HelpMarker("How long it vibrates. Unchecked uses the default limit.");
            if (toyTriggerHasDurationInput)
            {
                ImGui.SameLine();
                ItemWidth(120);
                ImGui.SliderInt("seconds##toyTriggerDuration", ref toyTriggerDurationSecondsInput, 1, ToyControlCommand.MaxDurationSeconds);
            }
        }

        ItemWidth(120);
        ImGui.SliderInt("Cooldown (s)##toyTriggerCooldown", ref toyTriggerCooldownInput, 2, 300);
        IconGlyph.HelpMarker("Minimum time between firings (at least 2 seconds).");

        ImGui.Spacing();
        if (editingRule is not null)
        {
            if (ImGui.Button("Save trigger##toyTrigger"))
            {
                ApplyToyTriggerDraft(editingRule);
                config.Save();
                LoadToyTriggerDraft(editingRule);
            }
            ContinueRowOrWrap(ButtonWidth("Cancel"));
            if (ImGui.Button("Cancel##toyTrigger"))
                LoadToyTriggerDraft(editingRule);
        }
        else if (ImGui.Button("Add trigger##toyTrigger"))
        {
            var rule = new ToyTriggerRule { Enabled = true };
            ApplyToyTriggerDraft(rule);
            config.ToyTriggerRules.Add(rule);
            config.Save();
            LoadToyTriggerDraft(rule);
            toyTriggerList.Select($"trigger:{rule.Id}");
        }
    }

    /// Keeps the rule's Id and Enabled state so the evaluator's cooldown tracking carries over.
    private void ApplyToyTriggerDraft(ToyTriggerRule rule)
    {
        rule.Kind = toyTriggerKindInput;
        rule.HealthPercentThreshold = toyTriggerHealthThresholdInput;
        rule.RestrictionKind = toyTriggerRestrictionKindInput;
        rule.SpellJobIds = toyTriggerSpellJobIdsInput.ToList();
        rule.SpellActionIds = toyTriggerSpellActionIdsInput.ToList();
        rule.EmoteIds = toyTriggerEmoteIdsInput.ToList();
        rule.SourcePlayers = toyTriggerSourcePlayersInput.ToList();
        rule.IntensityPercent = toyTriggerUsePatternInput ? null : toyTriggerIntensityInput;
        rule.PatternName = toyTriggerUsePatternInput ? toyTriggerPatternNameInput.Trim() : null;
        rule.DurationSeconds = toyTriggerUsePatternInput || !toyTriggerHasDurationInput ? null : toyTriggerDurationSecondsInput;
        rule.CooldownSeconds = Math.Max(2, toyTriggerCooldownInput);
    }

    private void LoadToyTriggerDraft(ToyTriggerRule rule)
    {
        editingToyTriggerId = rule.Id;
        toyTriggerKindInput = rule.Kind;
        toyTriggerHealthThresholdInput = rule.HealthPercentThreshold;
        toyTriggerRestrictionKindInput = rule.RestrictionKind;
        toyTriggerSpellJobIdsInput.Clear();
        toyTriggerSpellJobIdsInput.UnionWith(rule.SpellJobIds);
        toyTriggerSpellActionIdsInput.Clear();
        toyTriggerSpellActionIdsInput.UnionWith(rule.SpellActionIds);
        toyTriggerEmoteIdsInput.Clear();
        toyTriggerEmoteIdsInput.UnionWith(rule.EmoteIds);
        toyTriggerSourcePlayersInput.Clear();
        toyTriggerSourcePlayersInput.AddRange(rule.SourcePlayers);
        toyTriggerSpellJobSearch = "";
        toyTriggerSpellActionSearch = "";
        toyTriggerEmoteSearch = "";
        toyTriggerSourcePlayerInput = "";
        toyTriggerUsePatternInput = rule.PatternName is { Length: > 0 };
        toyTriggerPatternNameInput = rule.PatternName ?? "";
        toyTriggerIntensityInput = rule.IntensityPercent ?? 50;
        toyTriggerHasDurationInput = rule.DurationSeconds is not null;
        toyTriggerDurationSecondsInput = rule.DurationSeconds ?? 10;
        toyTriggerCooldownInput = rule.CooldownSeconds;
    }

    private void ResetToyTriggerDraft()
    {
        editingToyTriggerId = null;
        toyTriggerKindInput = ToyTriggerKind.HealthPercent;
        toyTriggerHealthThresholdInput = 50;
        toyTriggerRestrictionKindInput = RestraintRuleKind.Gagged;
        toyTriggerSpellJobIdsInput.Clear();
        toyTriggerSpellActionIdsInput.Clear();
        toyTriggerEmoteIdsInput.Clear();
        toyTriggerSourcePlayersInput.Clear();
        toyTriggerSpellJobSearch = "";
        toyTriggerSpellActionSearch = "";
        toyTriggerEmoteSearch = "";
        toyTriggerSourcePlayerInput = "";
        toyTriggerUsePatternInput = false;
        toyTriggerPatternNameInput = "";
        toyTriggerIntensityInput = 50;
        toyTriggerHasDurationInput = false;
        toyTriggerDurationSecondsInput = 10;
        toyTriggerCooldownInput = 5;
    }

    /// Checking none means "any emote".
    private void DrawToyEmoteFilter()
    {
        ItemWidth(200);
        ImGui.InputTextWithHint("##toyTriggerEmoteSearch", "Filter emotes...", ref toyTriggerEmoteSearch, 32);
        IconGlyph.HelpMarker("Which emote(s) this trigger reacts to. Leave every emote unchecked to react to any emote aimed at you.");
        if (toyTriggerEmoteIdsInput.Count > 0)
        {
            ContinueRowOrWrap(ButtonWidth("Clear"));
            if (ImGui.Button("Clear##toyTriggerEmotes"))
                toyTriggerEmoteIdsInput.Clear();
        }

        using var _ = ImRaii.Child("toyTriggerEmoteList", new Vector2(0, Scaled(110)), true);
        var search = toyTriggerEmoteSearch.Trim();
        foreach (var emote in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>()
                     .Where(e => e.RowId > 0 && e.Name.ExtractText().Length > 0)
                     .Select(e => (e.RowId, Name: e.Name.ExtractText(), Command: e.TextCommand.ValueNullable?.Command.ExtractText() ?? ""))
                     .Where(e => search.Length == 0 || e.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || e.Command.Contains(search, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(e => toyTriggerEmoteIdsInput.Contains(e.RowId))
                     .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var isChecked = toyTriggerEmoteIdsInput.Contains(emote.RowId);
            var label = emote.Command.Length > 0 ? $"{emote.Name} ({emote.Command})" : emote.Name;
            if (ImGui.Checkbox($"{label}##toyTriggerEmote{emote.RowId}", ref isChecked))
            {
                if (isChecked) toyTriggerEmoteIdsInput.Add(emote.RowId);
                else toyTriggerEmoteIdsInput.Remove(emote.RowId);
            }
        }
    }

    private void DrawToyTriggerSourcePlayers()
    {
        Section.SubHeading("Only from these players");
        IconGlyph.HelpMarker("Empty = anyone. \"Name Surname\" matches any world; add @World for just one.");

        foreach (var entry in toyTriggerSourcePlayersInput.ToList())
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(entry);
            ContinueRowOrWrap(ButtonWidth("Remove"));
            if (ImGui.Button($"Remove##toyTriggerSource{entry}"))
                toyTriggerSourcePlayersInput.Remove(entry);
        }

        void AddPlayer(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length > 0 && !toyTriggerSourcePlayersInput.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                toyTriggerSourcePlayersInput.Add(trimmed);
        }

        ItemWidth(220);
        var submitted = ImGui.InputTextWithHint("##toyTriggerSourceInput", "Name Surname[@World]", ref toyTriggerSourcePlayerInput, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ContinueRowOrWrap(ButtonWidth("Add"));
        if ((ImGui.Button("Add##toyTriggerSource") || submitted) && toyTriggerSourcePlayerInput.Trim().Length > 0)
        {
            AddPlayer(toyTriggerSourcePlayerInput);
            toyTriggerSourcePlayerInput = "";
        }

        var target = Plugin.TargetManager.Target as Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter;
        using (ImRaii.Disabled(target is null))
        {
            if (ImGui.Button("Add my target##toyTriggerSource") && target is not null)
                AddPlayer($"{target.Name.TextValue}@{target.HomeWorld.ValueNullable?.Name.ExtractText()}");
        }
        if (target is null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Target a player character first.");

        foreach (var pairing in plugin.Configuration.ActivePairings)
        {
            var peer = $"{pairing.PeerName}@{pairing.PeerWorld}";
            if (toyTriggerSourcePlayersInput.Contains(peer, StringComparer.OrdinalIgnoreCase)) continue;
            var label = $"Add {pairing.PeerName}";
            ContinueRowOrWrap(ButtonWidth(label));
            if (ImGui.Button($"{label}##toyTriggerSourcePeer{pairing.Id}"))
                AddPlayer(peer);
        }
    }

    /// Checking none means "any job".
    private void DrawToySpellJobFilter()
    {
        ItemWidth(200);
        ImGui.InputTextWithHint("##toyTriggerSpellJobSearch", "Filter jobs...", ref toyTriggerSpellJobSearch, 32);
        IconGlyph.HelpMarker("Which caster job(s) this trigger reacts to. Leave every job unchecked to match any job.");

        using var _ = ImRaii.Child("toyTriggerSpellJobList", new Vector2(0, Scaled(90)), true);
        var search = toyTriggerSpellJobSearch.Trim();
        foreach (var job in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>()
                     .Where(j => j.RowId > 0 && j.Role > 0 && j.Abbreviation.ExtractText().Length > 0)
                     .Where(j => search.Length == 0 || j.Name.ExtractText().Contains(search, StringComparison.OrdinalIgnoreCase) || j.Abbreviation.ExtractText().Contains(search, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(j => j.Abbreviation.ExtractText()))
        {
            var isChecked = toyTriggerSpellJobIdsInput.Contains(job.RowId);
            if (ImGui.Checkbox($"{job.Abbreviation.ExtractText()} - {job.Name.ExtractText()}##toyTriggerSpellJob{job.RowId}", ref isChecked))
            {
                if (isChecked) toyTriggerSpellJobIdsInput.Add(job.RowId);
                else toyTriggerSpellJobIdsInput.Remove(job.RowId);
            }
        }
    }

    /// Thousands of rows, so it needs search text first (capped at 100). Checking none means "any action".
    private void DrawToySpellActionFilter()
    {
        ItemWidth(200);
        ImGui.InputTextWithHint("##toyTriggerSpellActionSearch", "Search skill/spell name...", ref toyTriggerSpellActionSearch, 32);
        IconGlyph.HelpMarker("Which skills fire this trigger. None checked = any. Type to search.");

        var search = toyTriggerSpellActionSearch.Trim();
        if (search.Length == 0)
        {
            foreach (var actionId in toyTriggerSpellActionIdsInput.ToList())
            {
                var selectedName = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(actionId)?.Name.ExtractText() ?? actionId.ToString();
                TextWithActions($"Selected: {selectedName}", ButtonWidth("Remove"));
                if (ImGui.Button($"Remove##toyTriggerSpellAction{actionId}"))
                    toyTriggerSpellActionIdsInput.Remove(actionId);
            }
            return;
        }

        using var _ = ImRaii.Child("toyTriggerSpellActionList", new Vector2(0, Scaled(90)), true);
        foreach (var action in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()
                     .Where(a => a.IsPlayerAction && a.Name.ExtractText().Contains(search, StringComparison.OrdinalIgnoreCase))
                     .Take(100))
        {
            var isChecked = toyTriggerSpellActionIdsInput.Contains(action.RowId);
            if (ImGui.Checkbox($"{action.Name.ExtractText()}##toyTriggerSpellAction{action.RowId}", ref isChecked))
            {
                if (isChecked) toyTriggerSpellActionIdsInput.Add(action.RowId);
                else toyTriggerSpellActionIdsInput.Remove(action.RowId);
            }
        }
    }

    private static bool IsKnownPatternName(PluginConfig config, string name) =>
        ToyControlCommand.IsBuiltInPattern(name) ||
        config.ToyPatterns.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string DescribeTrigger(ToyTriggerRule rule, bool full = false)
    {
        var condition = rule.Kind switch
        {
            ToyTriggerKind.HealthPercent => $"Health <= {rule.HealthPercentThreshold}%",
            ToyTriggerKind.PlayerDamage => "Hit",
            ToyTriggerKind.RestrictionActive => $"{rule.RestrictionKind} active",
            ToyTriggerKind.SpellCastOnYou => DescribeSpellFilter(rule, full),
            ToyTriggerKind.EmoteOnYou => DescribeEmoteFilter(rule, full),
            _ => rule.Kind.ToString(),
        };
        if ((rule.Kind is ToyTriggerKind.PlayerDamage or ToyTriggerKind.SpellCastOnYou or ToyTriggerKind.EmoteOnYou) && rule.SourcePlayers.Count > 0)
            condition += $" from {(!full && rule.SourcePlayers.Count > 2 ? $"{rule.SourcePlayers.Count} players" : string.Join(", ", rule.SourcePlayers))}";
        var action = rule.PatternName is { Length: > 0 } name ? $"pattern \"{name}\"" : $"{rule.IntensityPercent ?? 0}% vibrate{(rule.DurationSeconds is { } d ? $" for {d}s" : "")}";
        return $"{condition} -> {action} (cooldown {rule.CooldownSeconds}s)";
    }

    private static string DescribeEmoteFilter(ToyTriggerRule rule, bool full)
    {
        if (rule.EmoteIds.Count == 0)
            return "Any emote on you";
        if (!full && rule.EmoteIds.Count > 3)
            return $"Emote on you ({rule.EmoteIds.Count} emotes)";
        return $"Emote on you ({string.Join(", ", rule.EmoteIds.Select(id => Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>().GetRowOrDefault(id)?.Name.ExtractText() ?? id.ToString()))})";
    }

    /// `full` lists every one, for the hover tooltip.
    private static string DescribeSpellFilter(ToyTriggerRule rule, bool full = false)
    {
        const int maxListed = 3;
        if (rule.SpellJobIds.Count == 0 && rule.SpellActionIds.Count == 0)
            return "Any spell cast on you";

        var jobs = rule.SpellJobIds.Count == 0 ? "any job" :
            !full && rule.SpellJobIds.Count > maxListed ? $"{rule.SpellJobIds.Count} jobs" :
            string.Join('/', rule.SpellJobIds.Select(id => Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(id)?.Abbreviation.ExtractText() ?? id.ToString()));
        var actions = rule.SpellActionIds.Count == 0 ? "any action" :
            !full && rule.SpellActionIds.Count > maxListed ? $"{rule.SpellActionIds.Count} actions" :
            string.Join(", ", rule.SpellActionIds.Select(id => Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(id)?.Name.ExtractText() ?? id.ToString()));
        return $"Spell cast on you ({jobs}, {actions})";
    }

    private void DrawToyControlQuickSection(bool canSend)
    {
        IconGlyph.Text(FontAwesomeIcon.Plug, "Toy Control");
        ImGui.Separator();
        IconGlyph.WrappedDisabled($"Commands stop after at most {ToyControlCommand.MaxDurationSeconds} seconds.");

        using (Section.Begin("toyQuickStatus", "Status"))
            ToyStatusView.DrawOwner(plugin.OwnerToyStatus.ForActivePairing);

        var vibrateBox = Section.Begin("toyQuickVibrate", "Vibrate");
        ItemWidth(200);
        ImGui.SliderInt("Intensity##toyControl", ref toyVibrateIntensity, 0, 100, "%d%%");

        if (ImGui.Checkbox("Permanent (runs until stopped)##toyControlPermanent", ref toyVibrateIsPermanent) && toyVibrateIsPermanent)
            toyVibrateHasDuration = false;
        if (toyVibrateIsPermanent)
            IconGlyph.WrappedDisabled("Still capped by the Sub's own configured permanent-mode backstop ceiling, not truly unbounded.");
        else
        {
            ImGui.Checkbox("Duration##toyControlHasDuration", ref toyVibrateHasDuration);
            if (toyVibrateHasDuration)
            {
                ImGui.SameLine();
                ItemWidth(120);
                ImGui.SliderInt("seconds##toyControlDuration", ref toyVibrateDurationSeconds, 1, ToyControlCommand.MaxDurationSeconds);
            }
        }

        var duration = toyVibrateIsPermanent ? ToyDuration.Permanent : toyVibrateHasDuration ? ToyDuration.Bounded(toyVibrateDurationSeconds) : ToyDuration.Unspecified;
        var vibrateCommand = ToyControlCommand.BuildVibrateCommand(toyVibrateIntensity, duration);
        DrawSendOnly(vibrateCommand, canSend, "toyControlVibrate", "Send vibrate");
        vibrateBox.Dispose();

        var patternsBox = Section.Begin("toyQuickPatterns", "Patterns & stop");
        for (var i = 0; i < ToyControlCommand.BuiltInPatternNames.Count; i++)
        {
            var name = ToyControlCommand.BuiltInPatternNames[i];
            var label = char.ToUpperInvariant(name[0]) + name[1..];
            if (i > 0) ContinueRowOrWrap(ButtonWidth(label));
            DrawSendOnly(ToyControlCommand.BuildPatternCommand(name), canSend, $"toyControl{label}", label);
        }

        ImGui.Spacing();
        DrawSendOnly(ToyControlCommand.BuildStopCommand(), canSend, "toyControlStop", "Send stop");
        patternsBox.Dispose();

        using (Section.Begin("toyQuickCustomPatterns"))
            DrawToyPatternEditor(canSend);
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

    private static void DrawGlowPicker(string idSuffix, ref bool hasGlow, ref Vector3 glow)
    {
        ImGui.Checkbox($"Glow##{idSuffix}", ref hasGlow);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Honorific title glow color - optional, off by default.");
        if (hasGlow)
        {
            ImGui.SameLine();
            ImGui.ColorEdit3($"##{idSuffix}_glow", ref glow);
        }
    }

    private void DrawSendCopyButtons(string command, bool canSend, string idSuffix, string sendLabel = "Send")
    {
        var messages = plugin.ChatComposer.ComposeAll(command);
        var fits = ChatComposer.AllFit(messages);

        using (ImRaii.Disabled(!canSend || !fits))
        {
            if (ImGui.Button($"{sendLabel}##{idSuffix}"))
                plugin.ChatSender.SendAll(messages);
        }
        TutorialService.Anchor(TutorialAnchors.QuickSend);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(!fits ? "Command is too long for a safe chat payload." : canSend ? string.Join("\n", messages) : "No /tell target yet - pairing hasn't captured your Sub's name.");

        ContinueRowOrWrap(ButtonWidth("Copy"));
        // A command that goes out as several messages can't be pasted as one.
        using (ImRaii.Disabled(!fits || messages.Count > 1))
        if (ImGui.Button($"Copy##{idSuffix}"))
            ImGui.SetClipboardText(messages[0]);
        if (messages.Count > 1 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip($"Too long for one message - Send delivers it as {messages.Count} messages, one per action.");
    }

    private void DrawSendOnly(string command, bool canSend, string idSuffix, string label)
    {
        var messages = plugin.ChatComposer.ComposeAll(command);
        var fits = ChatComposer.AllFit(messages);
        using (ImRaii.Disabled(!canSend || !fits))
        {
            if (ImGui.Button($"{label}##{idSuffix}"))
                plugin.ChatSender.SendAll(messages);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(!fits ? "Command is too long for a safe chat payload." : canSend ? string.Join("\n", messages) : "No /tell target yet - pairing hasn't captured your Sub's name.");
    }

    private static int DrawItemSelector(string comboId, string[] labels, ref int? selectedIndex)
    {
        var index = Math.Clamp(selectedIndex ?? 0, 0, labels.Length - 1);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo(comboId, ref index, labels, labels.Length))
            selectedIndex = index;
        selectedIndex ??= index;
        return index;
    }

    /// Category and control words are matched before any Sub alias, so an alias with either name could never fire.
    private static bool IsReserved(string alias) =>
        ChatCommandListener.ReservedCategoryWords.Contains(alias.Trim(), StringComparer.OrdinalIgnoreCase)
        || ControlWords.All.Contains(alias.Trim(), StringComparer.OrdinalIgnoreCase);

    private static void DrawReservedWordWarning(string alias)
    {
        if (IsReserved(alias))
            IconGlyph.WrappedColored(Theme.Warning, $"\"{alias.Trim()}\" is reserved for a fixed Owner command - pick a different alias.");
    }

    private static void DrawFixedWord(string label, string word)
    {
        ImGui.TextUnformatted($"{label}:");
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.AccentHover);
        ImGui.TextUnformatted(word);
        ImGui.PopStyleColor();
    }

    /// `width` null keeps the form's default item width.
    private bool DrawAttachedMoodlePicker(string id, AttachedMoodleRef? current, PluginConfig config, out AttachedMoodleRef? picked, float? width = 220)
    {
        picked = current;
        var preview = current is null ? "None" : MoodlesTextFormat.StripMarkup(current.StatusName);
        if (width is { } w)
            ItemWidth(w);

        if (DependencyGates.FeatureBlockedReason(plugin, DependencyId.Moodles) is { } blocked)
        {
            using (ImRaii.Disabled())
                if (ImGui.BeginCombo($"Moodle (optional)##{id}", preview))
                    ImGui.EndCombo();
            IconGlyph.WrappedColored(Theme.StatusMissing, blocked);
            return false;
        }

        var changed = false;
        if (ImGui.BeginCombo($"Moodle (optional)##{id}", preview))
        {
            if (ImGui.Selectable($"None##{id}", current is null))
            {
                picked = null;
                changed = true;
            }
            var statuses = config.MoodlesMapping.LocalCatalog.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (statuses.Count == 0)
                ImGui.TextDisabled("No scanned Moodles statuses - rescan in Settings (gear icon).");
            foreach (var status in statuses)
            {
                if (!Guid.TryParse(status.StatusId, out var statusId))
                    continue;
                if (ImGui.Selectable($"{MoodlesTextFormat.StripMarkup(status.Name)}##{id}_{status.StatusId}", current?.StatusId == statusId))
                {
                    picked = new AttachedMoodleRef { StatusId = statusId, StatusName = status.Name };
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        IconGlyph.HelpMarker("Added with this and removed when it's cleared.");
        return changed;
    }

    private void SavePermission(Action apply)
    {
        apply();
        plugin.Configuration.Save();
    }

    private static bool ImGuiCheckbox(string label, bool value, out bool newValue)
    {
        newValue = value;
        var changed = ImGui.Checkbox(label, ref newValue);
        return changed;
    }
}
