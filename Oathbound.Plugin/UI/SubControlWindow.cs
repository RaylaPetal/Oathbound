using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Oathbound.Plugin.UI;

/// collar/ui-organization "Sub Control window": opened via the header's toggle (`CollarWindow.
/// DrawSubControlToggle`), this is a single console listing every category's *full* sendable command list
/// (not just favorites - see `QuickAccessMenu.CategorizedAll`), Send-only (no Favorite/Edit/Remove - those
/// are authoring actions that stay in each category's own module window), so ordering a Sub to do something
/// never requires opening a category tab or having pre-favorited anything. Unlike every other secondary
/// window in this plugin, its position is not independently movable - `PreDraw` re-docks it to
/// `CollarWindow`'s current right edge every frame (see design.md's "Continuous docking mechanism") and
/// closes itself once the main window closes.
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
        // NoMove: position is programmatically glued every frame in PreDraw below - offering a drag
        // affordance that would just snap back the next frame is worse than not offering one at all.
        Flags = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse;
        // 540 wide: room for a restraint row's Send + a readable label + the full Timed lock picker
        // (Timed combo, minutes field with -/+ and "min") without the label being cut down to nothing.
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(540, 260), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    public void Dispose() { }

    /// design.md "Continuous docking mechanism" / "Auto-close when the main window closes": re-pins this
    /// window to `CollarWindow`'s current right edge every frame (`ImGuiCond.Always`, so it wins any stray
    /// drag), and closes this window the frame after the main window closes - `DrawInternal`'s own is-open
    /// check happens once per window per frame, ahead of `PreDraw`, so setting `IsOpen = false` here takes
    /// effect starting next frame rather than retroactively skipping this one; an accepted, imperceptible
    /// one-frame trade-off (see design.md's Risks) for not needing a separately-registered close hook.
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
            IconGlyph.WrappedDisabled("Sub Control is an Owner-side concept - open it while paired as Owner to see and send every command configured for your Sub.");
            return;
        }

        var canSend = plugin.Configuration.ActivePairing is { Direction: PairingDirection.OwnerSide };
        if (!canSend)
            IconGlyph.WrappedColored(Theme.Warning, "No /tell target yet - every Send below is disabled until an Owner-side pairing is active.");

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
        DrawTeleportRow(canSend);
    }

    private static bool MatchesGestureFilter(QuickCommand cmd, string filter) =>
        (cmd.GestureModName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
        || (cmd.GestureGroupName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private void DrawCategory(string label, List<QuickCommand> commands, bool canSend, Func<string, string>? displayLabel)
    {
        if (commands.Count == 0)
            return;
        if (!ImGui.CollapsingHeader($"{label} ({commands.Count})###subControlCategory_{label}"))
            return;

        ImGui.Indent();
        foreach (var cmd in commands)
            DrawSendRow(displayLabel?.Invoke(cmd.Label) ?? cmd.Label, OwnerMoodleOverride.ForSend(plugin.Configuration, cmd), canSend);
        ImGui.Unindent();
    }

    /// collar/ui-organization "Large category sections stay decluttered": Animation and Restraints get their
    /// own search filter over the already-configured, sendable list - independent search state per category,
    /// separate from any module window's own search-in-progress (design.md's "Row rendering" decision).
    private void DrawSearchableCategory(string label, List<QuickCommand> commands, bool canSend, ref string search, Func<QuickCommand, string, bool>? extraMatch)
    {
        if (commands.Count == 0)
            return;
        if (!ImGui.CollapsingHeader($"{label} ({commands.Count})###subControlCategory_{label}"))
            return;

        ImGui.Indent();
        ImGui.SetNextItemWidth(Math.Max(180, ImGui.GetContentRegionAvail().X));
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
                // collar/restraint-lock-timer: the same per-restraint timer as its module-window row (one
                // shared QuickCommand.LockSeconds), so a change here shows up there and vice versa. Its width
                // is reserved up front so a long label gets shortened rather than pushing it off the edge.
                var hasLock = OwnerLockOption.Accepts(cmd.Command);
                DrawSendRow(cmd.Label, OwnerMoodleOverride.ForSend(plugin.Configuration, cmd), canSend, hasLock ? OwnerLockOption.InlineWidth(cmd) : 0f);
                if (hasLock)
                    OwnerLockOption.DrawInline($"subControl_{cmd.Label}_{cmd.Command}", cmd, plugin.Configuration);
            }
        }
        ImGui.Unindent();
    }

    /// Animation rows grouped under one collapsible node per mod, the same grouping the Animation module
    /// window uses - so each row only needs the short "animation — pose" label instead of repeating the
    /// mod name on every line. Rows keep the Sub's own manifest order (not alphabetical, see
    /// ModuleWindow.DrawGestureQuickSection). While searching, every mod with a match is opened.
    private void DrawAnimationRows(List<QuickCommand> visible, bool searching, bool canSend)
    {
        foreach (var mod in visible.GroupBy(c => c.GestureModName ?? "Other").OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (searching)
                ImGui.SetNextItemOpen(true);
            if (!ImGui.TreeNodeEx($"{mod.Key} ({mod.Count()})##subControlAnimMod_{mod.Key}"))
                continue;

            var rows = mod.OrderBy(c => c.GestureGroupOrder).ThenBy(c => c.GestureOptionOrder)
                .Select(c => (Cmd: c, Entry: ModuleWindow.AutoLabeledGesture(plugin.Configuration, c)))
                .ToList();

            // A big mod gets its own search box, scoped to just that mod's animations.
            var expandVariants = searching;
            if (rows.Count > ModSearchThreshold)
            {
                var modFilter = modSearches.GetValueOrDefault(mod.Key, "");
                ImGui.SetNextItemWidth(Math.Max(180, ImGui.GetContentRegionAvail().X));
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

            // Pose/emote variants of one animation (e.g. the same dance exported for six emotes) fold into one
            // collapsible entry whose rows show only the variant. Only entries still carrying their imported
            // label are grouped - a renamed/manual entry stays its own row, same rule as the module window.
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

    /// Mods with more animations than this get their own search box inside their group.
    private const int ModSearchThreshold = 15;
    private readonly Dictionary<string, string> modSearches = new();

    private void DrawCollarSection(bool canSend)
    {
        if (!ImGui.CollapsingHeader("Collar###subControlCategory_Collar"))
            return;

        ImGui.Indent();
        DrawSendRow("Collar lock", "collar lock", canSend);
        DrawSendRow("Collar unlock", "collar unlock", canSend);
        ImGui.Unindent();
    }

    /// collar/ui-organization "Toy Control section offers only discrete, one-shot commands": built-in
    /// patterns, stop, and the Owner's own saved patterns only - no intensity/duration control, matching
    /// design.md's "Full-category data shaping" decision (Toy Control has no `OwnerQuickCommands` list of
    /// its own to draw on).
    private void DrawToyControlSection(bool canSend)
    {
        if (!ImGui.CollapsingHeader("Toy Control###subControlCategory_ToyControl"))
            return;

        ImGui.Indent();
        ToyStatusView.DrawOwner(plugin.OwnerToyStatus.ForActivePairing);
        foreach (var (label, command) in ToyControlQuickRows())
            DrawSendRow(label, command, canSend);
        ImGui.Unindent();
    }

    private IEnumerable<(string Label, string Command)> ToyControlQuickRows()
    {
        foreach (var name in ToyControlCommand.BuiltInPatternNames)
            yield return (char.ToUpperInvariant(name[0]) + name[1..], ToyControlCommand.BuildPatternCommand(name));
        yield return ("Stop", ToyControlCommand.BuildStopCommand());
        foreach (var pattern in plugin.Configuration.ToyPatterns)
            yield return (pattern.Name, ToyControlCommand.BuildCustomSequenceCommand(pattern.Steps, pattern.Loop));
    }

    /// Teleport can't join the regular rows above - it has no static command text, resolved live via
    /// `TeleportSendAction` instead, matching `FavoritesWindow.DrawTeleportRow` exactly (including its
    /// failure notification) rather than gating it behind whether it's favorited, since this window shows
    /// everything.
    private void DrawTeleportRow(bool canSend)
    {
        if (!ImGui.CollapsingHeader("Teleport###subControlCategory_Teleport"))
            return;

        ImGui.Indent();
        using (ImRaii.Disabled(!canSend))
        {
            if (ImGui.SmallButton("Send##subControlTeleport"))
            {
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
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("Teleport Sub to me");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(canSend ? "Teleport your paired Sub to your current position." : "No /tell target yet - pairing hasn't captured your Sub's name.");
        ImGui.Unindent();
    }

    /// One Send button plus its label. The label is shortened with "..." to fit what's left of the row (minus
    /// `reservedWidth` for anything drawn after it on the same line), so the window never needs to grow to
    /// show a long name; hovering shows the full label (`fullLabel` when the row shows a shorter one) above
    /// the exact text that would be sent.
    private void DrawSendRow(string label, string command, bool canSend, float reservedWidth = 0f, string? fullLabel = null)
    {
        var messages = plugin.ChatComposer.ComposeAll(command);
        var fits = ChatComposer.AllFit(messages);
        using (ImRaii.Disabled(!canSend || !fits))
        {
            if (ImGui.SmallButton($"Send##subControl_{label}_{command}"))
                plugin.ChatSender.SendAll(messages);
        }
        ImGui.SameLine();
        var available = ImGui.GetContentRegionAvail().X - reservedWidth;
        var shown = FitWithEllipsis(label, available);
        ImGui.TextUnformatted(shown);
        if (ImGui.IsItemHovered())
        {
            var status = !fits ? "Command is too long for a safe chat payload." : canSend ? string.Join("\n", messages) : "No /tell target yet - pairing hasn't captured your Sub's name.";
            ImGui.SetTooltip($"{fullLabel ?? label}\n\n{status}");
        }
    }

    private const string Ellipsis = "...";

    /// `text` unchanged when it fits in `maxWidth`, otherwise the longest prefix that fits with "..."
    /// appended (binary search on length - labels here can run past 100 characters).
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
