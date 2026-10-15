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
            case "cage":
                DrawCageModule(isOwner);
                break;
            case "games":
                DrawGamesModule();
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
}
