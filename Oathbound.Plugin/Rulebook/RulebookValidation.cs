using System.Collections.Generic;
using System.Linq;

namespace Oathbound.Plugin.Rulebook;

/// Whole-document checks: the Owner can't publish what fails here, and the Sub refuses a version that fails here.
public static class RulebookValidation
{
    public static string? Check(RulebookDocument doc)
    {
        var ids = new HashSet<string>();
        foreach (var (id, name) in doc.AllRules())
        {
            if (string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                return "Two rules share an id.";
            if (name.Length > RulebookLimits.MaxNameLength)
                return $"\"{name[..20]}...\" has a name longer than {RulebookLimits.MaxNameLength} characters.";
        }

        foreach (var o in doc.Oaths)
            if (CheckOath(o) is { } e) return $"Oath \"{Label(o.Name)}\": {e}";

        if (doc.Deck.Count > RulebookLimits.MaxDeckCards)
            return $"The deck holds more than {RulebookLimits.MaxDeckCards} cards.";
        foreach (var c in doc.Deck)
        {
            if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > RulebookLimits.MaxNameLength)
                return "Every card needs a name.";
            if (c.Weight is < RulebookLimits.MinCardWeight or > RulebookLimits.MaxCardWeight)
                return $"Card \"{Label(c.Name)}\": weight must be {RulebookLimits.MinCardWeight}-{RulebookLimits.MaxCardWeight}.";
            if (c.Consequence.Count == 0)
                return $"Card \"{Label(c.Name)}\" does nothing.";
            if (ConsequenceValidator.CheckAll(c.Consequence) is { } e)
                return $"Card \"{Label(c.Name)}\": {e}";
        }

        foreach (var p in doc.Places)
        {
            if (p.Places.Count == 0)
                return $"Place rule \"{Label(p.Name)}\" has no places.";
            if (p.Enter.Count + p.Leave.Count == 0)
                return $"Place rule \"{Label(p.Name)}\" does nothing.";
            if ((CheckCooldown(p.CooldownSeconds) ?? ConsequenceValidator.CheckAll(p.Enter.Concat(p.Leave))) is { } e)
                return $"Place rule \"{Label(p.Name)}\": {e}";
        }

        foreach (var p in doc.Presence)
        {
            if (p.RangeYalms is < RulebookLimits.MinRangeYalms or > RulebookLimits.MaxRangeYalms)
                return $"Presence rule \"{Label(p.Name)}\": range must be {RulebookLimits.MinRangeYalms}-{RulebookLimits.MaxRangeYalms} yalms.";
            if (p.Arrive.Count + p.Depart.Count == 0)
                return $"Presence rule \"{Label(p.Name)}\" does nothing.";
            if ((CheckCooldown(p.CooldownSeconds) ?? ConsequenceValidator.CheckAll(p.Arrive.Concat(p.Depart))) is { } e)
                return $"Presence rule \"{Label(p.Name)}\": {e}";
        }

        if (doc.Thresholds.Count > RulebookLimits.MaxThresholds)
            return $"More than {RulebookLimits.MaxThresholds} ledger thresholds.";
        foreach (var t in doc.Thresholds)
        {
            if (t.Score is < RulebookLimits.MinLedger or > RulebookLimits.MaxLedger)
                return $"Threshold \"{Label(t.Name)}\": score must be {RulebookLimits.MinLedger} to {RulebookLimits.MaxLedger}.";
            if (t.ResetTo is < RulebookLimits.MinLedger or > RulebookLimits.MaxLedger)
                return $"Threshold \"{Label(t.Name)}\": reset value is out of range.";
            if (t.Consequence.Count == 0 && !t.DrawCard)
                return $"Threshold \"{Label(t.Name)}\" does nothing.";
            if ((CheckCooldown(t.CooldownSeconds) ?? ConsequenceValidator.CheckAll(t.Consequence)) is { } e)
                return $"Threshold \"{Label(t.Name)}\": {e}";
        }

        return CheckCooldown(doc.DrawOn.CooldownSeconds) is { } drawError ? $"Deck draws: {drawError}" : null;
    }

    private static string? CheckOath(Oath o)
    {
        if (o.Kept.Count + o.Broken.Count == 0 && o.KeptLedger == 0 && o.BrokenLedger == 0 && o.LedgerPerDone == 0 && o.LedgerPerMissed == 0)
            return "it has no outcome.";
        if (!OathConditions.IsRitual(o.Condition) && (o.LedgerPerDone != 0 || o.LedgerPerMissed != 0))
            return "only rituals score the ledger each time.";
        if (o.Scope == OathScope.NextDuty && !OathConditions.IsDuty(o.Condition))
            return "only duty oaths can last for the next duty.";
        if (o.Scope == OathScope.ForATime && o.DurationMinutes is < RulebookLimits.MinOathMinutes or > RulebookLimits.MaxOathMinutes)
            return "a timed oath lasts 10 minutes to 30 days.";
        if (OathConditions.IsRitual(o.Condition))
        {
            if (o.PeriodDays is < 1 or > RulebookLimits.MaxPeriodDays)
                return $"it repeats every 1-{RulebookLimits.MaxPeriodDays} days.";
            if (o.TimesPerPeriod is < 1 or > RulebookLimits.MaxTimesPerPeriod)
                return $"it's done 1-{RulebookLimits.MaxTimesPerPeriod} times each time.";
            if (o.DurationMinutes < o.PeriodDays * 1440)
                return "it has to last at least one full repeat.";
        }
        if (o.Condition == OathCondition.GreetOwner && o.EmoteId == 0 && string.IsNullOrEmpty(o.AnimationId))
            return "pick the gesture or animation they greet you with.";
        if (o.Condition == OathCondition.GreetOwner && o.HoldSeconds is < Commands.GestureCommand.MinHoldSeconds or > Commands.GestureCommand.MaxHoldSeconds)
            return $"the hold is {Commands.GestureCommand.MinHoldSeconds}-{Commands.GestureCommand.MaxHoldSeconds} seconds.";
        if (o.AnimationLabel.Length > RulebookLimits.MaxPhraseLength * 4)
            return "the animation name is too long.";
        if (OathConditions.NeedsPhrase(o.Condition) && string.IsNullOrWhiteSpace(o.Phrase))
            return o.Condition == OathCondition.AddressOwner ? "set the word they must call you." : "set the forbidden word.";
        if (o.Phrase.Length > RulebookLimits.MaxPhraseLength)
            return $"the words are at most {RulebookLimits.MaxPhraseLength} characters.";
        if (o.Condition == OathCondition.DutyTimeLimit && o.TimeLimitMinutes is < 1 or > 600)
            return "the duty time limit must be 1-600 minutes.";
        if (o.Condition is OathCondition.StayInPlaces or OathCondition.AvoidPlaces && o.Places.Count == 0)
            return "it needs at least one place.";
        if (o.Condition == OathCondition.Curfew && (o.CurfewStartMinutes is < 0 or >= 1440 || o.CurfewEndMinutes is < 0 or >= 1440 || o.CurfewStartMinutes == o.CurfewEndMinutes))
            return "the curfew needs a start and end time.";
        if (o.KeptLedger is < -RulebookLimits.MaxLedgerChange or > RulebookLimits.MaxLedgerChange ||
            o.BrokenLedger is < -RulebookLimits.MaxLedgerChange or > RulebookLimits.MaxLedgerChange ||
            o.LedgerPerDone is < -RulebookLimits.MaxLedgerChange or > RulebookLimits.MaxLedgerChange ||
            o.LedgerPerMissed is < -RulebookLimits.MaxLedgerChange or > RulebookLimits.MaxLedgerChange)
            return $"a ledger change is at most {RulebookLimits.MaxLedgerChange}.";
        return ConsequenceValidator.CheckAll(o.Kept.Concat(o.Broken));
    }

    private static string? CheckCooldown(int seconds) =>
        seconds < RulebookLimits.MinCooldownSeconds ? $"the cooldown must be at least {RulebookLimits.MinCooldownSeconds} seconds." : null;

    private static string Label(string name) => string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name;
}
