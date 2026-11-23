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
            Status.Line(StatusKind.Error, blockedReason);
            IconGlyph.WrappedDisabled("Install or enable it from /xlplugins. This window updates once it's detected.");
            return;
        }

        var chrome = ModuleChrome.GetValueOrDefault(activeModule, new ModuleChromeEntry(FontAwesomeIcon.Question, activeModule, activeModule, false));
        DrawModuleHeading(chrome.Icon, isOwner ? chrome.OwnerTitle : chrome.SubTitle, isOwner ? OwnerHeadingAction() : null);
        DrawActiveBanner(isOwner);
        var canSend = false;
        if (isOwner && chrome.OwnerSends)
        {
            var target = SendReadiness.For(plugin);
            canSend = target.CanSend;
            if (target.BlockedReason is { } reason)
                Status.Line(StatusKind.Caution, reason);
        }

        switch (activeModule)
        {
            case "title":
                if (isOwner) DrawTitleQuickSection(canSend);
                else DrawTitleModule();
                break;
            case "outfit":
                if (isOwner) DrawOutfitQuickSection(canSend);
                else DrawWardrobeModule();
                break;
            case "animation":
                if (isOwner) DrawGestureQuickSection(canSend);
                else DrawGestureModule();
                break;
            case "moodles":
                if (isOwner) DrawMoodlesQuickSection(canSend);
                else DrawMoodlesModule();
                break;
            case "restraints":
                if (isOwner) DrawRestraintQuickSection(canSend);
                else DrawRestraintsModule();
                break;
            case "toycontrol":
                if (isOwner) DrawToyControlQuickSection(canSend);
                else DrawToyControlModule();
                break;
            case "customtriggers":
                if (isOwner)
                {
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
                if (isOwner) DrawCollarQuickSection(canSend);
                else DrawCollarModule();
                break;
            case "follow":
                // Status spans both columns, so the two columns' boxes start level.
                if (isOwner)
                {
                    OwnerStatusView.Draw(plugin);
                    TwoColumns("followColumns", () => DrawFollowQuickSectionBody(canSend), () => DrawLeashLineSection(plugin.Configuration));
                }
                else
                    TwoColumns("followColumns", DrawFollowLeashModule, () => DrawLeashLineSection(plugin.Configuration));
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
            case "cage":
                DrawCageModule(isOwner, canSend);
                break;
            case "games":
                DrawGamesModule();
                break;
            case "achievements":
                DrawAchievementsModule();
                break;
        }
    }

    /// `OwnerSends`: the Owner's view sends commands, so it shows why it can't when it can't.
    private readonly record struct ModuleChromeEntry(FontAwesomeIcon Icon, string OwnerTitle, string SubTitle, bool OwnerSends);

    /// Every module's heading, drawn by the window so heading, banner and send notice always sit in the same order.
    private static readonly Dictionary<string, ModuleChromeEntry> ModuleChrome = new()
    {
        ["title"] = new(FontAwesomeIcon.Heading, "Title", "Title Aliases", true),
        ["outfit"] = new(FontAwesomeIcon.Tshirt, "Outfit", "Outfit", true),
        ["animation"] = new(FontAwesomeIcon.TheaterMasks, "Animation", "Animation", true),
        ["moodles"] = new(FontAwesomeIcon.Smile, "Moodles", "Moodles Aliases", true),
        ["restraints"] = new(FontAwesomeIcon.Handcuffs, "Restraints", "Restraints", true),
        ["toycontrol"] = new(FontAwesomeIcon.Plug, "Toy Control", "Toy Control (Intiface)", true),
        ["customtriggers"] = new(FontAwesomeIcon.BoltLightning, "Custom Triggers", "Custom Triggers", true),
        ["collar"] = new(FontAwesomeIcon.Lock, "Collar", "Collar", true),
        ["follow"] = new(FontAwesomeIcon.Link, "Follow / Leash", "Follow / Leash", true),
        ["cage"] = new(FontAwesomeIcon.Dungeon, "Cage", "Cage", true),
        // The rulebook names its own pairing problems; its sends say why on hover.
        ["rulebook"] = new(FontAwesomeIcon.Book, "Rulebook", "Rulebook", false),
        ["sync"] = new(FontAwesomeIcon.CloudDownloadAlt, "Sync", "Sync", false),
        ["reactions"] = new(FontAwesomeIcon.Magic, "Reactions", "Reactions", false),
        ["games"] = new(FontAwesomeIcon.Dice, "Games", "Games", false),
        ["achievements"] = new(FontAwesomeIcon.Trophy, "Achievements", "Achievements", false),
    };

    /// The action is right-aligned, or wraps under the heading when the window is too narrow.
    private static void DrawModuleHeading(FontAwesomeIcon icon, string title, (float Width, Action Draw)? action)
    {
        IconGlyph.Text(icon, title);
        if (action is { } a)
        {
            ImGui.SameLine();
            var avail = ImGui.GetContentRegionAvail().X;
            if (avail < a.Width)
                ImGui.NewLine();
            else if (avail > a.Width)
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - a.Width);
            a.Draw();
        }
        ImGui.Separator();
    }

    /// Clear imported, on the Owner lists that hold what the Sub shared.
    private (float Width, Action Draw)? OwnerHeadingAction()
    {
        var quick = plugin.Configuration.QuickCommands;
        return activeModule switch
        {
            "outfit" => (ConfirmDelete.ClearImportedWidth, () => ClearImportedButton("outfitQuick", quick.Outfits, "outfits", QuickList("outfitQuick"))),
            "moodles" => (ConfirmDelete.ClearImportedWidth, () => ClearImportedButton("moodlesQuick", quick.Moodles, "moodles", ownerMoodleList)),
            "restraints" => (ConfirmDelete.ClearImportedWidth, () =>
                ClearImportedButton("restraintsQuick", quick.Restraints, "restraints", ownerRestraintList, cmd => ImageTile.Delete(cmd.ImageFile))),
            _ => null,
        };
    }

    /// Removes only what came from the Sub; what the Owner made or saved stays.
    private void ClearImportedButton(string id, List<QuickCommand> list, string noun, ListDetail listDetail, Action<QuickCommand>? onRemoved = null)
    {
        var imported = list.Where(IsImported).ToList();
        if (!ConfirmDelete.ClearImported(id, imported.Count, noun))
            return;
        foreach (var cmd in imported)
        {
            list.Remove(cmd);
            onRemoved?.Invoke(cmd);
        }
        if (ReferenceEquals(list, plugin.Configuration.QuickCommands.Restraints))
            ImageTile.DeleteUnusedShared(list.Select(c => c.SharedImageFile));
        CancelQuickCommandEdit();
        listDetail.Deleted();
        plugin.Configuration.Save();
    }

    private static bool IsImported(QuickCommand cmd) => cmd.Source == ImportSource.Imported;

    private bool ResolveOwnerModeView()
    {
        var config = plugin.Configuration;
        var isOwner = config.ResolveActiveDirection() == PairingDirection.OwnerSide;

        // No Save here: it's only a fallback default, so it rides along on the next save.
        if (config.Role == PluginRole.Switch && config.SwitchLastUsedOwnerView != isOwner)
            config.SwitchLastUsedOwnerView = isOwner;
        return isOwner;
    }

    private void DrawOpenSettingsButton(string label, SettingsTab tab)
    {
        if (ImGui.Button(label))
            plugin.SettingsWindow.ShowTab(tab);
    }

    /// A permission toggle stays disabled until its acknowledgement is given, so that's the tab to open first.
    private void DrawOpenPermissionButton(bool acknowledged, string id)
    {
        if (!acknowledged)
            DrawOpenSettingsButton($"Open acknowledgements##{id}", SettingsTab.Tos);
        else if (ImGui.Button($"Open Permissions##{id}"))
            plugin.SettingsWindow.ShowPermissionsTab();
    }

    private void DrawGoToSyncTabPrompt(string message)
    {
        IconGlyph.WrappedDisabled(message);
        if (ImGui.Button("Go to Sync"))
            Show("sync");
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

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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
            Status.Line(StatusKind.Caution, $"\"{alias.Trim()}\" is reserved for a fixed Owner command - pick a different alias.");
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
            Status.Line(StatusKind.Error, blocked);
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
            {
                ImGui.TextDisabled("No scanned Moodles statuses yet.");
                if (ImGui.Selectable($"Go to Sync to scan your Moodles##{id}_sync"))
                    Show("sync");
            }
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
}
