using System;
using System.Collections.Generic;
using System.Linq;

namespace Oathbound.Plugin.Rulebook;

/// Each rule as one plain sentence: what sets it off, then what happens. Built from the same fragments as RuleText,
/// with points and card draws phrased the way players think of them.
public static class RuleSentence
{
    public static string Of(object rule, string owner) => rule switch
    {
        Oath o => For(o),
        PlaceRule p => For(p),
        PresenceRule p => For(p, owner),
        TimeRule t => For(t),
        LedgerThreshold t => For(t),
        DeckCard c => For(c),
        ShopItem i => For(i),
        _ => "",
    };

    /// Every rule of a document by id.
    public static Dictionary<string, string> ById(RulebookDocument doc, string owner)
    {
        var map = new Dictionary<string, string>();
        foreach (var o in doc.Oaths) map[o.Id] = For(o);
        foreach (var c in doc.Deck) map[c.Id] = For(c);
        foreach (var p in doc.Places) map[p.Id] = For(p);
        foreach (var p in doc.Presence) map[p.Id] = For(p, owner);
        foreach (var t in doc.Thresholds) map[t.Id] = For(t);
        foreach (var t in doc.Times) map[t.Id] = For(t);
        foreach (var i in doc.Shop) map[i.Id] = For(i);
        return map;
    }

    public static string For(Oath oath)
    {
        var parts = new List<string> { RuleText.Cap(OathText.Describe(oath)) + "." };
        if (OathConditions.IsRitual(oath.Condition))
        {
            Add(parts, "Done", Points(oath.LedgerPerDone));
            Add(parts, "Missed", Points(oath.LedgerPerMissed));
        }
        Add(parts, "Kept", Outcome(oath.Kept, oath.KeptLedger));
        Add(parts, "Broken", Outcome(oath.Broken, oath.BrokenLedger));
        return string.Join(" ", parts);
    }

    public static string For(PlaceRule rule)
    {
        var places = rule.Places.Count == 0 ? "(no places)" : string.Join(", ", rule.Places.Select(RulebookPlaces.Describe));
        var parts = new List<string>();
        Add(parts, $"When you enter {places}", Outcome(rule.Enter));
        Add(parts, "When you leave", Outcome(rule.Leave));
        return parts.Count == 0 ? $"Watches {places}, but does nothing yet." : string.Join(" ", parts);
    }

    public static string For(PresenceRule rule, string owner)
    {
        var arrive = Outcome(rule.Arrive);
        if (rule.LeashOnArrive) arrive = Join(arrive, $"leash you to {owner}");
        if (!string.IsNullOrWhiteSpace(rule.ArriveTell)) arrive = Join(arrive, $"you /tell {owner} \"{rule.ArriveTell.Trim()}\"");
        var depart = Outcome(rule.Depart);
        if (rule.UnleashOnDepart) depart = Join(depart, "take the leash off");
        if (!string.IsNullOrWhiteSpace(rule.DepartTell)) depart = Join(depart, $"you /tell {owner} \"{rule.DepartTell.Trim()}\"");
        var parts = new List<string>();
        Add(parts, $"When {owner} comes within {rule.RangeYalms:0} yalms", arrive);
        Add(parts, "When they leave", depart);
        return parts.Count == 0 ? $"Watches for {owner} nearby, but does nothing yet." : string.Join(" ", parts);
    }

    public static string For(TimeRule rule)
    {
        var when = rule.Trigger == TimeTrigger.OnLogin ? "Each time you log in" : $"{RuleText.Days(rule.Weekdays)} at {OathText.Clock(rule.Minutes)}";
        return $"{when}: {Outcome(rule.Consequence) ?? "nothing yet"}.";
    }

    public static string For(LedgerThreshold t)
    {
        var does = Outcome(t.Consequence);
        if (t.DrawCard) does = Join(does, $"draw a {CardPiles.Word(t.Pile)}");
        if (t.ResetTo is { } to) does = Join(does, $"reset points to {to}");
        return $"When points reach {t.Score} or {(t.Direction == ThresholdDirection.AtOrAbove ? "more" : "less")}: {does ?? "nothing yet"}.";
    }

    public static string For(DeckCard card) =>
        $"{(card.Pile == CardPile.Reward ? "Reward" : "Punishment")} card: {Outcome(card.Consequence) ?? "nothing yet"}.";

    public static string For(ShopItem item)
    {
        var what = !string.IsNullOrWhiteSpace(item.Name) ? item.Name.Trim()
            : item.DrawReward ? "draw a reward" : Outcome(item.Consequence) ?? "nothing yet";
        return $"Spend {item.Price} points: {what}.";
    }

    /// "+5 points" / "draw a punishment" / the command's own description, joined; null when there's nothing.
    public static string? Outcome(IReadOnlyList<string> commands, int ledger = 0)
    {
        var parts = new List<string>();
        if (ledger != 0)
            parts.Add(Points(ledger)!);
        foreach (var command in commands)
            parts.Add(Describe(command));
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    public static string Describe(string command)
    {
        var trimmed = command.Trim();
        var space = trimmed.IndexOf(' ');
        var word = space < 0 ? trimmed : trimmed[..space];
        var rest = space < 0 ? "" : trimmed[(space + 1)..];
        if (word.Equals(ConsequenceValidator.LedgerWord, StringComparison.OrdinalIgnoreCase) && LedgerCommand.TryParse(rest, out var delta, out _))
            return Points(delta)!;
        if (word.Equals(ConsequenceValidator.DeckWord, StringComparison.OrdinalIgnoreCase) && CardPiles.TryParseDraw(rest, out var pile))
            return $"draw a {CardPiles.Word(pile)}";
        return ConsequenceText.Describe(trimmed);
    }

    /// Points a command list adds (positive) or takes (negative), or 0.
    public static int PointsIn(IReadOnlyList<string> commands)
    {
        var total = 0;
        foreach (var command in commands)
        {
            var trimmed = command.Trim();
            var space = trimmed.IndexOf(' ');
            if (space > 0 && trimmed[..space].Equals(ConsequenceValidator.LedgerWord, StringComparison.OrdinalIgnoreCase)
                && LedgerCommand.TryParse(trimmed[(space + 1)..], out var delta, out _))
                total += delta;
        }
        return total;
    }

    private static string? Points(int n) => n == 0 ? null : $"{(n > 0 ? "+" : "")}{n} points";

    private static string? Join(string? a, string b) => a is null ? b : $"{a}, {b}";

    private static void Add(List<string> parts, string label, string? outcome)
    {
        if (outcome is not null)
            parts.Add($"{label}: {outcome}.");
    }
}
