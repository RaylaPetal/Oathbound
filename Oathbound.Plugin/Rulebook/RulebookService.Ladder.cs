using System;
using System.Linq;
using Dalamud.Interface.ImGuiNotification;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Rulebook;

/// The escalation ladder. Every move is a `ladder up|down` consequence through the queue, so the combat hold applies;
/// when it runs, the old stage's restraint comes off and the new one goes on through the normal restraint dispatch.
/// The stage number outlives panic and the permission going off; its restraint is put back once the rulebook runs again.
public sealed partial class RulebookService
{
    private RestraintCommand? restraints;

    /// Set once by Plugin.
    public void AttachRestraints(RestraintCommand restraintCommand)
    {
        restraints = restraintCommand;
        restraintCommand.StruggleFailed += OnStruggleFailed;
        restraintCommand.LadderStruggled += OnLadderStruggled;
    }

    public void DetachRestraints()
    {
        if (restraints is null)
            return;
        restraints.StruggleFailed -= OnStruggleFailed;
        restraints.LadderStruggled -= OnLadderStruggled;
        restraints = null;
    }

    public static string LadderStageName(Ladder ladder, int stage) =>
        stage <= 0 ? "free" : stage <= ladder.Stages.Count && ladder.Stages[stage - 1].Name is { Length: > 0 } name ? name : $"stage {stage}";

    /// `ladder up`, `ladder down`, or the Owner's own `ladder set <n>`.
    public LocalTestResult Ladder(string rest, PairingState? sourcePairing)
    {
        if (sourcePairing is null || !IsRunning(sourcePairing) || sourcePairing.Rulebook.Accepted?.Ladder is not { Stages.Count: > 0 } ladder)
            return LocalTestResult.Fail("No running rulebook with a ladder for this pairing.");
        if (!LadderCommand.TryParse(rest, allowSet: true, out var delta, out var setTo))
            return LocalTestResult.Fail("Expected \"ladder up\", \"ladder down\" or \"ladder set <n>\".");
        var state = sourcePairing.Rulebook.Ladder;
        return MoveLadder(sourcePairing, ladder, setTo ?? state.Stage + delta);
    }

    private LocalTestResult MoveLadder(PairingState pairing, Ladder ladder, int target)
    {
        var state = pairing.Rulebook.Ladder;
        var to = Math.Clamp(target, 0, ladder.Stages.Count);
        if (to == state.Stage)
        {
            var why = to == 0 ? "already free" : to == ladder.Stages.Count ? "already at the top" : "already there";
            Log(pairing, RulebookEventKind.ConsequenceSkipped, Rulebook.Ladder.RuleId, $"Ladder: {why} (stage {to}).");
            return LocalTestResult.Ok($"The ladder is {why}.");
        }

        ReleaseLadderRestraint(state);
        var up = to > state.Stage;
        state.Stage = to;
        state.Released = false;
        if (up)
            state.DecaySeconds = 0;
        var name = LadderStageName(ladder, to);
        Log(pairing, RulebookEventKind.ConsequenceRan, Rulebook.Ladder.RuleId,
            to == 0 ? "Ladder loosened all the way: nothing from it is worn." : $"Ladder {(up ? "tightened" : "loosened")} to stage {to}, \"{name}\".");
        Notify(up ? "Ladder tightened" : "Ladder loosened", to == 0 ? "You're off the ladder." : $"Stage {to} of {ladder.Stages.Count}: {name}.",
            up ? NotificationType.Warning : NotificationType.Success);
        if (to > 0)
            ApplyLadderStage(pairing, ladder);
        config.SaveNow();
        return LocalTestResult.Ok(to == 0 ? "Ladder loosened all the way." : $"Ladder at stage {to}, \"{name}\".");
    }

    /// Puts the current stage's restraint on through the normal dispatch, so the Restraints permission still decides.
    /// A refusal leaves the stage off until the ladder next moves rather than retrying every second.
    private void ApplyLadderStage(PairingState pairing, Ladder ladder)
    {
        var state = pairing.Rulebook.Ladder;
        if (restraints is null || state.Stage <= 0 || state.Stage > ladder.Stages.Count)
            return;
        var stage = ladder.Stages[state.Stage - 1];
        var command = RestraintStruggle.Insert(stage.Command, new StruggleSetting(stage.Struggle, 0));
        restraints.LastLockedRuntimeId = null;
        var result = listener.RunRulebookCommand(command, pairing);
        var worn = restraints.LastLockedRuntimeId is { } id ? config.WornRestraints.FirstOrDefault(w => w.RuntimeId == id) : null;
        if (!result.Success || worn?.Lock is not { } wornLock)
        {
            state.Released = true;
            Log(pairing, RulebookEventKind.ConsequenceSkipped, Rulebook.Ladder.RuleId, $"Ladder stage {state.Stage}: skipped \"{ConsequenceText.Describe(stage.Command)}\" - {result.Message}");
            return;
        }
        wornLock.Ladder = true;
        wornLock.StruggleNextTryUtc = state.StruggleNextTryUtc is { } next && next > DateTime.UtcNow ? next : null;
        state.RuntimeId = worn.RuntimeId;
        config.Save();
    }

    private void ReleaseLadderRestraint(LadderState state)
    {
        if (state.RuntimeId is { } id)
            restraints?.ReleaseLadderStage(id);
        state.RuntimeId = null;
    }

    /// Every second for every Sub pairing, running or not.
    private void TickLadder(PairingState pairing)
    {
        var state = pairing.Rulebook.Ladder;
        if (pairing.Rulebook.Accepted?.Ladder is not { Stages.Count: > 0 } ladder)
        {
            if (state.Stage != 0 || state.RuntimeId is not null)
                ResetLadder(pairing);
            return;
        }
        var now = Now();
        var worn = state.RuntimeId is { } id ? config.WornRestraints.FirstOrDefault(w => w.RuntimeId == id) : null;

        if (!IsRunning(pairing))
        {
            // Panic, a pause or the permission off: the restraint comes off, the stage stays for when it runs again.
            if (state.RuntimeId is not null)
            {
                ReleaseLadderRestraint(state);
                config.Save();
            }
            state.DecayCheckedUnixSeconds = now;
            return;
        }

        if (state.RuntimeId is not null && worn is null)
        {
            state.RuntimeId = null;
            state.Released = true;
            Log(pairing, RulebookEventKind.ConsequenceSkipped, Rulebook.Ladder.RuleId, $"Ladder stage {state.Stage}'s restraint came off outside the ladder; it stays off until the ladder moves.");
        }

        if (state.Stage > 0 && state.RuntimeId is null && !state.Released
            && !RulebookGameEvents.ShouldHoldConsequences() && RestraintCommand.CanChangeCharacter())
        {
            ApplyLadderStage(pairing, ladder);
            config.Save();
        }

        TickLadderDecay(pairing, ladder, now);
    }

    private void TickLadderDecay(PairingState pairing, Ladder ladder, long now)
    {
        var state = pairing.Rulebook.Ladder;
        var since = state.DecayCheckedUnixSeconds;
        state.DecayCheckedUnixSeconds = now;
        if (ladder.DecayHours <= 0 || state.Stage <= 0)
        {
            state.DecaySeconds = 0;
            return;
        }
        if (since > 0 && now > since)
            state.DecaySeconds += now - since;
        var period = ladder.DecayHours * 3600L;
        if (state.DecaySeconds < period)
            return;
        // A long absence catches up all at once, never past free.
        var steps = (int)Math.Min(state.DecaySeconds / period, state.Stage);
        state.DecaySeconds = 0;
        Log(pairing, RulebookEventKind.ConsequenceRan, Rulebook.Ladder.RuleId, $"Ladder decay: {steps} stage{(steps == 1 ? "" : "s")} loosened with nothing tightening it.");
        Fire(pairing, Rulebook.Ladder.RuleId, "Ladder decay", Enumerable.Repeat(LadderCommand.DownCommand, steps).ToList(), null, 0);
        config.Save();
    }

    /// Ladder seconds the Sub has left until decay loosens a stage, or null without decay.
    public TimeSpan? LadderDecayLeft(PairingState pairing) =>
        pairing.Rulebook.Accepted?.Ladder is { DecayHours: > 0 } ladder && pairing.Rulebook.Ladder.Stage > 0
            ? TimeSpan.FromSeconds(Math.Max(0, ladder.DecayHours * 3600L - pairing.Rulebook.Ladder.DecaySeconds))
            : null;

    private void ResetLadder(PairingState pairing)
    {
        var state = pairing.Rulebook.Ladder;
        ReleaseLadderRestraint(state);
        pairing.Rulebook.Ladder = new LadderState();
    }

    /// A stage whose restraint changed in the new version comes off, and the tick puts the new one on.
    private void ReconcileLadder(PairingState pairing, RulebookDocument? previous, RulebookDocument doc)
    {
        var state = pairing.Rulebook.Ladder;
        if (doc.Ladder is not { Stages.Count: > 0 } ladder)
        {
            ResetLadder(pairing);
            return;
        }
        var stage = Math.Min(state.Stage, ladder.Stages.Count);
        var before = previous?.Ladder is { } old && state.Stage > 0 && state.Stage <= old.Stages.Count ? old.Stages[state.Stage - 1] : null;
        var after = stage > 0 ? ladder.Stages[stage - 1] : null;
        if (stage != state.Stage || before?.Command != after?.Command || before?.Struggle != after?.Struggle)
        {
            ReleaseLadderRestraint(state);
            state.Released = false;
        }
        state.Stage = stage;
    }

    private PairingState? LadderPairing(Guid? pairingId) =>
        pairingId is { } id && config.FindPairingById(id) is { IsPaired: true, Direction: PairingDirection.SubSide } pairing
        && IsRunning(pairing) && pairing.Rulebook.Accepted?.Ladder is { Stages.Count: > 0 }
            ? pairing
            : null;

    private void OnStruggleFailed(Guid? pairingId)
    {
        if (LadderPairing(pairingId) is not { } pairing || pairing.Rulebook.Accepted!.Ladder!.FailedStruggles is not (> 0 and var every))
            return;
        var state = pairing.Rulebook.Ladder;
        state.FailedStruggles++;
        if (state.FailedStruggles < every)
        {
            config.Save();
            return;
        }
        state.FailedStruggles = 0;
        Log(pairing, RulebookEventKind.ConsequenceRan, Rulebook.Ladder.RuleId, every == 1 ? "A failed struggle tightens the ladder." : $"{every} failed struggles tighten the ladder.");
        Fire(pairing, Rulebook.Ladder.RuleId, "Ladder (failed struggle)", [LadderCommand.UpCommand], null, 0);
    }

    private void OnLadderStruggled(Guid? pairingId, bool escaped, DateTime nextTry)
    {
        if (LadderPairing(pairingId) is not { } pairing)
            return;
        pairing.Rulebook.Ladder.StruggleNextTryUtc = nextTry;
        Log(pairing, RulebookEventKind.ConsequenceRan, Rulebook.Ladder.RuleId, escaped ? "Struggled free of a ladder stage: the ladder loosens." : "Failed to struggle out of a ladder stage: the ladder tightens.");
        Fire(pairing, Rulebook.Ladder.RuleId, escaped ? "Ladder (escape)" : "Ladder (failed struggle)", [escaped ? LadderCommand.DownCommand : LadderCommand.UpCommand], null, 0);
    }
}
