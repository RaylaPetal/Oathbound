using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Rulebook;
using static Oathbound.Plugin.UI.Layout;

namespace Oathbound.Plugin.UI;

public sealed partial class ModuleWindow
{
    private static readonly string[] LadderFailedStruggleNames = ["Off", "Every failed struggle", "Every 2", "Every 3", "Every 4", "Every 5", "Every 6", "Every 7", "Every 8", "Every 9"];

    /// A saved restraint as a stage puts it on: the ladder is its lock, so any timer or key comes off.
    private static string StageCommand(string command)
    {
        var (word, rest) = SplitCommand(command);
        return rest.Length == 0 ? command : $"{word} {LockTimerOption.Strip(rest, out _).Trim()}";
    }

    private void DrawLadderSection(RulebookDocument draft)
    {
        var config = plugin.Configuration;
        var ladder = draft.Ladder;
        var count = ladder?.Stages.Count ?? 0;
        using var section = Section.Begin("rbLadder", $"Ladder ({count}/{RulebookLimits.MaxLadderStages} stages)");
        IconGlyph.WrappedDisabled("Restraints that tighten and loosen a stage at a time. Each stage is one restraint, locked until the ladder moves. Once there's a stage, every outcome can Tighten or Loosen it.");
        if (count > 0)
            Status.Line(StatusKind.Caution, NewSubNote);

        var changed = false;
        var restraintChoices = SavedConsequenceChoices().Where(c => c.Kind == "Restraints")
            .Select(c => (c.Display, Command: StageCommand(OwnerMoodleOverride.ForSend(config, c.Cmd))))
            .Where(c => ConsequenceValidator.CheckStageCommand(c.Command) is null)
            .ToList();

        for (var i = 0; ladder is not null && i < ladder.Stages.Count; i++)
        {
            var stage = ladder.Stages[i];
            ImGui.PushID(stage.Id);
            Section.SubHeading($"Stage {i + 1}");
            if (DrawName(stage.Name, out var name)) { stage.Name = name; changed = true; }

            ItemWidth(260);
            var preview = stage.Command.Length == 0 ? "Pick a restraint" : RuleText.Cap(ConsequenceText.Describe(stage.Command));
            if (ImGui.BeginCombo("##stageRestraint", preview))
            {
                if (restraintChoices.Count == 0)
                    ImGui.TextDisabled("No saved restraints yet. Save some in the Restraints module.");
                foreach (var (display, command) in restraintChoices)
                    if (ImGui.Selectable($"{display}##{command}", command == stage.Command))
                    {
                        stage.Command = command;
                        changed = true;
                    }
                ImGui.EndCombo();
            }
            ContinueRowOrWrap(Scaled(150));
            ItemWidth(150);
            var struggle = (int)stage.Struggle;
            if (ImGui.Combo("##stageStruggle", ref struggle, RestraintStruggle.LevelNames, RestraintStruggle.LevelNames.Length))
            {
                stage.Struggle = (StruggleLevel)struggle;
                changed = true;
            }
            IconGlyph.HelpMarker("Double or nothing: escaping loosens the ladder a stage, failing tightens it a stage.");

            if (stage.Command.Length > 0 && ConsequenceValidator.CheckStageCommand(stage.Command) is { } problem)
                Status.Line(StatusKind.Caution, RuleText.Cap(problem));
            else if (stage.Struggle != StruggleLevel.None && stage.Command.Length > 0 && !RestraintStruggle.Accepts(stage.Command))
                Status.Line(StatusKind.Caution, "This restraint can't carry a struggle setting, so it can't be struggled.");

            using (ImRaii.Disabled(i == 0))
            {
                if (ImGui.Button("Up"))
                {
                    (ladder.Stages[i - 1], ladder.Stages[i]) = (ladder.Stages[i], ladder.Stages[i - 1]);
                    changed = true;
                }
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(i == ladder.Stages.Count - 1))
            {
                if (ImGui.Button("Down"))
                {
                    (ladder.Stages[i + 1], ladder.Stages[i]) = (ladder.Stages[i], ladder.Stages[i + 1]);
                    changed = true;
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Remove"))
            {
                ladder.Stages.RemoveAt(i);
                changed = true;
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }

        using (ImRaii.Disabled(count >= RulebookLimits.MaxLadderStages))
        {
            if (ImGui.Button("+ Add stage"))
            {
                draft.Ladder ??= new Ladder();
                draft.Ladder.Stages.Add(new LadderStage { Name = $"Stage {draft.Ladder.Stages.Count + 1}", Struggle = StruggleLevel.Medium });
                changed = true;
            }
        }

        if (draft.Ladder is { Stages.Count: > 0 } l)
        {
            Section.SubHeading("What moves it");
            IconGlyph.WrappedDisabled("Tighten and Loosen in any outcome, your Tighten / Loosen buttons on the Overview, and:");
            ItemWidth(200);
            var failed = Math.Clamp(l.FailedStruggles, 0, RulebookLimits.MaxLadderFailedStruggles);
            if (ImGui.Combo("failed struggles tighten it##ladderFailed", ref failed, LadderFailedStruggleNames, LadderFailedStruggleNames.Length))
            {
                l.FailedStruggles = failed;
                changed = true;
            }
            IconGlyph.HelpMarker("Counts failed struggles against your other locks on them, not the ladder's own stages.");

            var decays = l.DecayHours > 0;
            if (ImGui.Checkbox("Loosens by itself##ladderDecay", ref decays))
            {
                l.DecayHours = decays ? 8 : 0;
                changed = true;
            }
            if (decays)
            {
                ImGui.SameLine();
                ItemWidth(100);
                var hours = l.DecayHours;
                if (ImGui.InputInt("hour(s) with nothing tightening it##ladderDecayHours", ref hours, 1, 8))
                {
                    l.DecayHours = Math.Clamp(hours, 1, RulebookLimits.MaxLadderDecayHours);
                    changed = true;
                }
            }
        }
        else if (draft.Ladder is { Stages.Count: 0 })
            draft.Ladder = null;

        if (changed)
            config.Save();
    }

    private void DrawSubLadder(PairingState pairing, RulebookDocument accepted)
    {
        if (accepted.Ladder is not { Stages.Count: > 0 } ladder)
            return;
        var service = plugin.RulebookService;
        var state = pairing.Rulebook.Ladder;
        using var section = Section.Begin("rbSubLadder", "Your ladder");
        for (var i = 0; i < ladder.Stages.Count; i++)
        {
            var stage = ladder.Stages[i];
            var here = state.Stage == i + 1;
            var text = $"{(here ? "> " : "  ")}Stage {i + 1}: {RulebookService.LadderStageName(ladder, i + 1)} - {ConsequenceText.Describe(stage.Command)}" +
                (stage.Struggle == StruggleLevel.None ? "" : $", {RestraintStruggle.LevelNames[(int)stage.Struggle].ToLowerInvariant()} struggle");
            IconGlyph.WrappedColored(here ? Theme.Caution : Theme.Muted, text);
        }
        if (state.Stage == 0)
            Status.Line(StatusKind.Ok, "You're free of the ladder.");
        else if (!service.IsRunning(pairing))
            IconGlyph.WrappedDisabled("Paused: its restraint is off, and comes back on when your rulebook runs again.");
        else if (state.Released)
            IconGlyph.WrappedDisabled("This stage's restraint is off until the ladder next moves.");
        if (ladder.FailedStruggles > 1)
            IconGlyph.WrappedDisabled($"{state.FailedStruggles} of {ladder.FailedStruggles} failed struggles before it tightens.");
        if (service.LadderDecayLeft(pairing) is { } decay)
            IconGlyph.WrappedDisabled($"Loosens by itself {TimeText.Until(decay)} if nothing tightens it.");
        if (state.StruggleNextTryUtc is { } next && next > DateTime.UtcNow)
            IconGlyph.WrappedDisabled($"Next struggle {TimeText.Until(next)}.");
    }
}
