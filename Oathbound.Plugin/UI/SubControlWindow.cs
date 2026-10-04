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

        var canSend = plugin.Configuration.ActivePairing is { Direction: PairingDirection.OwnerSide };
        if (!canSend)
            IconGlyph.WrappedColored(Theme.Warning, "No /tell target yet - every Send below is disabled until an Owner-side pairing is active.");
        else
            OwnerStatusView.Draw(plugin);

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

    /// Animation and Restraints get their own search, separate from the module windows'.
    private void DrawSearchableCategory(string label, List<QuickCommand> commands, bool canSend, ref string search, Func<QuickCommand, string, bool>? extraMatch)
    {
        if (commands.Count == 0)
            return;
        if (!ImGui.CollapsingHeader($"{label} ({commands.Count})###subControlCategory_{label}"))
            return;

        ImGui.Indent();
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
                // Shares QuickCommand.LockSeconds with the module row. Its width is reserved so a long label gets shortened.
                var hasLock = OwnerLockOption.Accepts(cmd.Command);
                DrawSendRow(cmd.Label, OwnerMoodleOverride.ForSend(plugin.Configuration, cmd), canSend, hasLock ? OwnerLockOption.InlineWidth(cmd) : 0f);
                if (hasLock)
                    OwnerLockOption.DrawInline($"subControl_{cmd.Label}_{cmd.Command}", cmd, plugin.Configuration);
            }
        }
        ImGui.Unindent();
    }

    /// Grouped per mod like the Animation module, in the Sub's manifest order. Searching opens every matching mod.
    private void DrawAnimationRows(List<QuickCommand> visible, bool searching, bool canSend)
    {
        // Fixed actions (Stop animation) aren't animations, so they go above the per-mod groups.
        var fixedCommands = QuickAccessMenu.FixedActions.Where(a => a.Category == "Animation").Select(a => a.Command).ToHashSet();
        foreach (var cmd in visible.Where(c => fixedCommands.Contains(c.Command)))
            DrawSendRow(cmd.Label, cmd.Command, canSend, 0f);

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
        if (!ImGui.CollapsingHeader("Collar###subControlCategory_Collar"))
            return;

        ImGui.Indent();
        DrawSendRow("Collar lock", "collar lock", canSend);
        DrawSendRow("Collar unlock", "collar unlock", canSend);
        ImGui.Unindent();
    }

    /// Discrete, one-shot commands only - no intensity/duration control.
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

    /// Teleport has no static command text; resolved live via TeleportSendAction.
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

    /// The label is shortened to fit; hovering shows the full label and the exact text that would be sent.
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
