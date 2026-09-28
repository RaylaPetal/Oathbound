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

namespace Oathbound.Plugin.UI;

/// collar/ui-organization "Module content opens in its own window, not inline": one reusable window whose
/// content switches on the active module id, matching NavBar's own "a new module is one more array entry,
/// not a wider surface" scaling approach - see redesign-nav-and-modules's design.md. Everything that used to
/// render inline below CollarWindow's nav bar lives here now: every category's Owner/Sub content, the
/// guided-tutorial callout, and the shared quick-command editor.
public sealed partial class ModuleWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private string activeModule = "title";

    public ModuleWindow(Plugin plugin) : base("Oathbound###CollarModuleWindow")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(465, 520), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    public void Dispose() { }

    /// The one entry point CollarWindow's nav-click routing and TutorialDriver (via
    /// CollarWindow.SetActiveModuleForTutorial) use to pick which module this window shows.
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
    private int? selectedTitleIndex;

    private string newOutfitAlias = "";
    private int newOutfitDesignIndex;
    private AttachedMoodleRef? newOutfitMoodle;

    /// collar/attached-moodles: the Owner's moodle pick for the direct slot/item restraint being built (null =
    /// no moodle - an ad-hoc device has no Sub-side default). Saved per-command picks live on QuickCommand.
    private string? adHocMoodleOverride;
    /// The saved-command editor's moodle pick while editing an outfit command.
    private string? editingQuickMoodle;
    private bool newOutfitLocked = true;
    private int? selectedOutfitIndex;

    private string newGestureAlias = "";
    private GestureCatalogEntry? selectedAliasGesture;
    private int? selectedGestureAliasIndex;

    private string newMoodleAlias = "";
    private int newMoodleStatusIndex;
    private int? selectedMoodleAliasIndex;

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
    private int? selectedCustomTriggerIndex;

    /// Owner-side ad-hoc Custom Trigger draft (collar/custom-triggers "custom commands should also be
    /// creatable via the Owner commands menu") - separate field set from the Sub-side draft above (ct*),
    /// since these live on a different tab and build actions by NAME only rather than by picking from this
    /// client's own local catalogs (the Owner's install has no access to the Sub's WardrobeMapping/
    /// GestureMapping/MoodlesMapping/RestraintMapping - only the Sub does), mirroring the freeform
    /// "type the exact name your Sub told you" pattern the other Owner quick-command sections already use.
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
    private ApiEquipSlot? newDeviceSlot;
    private ulong? newDeviceItemId;
    private readonly RestraintRuleEditState newDeviceRuleEdit = new();
    private string? editingDeviceId;
    private string? selectedDeviceId;

    private string ownerRestraintSearch = "";
    private string subRestraintSearch = "";

    /// Owner-side ad-hoc device draft (collar/restraints "Owner-authored ad-hoc restraint device") - an
    /// optional mod-filtered slot+item picked directly, with no Sub-side captured device to reference by
    /// name. Gear is optional - a rules-only ad-hoc device leaves the mod/slot/item unset.
    private string newAdHocLabel = "";
    private readonly RestraintRuleEditState newAdHocRuleEdit = new();

    private static readonly string[] PoseNames = ["Ground Sit", "Sit", "Doze"];

    /// collar/chat-transport "Trigger-phrase command delivery over a selectable channel" - order matches
    /// the ChatChannel enum exactly, since the header combo indexes into this by (int)config.OutgoingChannel.

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

    /// collar/toy-control: Sub-only custom pattern editor working state - a new pattern under construction
    /// (or, while non-null, the id of an existing one being edited in place).
    private string toyPatternNameInput = "";
    private bool toyPatternLoopInput;
    private readonly List<PatternStep> toyPatternStepsInput = new();
    private string? toyPatternEditingId;
    private string? toyPatternError;
    private int? selectedToyPatternIndex;

    /// collar/toy-control: Sub-only trigger editor working state for a new/edited ToyTriggerRule.
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

    /// ImGui frame the Owner Sync tab was last drawn on - a gap means it was just (re)opened.
    private int lastSyncTabFrame = -10;

    /// collar/ui-organization: search text filtering the Owner's Gesture quick-command list.
    private string gestureQuickSearch = "";
    private QuickCommand? editingQuickCommand;
    private List<QuickCommand>? editingQuickList;
    private string editingQuickLabel = "";
    private string editingQuickPayload = "";
    private string editingQuickTarget = "";
    private string editingQuickOriginalTarget = "";
    private bool editingQuickTitleIsPrefix;
    private bool editingQuickOriginalTitleIsPrefix;
    /// Saved-outfit editor: lock the outfit (`outfit lock`) or just apply it (`outfit wear`).
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

    /// Owner-side per-quick-command rule editor state (collar/restraints "Owner assigns rules to a quick
    /// command"), keyed by the quick command's Label - transient UI-only state, not persisted itself (the
    /// chosen rules are saved onto the QuickCommand on "Save rules").
    private readonly HashSet<string> expandedRestraintRuleEditors = new();
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
        public bool ArmsCuffed;
        public string? ArmsCuffedAnimationId;
        public bool LegsCuffed;
        public string? LegsCuffedAnimationId;
        public bool FullBodyCuffed;
        public string? FullBodyCuffedAnimationId;
    }

    /// Shared purple window chrome (Theme.PushWindowStyle) - pushed before Begin, popped after End.
    public override void PreDraw() => Theme.PushWindowStyle();
    public override void PostDraw() => Theme.PopWindowStyle();

    public override void Draw()
    {
        DrawTutorialCallout();
        using var card = Card.Begin("moduleCard");
        var isOwner = ResolveOwnerModeView();

        // collar/ui-organization: a module window already open when its dependency disappears shows why in
        // place of its content, until the dependency is detected again (same rule as the nav tile).
        if (DependencyGates.ModuleBlockedReason(plugin, activeModule) is { } blockedReason)
        {
            IconGlyph.WrappedColored(Theme.StatusMissing, blockedReason);
            IconGlyph.WrappedDisabled("Install or enable it from /xlplugins - this window comes back on its own once it's detected. Settings shows every dependency's status.");
            return;
        }

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
                    using (Section.Begin("ctqSaved", "Saved"))
                        DrawSavedAliasCommands(canSend);
                    using (Section.Begin("ctqBuilder", "Build a bundle"))
                        DrawCustomTriggerQuickSection(canSend);
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
                if (isOwner) DrawFollowQuickSection(DrawOwnerCanSendBanner());
                else DrawFollowLeashModule();
                break;
            case "sync":
                DrawSyncTab(isOwner);
                break;
            case "reactions":
                DrawReactionsModule();
                break;
            case "permissions":
                // Only reached from the guided tutorial's Permissions step - the real controls live in Settings.
                IconGlyph.WrappedDisabled("Permissions now live in Settings, on the Permissions tab.");
                if (ImGui.SmallButton("Open Settings > Permissions"))
                    plugin.SettingsWindow.ShowPermissionsTab();
                break;
        }
    }

    /// collar/onboarding: renders the current guided-tutorial step's explanation and Next/Exit controls
    /// above the module card, driven entirely by `plugin.TutorialDriver` (see design.md's "Tutorial driver
    /// lives outside CollarWindow" decision) - this window only reads the driver's current step each frame,
    /// it never owns tutorial state itself.
    private void DrawTutorialCallout()
    {
        var driver = plugin.TutorialDriver;
        if (driver.CurrentStep is not { } step)
            return;

        var text = driver.ActiveDirection == PairingDirection.OwnerSide ? step.OwnerText : step.SubText;
        // Fixed, compact height + noScroll: Card.Begin's default (0,0) size fills all remaining window
        // space in ImGui, which left no room for the module card below and forced the whole window to
        // scroll - matching the nav bar's own fixed-height, noScroll card for the same reason.
        using var card = Card.Begin("tutorialCallout", new Vector2(0, 130), noScroll: true);
        IconGlyph.Text(FontAwesomeIcon.GraduationCap, $"Tutorial ({driver.CurrentStepNumber}/{driver.TotalSteps}): {step.TabLabel}");
        ImGui.Separator();
        ImGui.TextWrapped(text ?? "");
        if (ImGui.Button(driver.IsLastStep ? "Finish" : "Next"))
            driver.Advance();
        ImGui.SameLine();
        if (ImGui.Button("Exit tutorial"))
            driver.ExitEarly();
    }

    /// collar/ui-organization "Category tabs present role-aware content": the "no /tell target yet"
    /// warning DrawOwnerModule used to show once for the whole accordion, now shown at the top of every
    /// shared category tab's Owner-role view instead. Returns whether Send should be enabled on that tab.
    private bool DrawOwnerCanSendBanner()
    {
        var canSend = plugin.Configuration.ActivePairing is { Direction: PairingDirection.OwnerSide };
        if (!canSend)
            IconGlyph.WrappedColored(Theme.Warning, "No /tell target yet - Send is disabled until an Owner-side pairing is active (select one in the header, or pair from Settings' handshake if you have none). Copy still works any time.");
        return canSend;
    }

    /// collar/multi-pairing "Active pairing selection drives outgoing commands and role-aware views": which
    /// direction's view a shared category tab renders. The active pairing's own direction wins when one is
    /// selected; otherwise it falls back to the device's Role (Owner/Sub), and for a Switch with nothing
    /// active, the last direction it was showing.
    private bool ResolveOwnerModeView()
    {
        var config = plugin.Configuration;
        var isOwner = config.ResolveActiveDirection() == PairingDirection.OwnerSide;

        // Deliberately no config.Save() here: this only ever matters as a fallback default when no
        // pairing is active (see ResolveActiveDirection), so forcing an immediate synchronous write every
        // time it merely tracks the current pairing's own direction - e.g. once per pairing switch - would
        // double the cost of that switch for no reason. It rides along on whatever save happens next.
        if (config.Role == PluginRole.Switch && config.SwitchLastUsedOwnerView != isOwner)
            config.SwitchLastUsedOwnerView = isOwner;
        return isOwner;
    }

    /// collar/ui-organization: replaces the stale "use \"Import commands\" above" message these
    /// quick-command sections used to show back when Import commands lived in the same accordion just
    /// above them - it's on its own Sync tab now, so this jumps there directly instead of naming a
    /// location that's no longer nearby.
    private void DrawGoToSyncTabPrompt(string message)
    {
        IconGlyph.WrappedDisabled(message);
        if (ImGui.SmallButton("Go to Sync tab"))
            Show("sync");
    }
    /// collar/ui-organization "Sync tab holds catalog relay sync and import/reset": Owner gets the
    /// existing catalog relay sync + offline file fallback controls; Sub gets the offline/manual export
    /// action (see DrawSubExportSection) plus a note about the Permissions "Catalog sync (relay)" toggle -
    /// scanning itself stays in Settings, since that's about this client's own local mod setup.
    private void DrawSyncTab(bool ownerMode)
    {
        if (!ownerMode)
        {
            DrawSubExportSection();
            return;
        }

        // collar/catalog-sync "Owner opens the Sync tab": a check the moment the tab becomes visible (not every
        // frame it stays visible), skipped if the last successful check was under a minute ago.
        var frame = ImGui.GetFrameCount();
        if (frame - lastSyncTabFrame > 1 && plugin.Configuration.ActivePairing is { Direction: PairingDirection.OwnerSide } openedPairing)
            plugin.CatalogAutoSync.RequestOwnerCheck(openedPairing, force: false);
        lastSyncTabFrame = frame;

        using (Section.Begin("catalogRelay"))
            DrawCatalogRelaySection();

        using (Section.Begin("catalogFileFallback"))
        {
            if (ImGui.CollapsingHeader("Offline / legacy file fallback##catalogFileFallback"))
            {
                IconGlyph.WrappedDisabled("Use this only when relay sync is unavailable or when importing an older catalog file. Normal paired catalog updates use Cloudflare automatically.");
                DrawImportCommandsButton();
            }
        }
    }

    /// collar/ui-organization: the Sub-role view of the Sync tab. Relay sync itself is entirely the
    /// Owner's action (request refresh) with nothing for the Sub to click - what the Sub actually does is
    /// export a file for the offline/manual fallback, previously Settings' "Scan & Export" card, moved
    /// here so a Sub has one real action on their own Sync tab instead of a purely informational message.
    /// Scanning itself (rescanning designs/animations/moodles/restraint mods) stays in Settings, since
    /// that's about this client's own local Penumbra/Glamourer/Moodles setup, not catalog sync.
    /// collar/ui-organization: the Sub-role Sync tab now owns everything catalog-related end to end -
    /// relay sync explanation, scanning (moved here from Settings' former "Scanning" tab), and the
    /// offline/manual export action - rather than splitting scanning into Settings and sync/export here.
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
        IconGlyph.HelpMarker("Rescans Wardrobe, Animation, Moodles, and the explicitly shared Penumbra restraint folders. Captured item devices are left untouched.");
        scanBox.Dispose();

        using (Section.Begin("scanWardrobe"))
            DrawGatedScan(DependencyId.Glamourer, () => DrawWardrobeScanBody(config));
        using (Section.Begin("scanGesture"))
            DrawGatedScan(DependencyId.Penumbra, () => DrawGestureScanBody(config));
        using (Section.Begin("scanRestraint"))
            DrawGatedScan(DependencyId.Penumbra, () => DrawRestraintScanBody(config));
        using (Section.Begin("scanMoodles"))
            DrawGatedScan(DependencyId.Moodles, () => DrawMoodlesScanBody(config));

        // collar/catalog-sync "shared presets are copies": a custom trigger is shared as one self-contained
        // command, sent one message per action if it doesn't fit in one - a trigger with an action too long
        // for a single message is left out, and said so here.
        var tooLong = config.Aliases.CustomTriggers.Where(t => SharedPresetCommands.IsTooLongToShare(t, config)).Select(t => t.Alias).ToList();
        if (tooLong.Count > 0)
        {
            using (Section.Begin("subShareLimits", "Not shared"))
                IconGlyph.WrappedColored(Theme.Warning, $"{tooLong.Count} custom trigger(s) too long to share: {string.Join(", ", tooLong)}. Each action has to fit in one chat message - shorten the long ones (e.g. shorter restraint, animation or preset names, or chat text) to share them with your Owner.");
        }

        using var exportBox = Section.Begin("subExport", "Offline / manual export");
        IconGlyph.WrappedDisabled("Only needed if relay sync is unavailable. Export one file here to send your Owner manually.");

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

    /// collar/catalog-sync automatic sync, Sub view: what's shared, when it last went out to each Owner, and
    /// the one control the Sub has over the schedule (periodic rescans). Publishing itself needs no click.
    private void DrawSubAutoSyncInfo(PluginConfig config)
    {
        IconGlyph.WrappedDisabled("Whenever your catalog changes, it's shared with your paired Owner automatically through the Oathbound Cloudflare relay, end-to-end encrypted. No chat messages are sent. Your Owner's plugin picks it up within about an hour.");

        if (ImGuiCheckbox("Automatically rescan every hour", config.AutoRescanCatalogs, out var autoRescan))
        {
            config.AutoRescanCatalogs = autoRescan;
            config.Save();
        }
        IconGlyph.HelpMarker("Re-reads your Glamourer designs, Penumbra animation/restraint mods, and Moodles statuses at login and about once an hour, using the selections below - so new additions reach your Owner without a manual rescan. A plugin that isn't loaded is skipped and keeps its previous list.");

        if (!config.Permissions.RelayCatalogSync)
        {
            IconGlyph.WrappedColored(Theme.Warning, "\"Catalog sync (relay)\" is off in Permissions, so nothing is shared. Changes go out as soon as you turn it on.");
            return;
        }

        foreach (var pairing in config.Pairings.Where(p => p is { Direction: PairingDirection.SubSide, IsPaired: true }))
        {
            var name = pairing.PeerName ?? "your Owner";
            if (pairing.LastPublishedCatalogUnixSeconds > 0)
                IconGlyph.WrappedDisabled($"Last shared with {name}: {DateTimeOffset.FromUnixTimeSeconds(pairing.LastPublishedCatalogUnixSeconds).LocalDateTime:g}.");
            else
                IconGlyph.WrappedDisabled($"Not shared with {name} yet - it goes out once their plugin has checked in (Owners on an older plugin can still use Request refresh).");
        }
    }

    /// collar/ui-organization "Partly dependent features are disabled inline": each catalog source is scanned from
    /// its own plugin, so one missing plugin disables only that source's section.
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
        IconGlyph.WrappedDisabled("No folders = all saved designs. Add folders only when you want to restrict the catalog. Outfit aliases live in the main window's Outfit tab.");
        IconGlyph.HelpMarker("With folders configured, only designs inside those Glamourer design-browser folder prefixes are scanned. Clear every folder to scan all saved designs.");

        DrawAllowlistBody(config.WardrobeFolderAllowlist, ref newWardrobeAllowlistFolder, "wardrobe");

        ImGui.Spacing();
        if (ImGui.Button("Rescan wardrobe"))
            plugin.OutfitCommand.Rescan();
        IconGlyph.HelpMarker("Re-reads your saved Glamourer designs. An empty folder list includes all designs; otherwise only matching folders are included.");

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

        if (ImGui.SmallButton("Copy names##wardrobe"))
            ImGui.SetClipboardText(string.Join("\n", wardrobe.LocalDesigns.Values.Select(d => d.Name)));
        IconGlyph.HelpMarker("Copies the list below as plain text, one design per line - paste it to your Owner (Discord, voice-to-text, etc) so they know exactly what names they can reference with a direct override (\"outfit lock <name>\").");

        using var _ = ImRaii.Child("wardrobeCatalog", new Vector2(0, 80), true);
        foreach (var entry in wardrobe.LocalDesigns.Values)
            ImGui.BulletText(entry.Name);
    }

    private void DrawGestureScanBody(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.TheaterMasks, "Animation mods to scan");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("No folders and no selected mods scans everything. Folders select a union; explicit mods narrow that union.");
        DrawPenumbraFolderPicker("Animation folders", config.SelectedGestureFolders, config);
        ImGui.InputTextWithHint("##gestureModSearch", "Search mod names...", ref gestureModSearch, 128);
        var installed = plugin.GestureCommand.GetInstalledMods();
        using (ImRaii.Child("gestureModPicker", new Vector2(0, 180), true))
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
        IconGlyph.HelpMarker("Reads every installed mod when none are selected, or only explicit selections otherwise. Disabled mods remain eligible and are enabled temporarily when played.");

        DrawGestureScanFeedback();
    }

    private void DrawRestraintScanBody(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.Lock, "Shared Penumbra restraints");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Only options below folders selected here are shared with your Owner. No folders means no Penumbra restraints are shared.");
        DrawPenumbraFolderPicker("Restraint folders", config.SelectedRestraintFolders, config);
        if (ImGui.Button("Rescan restraints")) plugin.RestraintCommand.RescanCatalog();
        var command = plugin.RestraintCommand;
        if (command.LastScanError is { } error) IconGlyph.WrappedColored(Theme.Danger, error);
        else if (command.LastScanTotalMods is not null)
            IconGlyph.WrappedColored(config.RestraintMapping.LocalCatalog.Count > 0 ? Theme.Success : Theme.Warning,
                $"Matched {command.LastScanMatchedMods} mod(s); found {config.RestraintMapping.LocalCatalog.Count} restraint option(s).");
        if (config.SelectedRestraintFolders.Count == 0)
            IconGlyph.WrappedColored(Theme.Warning, "No folders selected: the shared Penumbra restraint catalog is empty.");
        using var child = ImRaii.Child("restraintCatalogPreview", new Vector2(0, 100), true);
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
            ImGui.TextUnformatted(missing ? $"{folder} (missing)" : folder);
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove##{label}{folder}")) { selected.Remove(folder); config.Save(); }
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

        if (ImGui.SmallButton("Copy names##gesture"))
        {
            ImGui.SetClipboardText(plugin.GestureCommand.ExportCatalog());
        }
        IconGlyph.HelpMarker("Copies versioned entries containing the mod, animation option, tied trigger, and selections for the Owner's Add from clipboard action.");

        using var _ = ImRaii.Child("gestureCatalog", new Vector2(0, 80), true);
        foreach (var entry in gestureMapping.LocalCatalog.Values)
        {
            ImGui.BulletText(entry.DisplayLabel);
        }
    }

    /// collar/moodles: no folder allowlist, unlike Wardrobe/Gesture - Moodles statuses have no folder-
    /// organization concept, every registered status is eligible (design.md's decision). Reads individual
    /// statuses (buffs/debuffs) rather than bundled presets, so the Owner can command a single status.
    private void DrawMoodlesScanBody(PluginConfig config)
    {
        IconGlyph.Text(FontAwesomeIcon.TheaterMasks, "Moodles status scan");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Reads your own registered Moodles statuses (buffs/debuffs) directly - nothing to allowlist. Moodles apply/clear commands live in the main window's Moodles tab.");

        if (ImGui.Button("Rescan Moodles statuses"))
            plugin.MoodlesCommand.Rescan();
        IconGlyph.HelpMarker("Re-reads your registered statuses from your own Moodles plugin - run this after adding a new status before it'll show up for your Owner to reference.");

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

        if (ImGui.SmallButton("Copy names##moodles"))
            ImGui.SetClipboardText(string.Join("\n", moodlesMapping.LocalCatalog.Values.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x)));
        IconGlyph.HelpMarker("Copies the list below as plain text, one status per line - paste it to your Owner (Discord, voice-to-text, etc) so they know exactly what names they can reference with \"moodle apply <name>\".");

        using var _ = ImRaii.Child("moodlesCatalog", new Vector2(0, 80), true);
        foreach (var entry in moodlesMapping.LocalCatalog.Values)
        {
            ImGui.PushID(entry.StatusId);
            ImGui.BulletText(entry.Name);
            ImGui.PopID();
        }
    }

    /// Wardrobe folder scopes use "empty = all" semantics and prefix matching when narrowed.
    private void DrawAllowlistBody(List<string> allowlist, ref string newFolderInput, string idSuffix)
    {
        for (var i = 0; i < allowlist.Count; i++)
        {
            ImGui.PushID(i);
            ImGui.BulletText(allowlist[i]);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                allowlist.RemoveAt(i);
                plugin.Configuration.Save();
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }

        ImGui.InputText($"##newFolder_{idSuffix}", ref newFolderInput, 128);
        ImGui.SameLine();
        if (ImGui.Button($"Add folder##{idSuffix}") && newFolderInput.Length > 0)
        {
            allowlist.Add(newFolderInput);
            plugin.Configuration.Save();
            newFolderInput = "";
        }
    }
    /// Drawn by SettingsWindow's Permissions tab (collar/ui-organization "Permissions live in a Settings tab").
    /// collar/ui-organization "Permissions flag categories whose dependency is missing": a red note only - the
    /// toggle stays usable so a permission can be set ahead of installing the plugin.
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
            IconGlyph.HelpMarker("Lets a paired Owner temporarily enable a selected animation mod and immediately play its tied gesture. Disable this permission at any time to reject commands.");
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
        IconGlyph.HelpMarker("Lets your configured collar item apply and lock automatically when you accept a pairing (Collar tab). Configuring an item alone does nothing without this enabled too.");

        if (ImGuiCheckbox("Moodles", permissions.Moodles, out var newMoodles))
            SavePermission(() => permissions.Moodles = newMoodles);
        IconGlyph.HelpMarker("Lets a paired Owner apply or clear a Moodle (status effect) from your own registered statuses via a trigger tell - applies immediately, no confirmation queue.");
        DrawPermissionDependencyNote(DependencyId.Moodles);

        group.Dispose();
        group = Section.Begin("permCatalog", "Catalog sync");
        if (ImGuiCheckbox("Catalog sync (relay)", permissions.RelayCatalogSync, out var newRelayCatalogSync))
            SavePermission(() => permissions.RelayCatalogSync = newRelayCatalogSync);
        IconGlyph.HelpMarker("Lets your paired Owner request an automatic, end-to-end encrypted refresh of your exported catalog (at most once every four hours) instead of you sending a file manually. The relay never sees the plaintext. Off by default; manual file export/import always remains available regardless of this setting.");

        group.Dispose();
        group = Section.Begin("permCustomChat", "Custom chat (own acknowledgement)");
        if (!config.CustomChatAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Custom chat messages require their own dedicated acknowledgement on the ToS tab first - separate from the general ToS checkbox.");

        using (ImRaii.Disabled(!config.CustomChatAcknowledged))
        {
            if (ImGuiCheckbox("Custom chat messages", permissions.CustomChatMessages, out var newCustomChat))
                SavePermission(() => permissions.CustomChatMessages = newCustomChat);
            IconGlyph.HelpMarker("Lets a Custom Trigger's chat action send arbitrary text to any channel (including public chat) as your own character. A materially broader automation surface than Animation's closed set of self-targeting commands - see the README's Automation risk section.");
        }

        group.Dispose();
        group = Section.Begin("permToyControl", "Toy control (own acknowledgement)");
        if (!config.ToyControlAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Toy control requires its own dedicated acknowledgement on the ToS tab first - separate from every other checkbox.");

        using (ImRaii.Disabled(!config.ToyControlAcknowledged))
        {
            if (ImGuiCheckbox("Toy control", permissions.ToyControl, out var newToyControl))
                SavePermission(() => permissions.ToyControl = newToyControl);
            IconGlyph.HelpMarker("Lets a paired Owner remotely vibrate or run a pattern on a toy you have connected via Intiface Central, until it stops itself, you stop it, or a maximum duration elapses. Directly actuates a physical device, not just in-game state - the single heaviest automation footprint in this plugin - see the README's Automation risk section.");
        }
        group.Dispose();
    }

    private void DrawTitleModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.Heading, "Title Aliases");
        ImGui.Separator();

        using (Section.Begin("titleFixed", "Fixed words"))
        {
            DrawFixedWord("Clear title", ControlWords.ClearTitle);
            IconGlyph.HelpMarker("The fixed word that removes your current Honorific title - separate from the named aliases below, which each apply a specific title. Not renamable, so your Owner always knows it.");
        }

        var titles = config.Aliases.Titles;
        using (Section.Begin("titleAliases", "Your title aliases"))
        {
            if (titles.Count == 0)
            {
                IconGlyph.WrappedDisabled("No title aliases yet - add one below.");
            }
            else
            {
                var labels = titles.Select(t => t.Alias).ToArray();
                var i = DrawItemSelector("##titleSelect", labels, ref selectedTitleIndex);
                var t = titles[i];
                ImGui.Indent();
                ImGui.TextUnformatted($"\"{t.Text}\" ({(t.IsPrefix ? "prefix" : "suffix")})");
                ImGui.Unindent();
                if (ImGui.SmallButton("Remove"))
                {
                    titles.RemoveAt(i);
                    config.Save();
                    selectedTitleIndex = null;
                }
            }
        }

        using (Section.Begin("titleAdd", "Add a title alias"))
        {
            ImGui.InputText("Alias##newTitle", ref newTitleAlias, 32);
            IconGlyph.HelpMarker("Short word the Owner types after the trigger phrase to apply this title, e.g. \"command goodgirl\".");
            ImGui.InputText("Title text##newTitle", ref newTitleText, 64);
            IconGlyph.HelpMarker("The exact title text applied via Honorific.");
            ImGui.Checkbox("Prefix (not suffix)##newTitle", ref newTitleIsPrefix);
            IconGlyph.HelpMarker("Show the title before your name instead of after it.");
            ImGui.ColorEdit3("Color##newTitle", ref newTitleColor);
            IconGlyph.HelpMarker("Honorific title color.");
            DrawGlowPicker("newTitle", ref newTitleHasGlow, ref newTitleGlow);
            DrawReservedWordWarning(newTitleAlias);
            if (ImGui.Button("Add title alias") && newTitleAlias.Length > 0 && newTitleText.Length > 0 && !IsReserved(newTitleAlias))
            {
                titles.Add(new TitleAliasDefinition { Alias = newTitleAlias, Text = newTitleText, IsPrefix = newTitleIsPrefix, Color = newTitleColor, Glow = newTitleHasGlow ? newTitleGlow : null });
                config.Save();
                newTitleAlias = "";
                newTitleText = "";
            }
        }
    }

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
        using (Section.Begin("outfitAliases", "Your outfit aliases"))
        {
            if (outfits.Count == 0)
            {
                IconGlyph.WrappedDisabled("No outfit aliases yet - add one below.");
            }
            else
            {
                var labels = outfits.Select(o => o.Alias).ToArray();
                var i = DrawItemSelector("##outfitSelect", labels, ref selectedOutfitIndex);
                var o = outfits[i];
                ImGui.Indent();
                ImGui.TextUnformatted($"{o.DesignName} ({(o.Locked ? "locked" : "unlocked")})");
                if (DrawAttachedMoodlePicker($"outfitAlias_{i}", o.AttachedMoodle, config, out var aliasMoodle))
                {
                    o.AttachedMoodle = aliasMoodle;
                    config.Save();
                }
                ImGui.Unindent();
                if (ImGui.SmallButton("Remove"))
                {
                    outfits.RemoveAt(i);
                    config.Save();
                    selectedOutfitIndex = null;
                }
            }
        }

        var designs = config.WardrobeMapping.LocalDesigns.Values.ToList();
        if (designs.Count == 0)
        {
            IconGlyph.WrappedDisabled("No scanned designs yet - rescan in Settings (gear icon) first.");
            return;
        }

        var designNames = designs.Select(d => d.Name).ToArray();
        using (Section.Begin("outfitAdd", "Add an outfit alias"))
        {
            newOutfitDesignIndex = Math.Clamp(newOutfitDesignIndex, 0, designNames.Length - 1);
            ImGui.InputText("Alias##newOutfit", ref newOutfitAlias, 32);
            IconGlyph.HelpMarker("Short word the Owner types after the trigger phrase to apply this outfit.");
            ImGui.Combo("Design##newOutfit", ref newOutfitDesignIndex, designNames, designNames.Length);
            IconGlyph.HelpMarker("Which scanned Glamourer design this alias applies - any design inside your allowlisted folders (Settings) is fair game, no separate approval step. Rescan in Settings if the one you want isn't listed.");
            if (DrawAttachedMoodlePicker("newOutfit", newOutfitMoodle, config, out var pickedOutfitMoodle, width: null))
                newOutfitMoodle = pickedOutfitMoodle;
            ImGui.Checkbox("Lock##newOutfit", ref newOutfitLocked);
            IconGlyph.HelpMarker("Lock the design's own equipment slots after applying, so only this plugin's release action can change them - every other slot stays freely editable.");
            DrawReservedWordWarning(newOutfitAlias);
            if (ImGui.Button("Add outfit alias") && newOutfitAlias.Length > 0 && !IsReserved(newOutfitAlias))
            {
                var design = designs[newOutfitDesignIndex];
                outfits.Add(new OutfitAliasDefinition
                {
                    Alias = newOutfitAlias,
                    DesignId = design.DesignId,
                    DesignName = design.Name,
                    Locked = newOutfitLocked,
                    AttachedMoodle = newOutfitMoodle,
                });
                config.Save();
                newOutfitAlias = "";
                newOutfitMoodle = null;
            }
        }
    }

    /// collar/restraints: captures a single equipped gear piece (Wrists, Body, etc. - any of the 10
    /// lockable slots) directly from what's currently equipped, the same capture mechanism CollarState uses
    /// for the collar item, as a named restraint device carrying restriction rules - no scan/design library
    /// involved. Then create Sub-alias entries that toggle them (RestraintCommand.Toggle).
    private void DrawRestraintsModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.Handcuffs, "Restraints");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Choose scanned Penumbra mods and configure the restraints you want to share. Your Owner receives both these ready-made restraints and the complete scanned mod library for creating their own.");
        IconGlyph.WrappedDisabled("Owner force-release is always `restraint unlock`. Each restraint's alias toggles it on and off when your Owner sends it on its own, and force-applies it after `restraint lock`.");

        DrawSubModRestraints(config);

        // Rules-only restraints: a named set of restriction rules (forced pose, walk-only, gagged, action block - cuffs
        // are device restrictions, so they live on the mod restraints above) with no
        // gear of its own - gear-carrying restraints come from the shared Penumbra mods above. Devices captured
        // with gear by older versions still work and keep their gear when edited here (the draft carries the
        // device's existing slot/item through untouched); there's just no way to pick new gear.
        var devices = config.RestraintMapping.Devices.Values.ToList();
        if (devices.Count > 0)
        {
            var selectedBox = Section.Begin("capturedDevices", "Rules-only restraints");
            var selDeviceIndex = selectedDeviceId is { } selId ? devices.FindIndex(d => d.Id == selId) : -1;
            var indexHolder = selDeviceIndex < 0 ? (int?)null : selDeviceIndex;
            var labels = devices.Select(d => $"{d.Name}{(plugin.RestraintCommand.IsActive(d.Id) ? "  • Active" : "")}").ToArray();
            var i = DrawItemSelector("##deviceSelect", labels, ref indexHolder);
            var device = devices[i];
            selectedDeviceId = device.Id;

            ImGui.PushID($"device_{device.Id}");
            IconGlyph.WrappedDisabled($"Rules: {string.Join(" · ", device.Rules.Select(CommandPresentation.Rule))}");
            if (device.ItemId is { } deviceItemId)
                IconGlyph.WrappedDisabled($"Gear (from an older version): {device.Slot} · {GetItemName(deviceItemId)}");
            IconGlyph.WrappedDisabled($"Moodle: {(device.AttachedMoodle is { } m ? MoodlesTextFormat.StripMarkup(m.StatusName) : "none")}");
            var staleAnimation = device.Rules.Any(r =>
                r.Kind is RestraintRuleKind.ArmsCuffed or RestraintRuleKind.LegsCuffed or RestraintRuleKind.FullBodyCuffed
                && (string.IsNullOrWhiteSpace(r.AnimationId) || !config.GestureMapping.LocalCatalog.ContainsKey(r.AnimationId)));
            if (staleAnimation)
                IconGlyph.WrappedColored(Theme.Warning, "A cuff animation is stale. Choose Edit and select the animation again before using this restraint.");

            if (ImGui.SmallButton("Edit"))
                LoadDeviceDraft(device);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                plugin.RestraintCommand.RemoveDevice(device.Id);
                selectedDeviceId = null;
            }
            ImGui.PopID();
            selectedBox.Dispose();
        }

        var formBox = Section.Begin("deviceForm", editingDeviceId is null ? "New rules-only restraint" : "Edit rules-only restraint");
        ImGui.InputText("Alias##newDevice", ref newDeviceName, 32);
        IconGlyph.HelpMarker("The word your Owner uses for this restraint. For example, with an alias of \"armcuffs\": sending \"armcuffs\" toggles it on/off, and \"restraint lock armcuffs\" force-applies it.");
        DrawReservedWordWarning(newDeviceName);
        if (DrawAttachedMoodlePicker("newDevice", newDeviceMoodle, config, out var pickedDeviceMoodle, width: null))
            newDeviceMoodle = pickedDeviceMoodle;

        Section.SubHeading("Restrictions");
        DrawRestraintRuleCheckboxes(newDeviceRuleEdit, "newDevice", allowCustomizePreset: true, rulesOnly: true);

        var hasAnyRule = HasAnyRule(newDeviceRuleEdit);
        var boundAnimationsConfigured = BoundAnimationsConfigured(newDeviceRuleEdit);
        if (hasAnyRule && !boundAnimationsConfigured)
            IconGlyph.WrappedColored(Theme.Warning, "Choose an animation for every checked Arms/Legs/Full Body Cuffed rule before saving.");

        var duplicateDeviceName = devices.Any(d => d.Id != editingDeviceId &&
            string.Equals(d.Name, newDeviceName.Trim(), StringComparison.OrdinalIgnoreCase))
            || config.RestraintMapping.ConfiguredMods.Any(m => string.Equals(m.Alias.Trim(), newDeviceName.Trim(), StringComparison.OrdinalIgnoreCase));
        // Only measured once the draft is complete - an unfinished rule (e.g. a mod forced pose with no
        // animation picked yet) has nothing meaningful to measure, and Save is disabled for it anyway.
        var safeDeviceCommand = !hasAnyRule || !boundAnimationsConfigured
            || CommandSelector.Fits(RestraintCommand.BuildLockCommand(newDeviceName.Trim(), ToRules(newDeviceRuleEdit)));
        if (duplicateDeviceName)
            IconGlyph.WrappedColored(Theme.Warning, "Another restraint already uses this alias.");
        if (!safeDeviceCommand)
            IconGlyph.WrappedColored(Theme.Warning, "This restraint name and rule set are too long for a safe command.");
        ImGui.Spacing();
        ImGui.Separator();
        using (ImRaii.Disabled(newDeviceName.Trim().Length == 0 || IsReserved(newDeviceName) || !hasAnyRule || !boundAnimationsConfigured || duplicateDeviceName || !safeDeviceCommand))
        {
            if (ImGui.Button(editingDeviceId is null ? "Add restraint" : "Save restraint"))
            {
                var rules = ToRules(newDeviceRuleEdit);

                // New ones never carry gear; an edited older device keeps whatever gear it already had.
                var saved = editingDeviceId is null
                    ? plugin.RestraintCommand.CaptureDeviceFromItem(null, null, newDeviceName, rules, newDeviceMoodle)
                    : SaveDeviceDraft(newDeviceSlot, newDeviceItemId, rules);
                if (saved)
                {
                    ResetDeviceDraft();
                }
            }
        }
        if (editingDeviceId is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel##editDevice"))
                ResetDeviceDraft();
        }
        formBox.Dispose();
    }

    private void DrawSubModRestraints(PluginConfig config)
    {
        var configured = config.RestraintMapping.ConfiguredMods;
        using (Section.Begin("detectedRestraintMods", "Detected restraint mods"))
        {
            ImGui.InputTextWithHint("##subRestraintSearch", "Search scanned restraint mods...", ref subRestraintSearch, 128);
            using (ImRaii.Child("subRestraintModBrowser", new Vector2(0, 130), true))
            {
                foreach (var entry in config.RestraintMapping.LocalCatalog.Values
                             .Where(x => string.IsNullOrWhiteSpace(subRestraintSearch) || x.ModName.Contains(subRestraintSearch.Trim(), StringComparison.OrdinalIgnoreCase))
                             .OrderBy(x => x.ModName))
                {
                    ImGui.TextUnformatted(entry.ModName);
                    ImGui.SameLine();
                    var alreadyConfiguredCount = configured.Count(x => x.CatalogId == entry.Id);
                    if (ImGui.SmallButton($"{(alreadyConfiguredCount > 0 ? "Choose again" : "Choose")}##subRestraint_{entry.Id}"))
                    {
                        // A mod can be configured more than once with different restriction rules (collar/
                        // restraints "create a mod restraint for the same mod") - each gets its own name so the
                        // Sub can tell entries for the same mod apart in the list below.
                        var name = alreadyConfiguredCount == 0 ? entry.ModName : $"{entry.ModName} ({alreadyConfiguredCount + 1})";
                        var created = new ConfiguredModRestraint { CatalogId = entry.Id, Name = name };
                        configured.Add(created);
                        var key = $"submod:{created.Id}";
                        expandedRestraintRuleEditors.Add(key);
                        restraintRuleEdits[key] = new RestraintRuleEditState();
                        config.Save();
                    }
                }
            }
        }

        using var configuredBox = Section.Begin("configuredRestraintMods", "My configured mod restraints");
        if (configured.Count == 0)
        {
            IconGlyph.WrappedDisabled("Choose a detected mod above, then assign its restriction rules.");
            return;
        }
        foreach (var created in configured.ToArray())
        {
            var key = $"submod:{created.Id}";
            var missing = !config.RestraintMapping.LocalCatalog.ContainsKey(created.CatalogId);
            ImGui.TextUnformatted(created.Name);
            ImGui.SameLine();
            if (ImGui.SmallButton($"{(expandedRestraintRuleEditors.Contains(key) ? "Close" : "Configure")}##{key}"))
            {
                if (!expandedRestraintRuleEditors.Remove(key))
                {
                    expandedRestraintRuleEditors.Add(key);
                    restraintRuleEdits[key] = FromRules(created.Rules);
                }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton($"Remove##{key}"))
            {
                configured.Remove(created);
                expandedRestraintRuleEditors.Remove(key);
                restraintRuleEdits.Remove(key);
                config.Save();
                continue;
            }
            if (missing) IconGlyph.WrappedColored(Theme.Warning, "This mod is outside the latest scan and will not be exported.");
            if (expandedRestraintRuleEditors.Contains(key) && restraintRuleEdits.TryGetValue(key, out var edit))
            {
                using (Section.Begin(key))
                {
                    Section.Heading("Name, alias & item");
                    var nameBuffer = created.Name;
                    if (ImGui.InputText($"Name##{key}", ref nameBuffer, 80) && nameBuffer.Trim().Length > 0)
                    {
                        created.Name = nameBuffer;
                        config.Save();
                    }
                    IconGlyph.HelpMarker("Your own label for this configured restraint - rename it so you can tell entries for the same mod apart.");
                    var aliasBuffer = created.Alias;
                    if (ImGui.InputTextWithHint($"Alias##{key}", "optional", ref aliasBuffer, 32))
                    {
                        created.Alias = aliasBuffer.Trim();
                        config.Save();
                    }
                    IconGlyph.HelpMarker("Optional short word for your Owner: sending it on its own toggles this restraint on/off, and \"restraint lock <alias>\" force-applies it. Leave empty if your Owner only uses the restraint from their imported list.");
                    DrawReservedWordWarning(created.Alias);
                    if (created.Alias.Length > 0
                        && (config.RestraintMapping.ConfiguredMods.Any(m => m != created && string.Equals(m.Alias.Trim(), created.Alias, StringComparison.OrdinalIgnoreCase))
                            || config.RestraintMapping.Devices.Values.Any(d => string.Equals(d.Name.Trim(), created.Alias, StringComparison.OrdinalIgnoreCase))))
                        IconGlyph.WrappedColored(Theme.Warning, "Another restraint already uses this alias - only one of them will respond to it.");
                    ImGui.TextUnformatted($"Glamourer item: {(created.ItemId is { } equippedItem ? GetItemName(equippedItem) : "(none chosen)")}");
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Choose item...##{key}"))
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

                    ImGui.Spacing();
                    ImGui.Separator();
                    var valid = created.ItemId > 0 && GlamourerIpc.GetItemSlot((uint)created.ItemId.Value) is not null && HasAnyRule(edit) && BoundAnimationsConfigured(edit);
                    using (ImRaii.Disabled(!valid || missing))
                    if (ImGui.Button($"Save restraint##{key}"))
                    {
                        created.Rules = ToRules(edit);
                        config.Save();
                        expandedRestraintRuleEditors.Remove(key);
                        restraintRuleEdits.Remove(key);
                    }
                }
            }
        }
    }

    private static string PoseName(int poseModeId) => poseModeId is >= 1 and <= 3 ? PoseNames[poseModeId - 1] : "unknown";

    private static bool HasAnyRule(RestraintRuleEditState edit) =>
        edit.ForcedPose || edit.WalkOnly || edit.ActionBlock || edit.Gagged || edit.ArmsCuffed || edit.LegsCuffed || edit.FullBodyCuffed;

    /// Gagged's animation is deliberately excluded here - unlike Arms/Legs/Full Body Cuffed (which are
    /// pure animation-hold rules with no effect at all if unconfigured), Gagged's chat-garble restriction
    /// always applies on its own, so its animation stays optional even while the rule is checked.
    private bool BoundAnimationsConfigured(RestraintRuleEditState edit)
    {
        bool Contains(string id) => ResolveOwnerModeView()
            ? plugin.Configuration.GestureMapping.ImportedPeerCatalog.ContainsKey(id)
            : plugin.Configuration.GestureMapping.LocalCatalog.ContainsKey(id);
        bool Valid(bool enabled, string? id) => !enabled || id is not null && Contains(id);
        return Valid(edit.ArmsCuffed, edit.ArmsCuffedAnimationId)
            && Valid(edit.LegsCuffed, edit.LegsCuffedAnimationId)
            && Valid(edit.FullBodyCuffed, edit.FullBodyCuffedAnimationId)
            && Valid(edit.ForcedPose && edit.ForcedPoseIsMod, edit.ForcedPoseAnimationId);
    }

    /// collar/ui-organization "Restraint rule checkboxes are laid out two per row": shared by the Sub's
    /// device-capture editor, the Owner's per-quick-command editor, and the Owner's ad-hoc device editor -
    /// one `ImGui.Columns(2)` block per row keeps each checkbox's own dependent controls (pose combo,
    /// bound-animation picker) attached underneath it within its own column, regardless of how tall the
    /// other column's content is.
    private static readonly string[] ForcedPoseSourceNames = ["Vanilla pose", "Animation mod"];

    /// collar/restraints "Unified restriction toggle list": a single flat list of the seven restriction
    /// toggles - Forced Pose, Arms Cuffed, Legs Cuffed, Walk-only, Gagged, Fully Restrain, Action Block -
    /// replacing the old "Restraints" vs "Restrictions" header split (there was never a real behavioral
    /// distinction between them). Every rule still lands in the same single `RestraintRuleEditState`/
    /// `List<RestraintRuleAssignment>` this method's callers already share; only the layout changed.
    /// `allowCustomizePreset` is true only for the Sub's own editors (device capture, mod configuration) -
    /// an Owner-side editor (ad-hoc device, per-quick-command rules) never shows the Customize+ picker,
    /// since only the Sub's own client can enumerate the Sub's local Customize+ profiles.
    /// `rulesOnly` is for a restraint with no gear or mod of its own (the Sub's rules-only restraint form, the
    /// Owner's ad-hoc rules-only restraint): Arms Cuffed, Legs Cuffed and Fully Restrain are device
    /// restrictions, so they're left out - unless one is already checked on an older saved restraint, which
    /// still shows it so it can be unchecked (collar/restraint-restrictions "Unified restriction toggle list").
    private void DrawRestraintRuleCheckboxes(RestraintRuleEditState edit, string idSuffix, bool allowCustomizePreset = false, bool rulesOnly = false)
    {
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
        if (!rulesOnly || edit.ArmsCuffed)
            cells.Add(() =>
            {
                DrawBoundAnimationPicker("Arms Cuffed", ref edit.ArmsCuffed, edit.ArmsCuffedAnimationId, id => edit.ArmsCuffedAnimationId = id, $"{idSuffix}Arms");
                IconGlyph.HelpMarker("Temporarily activates the chosen animation and holds you in it until released, without affecting movement or actions.");
            });
        if (!rulesOnly || edit.LegsCuffed)
            cells.Add(() =>
            {
                DrawBoundAnimationPicker("Legs Cuffed", ref edit.LegsCuffed, edit.LegsCuffedAnimationId, id => edit.LegsCuffedAnimationId = id, $"{idSuffix}Legs");
                IconGlyph.HelpMarker("Temporarily activates the chosen animation and holds you in it until released, without affecting movement or actions.");
            });
        cells.Add(() =>
        {
            ImGui.Checkbox($"Walk-only##{idSuffix}", ref edit.WalkOnly);
            IconGlyph.HelpMarker("Forces walking and blocks running, without blocking directional movement input.");
        });
        cells.Add(() => DrawGaggedPicker(edit, idSuffix, allowCustomizePreset));
        if (!rulesOnly || edit.FullBodyCuffed)
            cells.Add(() =>
            {
                DrawBoundAnimationPicker("Fully Restrain", ref edit.FullBodyCuffed, edit.FullBodyCuffedAnimationId, id => edit.FullBodyCuffedAnimationId = id, $"{idSuffix}FullBody");
                IconGlyph.HelpMarker("Temporarily activates the chosen animation and holds you in it, and fully blocks movement input, until released - a fully custom-animation counterpart to forced pose.");
            });
        cells.Add(() =>
        {
            ImGui.Checkbox($"Action block##{idSuffix}", ref edit.ActionBlock);
            IconGlyph.HelpMarker("Blocks hotbar action/skill usage until released, without affecting movement.");
        });

        // Two per row, one ImGui.Columns block per row, so each toggle's own dependent controls stay under it
        // regardless of how tall the other column is. The full list keeps its original order/layout; the
        // rules-only list re-flows without empty cells.
        for (var i = 0; i < cells.Count; i += 2)
        {
            ImGui.Columns(2, $"restraintRules_{idSuffix}_row{i / 2 + 1}", false);
            cells[i]();
            ImGui.NextColumn();
            if (i + 1 < cells.Count)
                cells[i + 1]();
            ImGui.Columns(1);
        }
    }

    /// collar/restraints "Gagged toggle chat restriction"/"Optional Customize+ preset on Gagged": the
    /// chat-garble restriction always applies once checked; the animation and (Sub-side only) Customize+
    /// preset are both independently optional cosmetic layers on top of it, each individually clearable
    /// without unchecking the whole rule.
    private void DrawGaggedPicker(RestraintRuleEditState edit, string idSuffix, bool allowCustomizePreset)
    {
        ImGui.Checkbox($"Gagged##{idSuffix}", ref edit.Gagged);
        IconGlyph.HelpMarker("Garbles your outgoing chat text - the actual transmitted message, not just your own display - until released. See the README's Automation risk section before enabling.");
        if (!edit.Gagged)
            return;

        ImGui.Indent();
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

    /// collar/restraints "Arms Cuffed and Legs Cuffed rules...": a checkbox plus the same searchable
    /// animation picker `collar/gesture`'s "Add animation..." button already opens (AnimationPickerWindow),
    /// reused here for the Sub's device-capture UI and the Owner's per-quick-command rule editor alike.
    /// `onChosen` writes back to whatever field/property backs `currentAnimationId` - a plain delegate
    /// rather than `ref string?`, since the picker's selection callback fires on a later frame and a ref
    /// parameter can't be captured by that closure.
    private void DrawBoundAnimationPicker(string label, ref bool enabled, string? currentAnimationId, Action<string> onChosen, string idSuffix)
    {
        ImGui.Checkbox($"{label}##{idSuffix}", ref enabled);
        if (!enabled)
            return;

        ImGui.Indent();
        DrawAnimationChooser(currentAnimationId, onChosen, idSuffix);
        ImGui.Unindent();
    }

    /// The animation-choosing half of `DrawBoundAnimationPicker`, without its own enabling checkbox - used
    /// where a rule already has its own checkbox with a separate vanilla/mod source toggle (mod-sourced
    /// Forced Pose), so this only ever gets called once that toggle has already picked "mod".
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

        // The button itself shows what's chosen (compact name, full name + trigger on hover) and clicking it
        // opens the picker to change it - no separate "Change..." button with the choice as loose text below.
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

    /// A button whose label is the current choice (or a "Choose..." prompt when there's none) - clicking it
    /// opens that choice's picker. Stretches to the available width (minus room for a clear button) and
    /// shows a stale choice in the warning color.
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

    /// collar/restraints "Optional Customize+ preset on Gagged": the Customize+ counterpart to
    /// DrawAnimationChooser, but against a flat list of the Sub's own Customize+ profiles (CustomizePresetPickerWindow)
    /// instead of the mod/group/option/trigger tree AnimationPickerWindow shows - Customize+ profiles have
    /// no such hierarchy and are never shared via catalog sync (design.md's Non-Goals), so there is no
    /// Owner/imported-mode branch here at all.
    private void DrawCustomizePresetChooser(string? currentPresetId, string? currentPresetLabel, Action<string?, string?> onChosen, string idSuffix)
    {
        // collar/ui-organization "Partly dependent features are disabled inline": only this option needs Customize+.
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

    private void DrawGestureModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.TheaterMasks, "Animation");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Select animation mods and scan them in Settings, then define aliases from their named options here.");

        var gestures = config.Aliases.Gestures;
        using (Section.Begin("gestureAliases", "Your animation aliases"))
        {
            if (gestures.Count == 0)
            {
                IconGlyph.WrappedDisabled("No animation aliases yet - add one below.");
            }
            else
            {
                var labels = gestures.Select(g => g.Alias).ToArray();
                var i = DrawItemSelector("##gestureSelect", labels, ref selectedGestureAliasIndex);
                var g = gestures[i];
                var invalid = string.IsNullOrEmpty(g.GestureId) || !config.GestureMapping.LocalCatalog.ContainsKey(g.GestureId);
                ImGui.Indent();
                ImGui.TextWrapped($"{(g.AnimationName.Length > 0 ? g.AnimationName : g.EmoteName)} ({g.ModName}){(invalid ? " — rescan/recreate required" : "")}");
                ImGui.Unindent();
                if (ImGui.SmallButton("Remove"))
                {
                    gestures.RemoveAt(i);
                    config.Save();
                    selectedGestureAliasIndex = null;
                }
            }
        }

        var allOptions = config.GestureMapping.LocalCatalog.Values.Where(e => e.Trigger != null).ToList();
        if (allOptions.Count == 0)
        {
            IconGlyph.WrappedDisabled("No scanned/resolved gestures yet - rescan in Settings (gear icon) first.");
            return;
        }

        using (Section.Begin("gestureAdd", "Add an animation alias"))
        {
            ImGui.InputText("Alias##newGesture", ref newGestureAlias, 32);
            IconGlyph.HelpMarker("Short word the Owner types. With Animation permission enabled, this immediately enables the chosen animation temporarily and plays its tied trigger.");
            DrawReservedWordWarning(newGestureAlias);

            if (ImGui.Button(selectedAliasGesture is null ? "Add animation..." : "Change animation..."))
                plugin.AnimationPickerWindow.Open(entry => selectedAliasGesture = entry);

            var canAdd = newGestureAlias.Length > 0 && !IsReserved(newGestureAlias) && selectedAliasGesture is not null;
            using (ImRaii.Disabled(!canAdd))
            {
                if (ImGui.Button("Add gesture alias") && selectedAliasGesture is { } chosen)
                {
                    gestures.Add(new GestureAliasDefinition
                    {
                        Alias = newGestureAlias,
                        GestureId = chosen.Id,
                        AnimationName = chosen.AnimationName,
                        ModDirectory = chosen.ModDirectory,
                        ModName = chosen.ModName,
                        EmoteName = chosen.Trigger!.DisplayName.TrimStart('/'),
                    });
                    config.Save();
                    newGestureAlias = "";
                    selectedAliasGesture = null;
                }
            }
            if (selectedAliasGesture is { } picked)
            {
                ImGui.SameLine();
                IconGlyph.WrappedDisabled($"Selected: {picked.Label}");
            }
        }

        using (Section.Begin("gestureActive", "Active animation"))
        {
            using (ImRaii.Disabled(!plugin.GestureCommand.HasActiveTemporary))
            {
                if (ImGui.Button("Reset active gesture"))
                    plugin.GestureCommand.ResetActiveTemporary();
            }
            IconGlyph.HelpMarker("Reverts the currently active temporary mod activation back to its saved settings right now, instead of waiting for the automatic ~30s idle-timeout. Only enabled while a gesture's temporary activation is active.");
        }
    }

    /// collar/moodles "Sub can self-apply or self-clear a Moodle via alias": mirrors DrawGestureModule's
    /// exact shape (a list of defined aliases with Remove, a dedicated clear alias, an "add" form picking
    /// from the Sub's own scanned catalog) - the picking control here is a plain combo rather than a
    /// dedicated picker window since Moodles has no equivalent of AnimationPickerWindow/ItemPickerWindow.
    private void DrawMoodlesModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.Smile, "Moodles Aliases");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Scan your own registered Moodles statuses in Settings, then define aliases from them here.");

        using (Section.Begin("moodleFixed", "Fixed words"))
        {
            DrawFixedWord("Clear moodles", ControlWords.ClearMoodle);
            IconGlyph.HelpMarker("The fixed word that clears your Moodles - except moodles still attached to an active outfit, restraint, leash or collar. Not renamable, so your Owner always knows it.");
        }

        var moodleAliases = config.Aliases.Moodles;
        using (Section.Begin("moodleAliases", "Your moodle aliases"))
        {
            if (moodleAliases.Count == 0)
            {
                IconGlyph.WrappedDisabled("No Moodle aliases yet - add one below.");
            }
            else
            {
                var labels = moodleAliases.Select(m => m.Alias).ToArray();
                var i = DrawItemSelector("##moodleAliasSelect", labels, ref selectedMoodleAliasIndex);
                var m = moodleAliases[i];
                var invalid = string.IsNullOrEmpty(m.StatusId) || !config.MoodlesMapping.LocalCatalog.ContainsKey(m.StatusId);
                ImGui.Indent();
                ImGui.TextUnformatted($"{MoodlesTextFormat.StripMarkup(m.StatusName)}{(invalid ? " — rescan/recreate required" : "")}");
                ImGui.Unindent();
                if (ImGui.SmallButton("Remove"))
                {
                    moodleAliases.RemoveAt(i);
                    config.Save();
                    selectedMoodleAliasIndex = null;
                }
            }
        }

        var statuses = config.MoodlesMapping.LocalCatalog.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (statuses.Count == 0)
        {
            IconGlyph.WrappedDisabled("No scanned Moodles statuses yet - rescan in Settings (gear icon) first.");
            return;
        }

        using (Section.Begin("moodleAdd", "Add a moodle alias"))
        {
            ImGui.InputText("Alias##newMoodle", ref newMoodleAlias, 32);
            IconGlyph.HelpMarker("Short word the Owner types. With Moodles permission enabled, this immediately applies the chosen status.");
            DrawReservedWordWarning(newMoodleAlias);

            var statusNames = statuses.Select(s => MoodlesTextFormat.StripMarkup(s.Name)).ToArray();
            newMoodleStatusIndex = Math.Clamp(newMoodleStatusIndex, 0, statusNames.Length - 1);
            ImGui.Combo("Status##newMoodle", ref newMoodleStatusIndex, statusNames, statusNames.Length);

            if (ImGui.Button("Add Moodle alias") && newMoodleAlias.Length > 0 && !IsReserved(newMoodleAlias))
            {
                var chosen = statuses[newMoodleStatusIndex];
                moodleAliases.Add(new MoodlesAliasDefinition { Alias = newMoodleAlias, StatusId = chosen.StatusId, StatusName = chosen.Name });
                config.Save();
                newMoodleAlias = "";
            }
        }
    }

    /// collar/custom-triggers "Sub can define a multi-action Custom Trigger": builds one CustomTriggerAction
    /// at a time into a draft list (reusing each category's own existing picker - AnimationPickerWindow for
    /// Gesture, WardrobeMapping/RestraintMapping-backed combos for Outfit/Restraint matching
    /// DrawWardrobeModule/DrawRestraintsModule, a plain combo for Moodle matching DrawMoodlesModule, and a
    /// raw text box for Chat since design.md rules out any content/channel validation beyond the permission
    /// gate itself), then commits alias+actions together on "Save trigger". Each bundled action still checks
    /// its own category's permission at apply time (CustomTriggerCommand.Apply) - this UI doesn't duplicate
    /// those checks, it only builds the definition.
    private void DrawCustomTriggersModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.BoltLightning, "Custom Triggers");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Bundle multiple actions - title, outfit, animation, moodle, restraint, chat - behind one alias. Each action still needs its own category permission (Chat also needs the dedicated Custom chat messages acknowledgement in Settings) or it's skipped when the trigger fires.");

        var triggers = config.Aliases.CustomTriggers;
        using (Section.Begin("customTriggerList", "Your custom triggers"))
        {
            if (triggers.Count == 0)
            {
                IconGlyph.WrappedDisabled("No custom triggers yet - build one below.");
            }
            else
            {
                var labels = triggers.Select(t => t.Alias).ToArray();
                var i = DrawItemSelector("##customTriggerSelect", labels, ref selectedCustomTriggerIndex);
                var t = triggers[i];
                ImGui.PushID($"customTrigger_{i}");
                ImGui.Indent();
                foreach (var action in t.Actions)
                    DrawActionSummary(action);
                ImGui.Unindent();
                if (ImGui.SmallButton("Edit"))
                {
                    editingCustomTriggerIndex = i;
                    ctNewAlias = t.Alias;
                    ctDraftActions.Clear();
                    ctDraftActions.AddRange(t.Actions.Select(CloneAction));
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                {
                    triggers.RemoveAt(i);
                    config.Save();
                    selectedCustomTriggerIndex = null;
                }
                ImGui.PopID();
            }
        }

        using var editorBox = Section.Begin("customTriggerEditor", editingCustomTriggerIndex is null ? "New trigger" : "Edit trigger");
        ImGui.InputText("Alias##newCustomTrigger", ref ctNewAlias, 32);
        IconGlyph.HelpMarker("Short word the Owner types. Applies every permitted action below in order when triggered.");
        DrawReservedWordWarning(ctNewAlias);

        if (ctDraftActions.Count > 0)
        {
            Section.SubHeading("Actions in this trigger");
            for (var i = 0; i < ctDraftActions.Count; i++)
            {
                ImGui.PushID($"ctDraftAction_{i}");
                DrawActionSummary(ctDraftActions[i]);
                ImGui.SameLine();
                using (ImRaii.Disabled(i == 0))
                    if (ImGui.SmallButton("↑"))
                        MoveDraftAction(ctDraftActions, i, i - 1, ref editingCustomTriggerActionIndex);
                ImGui.SameLine();
                using (ImRaii.Disabled(i == ctDraftActions.Count - 1))
                    if (ImGui.SmallButton("↓"))
                        MoveDraftAction(ctDraftActions, i, i + 1, ref editingCustomTriggerActionIndex);
                // A restraint action only references a restraint - its rules/moodle are edited in the
                // Restraints tab, so there's nothing to edit here (remove and re-add to pick another one).
                if (ctDraftActions[i].Kind != CustomTriggerActionKind.Restraint)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Edit"))
                    {
                        LoadSubActionDraft(ctDraftActions[i]);
                        editingCustomTriggerActionIndex = i;
                    }
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
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

        // collar/ui-organization "Partly dependent features are disabled inline": a part whose category needs a
        // missing plugin can't be added or saved; the rest of the bundle editor keeps working.
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
                IconGlyph.HelpMarker("Toggles this device when the trigger fires - applies if inactive, releases if active, same as a plain restraint alias.");
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
                if (editingCustomTriggerIndex is { } editIndex && editIndex >= 0 && editIndex < triggers.Count)
                    triggers[editIndex] = replacement;
                else
                    triggers.Add(replacement);
                config.Save();
                ctNewAlias = "";
                ctDraftActions.Clear();
                editingCustomTriggerIndex = null;
                editingCustomTriggerActionIndex = null;
            }
        }
        if (editingCustomTriggerIndex is not null)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel##editCustomTrigger"))
            {
                ctNewAlias = "";
                ctDraftActions.Clear();
                editingCustomTriggerIndex = null;
                editingCustomTriggerActionIndex = null;
            }
        }
    }

    private static string SummarizeCustomTriggerAction(CustomTriggerAction a) => CommandPresentation.Action(a);

    private static void DrawActionSummary(CustomTriggerAction action)
    {
        var summary = SummarizeCustomTriggerAction(action);
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextWrapped(summary);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(summary);
    }

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
        target.ArmsCuffed = source.ArmsCuffed;
        target.ArmsCuffedAnimationId = source.ArmsCuffedAnimationId;
        target.LegsCuffed = source.LegsCuffed;
        target.LegsCuffedAnimationId = source.LegsCuffedAnimationId;
        target.FullBodyCuffed = source.FullBodyCuffed;
        target.FullBodyCuffedAnimationId = source.FullBodyCuffedAnimationId;
    }

    /// collar/collaring: the Sub's own configured Neck-slot collar. Capture-only (see design.md's "Collar
    /// capture, not manual entry") - equip what you want first, then click Capture. Locked once applied at
    /// pairing acceptance (AcceptPending), not from this tab - editing/clearing is disabled while locked,
    /// matching "Sub configures their own collar item" and "resists casual removal" together.
    private void DrawCollarModule()
    {
        var config = plugin.Configuration;
        var locked = plugin.RuntimeState.CollarForceLocked;
        IconGlyph.Text(FontAwesomeIcon.Lock, "Collar");
        ImGui.Separator();
        IconGlyph.WrappedDisabled("Equip the item you want in your Neck slot (any way you like), then capture it here. Applied and locked automatically the moment you accept a pairing - not from this tab.");

        if (locked)
            IconGlyph.WrappedColored(Theme.Danger, "Locked - applied at pairing. Only /oathboundpanic (your safeword) or your Owner's \"collar unlock\" releases it.");

        using var disabled = ImRaii.Disabled(locked);

        using (Section.Begin("collarItem", "Collar item"))
        {
            var collarChosenLabel = config.Collar.ItemId is { } collarItemId ? GetItemName(collarItemId) : "(none chosen)";
            ImGui.TextUnformatted($"Item: {collarChosenLabel}");
            ImGui.SameLine();
            if (ImGui.Button("Choose item...##collar"))
                plugin.ItemPickerWindow.Open(ApiEquipSlot.Neck, (chosenId, _) => plugin.CollarCommand.ConfigureFromItem(chosenId));
            IconGlyph.HelpMarker("Pick any Neck-slot item to save as your collar - it does not need to be equipped or owned.");

            if (config.Collar.IsConfigured)
            {
                ImGui.SameLine();
                if (ImGui.Button("Clear"))
                    plugin.CollarCommand.ClearConfiguredCollar();
            }
            else
            {
                IconGlyph.WrappedDisabled("No collar configured yet.");
            }
        }

        using (Section.Begin("collarMoodle", "Collar moodle"))
        {
            var collarMoodleLabel = config.Collar.MoodleStatusName is { } assignedMoodleName ? MoodlesTextFormat.StripMarkup(assignedMoodleName) : "(none assigned)";
            ImGui.TextUnformatted($"Moodle: {collarMoodleLabel}");
            IconGlyph.HelpMarker("Optional. Applied alongside your collar item when it locks, and periodically re-asserted for as long as the collar stays locked - removing it through Moodles' own UI won't make it stick. Cleared only by /oathboundpanic or your Owner's \"collar unlock\", the same as the collar item itself.");

            var collarMoodleStatuses = config.MoodlesMapping.LocalCatalog.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            // collar/ui-organization "Partly dependent features are disabled inline": assigning needs Moodles;
            // clearing an existing assignment (below) stays available.
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
                ImGui.SetNextItemWidth(220);
                ImGui.Combo("##newCollarMoodle", ref newCollarMoodleStatusIndex, collarMoodleStatusNames, collarMoodleStatusNames.Length);
                ImGui.SameLine();
                if (ImGui.SmallButton("Assign##collarMoodle"))
                {
                    var chosen = collarMoodleStatuses[newCollarMoodleStatusIndex];
                    config.Collar.MoodleStatusId = chosen.StatusId;
                    config.Collar.MoodleStatusName = chosen.Name;
                    config.Save();
                }
            }

            if (config.Collar.HasMoodleAssigned)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear##collarMoodle"))
                {
                    config.Collar.MoodleStatusId = null;
                    config.Collar.MoodleStatusName = null;
                    config.Save();
                }
            }
        }
    }

    /// collar/ui-organization: split out of DrawCollarModule so Collar and Follow / Leash can be
    /// independent tabs, matching the Owner side's existing separate Collar/Leash sections and the
    /// separate Follow permission toggle - no change to either section's own controls.
    private void DrawFollowLeashModule()
    {
        var config = plugin.Configuration;
        IconGlyph.Text(FontAwesomeIcon.Link, "Follow / Leash");
        ImGui.Separator();

        using (Section.Begin("leashFixed", "Fixed words"))
        {
            IconGlyph.WrappedDisabled("The words your Owner sends to engage or release the movement lock.");
            DrawFixedWord("Leash", ControlWords.Leash);
            IconGlyph.HelpMarker("Engages follow and blocks your own movement while the Follow / Leash permission is enabled.");
            DrawFixedWord("Unleash", ControlWords.Unleash);
            IconGlyph.HelpMarker("Releases the movement lock and restores normal input.");
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

    /// Best-effort display name for a raw Glamourer item id via Lumina's own Item sheet - falls back to
    /// the numeric id for sentinel/special values (e.g. "nothing equipped") that don't resolve to a real
    /// row, so a lookup miss never crashes the picker's chosen-item label.
    private static string GetItemName(ulong itemId)
    {
        var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault((uint)itemId);
        var name = row?.Name.ExtractText();
        return string.IsNullOrWhiteSpace(name) ? $"Item #{itemId}" : name;
    }

    /// collar/catalog-sync: the single Owner-side entry point that replaces the three former per-category
    /// "Add from clipboard" buttons - opens a native file picker for a Sub-exported catalog file and fills
    /// every category's quick-command list from it in one action (CatalogSyncService.ParseImport).
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
                    // Paired as this Sub's Owner: importing their file again replaces what they shared
                    // before (like a relay refresh). Otherwise, the old add-only import.
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

        /// collar/catalog-sync "Owner can reset every import to a blank slate": removes only the
        /// `Imported`-sourced entries from Title/Outfit/Gesture/Moodles/Restraints - those lists now mix
        /// import-sourced entries with the Owner's own manually-added/scanned ones (single-action aliases
        /// route into these same category lists now - see collar/catalog-sync), so a coarse `.Clear()`
        /// would also wipe out entries reset-imports was never meant to touch. The Custom Trigger Bundle
        /// list still shares one list between imported bundles and anything the Owner typed manually into
        /// the freeform composer, so it keeps the old coarse whole-list clear - the same reset already
        /// accepted for Restraints' manually-added entries (see collar/catalog-sync's spec).
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
            expandedRestraintRuleEditors.Clear();
            restraintRuleEdits.Clear();
            resetImportsResult = "All imports reset to a blank slate.";
            importResult = null;
        }
        IconGlyph.HelpMarker("Clears every import-sourced quick command (Title, Outfit, Animation, Moodles, Restraints) back out, leaving anything you added or scanned yourself in those same lists untouched; clears the entire Custom Trigger Bundle list - including any one-off commands you typed by hand, since imported bundles share that same list; and clears the browsable Animation/Restraints mod catalogs imported from your Sub, so a stale or duplicate entry from an earlier import can't linger until the next one.");

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

    /// collar/catalog-sync "Owner's Sync tab shows whether the catalog is up to date": one sync state for the
    /// active Owner-side pairing (warning style for everything but Up to date), when the Sub last published
    /// and when this device last imported, a no-cooldown "Check now", and the manual "Request refresh"
    /// fallback (disabled only while a request is already in flight). Only ever called from the Sync tab's
    /// Owner-role view (DrawSyncTab) - the Sub-role view is a separate, dedicated explanation instead.
    private void DrawCatalogRelaySection()
    {
        var relayService = plugin.CatalogSyncRelayService;
        var mailbox = plugin.CatalogMailboxService;
        var pairing = plugin.Configuration.ActivePairing;
        IconGlyph.Text(FontAwesomeIcon.CloudDownloadAlt, "Cloud catalog sync");
        IconGlyph.WrappedDisabled("Your Sub's catalog syncs automatically through the Oathbound Cloudflare relay, end-to-end encrypted: their plugin shares changes as they happen, and this plugin checks about once an hour. No chat messages or file transfers are needed.");
        if (pairing is not { Direction: PairingDirection.OwnerSide })
        {
            IconGlyph.WrappedDisabled("No Owner-side pairing is active - select one above, or pair from Settings first. The offline file fallback remains available below.");
            return;
        }

        var (stateColor, stateText) = DescribeCatalogSyncState(pairing, mailbox);
        IconGlyph.WrappedColored(stateColor, stateText);

        using (ImRaii.Disabled(mailbox.IsChecking(pairing.Id)))
        {
            if (ImGui.Button(mailbox.IsChecking(pairing.Id) ? "Checking..." : "Check now"))
                plugin.CatalogAutoSync.RequestOwnerCheck(pairing, force: true);
        }
        IconGlyph.HelpMarker("Checks the relay right away for a newer catalog from your Sub and imports it if there is one. Otherwise this happens automatically about once an hour.");

        ImGui.SameLine();
        using (ImRaii.Disabled(relayService.RequestInFlight))
        {
            if (ImGui.Button(relayService.RequestInFlight ? "Requesting..." : "Request refresh"))
                Plugin.FireAndForget(relayService.RequestRefreshAsync(pairing, System.Threading.CancellationToken.None));
        }
        IconGlyph.HelpMarker("Manual fallback: asks your Sub's plugin to send its catalog right now over a chat tell. Needs your Sub to be online, and works even if their plugin is too old for automatic sync.");

        if (pairing.SubLastPublishedUnixSeconds is { } published)
            IconGlyph.WrappedDisabled($"Your Sub last shared changes: {DateTimeOffset.FromUnixTimeSeconds(published).LocalDateTime:g}.");
        if (pairing.LastAcceptedCatalogSyncUnixSeconds > 0)
            IconGlyph.WrappedDisabled($"Last imported: {DateTimeOffset.FromUnixTimeSeconds(pairing.LastAcceptedCatalogSyncUnixSeconds).LocalDateTime:g} (snapshot #{pairing.LastImportedSnapshotId}).");
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

    /// The single state line, in the spec's priority order: Syncing > Last sync failed > Out of date (no
    /// successful check in ~2h) > No automatic updates from Sub > Up to date.
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
            return (Theme.Warning, $"Out of date: couldn't reach the relay since {DateTimeOffset.FromUnixTimeSeconds(lastOk).LocalDateTime:g}. Your Sub may have changed things since.");
        if (pairing.SubLastPublishedUnixSeconds is null)
            return (Theme.Warning, "No automatic updates from your Sub yet: their plugin may be too old, or their \"Catalog sync (relay)\" permission is off. Request refresh still works.");
        return (Theme.Success, "Up to date.");
    }

    /// collar/ui-organization: draws a section's icon+title, then (if `showClearAll`) a "Clear all" button
    /// right-aligned on that same row - no prior "title ... [button]" row existed anywhere in this UI, so
    /// this is the one shared right-alignment routine every quick-command section now uses (design.md
    /// decision #3), based on the same GetContentRegionAvail math DrawImportCommandsButton already used to
    /// center its own button. Wraps to its own line instead of being clipped when the window is too narrow
    /// for the button to fit next to the title.
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
            if (ImGui.SmallButton($"{label}##{idSuffix}"))
                onClearAll();
        }
        // Same rule under the title as every Sub module, so Owner and Sub tabs open the same way.
        ImGui.Separator();
    }

    /// The rendered width of a button carrying this visible text, ignoring any "##id" suffix - used to
    /// decide whether the next control in a row still fits before wrapping (see ContinueRowOrWrap).
    private static float ButtonWidth(string visibleLabel) =>
        ImGui.CalcTextSize(visibleLabel).X + ImGui.GetStyle().FramePadding.X * 2f;

    /// collar/ui-organization: keeps a horizontal row of buttons from being clipped off-window when it's
    /// too narrow to fit them all - continues the row with SameLine() when the next control still fits,
    /// otherwise wraps it onto a fresh line (ImGui.NewLine() undoes the SameLine positioning it just did).
    private static void ContinueRowOrWrap(float nextControlWidth)
    {
        ImGui.SameLine();
        if (ImGui.GetContentRegionAvail().X < nextControlWidth)
            ImGui.NewLine();
    }

    /// Collar only ever has one override verb - `collar unlock` - since the collar itself only ever
    /// applies as a side effect of pairing acceptance, never through a chat command (see
    /// ChatCommandListener.HandleForceCollar). No "Add Command" builder needed, just the fixed release row.
    private void DrawCollarQuickSection(bool canSend)
    {
        IconGlyph.Text(FontAwesomeIcon.Lock, "Collar");
        ImGui.Separator();
        using var commands = Section.Begin("collarQuick", "Commands");
        DrawFixedQuickRow("Collar lock", "collar lock", canSend, FixedActionIds.CollarLock);
        IconGlyph.HelpMarker("(Re-)attaches your Sub's configured collar item and locks it - the same thing that happens automatically at pairing, triggered manually. Use this to re-lock after \"Collar unlock,\" or to apply it for the first time if it wasn't configured/enabled yet when pairing was accepted.");

        DrawFixedQuickRow("Collar unlock", "collar unlock", canSend, FixedActionIds.CollarUnlock);
        IconGlyph.HelpMarker("Releases your Sub's locked collar without them needing to panic - it stays equipped, just no longer locked.");
    }

    private void DrawMoodlesQuickSection(bool canSend)
    {
        var quick = plugin.Configuration.QuickCommands.Moodles;
        DrawSectionTitleRow(FontAwesomeIcon.Smile, "Moodles", quick.Count > 0, "moodlesQuick", () =>
        {
            quick.Clear();
            plugin.Configuration.Save();
        });

        using (Section.Begin("moodlesQuickFixed", "Commands"))
            DrawFixedQuickRow("Clear moodle", "moodle clear", canSend, FixedActionIds.ClearMoodle);

        if (quick.Count == 0)
        {
            DrawGoToSyncTabPrompt("No Moodles statuses imported yet.");
            return;
        }

        // Sized to its rows, up to the room left in the tab - not a fixed box, and not stretched when short.
        using var _ = Section.List("moodlesQuickList");
        foreach (var cmd in quick.ToArray())
            DrawSavedQuickRow(cmd, quick, canSend, MoodlesTextFormat.StripMarkup);
    }

    /// Owner restraint authoring: browse the Sub's shared Penumbra mods first, explicitly choose only the
    /// ones that should become commands, configure their rules, then optionally build a direct slot/item
    /// restraint at the bottom. The former free-text legacy device-name creator is intentionally absent.
    private void DrawRestraintQuickSection(bool canSend)
    {
        DrawRestraintQuickSectionBody(canSend);
    }

    private void DrawRestraintQuickSectionBody(bool canSend)
    {
        var quick = plugin.Configuration.QuickCommands.Restraints;
        DrawSectionTitleRow(FontAwesomeIcon.Handcuffs, "Restraints", quick.Count > 0, "restraintsQuick", () =>
        {
            quick.Clear();
            plugin.Configuration.Save();
        });

        // Only ever called from the Owner-role dispatch now (collar/ui-organization's shared category
        // tabs), so this always reads the imported peer catalog - the Sub-role branch this used to need
        // when both lived in one shared method is gone.
        var catalog = plugin.Configuration.RestraintMapping.ImportedPeerCatalog.Values.ToList();

        using (Section.Begin("restraintQuickCommands", "Commands"))
        {
            DrawFixedQuickRow("Restraint unlock", "restraint unlock", canSend, FixedActionIds.RestraintUnlock);
            IconGlyph.HelpMarker("Force-releases every active restraint device and clears the force-lock, the same as your Sub's panic would for restraints specifically.");
        }

        var browserBox = Section.Begin("restraintQuickBrowser", "Available restraint mods");
        ImGui.InputTextWithHint("##ownerRestraintSearch", "Search available restraint mods...", ref ownerRestraintSearch, 128);
        IconGlyph.WrappedDisabled("Choose a mod to create one restraint command. Its Penumbra options stay exactly as your Sub configured them; enabling and locking are temporary until the global restraint unlock.");
        using (ImRaii.Child("restraintModBrowser", new Vector2(0, 150), true))
        {
            foreach (var entry in catalog.Where(x => string.IsNullOrWhiteSpace(ownerRestraintSearch) || x.ModName.Contains(ownerRestraintSearch.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.ModName))
            {
                ImGui.TextUnformatted(entry.ModName);
                ImGui.SameLine();
                var alreadyChosenCount = quick.Count(x => x.RestraintCatalogId == entry.Id);
                if (ImGui.SmallButton($"{(alreadyChosenCount > 0 ? "Choose again" : "Choose")}##restraintMod_{entry.Id}"))
                {
                    // A mod can be chosen more than once with different restriction rules (collar/restraints
                    // "create a mod restraint for the same mod") - the row below is keyed by Label, so each
                    // repeat gets a distinct one rather than colliding with the first.
                    var label = alreadyChosenCount == 0 ? entry.ModName : $"{entry.ModName} ({alreadyChosenCount + 1})";
                    quick.Add(new QuickCommand
                    {
                        Label = label,
                        Command = "",
                        Source = ImportSource.Manual,
                        Target = entry.Id,
                        RestraintCatalogId = entry.Id,
                    });
                    plugin.Configuration.Save();
                    expandedRestraintRuleEditors.Add(label);
                    restraintRuleEdits[label] = new RestraintRuleEditState();
                }
            }
        }

        browserBox.Dispose();

        using (Section.Begin("restraintQuickConfigured", "Configured mod restraints"))
        {
            var configuredMods = quick.Where(x => x.RestraintCatalogId is not null).ToArray();
            if (configuredMods.Length == 0)
            {
                IconGlyph.WrappedDisabled("No restraint mod has been chosen yet.");
            }
            else
            {
                using var _ = Section.List("restraintsQuickList", 260);
                foreach (var cmd in configuredMods) DrawRestraintQuickRow(cmd, quick, canSend);
            }
        }

        // collar/catalog-sync "shared presets are copies": the Sub's rules-only restraints arrive as
        // self-contained `restraint wear` commands - they keep working after the Sub deletes them.
        var sharedRulesOnly = quick.Where(x => x.RestraintCatalogId is null).ToArray();
        if (sharedRulesOnly.Length > 0)
        {
            using (Section.Begin("restraintQuickShared", "Shared rules-only restraints"))
            {
                foreach (var cmd in sharedRulesOnly)
                    DrawSavedQuickRow(cmd, quick, canSend);
            }
        }

        using (Section.Begin("restraintQuickAdHoc", "Rules-only restraint"))
            DrawAdHocRestraintSection(canSend);
    }

    /// collar/restraints "Owner-authored ad-hoc restraint device", now rules-only: a one-off set of
    /// restriction rules sent with no gear (`restraint wear - - "<label>" rules:...`, RestraintCommand.
    /// BuildWearCommand) rather than being added to the name-based `quick` list, since its full definition
    /// travels in the command text. Gear-carrying restraints come from the Sub's shared mods above.
    private void DrawAdHocRestraintSection(bool canSend)
    {
        IconGlyph.WrappedDisabled("Send restriction rules on their own - forced pose, walk-only, gagged or action block - with no gear. Cuffs belong to a device, so they come with your Sub's shared restraint mods above.");

        ImGui.SetNextItemWidth(220);
        ImGui.InputText("Label##adHocRestraint", ref newAdHocLabel, 32);
        IconGlyph.HelpMarker("Your own reference name for this restraint - never matched against anything on your Sub's side.");
        OwnerMoodleOverride.Draw("adHocRestraint", plugin.Configuration, ref adHocMoodleOverride);

        Section.SubHeading("Restrictions");
        DrawRestraintRuleCheckboxes(newAdHocRuleEdit, "adHocRestraint", rulesOnly: true);

        var hasAnyRule = HasAnyRule(newAdHocRuleEdit);
        var boundAnimationsConfigured = BoundAnimationsConfigured(newAdHocRuleEdit);
        if (hasAnyRule && !boundAnimationsConfigured)
            IconGlyph.WrappedColored(Theme.Warning, "Choose an animation for every checked Arms/Legs/Full Body Cuffed rule before sending.");

        ImGui.Spacing();
        ImGui.Separator();
        if (newAdHocLabel.Trim().Length > 0 && hasAnyRule && boundAnimationsConfigured)
        {
            var command = RestraintCommand.BuildWearCommand(null, null, newAdHocLabel.Trim(), ToRules(newAdHocRuleEdit));
            ImGui.TextUnformatted("Send this restraint:");
            ContinueRowOrWrap(ButtonWidth("Send"));
            DrawSendCopyButtons(OwnerMoodleOverride.Apply(command, adHocMoodleOverride), canSend, "adHocRestraint");
        }
        else
        {
            IconGlyph.WrappedColored(Theme.Warning, "Choose a label and at least one rule before this can be sent.");
        }
    }

    /// collar/custom-triggers "custom commands should also be creatable via the Owner commands menu":
    /// builds an ad-hoc, unnamed bundle one action at a time (mirroring DrawAdHocRestraintSection's
    /// draft-then-send shape) and sends it via `CustomTriggerCommand.BuildCastCommand`'s `customtrigger
    /// cast` wire grammar. Unlike the Sub-side DrawCustomTriggersModule, every non-Title/Chat action here
    /// is typed by name only, never picked from a local catalog - the Owner's own install has no access to
    /// the Sub's WardrobeMapping/GestureMapping/MoodlesMapping/RestraintMapping, only the Sub does, so this
    /// matches the existing "type the exact name your Sub told you" freeform pattern (e.g.
    /// DrawRestraintQuickSection's manual add) rather than the Sub-tab's picker-based one.
    private void DrawCustomTriggerQuickSection(bool canSend)
    {
        IconGlyph.WrappedDisabled("Bundle actions together by name - no dedicated alias needed on your Sub's side. Type each name exactly as your Sub told you; Title and Chat need no name at all.");

        ImGui.SetNextItemWidth(220);
        ImGui.InputText("Label##ctqLabel", ref ctqLabel, 32);
        IconGlyph.HelpMarker("Your own reference name for this bundle - never matched against anything on your Sub's side.");

        if (ctqDraftActions.Count > 0)
        {
            Section.SubHeading("Actions in this bundle");
            for (var i = 0; i < ctqDraftActions.Count; i++)
            {
                ImGui.PushID($"ctqDraftAction_{i}");
                DrawActionSummary(ctqDraftActions[i]);
                ImGui.SameLine();
                using (ImRaii.Disabled(i == 0))
                    if (ImGui.SmallButton("↑"))
                        MoveDraftAction(ctqDraftActions, i, i - 1, ref editingOwnerActionIndex);
                ImGui.SameLine();
                using (ImRaii.Disabled(i == ctqDraftActions.Count - 1))
                    if (ImGui.SmallButton("↓"))
                        MoveDraftAction(ctqDraftActions, i, i + 1, ref editingOwnerActionIndex);
                // Same as the Sub's editor: a restraint is edited in the Restraints tab, not from a bundle.
                if (ctqDraftActions[i].Kind != CustomTriggerActionKind.Restraint)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Edit"))
                    {
                        LoadOwnerActionDraft(ctqDraftActions[i]);
                        editingOwnerActionIndex = i;
                    }
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
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
        ImGui.SetNextItemWidth(160);
        ImGui.Combo("Action type##ctqKind", ref ctqKindIndex, kindNames, kindNames.Length);
        var kind = Enum.Parse<CustomTriggerActionKind>(kindNames[ctqKindIndex]);
        if (editingOwnerActionIndex is not null)
            IconGlyph.WrappedColored(Theme.Accent, "Editing this action. Change its values below, then choose Save.");

        switch (kind)
        {
            case CustomTriggerActionKind.Title:
                ImGui.SetNextItemWidth(220);
                ImGui.InputText("Text##ctqTitle", ref ctqTitleText, 64);
                ImGui.Checkbox("Prefix##ctqTitle", ref ctqTitleIsPrefix);
                ImGui.ColorEdit3("Color##ctqTitle", ref ctqTitleColor);
                DrawGlowPicker("ctqTitle", ref ctqTitleHasGlow, ref ctqTitleGlow);
                using (ImRaii.Disabled(ctqTitleText.Trim().Length == 0))
                {
                    if (ImGui.SmallButton($"{(editingOwnerActionIndex is null ? "Add" : "Save")}##ctqTitleBtn"))
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
                ImGui.SetNextItemWidth(220);
                ImGui.InputText("Design name##ctqOutfit", ref ctqOutfitName, 32);
                IconGlyph.HelpMarker("Type the exact wardrobe design name your Sub told you.");
                using (ImRaii.Disabled(ctqOutfitName.Trim().Length == 0))
                {
                    if (ImGui.SmallButton($"{(editingOwnerActionIndex is null ? "Add" : "Save")}##ctqOutfitBtn"))
                    {
                        CommitOwnerAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Outfit, OutfitDesignName = ctqOutfitName.Trim() });
                        ctqOutfitName = "";
                    }
                }
                break;

            case CustomTriggerActionKind.Gesture:
                ImGui.SetNextItemWidth(220);
                ImGui.InputText("Animation name##ctqGesture", ref ctqGestureName, 32);
                IconGlyph.HelpMarker("Type the exact animation name your Sub told you.");
                using (ImRaii.Disabled(ctqGestureName.Trim().Length == 0))
                {
                    if (ImGui.SmallButton($"{(editingOwnerActionIndex is null ? "Add" : "Save")}##ctqGestureBtn"))
                    {
                        CommitOwnerAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Gesture, GestureAnimationName = ctqGestureName.Trim() });
                        ctqGestureName = "";
                    }
                }
                break;

            case CustomTriggerActionKind.Moodle:
                ImGui.SetNextItemWidth(220);
                ImGui.InputText("Status name##ctqMoodle", ref ctqMoodleName, 32);
                IconGlyph.HelpMarker("Type the exact Moodles status name your Sub told you.");
                using (ImRaii.Disabled(ctqMoodleName.Trim().Length == 0))
                {
                    if (ImGui.SmallButton($"{(editingOwnerActionIndex is null ? "Add" : "Save")}##ctqMoodleBtn"))
                    {
                        CommitOwnerAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Moodle, MoodleStatusName = ctqMoodleName.Trim() });
                        ctqMoodleName = "";
                    }
                }
                break;

            case CustomTriggerActionKind.Restraint:
                ImGui.SetNextItemWidth(220);
                ImGui.InputText("Device name##ctqRestraint", ref ctqRestraintName, 32);
                IconGlyph.HelpMarker("Type the exact restraint device name your Sub told you. Always applies rather than toggling, since this bundle has no captured device to check the active state of - use the Restraints section above to release it.");
                using (ImRaii.Disabled(ctqRestraintName.Trim().Length == 0))
                {
                    if (ImGui.SmallButton($"{(editingOwnerActionIndex is null ? "Add" : "Save")}##ctqRestraintBtn"))
                    {
                        CommitOwnerAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Restraint, RestraintDeviceName = ctqRestraintName.Trim() });
                        ctqRestraintName = "";
                    }
                }
                break;

            case CustomTriggerActionKind.Chat:
                ImGui.SetNextItemWidth(320);
                ImGui.InputText("Message##ctqChat", ref ctqChatText, 400);
                IconGlyph.HelpMarker("Sent exactly as typed, unmodified - start it with a slash command (e.g. /sit) or a channel prefix (e.g. /p) to use those instead of the default chat channel. Needs your Sub's Custom chat messages permission and its own acknowledgement (see the README's Automation risk section).");
                using (ImRaii.Disabled(ctqChatText.Trim().Length == 0))
                {
                    if (ImGui.SmallButton($"{(editingOwnerActionIndex is null ? "Add" : "Save")}##ctqChatBtn"))
                    {
                        CommitOwnerAction(new CustomTriggerAction { Kind = CustomTriggerActionKind.Chat, ChatText = ctqChatText });
                        ctqChatText = "";
                    }
                }
                break;
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ctqLabel.Trim().Length > 0 && ctqDraftActions.Count > 0)
        {
            var command = CustomTriggerCommand.BuildCastCommand(ctqLabel.Trim(), ctqDraftActions);
            ImGui.TextUnformatted(editingOwnerBundle is null ? "Send or save this bundle:" : "Update this saved bundle:");
            ContinueRowOrWrap(ButtonWidth("Send"));
            DrawSendCopyButtons(command, canSend, "ctqCustomTrigger");
            ContinueRowOrWrap(ButtonWidth("Save bundle"));
            var aliases = plugin.Configuration.QuickCommands.Aliases;
            var stale = editingOwnerBundle is not null && !aliases.Contains(editingOwnerBundle);
            var duplicate = aliases.Any(q => !ReferenceEquals(q, editingOwnerBundle) &&
                (string.Equals(q.Label, ctqLabel.Trim(), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(q.Command, command, StringComparison.OrdinalIgnoreCase)));
            var safe = ChatComposer.AllFit(plugin.ChatComposer.ComposeAll(command));
            using (ImRaii.Disabled(stale || duplicate || !safe))
            {
                if (ImGui.SmallButton($"{(editingOwnerBundle is null ? "Save bundle" : "Save changes")}##ctqSave"))
                {
                    if (editingOwnerBundle is null)
                        aliases.Add(new QuickCommand { Label = ctqLabel.Trim(), Command = command });
                    else
                    {
                        editingOwnerBundle.Label = ctqLabel.Trim();
                        editingOwnerBundle.Command = command;
                    }
                    plugin.Configuration.Save();
                    ClearOwnerBundleDraft();
                }
            }
            if (stale) IconGlyph.WrappedColored(Theme.Warning, "This saved bundle was removed while it was being edited.");
            else if (duplicate) IconGlyph.WrappedColored(Theme.Warning, "Another saved bundle already uses this label or command.");
            else if (!safe) IconGlyph.WrappedColored(Theme.Warning, "This bundle is too long for a safe chat payload.");
            ContinueRowOrWrap(ButtonWidth("Cancel"));
            if (ImGui.SmallButton($"{(editingOwnerBundle is null ? "Clear bundle" : "Cancel")}##ctq"))
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
        ctqDraftActions.Clear();
        ctqDraftActions.AddRange(actions.Select(CloneAction));
        editingOwnerActionIndex = null;
    }

    private void DrawRestraintQuickRow(QuickCommand cmd, List<QuickCommand> list, bool canSend)
    {
        ImGui.PushID($"restraintQuick_{cmd.Label}");
        var hasRules = cmd.RestraintRules is { Count: > 0 };
        var hasEquipment = cmd.RestraintItemId > 0 && GlamourerIpc.GetItemSlot((uint)cmd.RestraintItemId.Value) is not null;
        var catalogAvailable = cmd.RestraintCatalogId is not { } availableCatalogId ||
            plugin.Configuration.RestraintMapping.ImportedPeerCatalog.ContainsKey(availableCatalogId) ||
            (plugin.Configuration.Role == PluginRole.Sub && plugin.Configuration.RestraintMapping.LocalCatalog.ContainsKey(availableCatalogId));

        ImGui.TextUnformatted(cmd.Label);
        ContinueRowOrWrap(ButtonWidth("Favorited"));
        DrawFavoriteToggle(cmd, cmd.Label);

        ImGui.Indent();
        using (ImRaii.Disabled(!hasRules || !hasEquipment || !catalogAvailable))
            DrawSendOnly(OwnerMoodleOverride.ForSend(plugin.Configuration, cmd), canSend, $"enable_{cmd.Label}", "Enable & lock");
        var configureLabel = hasRules && hasEquipment ? "Edit setup" : "Configure setup";
        ContinueRowOrWrap(ButtonWidth(configureLabel));
        var expanded = expandedRestraintRuleEditors.Contains(cmd.Label);
        if (ImGui.SmallButton(configureLabel))
        {
            if (expanded)
            {
                expandedRestraintRuleEditors.Remove(cmd.Label);
            }
            else
            {
                expandedRestraintRuleEditors.Add(cmd.Label);
                restraintRuleEdits[cmd.Label] = FromRules(cmd.RestraintRules);
            }
            expanded = !expanded;
        }
        ContinueRowOrWrap(ButtonWidth("Remove"));
        if (ImGui.SmallButton("Remove"))
        {
            list.Remove(cmd);
            expandedRestraintRuleEditors.Remove(cmd.Label);
            restraintRuleEdits.Remove(cmd.Label);
            plugin.Configuration.Save();
            ImGui.Unindent();
            ImGui.PopID();
            return;
        }
        ImGui.Unindent();

        if (!hasRules)
        {
            IconGlyph.WrappedColored(Theme.Warning, "No rules assigned yet—configure this restraint before enabling it.");
        }
        if (!hasEquipment)
            IconGlyph.WrappedColored(Theme.Warning, "Choose the Glamourer slot and item this mod should equip and lock.");
        if (!catalogAvailable)
            IconGlyph.WrappedColored(Theme.Warning, "This restraint mod is no longer present in the latest shared catalog. Choose it again after the Sub shares it.");

        if (expanded && restraintRuleEdits.TryGetValue(cmd.Label, out var edit))
        {
            using var editorBox = Section.Begin("restraintQuickEditor", "Name & item");
            var labelBuffer = cmd.Label;
            if (ImGui.InputText("Name##restraintQuickLabel", ref labelBuffer, 80))
            {
                var trimmedLabel = labelBuffer.Trim();
                var oldLabel = cmd.Label;
                if (trimmedLabel.Length > 0 && !list.Any(x => x != cmd && string.Equals(x.Label, trimmedLabel, StringComparison.OrdinalIgnoreCase)))
                {
                    cmd.Label = trimmedLabel;
                    if (expandedRestraintRuleEditors.Remove(oldLabel))
                        expandedRestraintRuleEditors.Add(trimmedLabel);
                    if (restraintRuleEdits.Remove(oldLabel, out var editState))
                        restraintRuleEdits[trimmedLabel] = editState;
                    plugin.Configuration.Save();
                }
            }
            IconGlyph.HelpMarker("Your own label for this configured restraint - rename it so you can tell entries for the same mod apart.");
            ImGui.TextUnformatted($"Glamourer item: {(cmd.RestraintItemId is { } equippedItem ? GetItemName(equippedItem) : "(none chosen)")}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Choose item...##restraintQuick"))
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

            // collar/attached-moodles: this restraint's own moodle pick - saved immediately, like the name.
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

            using (ImRaii.Disabled(!hasAnyRule || !boundAnimationsConfigured || !hasEquipment))
            {
                if (ImGui.SmallButton("Save rules##restraintQuickRule"))
                {
                    var rules = ToRules(edit);
                    cmd.RestraintRules = rules;
                    cmd.Command = cmd.RestraintCatalogId is { } catalogId && cmd.RestraintItemId is { } itemId
                        ? RestraintCommand.BuildCatalogLockCommand(catalogId, cmd.Label, itemId, rules)
                        : RestraintCommand.BuildLockCommand(cmd.Label, rules);
                    plugin.Configuration.Save();
                    expandedRestraintRuleEditors.Remove(cmd.Label);
                    restraintRuleEdits.Remove(cmd.Label);
                }
            }
        }

        ImGui.PopID();
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
                    break;
                case RestraintRuleKind.ArmsCuffed: edit.ArmsCuffed = true; edit.ArmsCuffedAnimationId = rule.AnimationId; break;
                case RestraintRuleKind.LegsCuffed: edit.LegsCuffed = true; edit.LegsCuffedAnimationId = rule.AnimationId; break;
                case RestraintRuleKind.FullBodyCuffed: edit.FullBodyCuffed = true; edit.FullBodyCuffedAnimationId = rule.AnimationId; break;
            }
        }
        return edit;
    }

    private List<RestraintRuleAssignment> ToRules(RestraintRuleEditState edit)
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
            });
        }
        if (edit.ArmsCuffed)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.ArmsCuffed, AnimationId = edit.ArmsCuffedAnimationId, AnimationLabel = LabelFor(edit.ArmsCuffedAnimationId) });
        if (edit.LegsCuffed)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.LegsCuffed, AnimationId = edit.LegsCuffedAnimationId, AnimationLabel = LabelFor(edit.LegsCuffedAnimationId) });
        if (edit.FullBodyCuffed)
            rules.Add(new RestraintRuleAssignment { Kind = RestraintRuleKind.FullBodyCuffed, AnimationId = edit.FullBodyCuffedAnimationId, AnimationLabel = LabelFor(edit.FullBodyCuffedAnimationId) });
        return rules;
    }

    private void DrawTitleQuickSection(bool canSend)
    {
        IconGlyph.Text(FontAwesomeIcon.Heading, "Title");
        ImGui.Separator();
        var quick = plugin.Configuration.QuickCommands.Titles;

        using (Section.Begin("titleQuickFixed", "Commands"))
            DrawFixedQuickRow("Clear title", "title clear", canSend, FixedActionIds.ClearTitle);

        var addBox = Section.Begin("titleQuickAdd", "Add a title command");
        ImGui.SetNextItemWidth(220);
        ImGui.InputText("##newQuickTitle", ref newTitleQuickText, 64);
        IconGlyph.HelpMarker("The exact title text applied via Honorific.");
        ImGui.Checkbox("Prefix (not suffix)##newQuickTitle", ref newTitleQuickIsPrefix);
        IconGlyph.HelpMarker("Show the title before your Sub's name instead of after it.");
        ImGui.ColorEdit3("Color##newQuickTitle", ref newTitleQuickColor);
        IconGlyph.HelpMarker("Honorific title color - matches the Sub's own Title alias color picker.");
        DrawGlowPicker("newQuickTitle", ref newTitleQuickHasGlow, ref newTitleQuickGlow);
        if (ImGui.SmallButton("Add Command##quickTitle") && newTitleQuickText.Trim().Length > 0)
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
            });
            plugin.Configuration.Save();
            newTitleQuickText = "";
            newTitleQuickIsPrefix = false;
            newTitleQuickColor = new Vector3(1, 1, 1);
            newTitleQuickHasGlow = false;
            newTitleQuickGlow = new Vector3(1, 1, 1);
        }
        IconGlyph.HelpMarker("Saves a one-click button that force-applies this exact title (with the chosen prefix/color) and locks it on - your Sub's own clear-title alias is refused while it's locked, only the \"Clear title\" button below (or their panic) releases it. Requires a Sub on this plugin version to recognize the styled command - see the README.");
        addBox.Dispose();

        if (quick.Count == 0)
            return;

        using var savedBox = Section.Begin("titleQuickSaved", "Saved titles");
        foreach (var cmd in quick.ToArray())
            DrawSavedQuickRow(cmd, quick, canSend);
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

        using (Section.Begin("outfitQuickFixed", "Commands"))
        {
            DrawFixedQuickRow("Unlock outfit", "outfit unlock", canSend, FixedActionIds.UnlockOutfit);
        }

        if (quick.Count == 0)
        {
            DrawGoToSyncTabPrompt("No outfits imported yet.");
            return;
        }

        // Same as Moodles' list above: sized to its rows, up to the room left in the tab.
        using var _ = Section.List("outfitQuickList");
        foreach (var cmd in quick.ToArray())
            DrawSavedQuickRow(cmd, quick, canSend);
    }

    /// collar/ui-organization: reworked to match the Sub's animation picker (AnimationPickerWindow) instead
    /// of one flat scrolling list - a Sub with 1000+ gestures made the old fixed-height flat child
    /// unusable. Grouped by GestureModName/GestureGroupName (carried through import - see
    /// CatalogSyncService.ImportGestureLines) with a search box filtering by mod/group/label.
    private void DrawGestureQuickSection(bool canSend)
    {
        var quick = plugin.Configuration.QuickCommands.Gestures;
        DrawSectionTitleRow(FontAwesomeIcon.TheaterMasks, "Animation", quick.Count > 0, "gestureQuick", () =>
        {
            quick.Clear();
            plugin.Configuration.Save();
        });

        if (quick.Count == 0)
        {
            DrawGoToSyncTabPrompt("No animations imported yet.");
            return;
        }

        var searchBox = Section.Begin("gestureQuickSearchBox", "Search");
        ImGui.SetNextItemWidth(Math.Max(180, ImGui.GetContentRegionAvail().X));
        ImGui.InputTextWithHint("##gestureQuickSearch", "Search mod, group, or animation...", ref gestureQuickSearch, 128);

        var filter = gestureQuickSearch.Trim();
        var visible = quick.Where(c => filter.Length == 0
            || c.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || (c.GestureModName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
            || (c.GestureGroupName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        IconGlyph.WrappedDisabled($"{visible.Count} shown / {quick.Count} imported");
        searchBox.Dispose();

        using var _ = Section.List("gestureQuickList");
        if (visible.Count == 0)
        {
            IconGlyph.WrappedDisabled("No animations match this search.");
            return;
        }

        foreach (var modGroup in visible.GroupBy(c => c.GestureModName ?? "Ungrouped").OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var modFlags = filter.Length > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            if (!ImGui.CollapsingHeader($"{modGroup.Key} ({modGroup.Count()})##gestureQuickMod_{modGroup.Key}", modFlags))
                continue;

            ImGui.Indent();
            // Ordered by GestureGroupOrder/GestureOptionOrder (the Sub's own Penumbra manifest order,
            // carried through import - see CatalogSyncService.ImportGestureLines), not alphabetically -
            // an alphabetic sort of option names like "1".."400" would put "10" before "2".
            foreach (var subGroup in modGroup.GroupBy(c => c.GestureGroupName ?? "").OrderBy(g => g.Min(c => c.GestureGroupOrder)))
            {
                var hasGroupLabel = subGroup.Key.Length > 0;
                var groupOpen = true;
                if (hasGroupLabel)
                {
                    var groupFlags = subGroup.Count() <= 4 || filter.Length > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
                    groupOpen = ImGui.TreeNodeEx($"{subGroup.Key}##gestureQuickGroup_{modGroup.Key}_{subGroup.Key}", groupFlags);
                }

                if (groupOpen)
                {
                    // One heading per animation with a short row per pose/emote variant under it (a mod's
                    // "Laying Makeout" exported for Sit Pose 4 and Sit Pose 5 reads as one animation, not
                    // two). Each variant keeps its own Favorite/Send/Copy/Edit/Remove since each is its own
                    // command. Only entries still carrying their imported label are grouped/shortened - an
                    // Owner-renamed entry keeps its own text as a standalone row.
                    var variantsByAnimation = subGroup.OrderBy(c => c.GestureOptionOrder)
                        .Select(c => (Cmd: c, Entry: AutoLabeledGesture(c)))
                        .GroupBy(v => v.Entry?.AnimationName ?? $"\u0001{v.Cmd.Label}\u0001{v.Cmd.Command}");
                    foreach (var variants in variantsByAnimation)
                    {
                        var rows = variants.ToList();
                        if (rows.Count == 1)
                        {
                            var (cmd, entry) = rows[0];
                            DrawSavedQuickRow(cmd, quick, canSend, entry is null ? null : _ => $"{entry.AnimationName} — {entry.Trigger!.Label}");
                            continue;
                        }

                        ImGui.TextUnformatted(variants.Key);
                        ImGui.Indent();
                        foreach (var (cmd, entry) in rows)
                            DrawSavedQuickRow(cmd, quick, canSend, _ => entry!.Trigger!.Label);
                        ImGui.Unindent();
                    }
                }

                if (hasGroupLabel && groupOpen)
                    ImGui.TreePop();
            }
            ImGui.Unindent();
        }
    }

    /// The imported catalog entry behind a gesture quick command, when the command still shows its imported
    /// label (so it's safe to present as mod-less "animation — pose"); null for manual or renamed entries.
    private GestureExportEntry? AutoLabeledGesture(QuickCommand cmd) =>
        cmd.Target is { } target &&
        plugin.Configuration.GestureMapping.ImportedPeerCatalog.TryGetValue(target, out var entry) &&
        entry.Trigger is not null && cmd.Label == entry.DisplayLabel
            ? entry
            : null;

    /// Follow has no reserved-keyword override the way Title/Outfit/Gesture do (ChatCommandListener never
    /// added a "force follow" - it's always a plain alias), so there's nothing to auto-populate from a
    /// scan. "leash"/"unleash" are AliasBook's own defaults, shown as ready-to-use fixed rows so
    /// there's a working Send/Copy immediately even before the Owner adds anything - if the Sub renamed
    /// their engage/release words, the Owner adds the real ones the same way as any other Quick Command.
    private void DrawFollowQuickSection(bool canSend)
    {
        IconGlyph.Text(FontAwesomeIcon.Link, "Follow / Leash");
        ImGui.Separator();
        DrawFollowQuickSectionBody(canSend);
    }

    private void DrawFollowQuickSectionBody(bool canSend)
    {
        var quick = plugin.Configuration.QuickCommands.Follow;

        // collar/control-vocabulary: leash/unleash are fixed words every Sub understands, so there's nothing
        // to customize any more - only these two, plus any custom follow words saved by an older version
        // (listed so they can be removed; a current Sub ignores them).
        using (Section.Begin("followQuickCommands", "Commands"))
        {
            DrawFixedQuickRow("Leash", ControlWords.Leash, canSend, FixedActionIds.LeashDefault);
            // The leash's own moodle pick, saved - used wherever the Leash command is sent (here, Sub Control,
            // Favorites, the quick-access menu).
            var leashMoodle = plugin.Configuration.QuickCommands.LeashMoodleOverride;
            ImGui.Indent();
            OwnerMoodleOverride.Draw("leash", plugin.Configuration, ref leashMoodle);
            ImGui.Unindent();
            if (leashMoodle != plugin.Configuration.QuickCommands.LeashMoodleOverride)
            {
                plugin.Configuration.QuickCommands.LeashMoodleOverride = leashMoodle;
                plugin.Configuration.Save();
            }
            DrawFixedQuickRow("Unleash", ControlWords.Unleash, canSend, FixedActionIds.UnleashDefault);
        }

        if (quick.Count == 0)
            return;

        using var legacyBox = Section.Begin("followQuickLegacy", "Old custom follow words");
        IconGlyph.WrappedDisabled("Saved by an older version - your Sub's plugin now only understands leash / unleash, so these can be removed.");
        foreach (var cmd in quick.ToArray())
            DrawSavedQuickRow(cmd, quick, canSend);
    }

    /// collar/toy-control: the Sub's own Intiface connection setup - address, connect/disconnect,
    /// connected-device count, and a local "stop now" button independent of any Owner command, for the
    /// Sub's own peace of mind. Direct-override-only category (no alias list) like Restraints/Teleport,
    /// so there is no "Aliases" sub-section here.
    private void DrawToyControlModule()
    {
        IconGlyph.Text(FontAwesomeIcon.Plug, "Toy Control (Intiface)");
        ImGui.Separator();
        var config = plugin.Configuration;
        if (!config.ToyControlAcknowledged)
            IconGlyph.WrappedColored(Theme.Warning, "Requires the dedicated Toy Control acknowledgement in Settings (gear icon) and the Toy Control permission (Permissions tab) before an Owner's commands take effect.");

        if (toyIntifaceAddress.Length == 0)
            toyIntifaceAddress = config.IntifaceAddress;

        var connectionBox = Section.Begin("toyConnection", "Connection");
        var intiface = plugin.IntifaceIpc;
        ImGui.SetNextItemWidth(260);
        using (ImRaii.Disabled(intiface.IsConnected || intiface.IsConnecting))
            ImGui.InputText("Intiface address##toyControl", ref toyIntifaceAddress, 128);
        IconGlyph.HelpMarker("Intiface Central's own WebSocket address - the default matches Intiface Central's default listen address, change it only if you've configured Intiface differently.");

        if (intiface.IsConnected)
        {
            ContinueRowOrWrap(ButtonWidth("Disconnect"));
            if (ImGui.SmallButton("Disconnect##toyControl"))
                intiface.Disconnect();
        }
        else
        {
            var connectLabel = intiface.IsConnecting ? "Connecting..." : "Connect";
            ContinueRowOrWrap(ButtonWidth(connectLabel));
            using (ImRaii.Disabled(intiface.IsConnecting || toyIntifaceAddress.Trim().Length == 0))
            if (ImGui.SmallButton($"{connectLabel}##toyControl"))
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
        if (ImGui.SmallButton("Stop now##toyControlLocal"))
            plugin.ToyControlCommand.ForceStop();
        IconGlyph.HelpMarker("Stops every connected device immediately, independent of anything your Owner sent - for your own peace of mind, not tied to any permission.");
        connectionBox.Dispose();

        using (Section.Begin("toyStatus", "Status"))
            ToyStatusView.DrawSub(plugin.ToyControlCommand, intiface.IsConnected);

        var limitsBox = Section.Begin("toyLimits", "Limits");
        var defaultMaxDuration = config.DefaultMaxDurationSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Default max duration (s)##toyControlDefaultMax", ref defaultMaxDuration, 1, ToyControlCommand.MaxDurationSeconds))
        {
            config.DefaultMaxDurationSeconds = defaultMaxDuration;
            config.Save();
        }
        IconGlyph.HelpMarker($"How long a command runs when no duration was specified - an untimed vibrate, or any pattern's own outer ceiling. This can only shorten the {ToyControlCommand.MaxDurationSeconds}s hard ceiling, never lengthen it - an Owner's explicitly timed command is still capped at {ToyControlCommand.MaxDurationSeconds}s regardless of this setting. Permanent-mode commands use the separate setting just below instead.");

        var permanentBackstop = config.PermanentBackstopSeconds;
        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("Permanent-mode backstop (s)##toyControlPermanentBackstop", ref permanentBackstop))
        {
            config.PermanentBackstopSeconds = Math.Max(1, permanentBackstop);
            config.Save();
        }
        IconGlyph.HelpMarker("The hard ceiling a permanent-mode vibrate/pattern command is held to - still a real limit, just a much longer one than the default max duration above. Defaults to 14400 seconds (4 hours). Applies only when a command explicitly requests permanent mode.");
        limitsBox.Dispose();

        using (Section.Begin("toyPatterns"))
            DrawToyPatternEditor();

        using (Section.Begin("toyTriggers"))
            DrawToyTriggerEditor();
    }

    /// collar/toy-control "Sub-authored custom vibration patterns": add/edit/delete named step-sequence
    /// patterns. On the Sub's own client these are usable anywhere a built-in pattern name is (an Owner
    /// command, or a local trigger). An Owner has the identical editor for their own library of patterns
    /// to send, but since the Sub's client hasn't necessarily saved any of them, an Owner's saved pattern
    /// is sent by value (its full step sequence inline in the command, see `ToyControlCommand.
    /// BuildCustomSequenceCommand`) rather than by name - `ownerCanSend` non-null switches on that "Send"
    /// button per saved pattern instead of the Sub-only trigger-reference bookkeeping.
    private void DrawToyPatternEditor(bool? ownerCanSend = null)
    {
        if (!ImGui.CollapsingHeader("Custom Patterns##toyPatternHeader"))
            return;

        var config = plugin.Configuration;

        if (config.ToyPatterns.Count > 0)
        {
            ImGui.Indent();
            var patterns = config.ToyPatterns;
            var labels = patterns.Select(p => p.Name).ToArray();
            var i = DrawItemSelector("##toyPatternSelect", labels, ref selectedToyPatternIndex);
            var pattern = patterns[i];

            ImGui.TextUnformatted($"{pattern.Steps.Count} step(s), {(pattern.Loop ? "loops" : "no loop")}");
            if (ImGui.SmallButton($"Edit##toyPattern{pattern.Id}"))
            {
                toyPatternEditingId = pattern.Id;
                toyPatternNameInput = pattern.Name;
                toyPatternLoopInput = pattern.Loop;
                toyPatternStepsInput.Clear();
                toyPatternStepsInput.AddRange(pattern.Steps.Select(s => new PatternStep { IntensityPercent = s.IntensityPercent, DurationMs = s.DurationMs }));
            }
            ContinueRowOrWrap(ButtonWidth("Delete"));
            if (ImGui.SmallButton($"Delete##toyPattern{pattern.Id}"))
            {
                config.ToyPatterns.Remove(pattern);
                // collar/toy-control "Sub deletes a custom pattern currently referenced by a trigger":
                // disable (not silently orphan) any trigger rule that named this pattern. A no-op for
                // an Owner's own library - triggers are a Sub-local concept, so an Owner's
                // ToyTriggerRules is always empty.
                foreach (var rule in config.ToyTriggerRules.Where(r => string.Equals(r.PatternName, pattern.Name, StringComparison.OrdinalIgnoreCase)))
                    rule.Enabled = false;
                config.Save();
                if (toyPatternEditingId == pattern.Id)
                    ResetToyPatternInput();
                selectedToyPatternIndex = null;
            }
            if (ownerCanSend is { } canSend)
            {
                ContinueRowOrWrap(ButtonWidth("Send"));
                DrawSendOnly(ToyControlCommand.BuildCustomSequenceCommand(pattern.Steps, pattern.Loop), canSend, $"toyPatternSend{pattern.Id}", "Send");
                IconGlyph.HelpMarker("Sends this pattern's full step sequence directly to your paired Sub, by value - they don't need to have anything saved for this to work, and it doesn't add anything to their own saved pattern list.");
            }
            ImGui.Unindent();
        }

        ImGui.Spacing();
        IconGlyph.WrappedDisabled(toyPatternEditingId is null ? "New pattern" : "Editing pattern");
        ImGui.SetNextItemWidth(200);
        ImGui.InputText("Name##toyPatternName", ref toyPatternNameInput, 64);
        IconGlyph.HelpMarker("A name for this pattern, used to select it later - can't match a built-in preset name (weak/medium/strong/pulse) or another custom pattern you've already saved.");
        ImGui.Checkbox("Loop##toyPatternLoop", ref toyPatternLoopInput);
        IconGlyph.HelpMarker("If checked, the steps below repeat from the start once the last one ends, continuing until stopped, panic, or the safety ceiling. If unchecked, the pattern plays through the steps once and then stops on its own.");
        ImGui.Spacing();

        ImGui.Indent();
        for (var i = 0; i < toyPatternStepsInput.Count; i++)
        {
            var step = toyPatternStepsInput[i];
            IconGlyph.WrappedDisabled($"Step {i + 1}");
            ImGui.SetNextItemWidth(140);
            var intensity = step.IntensityPercent;
            if (ImGui.SliderInt($"Intensity##toyPatternStep{i}", ref intensity, 0, 100, "%d%%"))
                step.IntensityPercent = intensity;
            IconGlyph.HelpMarker("How strong the vibration is while this step is active.");

            ContinueRowOrWrap(160);
            ImGui.SetNextItemWidth(120);
            var durationMs = step.DurationMs;
            if (ImGui.InputInt($"ms##toyPatternStepDuration{i}", ref durationMs))
                step.DurationMs = Math.Max(0, durationMs);
            IconGlyph.HelpMarker("How long this step lasts, in milliseconds, before moving to the next step. 0 means this step holds indefinitely - it never advances on its own, only stopping at the pattern's overall ceiling, an explicit stop, or panic.");

            ContinueRowOrWrap(ButtonWidth("Up"));
            using (ImRaii.Disabled(i == 0))
            if (ImGui.SmallButton($"Up##toyPatternStep{i}"))
            {
                (toyPatternStepsInput[i - 1], toyPatternStepsInput[i]) = (toyPatternStepsInput[i], toyPatternStepsInput[i - 1]);
                break;
            }

            ContinueRowOrWrap(ButtonWidth("Down"));
            using (ImRaii.Disabled(i == toyPatternStepsInput.Count - 1))
            if (ImGui.SmallButton($"Down##toyPatternStep{i}"))
            {
                (toyPatternStepsInput[i + 1], toyPatternStepsInput[i]) = (toyPatternStepsInput[i], toyPatternStepsInput[i + 1]);
                break;
            }

            ContinueRowOrWrap(ButtonWidth("Remove"));
            if (ImGui.SmallButton($"Remove##toyPatternStep{i}"))
            {
                toyPatternStepsInput.RemoveAt(i);
                break;
            }

            if (i < toyPatternStepsInput.Count - 1)
                ImGui.Separator();
        }
        ImGui.Unindent();

        ImGui.Spacing();
        if (ImGui.SmallButton("Add step##toyPattern"))
            toyPatternStepsInput.Add(new PatternStep { IntensityPercent = 50, DurationMs = 500 });

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.SmallButton("Save pattern##toyPattern"))
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
                ResetToyPatternInput();
            }
        }
        if (toyPatternEditingId is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Cancel##toyPattern"))
                ResetToyPatternInput();
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

    /// collar/toy-control "Local automatic toy triggers": entirely gated on `ToyTriggersAcknowledged` -
    /// deliberately not on `PermissionSet.ToyControl` (that permission governs Owner-initiated commands
    /// only) and not on `ToyControlAcknowledged` (that acknowledgement's disclosure is about an Owner
    /// directly actuating a device, not the Sub's own device self-triggering off local game state).
    private void DrawToyTriggerEditor()
    {
        if (!ImGui.CollapsingHeader("Automatic Triggers##toyTriggerHeader"))
            return;

        var config = plugin.Configuration;

        if (!config.ToyTriggersAcknowledged)
        {
            IconGlyph.WrappedColored(Theme.Warning, "Requires the dedicated Automatic Triggers acknowledgement in Settings (gear icon) first - triggers act on their own, with no per-occurrence click, so they need their own explicit consent separate from Toy Control's.");
            return;
        }

        var runtimeState = plugin.RuntimeState;
        if (runtimeState.ToyTriggersSuspended)
        {
            IconGlyph.WrappedColored(Theme.Warning, "Triggers are suspended (panic was triggered). They will not fire again until you resume them.");
            if (ImGui.SmallButton("Resume triggers##toyTriggers"))
                runtimeState.ToyTriggersSuspended = false;
            IconGlyph.HelpMarker("Re-arms every enabled trigger below. Panic suspends all trigger evaluation on purpose, so nothing can fire again immediately after a panic - this is the only way to turn evaluation back on.");
            ImGui.Spacing();
        }

        if (config.ToyTriggerRules.Count > 0)
        {
            ImGui.Indent();
            foreach (var rule in config.ToyTriggerRules.ToList())
            {
                var enabled = rule.Enabled;
                if (ImGui.Checkbox($"##toyTriggerEnabled{rule.Id}", ref enabled))
                {
                    rule.Enabled = enabled;
                    config.Save();
                }
                ImGui.SameLine();
                // Long job/action filters are summarized as a count here, with the full list on hover.
                ImGui.PushTextWrapPos(0);
                ImGui.TextUnformatted(DescribeTrigger(rule));
                ImGui.PopTextWrapPos();
                var fullDescription = DescribeTrigger(rule, full: true);
                if (ImGui.IsItemHovered() && fullDescription != DescribeTrigger(rule))
                {
                    ImGui.BeginTooltip();
                    ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
                    ImGui.TextUnformatted(fullDescription);
                    ImGui.PopTextWrapPos();
                    ImGui.EndTooltip();
                }
                if (ImGui.SmallButton(editingToyTriggerId == rule.Id ? $"Editing##toyTrigger{rule.Id}" : $"Edit##toyTrigger{rule.Id}"))
                    LoadToyTriggerDraft(rule);
                ContinueRowOrWrap(ButtonWidth("Delete"));
                if (ImGui.SmallButton($"Delete##toyTrigger{rule.Id}"))
                {
                    config.ToyTriggerRules.Remove(rule);
                    config.Save();
                    if (editingToyTriggerId == rule.Id)
                        ResetToyTriggerDraft();
                }

                if (rule.PatternName is { Length: > 0 } targetPattern && !IsKnownPatternName(config, targetPattern))
                    IconGlyph.WrappedColored(Theme.Warning, $"\"{targetPattern}\" no longer exists - this trigger needs reconfiguration.");

                ImGui.Separator();
            }
            ImGui.Unindent();
        }

        ImGui.Spacing();
        var editingRule = editingToyTriggerId is { } editingId ? config.ToyTriggerRules.FirstOrDefault(r => r.Id == editingId) : null;
        if (editingToyTriggerId is not null && editingRule is null)
            ResetToyTriggerDraft();
        IconGlyph.WrappedDisabled(editingRule is null ? "New trigger" : "Edit trigger");
        ImGui.Spacing();
        var kindNames = new[] { "Health drops below %", "Hit (damage from anything)", "A restriction becomes active", "Spell cast on you", "Emote used on you" };
        var kindIndex = (int)toyTriggerKindInput;
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Condition##toyTriggerKind", ref kindIndex, kindNames, kindNames.Length))
            toyTriggerKindInput = (ToyTriggerKind)kindIndex;
        IconGlyph.HelpMarker("What local game-state event fires this trigger. Every option runs entirely on your own client - no message is ever sent to or from your Owner.");

        if (toyTriggerKindInput == ToyTriggerKind.HealthPercent)
        {
            ImGui.SetNextItemWidth(120);
            ImGui.SliderInt("Threshold %##toyTriggerHealth", ref toyTriggerHealthThresholdInput, 1, 100);
            IconGlyph.HelpMarker("Fires the first time your health drops to or below this percentage. It won't fire again while health stays below the threshold - it needs to rise back above it first (or the cooldown alone gates a fast in-and-out).");
        }
        else if (toyTriggerKindInput == ToyTriggerKind.RestrictionActive)
        {
            var restrictionNames = Enum.GetNames<RestraintRuleKind>();
            var restrictionIndex = (int)toyTriggerRestrictionKindInput;
            ImGui.SetNextItemWidth(200);
            if (ImGui.Combo("Restriction##toyTriggerRestriction", ref restrictionIndex, restrictionNames, restrictionNames.Length))
                toyTriggerRestrictionKindInput = (RestraintRuleKind)restrictionIndex;
            IconGlyph.HelpMarker("Fires the first time this restriction becomes active on you, from any device that applies it - not continuously while it stays active.");
        }
        else if (toyTriggerKindInput == ToyTriggerKind.PlayerDamage)
        {
            IconGlyph.WrappedDisabled("Fires each time you take damage from anything - another player, an enemy, or any other NPC. Add players below to only react to them (which rules out NPCs).");
            DrawToyTriggerSourcePlayers();
        }
        else if (toyTriggerKindInput == ToyTriggerKind.SpellCastOnYou)
        {
            IconGlyph.WrappedDisabled("Fires each time an action is used on you by another player character - damage, heal, buff, or debuff alike, never from an NPC. Optionally narrow it to specific job(s) and/or specific spell(s)/skill(s) below; leave both empty to match any action from any player.");
            DrawToySpellJobFilter();
            DrawToySpellActionFilter();
            DrawToyTriggerSourcePlayers();
        }
        else
        {
            IconGlyph.WrappedDisabled("Fires when another player uses an emote aimed at you (they have you targeted when they use it). Optionally narrow it to specific emote(s) and/or specific players; leave both empty to react to any emote from anyone.");
            DrawToyEmoteFilter();
            DrawToyTriggerSourcePlayers();
        }

        ImGui.Checkbox("Named pattern (instead of a fixed intensity)##toyTriggerUsePattern", ref toyTriggerUsePatternInput);
        IconGlyph.HelpMarker("Off: fire a plain vibration at a fixed intensity (with its own optional duration below). On: fire a named pattern - a built-in preset (weak/medium/strong/pulse) or one of your own saved custom patterns - instead, using that pattern's own timing.");
        if (toyTriggerUsePatternInput)
        {
            ImGui.SetNextItemWidth(180);
            ImGui.InputText("Pattern name##toyTriggerPattern", ref toyTriggerPatternNameInput, 64);
            IconGlyph.HelpMarker("Must exactly match a built-in preset name (weak/medium/strong/pulse) or one of your saved custom patterns - see the Custom Patterns section above. An unrecognized name means this trigger will fail closed and never actually fire.");
        }
        else
        {
            ImGui.SetNextItemWidth(140);
            ImGui.SliderInt("Intensity##toyTriggerIntensity", ref toyTriggerIntensityInput, 0, 100, "%d%%");
            IconGlyph.HelpMarker("How strong the vibration is when this trigger fires.");

            ImGui.Checkbox("Duration##toyTriggerHasDuration", ref toyTriggerHasDurationInput);
            IconGlyph.HelpMarker("How long the vibration runs once this trigger fires, before it stops on its own. Leave unchecked to use the default safety ceiling instead of a specific duration - either way, a hard local ceiling always applies.");
            if (toyTriggerHasDurationInput)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("seconds##toyTriggerDuration", ref toyTriggerDurationSecondsInput, 1, ToyControlCommand.MaxDurationSeconds);
            }
        }

        ImGui.SetNextItemWidth(120);
        ImGui.SliderInt("Cooldown (s)##toyTriggerCooldown", ref toyTriggerCooldownInput, 2, 300);
        IconGlyph.HelpMarker("Minimum time between firings of this trigger, even if its condition becomes true again sooner. A 2-second floor always applies regardless of this value.");

        ImGui.Spacing();
        if (editingRule is not null)
        {
            if (ImGui.SmallButton("Save trigger##toyTrigger"))
            {
                ApplyToyTriggerDraft(editingRule);
                config.Save();
                ResetToyTriggerDraft();
            }
            ContinueRowOrWrap(ButtonWidth("Cancel"));
            if (ImGui.SmallButton("Cancel##toyTrigger"))
                ResetToyTriggerDraft();
        }
        else if (ImGui.SmallButton("Add trigger##toyTrigger"))
        {
            var rule = new ToyTriggerRule { Enabled = true };
            ApplyToyTriggerDraft(rule);
            config.ToyTriggerRules.Add(rule);
            config.Save();
        }
    }

    /// Writes the editor's working state onto a rule - a fresh one for Add, or the existing rule in place for
    /// Save (keeping its Id and Enabled state, so the evaluator's per-rule cooldown tracking carries over).
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

    /// collar/toy-control "Emote used on you": a filterable multi-select of emotes (a few hundred rows, so
    /// the list is always shown and narrowed by the search box) - checking none means "any emote".
    private void DrawToyEmoteFilter()
    {
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##toyTriggerEmoteSearch", "Filter emotes...", ref toyTriggerEmoteSearch, 32);
        IconGlyph.HelpMarker("Which emote(s) this trigger reacts to. Leave every emote unchecked to react to any emote aimed at you.");
        if (toyTriggerEmoteIdsInput.Count > 0)
        {
            ContinueRowOrWrap(ButtonWidth("Clear"));
            if (ImGui.SmallButton("Clear##toyTriggerEmotes"))
                toyTriggerEmoteIdsInput.Clear();
        }

        using var _ = ImRaii.Child("toyTriggerEmoteList", new Vector2(0, 110), true);
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

    /// collar/toy-control: the optional "only these players" filter shared by the hit, spell-cast and emote
    /// triggers - typed as "Name Surname" (any world) or "Name Surname@World", or added in one click from
    /// the current target or a paired peer.
    private void DrawToyTriggerSourcePlayers()
    {
        Section.SubHeading("Only from these players");
        IconGlyph.HelpMarker("Leave empty to react to anyone. With players listed, only they can fire this trigger. \"Name Surname\" matches that name on any world; \"Name Surname@World\" matches only that world.");

        foreach (var entry in toyTriggerSourcePlayersInput.ToList())
        {
            ImGui.TextUnformatted(entry);
            ContinueRowOrWrap(ButtonWidth("Remove"));
            if (ImGui.SmallButton($"Remove##toyTriggerSource{entry}"))
                toyTriggerSourcePlayersInput.Remove(entry);
        }

        void AddPlayer(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length > 0 && !toyTriggerSourcePlayersInput.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                toyTriggerSourcePlayersInput.Add(trimmed);
        }

        ImGui.SetNextItemWidth(220);
        var submitted = ImGui.InputTextWithHint("##toyTriggerSourceInput", "Name Surname[@World]", ref toyTriggerSourcePlayerInput, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ContinueRowOrWrap(ButtonWidth("Add"));
        if ((ImGui.SmallButton("Add##toyTriggerSource") || submitted) && toyTriggerSourcePlayerInput.Trim().Length > 0)
        {
            AddPlayer(toyTriggerSourcePlayerInput);
            toyTriggerSourcePlayerInput = "";
        }

        var target = Plugin.TargetManager.Target as Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter;
        using (ImRaii.Disabled(target is null))
        {
            if (ImGui.SmallButton("Add my target##toyTriggerSource") && target is not null)
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
            if (ImGui.SmallButton($"{label}##toyTriggerSourcePeer{pairing.Id}"))
                AddPlayer(peer);
        }
    }

    /// collar/toy-control "Spell cast on you": a compact multi-select of playable combat jobs (Lumina's
    /// `ClassJob` sheet has ~40 rows, small enough to render inline without a search-gated list like the
    /// action picker below needs) - checking none means "any job".
    private void DrawToySpellJobFilter()
    {
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##toyTriggerSpellJobSearch", "Filter jobs...", ref toyTriggerSpellJobSearch, 32);
        IconGlyph.HelpMarker("Which caster job(s) this trigger reacts to. Leave every job unchecked to match any job.");

        using var _ = ImRaii.Child("toyTriggerSpellJobList", new Vector2(0, 90), true);
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

    /// collar/toy-control "Spell cast on you": the `Action` sheet has thousands of rows, so this requires
    /// search text before listing anything (capped at 100 matches) rather than rendering every player
    /// action inline - checking none means "any action". Restricted to `IsPlayerAction` rows so the list
    /// is actual castable player skills/spells, not every internal action-table entry.
    private void DrawToySpellActionFilter()
    {
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##toyTriggerSpellActionSearch", "Search skill/spell name...", ref toyTriggerSpellActionSearch, 32);
        IconGlyph.HelpMarker("Which specific action(s) this trigger reacts to. Leave every action unchecked to match any action. Type at least part of a skill/spell name to search - the full list is too large to show at once.");

        var search = toyTriggerSpellActionSearch.Trim();
        if (search.Length == 0)
        {
            foreach (var actionId in toyTriggerSpellActionIdsInput.ToList())
            {
                var selectedName = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(actionId)?.Name.ExtractText() ?? actionId.ToString();
                ImGui.TextUnformatted($"Selected: {selectedName}");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Remove##toyTriggerSpellAction{actionId}"))
                    toyTriggerSpellActionIdsInput.Remove(actionId);
            }
            return;
        }

        using var _ = ImRaii.Child("toyTriggerSpellActionList", new Vector2(0, 90), true);
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

    /// Compact by default: more than a few jobs/actions collapse to a count ("12 jobs") so the trigger row
    /// stays readable; `full` lists every one (used for the row's hover tooltip).
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

    /// collar/toy-control: the Owner's direct-override controls - an intensity slider with an optional
    /// duration, the fixed named patterns, and an explicit stop, each built via ToyControlCommand's static
    /// wire-grammar builders and sent through the same single-click DrawSendOnly path every other
    /// category's Owner quick-section already uses.
    private void DrawToyControlQuickSection(bool canSend)
    {
        IconGlyph.Text(FontAwesomeIcon.Plug, "Toy Control");
        ImGui.Separator();
        IconGlyph.WrappedDisabled($"Commands auto-stop after at most {ToyControlCommand.MaxDurationSeconds} seconds, regardless of the duration requested here.");

        using (Section.Begin("toyQuickStatus", "Status"))
            ToyStatusView.DrawOwner(plugin.OwnerToyStatus.ForActivePairing);

        var vibrateBox = Section.Begin("toyQuickVibrate", "Vibrate");
        ImGui.SetNextItemWidth(200);
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
                ImGui.SetNextItemWidth(120);
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
        IconGlyph.WrappedDisabled("Multi-action Custom Trigger bundles your Sub shares land in Saved at the top - a single-action alias imports straight into its own category instead. Type a bundle alias your Sub told you about, or any one-off override. Add Command saves it as a one-click button in Saved at the top.");

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
        IconGlyph.HelpMarker("Send fires this one /tell immediately - the same one-click, one-message shape as pressing an FFXIV macro, and the only thing in this plugin that ever sends chat for you. Copy never sends anything. Add Command saves the text above as a one-click button in Saved at the top.");
    }

    /// Saved bundles and commands (imported from the Sub, or saved by the builder/one-off composer) - drawn
    /// first in the Custom Triggers tab, since they're what the Owner reaches for most.
    private void DrawSavedAliasCommands(bool canSend)
    {
        var aliasQuick = plugin.Configuration.QuickCommands.Aliases;
        if (aliasQuick.Count == 0)
        {
            IconGlyph.WrappedDisabled("Nothing saved yet - bundles your Sub shares, and ones you save below, show up here.");
            return;
        }

        using var _ = Section.List("aliasQuickList", 300);
        foreach (var cmd in aliasQuick.ToArray())
            DrawSavedQuickRow(cmd, aliasQuick, canSend);
    }

    /// A built-in action (not user-saved, can't be removed) - "Clear title" and "Unlock outfit" always
    /// exist since every force-locked category needs a release valve regardless of what's been saved.
    /// `favoriteId` is one of the stable `FixedActionIds` constants - collar/ui-organization "Owner can
    /// favorite ... built-in fixed-action row[s]".
    private void DrawFixedQuickRow(string label, string command, bool canSend, string favoriteId)
    {
        ImGui.TextUnformatted(label);
        ContinueRowOrWrap(ButtonWidth("Favorited"));
        DrawFavoriteFixedActionToggle(favoriteId);
        ContinueRowOrWrap(ButtonWidth("Send"));
        DrawSendCopyButtons(OwnerMoodleOverride.ForSend(plugin.Configuration, command, null), canSend, $"fixed_{label}");
    }

    /// `displayLabel` lets a category-specific caller (e.g. Moodles, see collar/moodles' markup-stripping
    /// requirement) transform `cmd.Label` for display only - `cmd.Label` itself, the id-suffix strings
    /// below, and `cmd.Command` all keep using the raw stored value, since only display should ever change.
    private void DrawSavedQuickRow(QuickCommand cmd, List<QuickCommand> list, bool canSend, Func<string, string>? displayLabel = null)
    {
        var shownLabel = displayLabel?.Invoke(cmd.Label) ?? cmd.Label;
        if (cmd.Command.StartsWith("outfit wear ", StringComparison.OrdinalIgnoreCase))
            shownLabel += "  · not locked";
        // collar/attached-moodles: a command's own moodle pick shows right on its row.
        if (cmd.MoodleOverride is { } rowMoodle && OwnerMoodleOverride.Accepts(cmd.Command))
            shownLabel += $"  · moodle: {rowMoodle}";
        ImGui.TextUnformatted(shownLabel);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(shownLabel);
        ContinueRowOrWrap(ButtonWidth("Favorited"));
        DrawFavoriteToggle(cmd, $"{cmd.Label}_{cmd.Command}");
        ContinueRowOrWrap(ButtonWidth("Send"));
        DrawSendCopyButtons(OwnerMoodleOverride.ForSend(plugin.Configuration, cmd), canSend, $"{cmd.Label}_{cmd.Command}");
        // Edit expands this row's own editor right beneath it (and collapses it again) - a bundle is the
        // one exception, loaded into the bundle builder instead.
        var expanded = ReferenceEquals(editingQuickCommand, cmd);
        var editLabel = expanded ? "Close" : "Edit";
        ContinueRowOrWrap(ButtonWidth(editLabel));
        if (ImGui.SmallButton($"{editLabel}##{cmd.Label}_{cmd.Command}"))
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
        if (ImGui.SmallButton($"Remove##{cmd.Label}_{cmd.Command}"))
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

    /// A shared focused draft editor for Owner entries, drawn inline under the row being edited
    /// (DrawSavedQuickRow). Nothing touches the stored object until Save;
    /// Cancel and validation failures therefore remain lossless, and reference identity detects a row
    /// removed while the editor was open.
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
                IconGlyph.HelpMarker("Locked: your Sub can't change the outfit's pieces until you unlock it (restraints can still go over it). Not locked: the outfit is applied and your Sub is free to change it.");
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
                    source.MoodleOverride = editingQuickMoodle;
                if (editingQuickCategory == QuickEditCategory.Title)
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

    /// collar/ui-organization "Owner can favorite quick commands for quick access": a plain toggle shared
    /// by every quick-command row (DrawSavedQuickRow covers Title/Outfit/Gesture/Follow/Moodles/Aliases;
    /// DrawRestraintQuickRow calls this too, since its row layout is its own bespoke one) - never affects
    /// Send/Copy/Remove or any other per-row state.
    private void DrawFavoriteToggle(QuickCommand cmd, string idSuffix)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Warning, cmd.IsFavorite))
        {
            if (ImGui.SmallButton($"{(cmd.IsFavorite ? "Favorited" : "Favorite")}##fav_{idSuffix}"))
            {
                cmd.IsFavorite = !cmd.IsFavorite;
                plugin.Configuration.Save();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cmd.IsFavorite ? "Remove from favorites" : "Add to favorites");
    }

    /// Same shape as `DrawFavoriteToggle`, for a built-in fixed action (no backing `QuickCommand` to attach
    /// a bool to) - reads/writes `OwnerQuickCommands.FavoriteFixedActions` by the action's stable id instead.
    private void DrawFavoriteFixedActionToggle(string favoriteId)
    {
        var favorites = plugin.Configuration.QuickCommands.FavoriteFixedActions;
        var isFavorite = favorites.Contains(favoriteId);
        using (ImRaii.PushColor(ImGuiCol.Text, Theme.Warning, isFavorite))
        {
            if (ImGui.SmallButton($"{(isFavorite ? "Favorited" : "Favorite")}##fav_{favoriteId}"))
            {
                if (isFavorite) favorites.Remove(favoriteId);
                else favorites.Add(favoriteId);
                plugin.Configuration.Save();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(isFavorite ? "Remove from favorites" : "Add to favorites");
    }

    /// collar/title: shared "optional glow" control for every title-styling form (Sub alias creation, both
    /// Custom Trigger action editors, Owner quick-command create/edit) - a checkbox that reveals a
    /// `ColorEdit3` when enabled. `hasGlow`/`glow` are plain local editor state (matching every other
    /// title-styling field's shape, see design.md), converted to a `Vector3?` only when the caller builds
    /// its `TitleAliasDefinition`/`CustomTriggerAction`/`QuickCommand`.
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
            if (ImGui.SmallButton($"{sendLabel}##{idSuffix}"))
                plugin.ChatSender.SendAll(messages);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(!fits ? "Command is too long for a safe chat payload." : canSend ? string.Join("\n", messages) : "No /tell target yet - pairing hasn't captured your Sub's name.");

        ContinueRowOrWrap(ButtonWidth("Copy"));
        // A command that goes out as several messages can't be pasted as one - only Send spaces them out.
        using (ImRaii.Disabled(!fits || messages.Count > 1))
        if (ImGui.SmallButton($"Copy##{idSuffix}"))
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
            if (ImGui.SmallButton($"{label}##{idSuffix}"))
                plugin.ChatSender.SendAll(messages);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(!fits ? "Command is too long for a safe chat payload." : canSend ? string.Join("\n", messages) : "No /tell target yet - pairing hasn't captured your Sub's name.");
    }

    /// collar/ui-organization "configured-item lists use a compact selector once they can grow past a
    /// handful": shared by every module that lets the Sub build up a list of named things (title/outfit/
    /// gesture/moodle/restraint aliases, restraint devices, custom triggers, toy patterns) - one combo box
    /// to pick which item to inspect, instead of always rendering every item's full detail inline (which
    /// gets unreadable fast once there are more than a handful). Not used for the Owner's saved
    /// quick-command rows (DrawSavedQuickRow) - those are a one-click-Send toolbar, not a browse-then-edit
    /// list, and hiding all-but-one behind a dropdown would work against that - nor for the Sub's configured
    /// mod restraints (DrawSubModRestraints), which already collapse to one line each via their own
    /// per-item Configure/Close toggle.
    private static int DrawItemSelector(string comboId, string[] labels, ref int? selectedIndex)
    {
        var index = Math.Clamp(selectedIndex ?? 0, 0, labels.Length - 1);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo(comboId, ref index, labels, labels.Length))
            selectedIndex = index;
        selectedIndex ??= index;
        return index;
    }

    /// "title"/"outfit"/"gesture" are reserved for the Owner's direct override grammar (see
    /// ChatCommandListener.ReservedCategoryWords) - an alias with one of these exact names would be
    /// permanently unreachable, since the listener always routes to the override handler first.
    /// Category words (the Owner's direct overrides) and the fixed control words (collar/control-vocabulary)
    /// are both matched before any Sub alias, so an alias with either name could never fire.
    private static bool IsReserved(string alias) =>
        ChatCommandListener.ReservedCategoryWords.Contains(alias.Trim(), StringComparer.OrdinalIgnoreCase)
        || ControlWords.All.Contains(alias.Trim(), StringComparer.OrdinalIgnoreCase);

    private static void DrawReservedWordWarning(string alias)
    {
        if (IsReserved(alias))
            IconGlyph.WrappedColored(Theme.Warning, $"\"{alias.Trim()}\" is reserved for a fixed Owner command - pick a different alias.");
    }

    /// collar/control-vocabulary "Control words are fixed": shown as read-only information, not an edit field.
    private static void DrawFixedWord(string label, string word)
    {
        ImGui.TextUnformatted($"{label}:");
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.AccentHover);
        ImGui.TextUnformatted(word);
        ImGui.PopStyleColor();
    }

    /// collar/attached-moodles "Sub can attach a default moodle": "None" plus the Sub's own scanned Moodles
    /// statuses. Returns true when the pick changed; the caller stores `picked` and saves.
    /// `width` null keeps the surrounding form's default item width, so the picker lines up with the other
    /// dropdowns in a form instead of being narrower.
    private bool DrawAttachedMoodlePicker(string id, AttachedMoodleRef? current, PluginConfig config, out AttachedMoodleRef? picked, float? width = 220)
    {
        picked = current;
        var preview = current is null ? "None" : MoodlesTextFormat.StripMarkup(current.StatusName);
        if (width is { } w)
            ImGui.SetNextItemWidth(w);

        // collar/ui-organization "Partly dependent features are disabled inline": the attached moodle is the
        // only part of Outfit/Restraints/Follow that needs Moodles.
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
        IconGlyph.HelpMarker("Applied together with this, and removed when it's cleared - only this one moodle, never your others. Your Owner can pick a different one per command if your Moodles permission is on.");
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
