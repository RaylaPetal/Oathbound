using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Oathbound.Plugin.UI;

/// Send-only console of every category's full command list. Docked to CollarWindow's right edge every frame,
/// and closes when it does.
public sealed class SubControlWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly CollarWindow collarWindow;

    private string animationSearch = "";
    private string restraintsSearch = "";

    public SubControlWindow(Plugin plugin, CollarWindow collarWindow) : base("Sub Control###CollarSubControlWindow")
    {
        this.plugin = plugin;
        this.collarWindow = collarWindow;
        // Position is glued every frame, so a drag affordance would only snap back.
        Flags = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse;
        // Room for a restraint row's Send, a readable label and the full Timed lock picker.
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(540, 260), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    public void Dispose() { }

    /// ImGuiCond.Always wins any stray drag. Setting IsOpen here takes effect next frame - an accepted one-frame lag.
    public override void PreDraw()
    {
        Theme.PushWindowStyle();
        if (!collarWindow.IsOpen)
        {
            IsOpen = false;
            return;
        }

        ImGui.SetNextWindowPos(collarWindow.LastPosition + new Vector2(collarWindow.LastSize.X, 0f), ImGuiCond.Always);
    }

    public override void PostDraw() => Theme.PopWindowStyle();

    public override void Draw()
    {
        var isOwnerMode = plugin.Configuration.ResolveActiveDirection() == PairingDirection.OwnerSide;
        if (!isOwnerMode)
        {
            IconGlyph.WrappedDisabled("Sub Control is for Owners - open it while paired as Owner.");
            return;
        }

        using var colors = ImRaii.PushColor(ImGuiCol.Header, Theme.TileBg)
            .Push(ImGuiCol.HeaderHovered, Theme.TileBgHover)
            .Push(ImGuiCol.HeaderActive, Theme.TileBgHover)
            .Push(ImGuiCol.FrameBg, Theme.TileBg)
            .Push(ImGuiCol.FrameBgHovered, Theme.TileBgHover);
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Theme.TileRounding);

        var canSend = plugin.Configuration.ActivePairing is { Direction: PairingDirection.OwnerSide };
        var pairing = plugin.Configuration.GetActivePairing();
        estimate = pairing is { Direction: PairingDirection.OwnerSide } ? plugin.OwnerStatusEstimates.ForBanner(pairing) : null;
        collarLocked = pairing is null || plugin.OwnerCollarStatus.For(pairing) is not { } collar || collar.State != CollarStatusReporter.Unlocked;
        if (!canSend)
            IconGlyph.WrappedColored(Theme.Warning, "No /tell target yet - every Send below is disabled until an Owner-side pairing is active.");
        else
            OwnerStatusView.Draw(plugin, includeRestraints: false);
        ImGui.Spacing();

        var categorized = QuickAccessMenu.CategorizedAll(plugin.Configuration.QuickCommands);
        foreach (var (label, commands) in categorized)
        {
            switch (label)
            {
                case "Animation":
                    DrawSearchableCategory(label, commands, canSend, ref animationSearch, MatchesGestureFilter);
                    break;
                case "Restraints":
                    DrawSearchableCategory(label, commands, canSend, ref restraintsSearch, null);
                    break;
                case "Moodles":
                    DrawCategory(label, commands, canSend, MoodlesTextFormat.StripMarkup);
                    break;
                default:
                    DrawCategory(label, commands, canSend, null);
                    break;
            }
        }

        DrawCollarSection(canSend);
        DrawToyControlSection(canSend);
        DrawRulebookSection(canSend);
        DrawCageSection(canSend);
        DrawTeleportRow(canSend);
    }

    /// This frame's estimate of what's on the Sub; null when nothing was sent.
    private OwnerStatusEstimate? estimate;
    /// Unknown counts as locked, so collar unlock is never hidden when it might be needed.
    private bool collarLocked;

    /// The unlock-style commands, each shown only while the estimate says its category has something on.
    private static readonly Dictionary<string, Func<OwnerStatusEstimate, bool>> Releases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["title clear"] = e => e.LastTitle is not null,
        ["outfit unlock"] = e => e.LastOutfit is not null && e.LastOutfitLocked,
        [$"gesture {ChatComposer.StopGestureWord}"] = e => e.LastAnimation is not null,
        [ControlWords.Unleash] = e => e.Leashed,
        ["moodle clear"] = e => e.Moodles.Count > 0,
        ["restraint unlock"] = e => e.Restraints.Count > 0,
    };

    private static readonly Vector4 ReleaseTint = Theme.Warning;

    /// Releases that apply right now, and everything else.
    private (List<QuickCommand> Releases, List<QuickCommand> Others) SplitReleases(List<QuickCommand> commands)
    {
        var releases = commands.Where(c => Releases.ContainsKey(c.Command)).ToList();
        var active = releases.Where(c => estimate is { } e && Releases[c.Command](e)).ToList();
        return (active, commands.Except(releases).ToList());
    }

    private void DrawReleaseRow(string label, string command, bool canSend) =>
        DrawSendRow(label, command, canSend, tint: ReleaseTint);

    /// A collapsing header with the count as a small badge on its right edge instead of "(n)" in the title.
    private static bool CategoryHeader(string label, int? count = null)
    {
        var open = ImGui.CollapsingHeader($"{label}###subControlCategory_{label}");
        if (count is { } n)
        {
            var text = n.ToString();
            var textSize = ImGui.CalcTextSize(text);
            var padding = new Vector2(Layout.Scaled(7f), Layout.Scaled(1f));
            var max = ImGui.GetItemRectMax();
            var min = ImGui.GetItemRectMin();
            var pillMax = new Vector2(max.X - Layout.Scaled(8f), min.Y + (max.Y - min.Y + textSize.Y) / 2 + padding.Y);
            var pillMin = pillMax - textSize - padding * 2;
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(pillMin, pillMax, ImGui.GetColorU32(Theme.Accent with { W = 0.45f }), (pillMax.Y - pillMin.Y) / 2);
            draw.AddText(pillMin + padding, ImGui.GetColorU32(ImGuiCol.Text), text);
        }
        if (open)
            ImGui.Spacing();
        return open;
    }

    /// Only does anything once the Sub has accepted a rulebook; their client ignores it otherwise.
    private void DrawRulebookSection(bool canSend)
    {
        if (!CategoryHeader("Rulebook"))
            return;

        DrawSendRow("Draw a reward", Rulebook.CardPiles.DrawCommand(Rulebook.CardPile.Reward), canSend);
        DrawSendRow("Draw a punishment", Rulebook.CardPiles.DrawCommand(Rulebook.CardPile.Punishment), canSend);
        DrawSendRow("Merit +1", $"{Rulebook.ConsequenceValidator.LedgerWord} +1", canSend);
        DrawSendRow("Demerit -1", $"{Rulebook.ConsequenceValidator.LedgerWord} -1", canSend);
    }

    private static bool MatchesGestureFilter(QuickCommand cmd, string filter) =>
        (cmd.GestureModName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || (cmd.GestureGroupName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private void DrawCategory(string label, List<QuickCommand> all, bool canSend, Func<string, string>? displayLabel)
    {
        var (releases, commands) = SplitReleases(all);
        if (releases.Count + commands.Count == 0)
            return;
        if (!CategoryHeader(label, releases.Count + commands.Count))
            return;

        foreach (var cmd in releases)
            DrawReleaseRow(cmd.Label, cmd.Command, canSend);
        foreach (var cmd in commands)
            DrawSendRow(displayLabel?.Invoke(cmd.Label) ?? cmd.Label, OwnerMoodleOverride.ForSend(plugin.Configuration, cmd), canSend);
    }

    /// Animation and Restraints get their own search, separate from the module windows'.
    private void DrawSearchableCategory(string label, List<QuickCommand> all, bool canSend, ref string search, Func<QuickCommand, string, bool>? extraMatch)
    {
        var (releases, commands) = SplitReleases(all);
        if (releases.Count + commands.Count == 0)
            return;
        if (!CategoryHeader(label, releases.Count + commands.Count))
            return;

        // Each restraint the estimate has on, then unlock-all, above the search.
        if (label == "Restraints" && estimate is { } current)
            foreach (var restraint in current.Restraints.ToList())
                DrawReleaseRow($"Unlock {restraint.Reference} ({LockText(restraint)})", $"restraint unlock {restraint.Reference}", canSend);
        foreach (var cmd in releases)
            DrawReleaseRow(cmd.Label, cmd.Command, canSend);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint($"##subControlSearch_{label}", $"Search {label}...", ref search, 128);

        var filter = search.Trim();
        var visible = filter.Length == 0
            ? commands
            : commands.Where(c => c.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) || (extraMatch?.Invoke(c, filter) ?? false)).ToList();

        if (visible.Count == 0)
            IconGlyph.WrappedDisabled("No commands match this search.");
        else if (label == "Animation")
            DrawAnimationRows(visible, filter.Length > 0, canSend);
        else
        {
            foreach (var cmd in visible)
            {
                // Shares QuickCommand.LockSeconds with the module row.
                var hasLock = OwnerLockOption.Accepts(cmd.Command);
                DrawSendRow(cmd.Label, OwnerMoodleOverride.ForSend(plugin.Configuration, cmd), canSend,
                    trailing: hasLock ? () => OwnerLockOption.DrawStacked($"subControl_{cmd.Label}_{cmd.Command}", cmd, plugin.Configuration) : null);
            }
        }
    }

    private static string LockText(EstimatedRestraint restraint) =>
        (restraint.ExpiresAtUtc is { } until ? $"unlocks in {RestraintLock.Format(until - DateTime.UtcNow)}" : "locked")
        + (restraint.HasKey ? ", key" : "");

    /// Grouped per mod like the Animation module, in the Sub's manifest order. Searching opens every matching mod.
    private void DrawAnimationRows(List<QuickCommand> visible, bool searching, bool canSend)
    {
        // Fixed actions (Stop animation) aren't animations, so they go above the per-mod groups.
        var fixedCommands = QuickAccessMenu.FixedActions.Where(a => a.Category == "Animation").Select(a => a.Command).ToHashSet();
        foreach (var cmd in visible.Where(c => fixedCommands.Contains(c.Command)))
            DrawSendRow(cmd.Label, cmd.Command, canSend);

        foreach (var mod in visible.Where(c => !fixedCommands.Contains(c.Command)).GroupBy(c => c.GestureModName ?? "Other").OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (searching)
                ImGui.SetNextItemOpen(true);
            if (!ImGui.TreeNodeEx($"{mod.Key} ({mod.Count()})##subControlAnimMod_{mod.Key}"))
                continue;

            var rows = mod.OrderBy(c => c.GestureGroupOrder).ThenBy(c => c.GestureOptionOrder)
                .Select(c => (Cmd: c, Entry: ModuleWindow.AutoLabeledGesture(plugin.Configuration, c)))
                .ToList();

            // A big mod gets its own search box.
            var expandVariants = searching;
            if (rows.Count > ModSearchThreshold)
            {
                var modFilter = modSearches.GetValueOrDefault(mod.Key, "");
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputTextWithHint($"##subControlAnimModSearch_{mod.Key}", $"Search {mod.Key}...", ref modFilter, 128))
                    modSearches[mod.Key] = modFilter;
                var trimmed = modFilter.Trim();
                if (trimmed.Length > 0)
                    rows = rows.Where(r => r.Cmd.Label.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                        || (r.Cmd.GestureGroupName?.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
                if (rows.Count == 0)
                    IconGlyph.WrappedDisabled("No animations in this mod match.");
                expandVariants |= trimmed.Length > 0;
            }

            // Pose variants of one animation fold together. Renamed/manual entries stay their own rows.
            foreach (var variants in rows.GroupBy(r => r.Entry?.AnimationName ?? $"\u0001{r.Cmd.Label}\u0001{r.Cmd.Command}"))
            {
                var list = variants.ToList();
                if (list.Count == 1)
                {
                    var (cmd, entry) = list[0];
                    var shortLabel = entry is null ? cmd.Label : $"{entry.AnimationName} — {entry.Trigger!.Label}";
                    DrawSendRow(shortLabel, OwnerMoodleOverride.ForSend(plugin.Configuration, cmd), canSend, fullLabel: cmd.Label);
                    continue;
                }

                if (expandVariants)
                    ImGui.SetNextItemOpen(true);
                var heading = FitWithEllipsis($"{variants.Key} ({list.Count})", ImGui.GetContentRegionAvail().X - ImGui.GetTreeNodeToLabelSpacing());
                if (!ImGui.TreeNodeEx($"{heading}##subControlAnimVariants_{mod.Key}_{variants.Key}"))
                    continue;
                foreach (var (cmd, entry) in list)
                    DrawSendRow(entry!.Trigger!.Label, OwnerMoodleOverride.ForSend(plugin.Configuration, cmd), canSend, fullLabel: cmd.Label);
                ImGui.TreePop();
            }
            ImGui.TreePop();
        }
    }

    private const int ModSearchThreshold = 15;
    private readonly Dictionary<string, string> modSearches = new();

    private void DrawCollarSection(bool canSend)
    {
        if (!CategoryHeader("Collar"))
            return;

        if (collarLocked)
            DrawReleaseRow("Collar unlock", "collar unlock", canSend);
        DrawSendRow("Collar lock", "collar lock", canSend);
    }

    /// Discrete, one-shot commands only - no intensity/duration control.
    private void DrawToyControlSection(bool canSend)
    {
        if (!CategoryHeader("Toy Control"))
            return;

        ToyStatusView.DrawOwner(plugin.OwnerToyStatus.ForActivePairing);
        if (estimate?.LastToy is not null)
            DrawReleaseRow("Stop", ToyControlCommand.BuildStopCommand(), canSend);
        foreach (var (label, command) in ToyControlQuickRows())
            DrawSendRow(label, command, canSend);
    }

    private IEnumerable<(string Label, string Command)> ToyControlQuickRows()
    {
        foreach (var name in ToyControlCommand.BuiltInPatternNames)
            yield return (char.ToUpperInvariant(name[0]) + name[1..], ToyControlCommand.BuildPatternCommand(name));
        foreach (var pattern in plugin.Configuration.ToyPatterns)
            yield return (pattern.Name, ToyControlCommand.BuildCustomSequenceCommand(pattern.Steps, pattern.Loop));
    }

    /// Saved cages with their own lock settings; those are edited in the Cage module.
    private void DrawCageSection(bool canSend)
    {
        var cages = plugin.Configuration.SavedCages;
        if (!CategoryHeader("Cage", cages.Count))
            return;

        foreach (var cage in cages.ToList())
        {
            var lockText = cage.LockSeconds is { } s ? $"for {RestraintLock.Format(TimeSpan.FromSeconds(s))}" : "until you open it";
            var tooltip = canSend
                ? $"Cage your Sub in \"{cage.Name}\" ({cage.PlaceLabel}) {lockText}. If they aren't there, it waits for them."
                : "No /tell target yet - pairing hasn't captured your Sub's name.";
            if (DrawRow($"subControlCage_{cage.Id}", $"Cage: {cage.Name}", tooltip, canSend))
                ReportCageResult(CageSendAction.TrySend(plugin, cage));
        }
        if (DrawRow("subControlCageOpen", "Open cage", canSend ? "Let your Sub out, or call off a cage still waiting for them." : "No /tell target yet - pairing hasn't captured your Sub's name.", canSend, tint: ReleaseTint))
            ReportCageResult(CageSendAction.TryOpen(plugin));
    }

    private static void ReportCageResult((bool Success, string? Error) result)
    {
        if (result.Success)
            return;
        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = "Oathbound",
            Content = result.Error ?? "The cage command couldn't be sent.",
            Type = NotificationType.Warning,
            InitialDuration = TimeSpan.FromSeconds(5),
        });
    }

    /// Teleport has no static command text; resolved live via TeleportSendAction.
    private void DrawTeleportRow(bool canSend)
    {
        if (!CategoryHeader("Teleport"))
            return;

        var tooltip = canSend ? "Teleport your paired Sub to your current position." : "No /tell target yet - pairing hasn't captured your Sub's name.";
        if (!DrawRow("subControlTeleport", "Teleport Sub to me", tooltip, canSend))
            return;
        var (success, error) = TeleportSendAction.TryResolveAndSend(plugin);
        if (!success)
            Plugin.NotificationManager.AddNotification(new Notification
            {
                Title = "Oathbound",
                Content = error ?? "Teleport failed.",
                Type = NotificationType.Warning,
                InitialDuration = TimeSpan.FromSeconds(5),
            });
    }

    /// The label is shortened to fit; hovering shows the full label and the exact text that would be sent.
    /// `trailing` draws extra controls (the lock settings) on the lines below the label, inside the tile.
    private void DrawSendRow(string label, string command, bool canSend, string? fullLabel = null, Action? trailing = null, Vector4? tint = null)
    {
        var messages = plugin.ChatComposer.ComposeAll(command);
        var fits = ChatComposer.AllFit(messages);
        var status = !fits ? "Command is too long for a safe chat payload." : canSend ? string.Join("\n", messages) : "No /tell target yet - pairing hasn't captured your Sub's name.";
        if (DrawRow($"subControl_{label}_{command}", label, $"{fullLabel ?? label}\n\n{status}", canSend && fits, trailing, tint))
            plugin.ChatSender.SendAll(messages);
    }

    private static float SendButtonSize => ImGui.GetFrameHeight();

    /// One command as a rounded tile: label on the left, `trailing` below it, Send on the right. Returns true
    /// when Send was clicked. The background goes on a lower draw channel so it can be sized after the contents.
    /// `tint` colors the tile and its Send button, for the unlock rows.
    private static bool DrawRow(string id, string label, string tooltip, bool enabled, Action? trailing = null, Vector4? tint = null)
    {
        var pad = Layout.Scaled(8f);
        var margin = Layout.Scaled(4f);
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);

        ImGui.PushID(id);
        ImGui.BeginGroup();
        ImGui.SetCursorScreenPos(start + new Vector2(pad, margin));
        ImGui.AlignTextToFramePadding();
        var labelRoom = width - pad * 2 - SendButtonSize - ImGui.GetStyle().ItemSpacing.X;
        ImGui.TextUnformatted(FitWithEllipsis(label, labelRoom));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);

        if (trailing is not null)
        {
            // Darker than the tile, so the fields don't vanish into it.
            using var fields = ImRaii.PushColor(ImGuiCol.FrameBg, Theme.CardBg)
                .Push(ImGuiCol.FrameBgHovered, Theme.SectionBg)
                .Push(ImGuiCol.FrameBgActive, Theme.SectionBg)
                .Push(ImGuiCol.Button, Theme.Accent with { W = 0.35f })
                .Push(ImGuiCol.ButtonHovered, Theme.Accent with { W = 0.6f })
                .Push(ImGuiCol.ButtonActive, Theme.Accent with { W = 0.8f });
            ImGui.Indent(pad);
            trailing();
            ImGui.Unindent(pad);
        }
        ImGui.EndGroup();

        var min = start;
        var max = new Vector2(start.X + width, ImGui.GetItemRectMax().Y + margin);

        // Drawn after the contents so it can sit in the middle of the tile, however many lines they took.
        ImGui.SetCursorScreenPos(new Vector2(max.X - pad - SendButtonSize, (min.Y + max.Y - SendButtonSize) / 2));
        bool clicked;
        var accent = tint ?? Theme.Accent;
        using (ImRaii.Disabled(!enabled))
        using (ImRaii.PushColor(ImGuiCol.Button, accent with { W = 0.8f }).Push(ImGuiCol.ButtonHovered, tint ?? Theme.AccentHover).Push(ImGuiCol.ButtonActive, accent))
            clicked = IconGlyph.CenteredButton("send", FontAwesomeIcon.PaperPlane, SendButtonSize, new Vector4(1f, 1f, 1f, 1f));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(tooltip);
        ImGui.PopID();

        draw.ChannelsSetCurrent(0);
        var hovered = ImGui.IsMouseHoveringRect(min, max) && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);
        var background = tint is { } t ? t with { W = hovered ? 0.3f : 0.18f } : hovered ? Theme.TileBgHover : Theme.TileBg;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(background), Theme.TileRounding);
        draw.ChannelsMerge();
        // Moves past the bottom margin, leaving the usual item spacing as the gap to the next tile.
        ImGui.SetCursorScreenPos(new Vector2(start.X, max.Y));
        ImGui.Dummy(Vector2.Zero);
        return clicked;
    }

    private const string Ellipsis = "...";

    /// Binary search on length - labels can run past 100 characters.
    private static string FitWithEllipsis(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        var ellipsisWidth = ImGui.CalcTextSize(Ellipsis).X;
        int low = 0, high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid]).X + ellipsisWidth <= maxWidth)
                low = mid;
            else
                high = mid - 1;
        }
        return low == 0 ? Ellipsis : text[..low].TrimEnd() + Ellipsis;
    }
}
