using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;
using Oathbound.Plugin.Rulebook;
using static Oathbound.Plugin.UI.Layout;

namespace Oathbound.Plugin.UI;

public sealed partial class ModuleWindow
{
    /// A list of what happens, plus one "+ Add" popup. Only categories a rulebook may use are offered.
    private static ConsequenceKind KindOf(string command)
    {
        var t = command.Trim();
        static bool Starts(string text, string word) => text.StartsWith(word + " ", StringComparison.OrdinalIgnoreCase) || text.Equals(word, StringComparison.OrdinalIgnoreCase);
        if (Starts(t, "title")) return ConsequenceKind.Title;
        if (Starts(t, "outfit unlock")) return ConsequenceKind.UnlockOutfit;
        if (Starts(t, "outfit")) return ConsequenceKind.Outfit;
        if (Starts(t, "gesture")) return ConsequenceKind.Animation;
        if (Starts(t, "moodle")) return ConsequenceKind.Moodle;
        if (Starts(t, "restraint timer")) return ConsequenceKind.RestraintTimer;
        if (Starts(t, "restraint unlock")) return ConsequenceKind.UnlockRestraints;
        if (Starts(t, "restraint")) return ConsequenceKind.Restraint;
        if (Starts(t, "customtrigger")) return ConsequenceKind.CustomTrigger;
        if (Starts(t, "toy")) return ConsequenceKind.Toy;
        if (Starts(t, ConsequenceValidator.LedgerWord)) return ConsequenceKind.Ledger;
        if (Starts(t, ConsequenceValidator.DeckWord)) return ConsequenceKind.DeckDraw;
        if (Starts(t, "revert")) return ConsequenceKind.RevertAll;
        return ConsequenceKind.Typed;
    }

    private const int DefaultConsequenceSeconds = 3600;

    private static readonly (string Label, int? Seconds)[] DurationPresets =
    [
        ("Until I clear it", null), ("15 minutes", 900), ("30 minutes", 1800), ("1 hour", 3600), ("2 hours", 7200), ("6 hours", 21600),
        ("12 hours", 43200), ("1 day", 86400), ("3 days", 259200), ("7 days", 604800),
    ];

    private static float DurationComboWidth =>
        DurationPresets.Max(p => ImGui.CalcTextSize(p.Label).X) + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetFrameHeight();

    /// A title, outfit, moodle or restraint that a consequence puts on, which a duration takes off again.
    private static bool TakesDuration(string command)
    {
        var bare = WithDuration(command, null);
        return OwnerLockOption.IsOwnerLock(bare) || (OwnerLockOption.Accepts(bare) && !OwnerLockOption.IsGesture(bare));
    }

    private static int? DurationOf(string command)
    {
        var (word, rest) = SplitCommand(command);
        LockTimerOption.StripSeconds(rest, out var seconds);
        return word.Length == 0 ? null : seconds;
    }

    /// Rewrites the command's `lockfor:`; a restraint keeps its key.
    private static string WithDuration(string command, int? seconds)
    {
        var (word, rest) = SplitCommand(command);
        if (rest.Length == 0)
            return command;
        var bare = $"{word} {LockTimerOption.Strip(rest, out var existing)}";
        if (OwnerLockOption.IsOwnerLock(bare))
            return seconds is { } s ? LockTimerOption.InsertSeconds(bare, s) : bare;
        return LockTimerOption.Insert(bare, RestraintLock.FromSeconds(seconds).WithKey(existing.Key));
    }

    private static (string Word, string Tail) SplitCommand(string command)
    {
        var t = command.Trim();
        var space = t.IndexOf(' ');
        return space < 0 ? (t, "") : (t[..space], t[(space + 1)..]);
    }

    /// Returns the rewritten command when the Owner picks another duration.
    private static string? DrawDurationCombo(string command)
    {
        var current = DurationOf(command);
        var index = Array.FindIndex(DurationPresets, p => p.Seconds == current);
        var preview = index >= 0 ? DurationPresets[index].Label : RestraintLock.Format(TimeSpan.FromSeconds(current ?? 0));
        string? result = null;
        ImGui.SetNextItemWidth(DurationComboWidth);
        if (ImGui.BeginCombo("##duration", preview))
        {
            foreach (var (label, seconds) in DurationPresets)
                if (ImGui.Selectable(label, seconds == current) && seconds != current)
                    result = WithDuration(command, seconds);
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How long it stays on, locked, before it comes off by itself. You can always end it sooner.");
        return result;
    }

    /// Kinds with nothing to choose are added straight from their checkbox.
    private static string? FixedCommand(ConsequenceKind kind) => kind switch
    {
        ConsequenceKind.UnlockRestraints => "restraint unlock",
        ConsequenceKind.UnlockOutfit => "outfit unlock",
        ConsequenceKind.RevertAll => "revert all",
        _ => null,
    };

    /// One checkbox per kind of consequence; ticking one opens a picker for just that kind, unticking removes it.
    private bool DrawConsequenceEditor(string id, List<string> commands)
    {
        var changed = false;
        ImGui.PushID("cons" + id);
        var style = ImGui.GetStyle();
        // Points and card draws first: they're what most outcomes are, and what ties rules to the points and the deck.
        if (ImGui.Button("+ Give points"))
        {
            rbAddLedger = 5;
            OpenConsequencePicker(ConsequenceKind.Ledger, null);
        }
        ContinueRowOrWrap(ButtonWidth("+ Take points"));
        if (ImGui.Button("+ Take points"))
        {
            rbAddLedger = -5;
            OpenConsequencePicker(ConsequenceKind.Ledger, null);
        }
        ContinueRowOrWrap(ButtonWidth("+ Draw a reward"));
        if (ImGui.Button("+ Draw a reward"))
        {
            commands.Add(CardPiles.DrawCommand(CardPile.Reward));
            changed = true;
        }
        ContinueRowOrWrap(ButtonWidth("+ Draw a punishment"));
        if (ImGui.Button("+ Draw a punishment"))
        {
            commands.Add(CardPiles.DrawCommand(CardPile.Punishment));
            changed = true;
        }
        ContinueRowOrWrap(Scaled(140));
        ItemWidth(140);
        // Opened after EndCombo: a popup opened from inside the combo would get the combo's id and never show.
        ConsequenceKind? more = null;
        if (ImGui.BeginCombo("##more", "+ More..."))
        {
            foreach (var (kind, label) in ConsequenceKinds.Where(k => k.Kind is not (ConsequenceKind.Ledger or ConsequenceKind.DeckDraw)))
            {
                if (ImGui.Selectable($"{label}##more{kind}"))
                    more = kind;
            }
            ImGui.EndCombo();
        }
        if (more is { } picked)
        {
            if (FixedCommand(picked) is { } fixedCommand)
            {
                if (!commands.Contains(fixedCommand))
                    commands.Add(fixedCommand);
                changed = true;
            }
            else
                OpenConsequencePicker(picked, null);
        }
        if (commands.Count == 0)
            IconGlyph.WrappedDisabled("Nothing yet.");

        for (var i = 0; i < commands.Count; i++)
        {
            ImGui.PushID(i);
            var kind = KindOf(commands[i]);
            var label = ConsequenceKinds.First(k => k.Kind == kind).Label;
            var canChange = FixedCommand(kind) is null;
            var timed = TakesDuration(commands[i]);
            var actions = (timed ? DurationComboWidth + style.ItemSpacing.X : 0) + (canChange ? ButtonWidth("Change") + style.ItemSpacing.X : 0) + ButtonWidth("x");
            TextWithActions(kind is ConsequenceKind.Ledger or ConsequenceKind.DeckDraw
                ? RuleText.Cap(RuleSentence.Describe(commands[i]))
                : $"{label}: {RuleText.Cap(ConsequenceText.Describe(commands[i]))}", actions, t => ImGui.TextColored(Theme.TextMuted, t));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(commands[i]);
            if (timed && DrawDurationCombo(commands[i]) is { } retimed)
            {
                commands[i] = retimed;
                changed = true;
            }
            if (timed)
                ImGui.SameLine();
            if (canChange)
            {
                if (ImGui.Button("Change"))
                    OpenConsequencePicker(kind, i);
                ImGui.SameLine();
            }
            if (ImGui.Button("x"))
            {
                commands.RemoveAt(i);
                changed = true;
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }

        // Popups auto-size to their widest item; a long saved label would otherwise stretch it across the screen.
        ImGui.SetNextWindowSizeConstraints(new Vector2(Scaled(440), 0), new Vector2(Scaled(440), float.MaxValue));
        if (ImGui.BeginPopup("pick"))
        {
            if (DrawConsequencePicker() is { } command)
            {
                if (rbPickIndex is { } index && index < commands.Count)
                    commands[index] = command;
                else
                    commands.Add(command);
                changed = true;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        ImGui.PopID();
        return changed;
    }

    private void OpenConsequencePicker(ConsequenceKind kind, int? index)
    {
        rbPickKind = kind;
        rbPickIndex = index;
        rbAddSearch = "";
        rbAddPick = null;
        rbAddLock = null;
        rbAddTyped = "";
        ImGui.OpenPopup("pick");
    }

    /// Returns the command once the Owner clicks Use.
    private string? DrawConsequencePicker()
    {
        var kindLabel = ConsequenceKinds.First(k => k.Kind == rbPickKind).Label;
        ImGui.TextColored(Theme.AccentHover, kindLabel);
        ImGui.Separator();

        string? command = null;
        string? error = null;
        switch (rbPickKind)
        {
            case ConsequenceKind.Title:
                command = DrawSavedPicker("Titles", "No saved titles yet. Save some in the Title module.");
                break;
            case ConsequenceKind.Outfit:
                command = DrawSavedPicker("Outfits", "No saved outfits yet. Save some in the Outfit module.");
                break;
            case ConsequenceKind.Animation:
                command = DrawSavedPicker("Animations", "No saved animations yet. Save some in the Animation module.");
                break;
            case ConsequenceKind.Moodle:
                command = DrawSavedPicker("Moodles", "No saved moodles yet. Save some in the Moodles module.");
                break;
            case ConsequenceKind.Restraint:
                command = DrawSavedPicker("Restraints", "No saved restraints yet. Save some in the Restraints module.");
                break;
            case ConsequenceKind.CustomTrigger:
                command = DrawSavedPicker("Custom Triggers", "No saved Custom Triggers yet. Save some in the Custom Triggers module.");
                break;
            case ConsequenceKind.Ledger:
                ItemWidth(120);
                ImGui.InputInt("points", ref rbAddLedger, 1, 5);
                rbAddLedger = Math.Clamp(rbAddLedger, -RulebookLimits.MaxLedgerChange, RulebookLimits.MaxLedgerChange);
                ImGui.TextDisabled("Positive adds merits, negative adds demerits.");
                command = rbAddLedger == 0 ? null : LedgerCommandText(rbAddLedger, "");
                break;
            case ConsequenceKind.DeckDraw:
                ItemWidth(160);
                ImGui.Combo("pile", ref rbAddDeckPile, DeckPileNames, DeckPileNames.Length);
                command = $"{ConsequenceValidator.DeckWord} draw {(rbAddDeckPile == 1 ? "reward" : "punishment")}";
                break;
            case ConsequenceKind.RestraintTimer:
                ItemWidth(120);
                ImGui.InputInt("minutes", ref rbAddTimerMinutes, 5, 30);
                rbAddTimerMinutes = Math.Clamp(rbAddTimerMinutes, -RestraintCommand.MaxTimerAdjustSeconds / 60, RestraintCommand.MaxTimerAdjustSeconds / 60);
                ImGui.TextDisabled("Negative takes time off. Only changes restraints that are on a timer.");
                command = rbAddTimerMinutes == 0 ? null : $"restraint timer {(rbAddTimerMinutes > 0 ? "+" : "-")}{Math.Abs(rbAddTimerMinutes) * 60}";
                break;
            case ConsequenceKind.Toy:
                var patterns = ToyControlCommand.BuiltInPatternNames.ToArray();
                rbAddToy = Math.Clamp(rbAddToy, 0, patterns.Length - 1);
                ItemWidth(200);
                ImGui.Combo("pattern", ref rbAddToy, patterns, patterns.Length);
                command = ToyControlCommand.BuildPatternCommand(patterns[rbAddToy]);
                break;
            case ConsequenceKind.Typed:
                ImGui.SetNextItemWidth(-1);
                ImGui.InputTextWithHint("##typed", "e.g. moodle apply \"Blushing\"", ref rbAddTyped, CommandSelector.MaxCommandLength);
                if (rbAddTyped.Trim().Length > 0)
                {
                    error = ConsequenceValidator.Check(rbAddTyped);
                    command = error is null ? rbAddTyped.Trim() : null;
                }
                break;
        }

        if (error is not null)
            IconGlyph.WrappedColored(Theme.Warning, error);
        if (command is not null && rbPickKind != ConsequenceKind.Typed)
            ImGui.TextDisabled($"Will: {ConsequenceText.Describe(command)}");

        ImGui.Spacing();
        string? result = null;
        using (ImRaii.Disabled(command is null))
        {
            if (ImGui.Button("Use"))
                result = command;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            ImGui.CloseCurrentPopup();
        return result;
    }

    private string? DrawSavedPicker(string kind, string emptyText)
    {
        var config = plugin.Configuration;
        var choices = SavedConsequenceChoices().Where(c => c.Kind == kind).ToList();
        if (choices.Count == 0)
        {
            IconGlyph.WrappedDisabled(emptyText);
            return null;
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search", ref rbAddSearch, 64);
        var filter = rbAddSearch.Trim();
        using (Section.List("savedChoices", Scaled(220)))
        {
            var rows = choices.Where(c => filter.Length == 0 || c.Display.Contains(filter, StringComparison.OrdinalIgnoreCase) || c.Cmd.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var (_, display, cmd) in rows)
            {
                if (ImGui.Selectable($"{display}##{cmd.Command}", ReferenceEquals(rbAddPick, cmd)))
                {
                    rbAddPick = cmd;
                    // A reward or punishment that puts something on lasts an hour unless the saved command says otherwise.
                    rbAddLock = cmd.LockSeconds ?? (TakesDuration(cmd.Command) ? DefaultConsequenceSeconds : null);
                }
                if (ImGui.IsItemHovered() && display != cmd.Label)
                    ImGui.SetTooltip(cmd.Label);
            }
            if (rows.Count == 0)
                ImGui.TextDisabled("Nothing matches.");
        }

        if (rbAddPick is not { } pick)
            return null;
        if (OwnerLockOption.Accepts(pick.Command))
            OwnerLockOption.Draw("rbAdd", ref rbAddLock, OwnerLockOption.IsGesture(pick.Command), OwnerLockOption.IsOwnerLock(pick.Command));
        return OwnerLockOption.Apply(OwnerMoodleOverride.ForSend(config, pick), rbAddLock);
    }

    private List<(string Kind, string Display, QuickCommand Cmd)> SavedConsequenceChoices()
    {
        var q = plugin.Configuration.QuickCommands;
        var list = new List<(string, string, QuickCommand)>();
        void Add(string kind, IEnumerable<QuickCommand> commands, Func<QuickCommand, string>? display = null)
        {
            foreach (var cmd in commands)
                if (cmd.Command.Trim().Length > 0 && ConsequenceValidator.Check(cmd.Command) is null)
                    list.Add((kind, display?.Invoke(cmd) ?? cmd.Label, cmd));
        }
        Add("Titles", q.Titles);
        Add("Outfits", q.Outfits);
        Add("Animations", q.Gestures, ShortAnimationLabel);
        Add("Moodles", q.Moodles, c => MoodlesTextFormat.StripMarkup(c.Label));
        Add("Moodles", q.CustomMoodles, c => $"{MoodlesTextFormat.StripMarkup(c.Label)} (yours)");
        Add("Restraints", q.Restraints.Where(r => r.RestraintRules is { Count: > 0 } || r.RestraintCatalogId is null));
        Add("Custom Triggers", q.Aliases);
        return list;
    }

    private static string ShortAnimationLabel(QuickCommand cmd)
    {
        var label = cmd.Label;
        if (cmd.GestureModName is { Length: > 0 } mod && label.StartsWith(mod, StringComparison.OrdinalIgnoreCase))
            label = label[mod.Length..].TrimStart(' ', '-', '—');
        return label.Length > 0 ? label : cmd.Label;
    }

    // ---- Sub ----
}
