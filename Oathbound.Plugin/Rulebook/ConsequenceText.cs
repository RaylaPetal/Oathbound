using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Oathbound.Plugin.Commands;

namespace Oathbound.Plugin.Rulebook;

/// One plain sentence per consequence command, shown identically to the Owner while editing and the Sub in review.
public static partial class ConsequenceText
{
    public static string Describe(IReadOnlyList<string> commands) =>
        commands.Count == 0 ? "nothing" : string.Join("; ", commands.Select(Describe));

    public static string Describe(string command)
    {
        var text = command.Trim();
        var (word, rest) = Split(text);
        switch (word.ToLowerInvariant())
        {
            case "title":
                return $"set title {FirstQuoted(rest) ?? rest}";
            case "outfit":
                {
                    var (verb, name) = Split(rest);
                    return verb.ToLowerInvariant() switch
                    {
                        "lock" => $"wear and lock outfit {Unquote(name)}",
                        "wear" => $"wear outfit {Unquote(name)}",
                        "unlock" => "unlock outfit",
                        _ => text,
                    };
                }
            case "gesture":
                return $"play gesture {Unquote(rest)}";
            case "moodle":
                {
                    var (verb, name) = Split(rest);
                    return verb.Equals("apply", StringComparison.OrdinalIgnoreCase) ? $"apply moodle {Unquote(name)}" : verb.Equals("clear", StringComparison.OrdinalIgnoreCase) ? "clear moodle" : text;
                }
            case "restraint":
                return DescribeRestraint(rest, text);
            case "toy":
                return $"toy: {rest}";
            case "revert":
                return "revert everything the Owner can command";
            case ConsequenceValidator.DeckWord:
                return CardPiles.TryParseDraw(rest, out var pile) ? $"draw a {CardPiles.Word(pile)} card" : text;
            case ConsequenceValidator.LedgerWord:
                return LedgerCommand.TryParse(rest, out var delta, out var reason)
                    ? $"{(delta > 0 ? "+" : "")}{delta} ledger" + (reason.Length > 0 ? $" ({reason})" : "")
                    : text;
            case "customtrigger":
                {
                    var afterLock = LockTimerOption.Strip(rest, out var restraintLock).TrimStart();
                    if (afterLock.StartsWith("cast ", StringComparison.OrdinalIgnoreCase) &&
                        CustomTriggerCommand.TryParseCastCommand(afterLock[5..].Trim(), out var label, out var actions))
                        return $"\"{label}\": {string.Join(", ", actions.Select(CustomTriggerCommand.Summarize))}{LockSuffix(restraintLock)}";
                    return text;
                }
            default:
                return text;
        }
    }

    private static string DescribeRestraint(string rest, string text)
    {
        var afterLock = LockTimerOption.Strip(rest, out var restraintLock).TrimStart();
        var (verb, tail) = Split(afterLock);
        switch (verb.ToLowerInvariant())
        {
            case "unlock":
                return "unlock restraints";
            case "timer":
                return RestraintCommand.TryParseTimerAdjust(tail, out var delta)
                    ? $"{(delta > 0 ? "add" : "take")} {RestraintLock.Format(TimeSpan.FromSeconds(Math.Abs(delta)))} {(delta > 0 ? "to" : "off")} the restraint timer"
                    : text;
            case "lock" or "wear" or "catalog":
                return $"restrain with {FirstQuoted(tail) ?? "a restraint"}{LockSuffix(restraintLock)}";
            default:
                return text;
        }
    }

    private static string LockSuffix(RestraintLock restraintLock) =>
        restraintLock.Duration is { } d ? $", locked for {RestraintLock.Format(d)}" : "";

    private static (string Head, string Tail) Split(string text)
    {
        var t = text.Trim();
        var space = t.IndexOf(' ');
        return space < 0 ? (t, "") : (t[..space], t[(space + 1)..].Trim());
    }

    private static string Unquote(string text)
    {
        var t = text.Trim();
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t;
    }

    private static string? FirstQuoted(string text)
    {
        var m = QuotedPattern().Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex("\"([^\"]*)\"")]
    private static partial Regex QuotedPattern();
}

/// One plain sentence for an oath's condition and scope.
public static class OathText
{
    public static string Describe(Oath oath)
    {
        var condition = oath.Condition switch
        {
            OathCondition.NoDeaths => "don't die",
            OathCondition.NoWipes => "don't wipe",
            OathCondition.DutyTimeLimit => $"finish each duty within {oath.TimeLimitMinutes} minutes",
            OathCondition.StayInPlaces => $"stay in {Places(oath)}",
            OathCondition.AvoidPlaces => $"stay out of {Places(oath)}",
            OathCondition.Curfew => $"don't be logged in between {Clock(oath.CurfewStartMinutes)} and {Clock(oath.CurfewEndMinutes)}",
            OathCondition.GreetOwner => $"greet your Owner with {EmoteName(oath.EmoteId)} {Repeat(oath)}",
            OathCondition.MessageOwner => $"send your Owner a tell{Containing(oath.Phrase)} {Repeat(oath)}",
            OathCondition.CheckIn => $"log in at least once {Every(oath.PeriodDays)}",
            OathCondition.SayGoodnight => $"tell your Owner goodnight{Containing(oath.Phrase)} before you log off",
            OathCondition.AddressOwner => $"call your Owner \"{oath.Phrase.Trim()}\" in every tell to them",
            OathCondition.ForbiddenWord => $"never say \"{oath.Phrase.Trim()}\" in chat",
            OathCondition.QuietInPublic => "stay quiet in /say, /shout and /yell",
            _ => "?",
        };
        var scope = oath.Scope == OathScope.NextDuty
            ? "during the next duty"
            : $"for {Commands.RestraintLock.Format(System.TimeSpan.FromMinutes(oath.DurationMinutes))}";
        return $"{condition} {scope}";
    }

    private static string Repeat(Oath oath) =>
        $"{(oath.TimesPerPeriod == 1 ? "once" : oath.TimesPerPeriod == 2 ? "twice" : $"{oath.TimesPerPeriod} times")} {Every(oath.PeriodDays)}";

    private static string Every(int days) => days == 1 ? "a day" : $"every {days} days";

    private static string Containing(string phrase) => string.IsNullOrWhiteSpace(phrase) ? "" : $" saying \"{phrase.Trim()}\"";

    public static string EmoteName(uint emoteId)
    {
        if (emoteId == 0)
            return "a gesture";
        var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>().GetRowOrDefault(emoteId);
        var command = row?.TextCommand.ValueNullable?.Command.ExtractText();
        return string.IsNullOrWhiteSpace(command) ? row?.Name.ExtractText() ?? "a gesture" : command;
    }

    public static string Outcomes(Oath oath)
    {
        static string Part(System.Collections.Generic.IReadOnlyList<string> commands, int ledger)
        {
            var text = commands.Count == 0 ? "" : ConsequenceText.Describe(commands);
            if (ledger != 0)
                text += (text.Length > 0 ? "; " : "") + $"{(ledger > 0 ? "+" : "")}{ledger} ledger";
            return text.Length == 0 ? "nothing" : text;
        }
        return $"if kept: {Part(oath.Kept, oath.KeptLedger)}. If broken: {Part(oath.Broken, oath.BrokenLedger)}.";
    }

    private static string Places(Oath oath) =>
        oath.Places.Count == 0 ? "(no places)" : string.Join(", ", System.Linq.Enumerable.Select(oath.Places, RulebookPlaces.Describe));

    public static string Clock(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";
}

/// One summary line per rule; the Sub's review diff and the Owner's lists both use these.
public static class RuleText
{
    public static string Place(PlaceRule r) =>
        $"In {string.Join(", ", System.Linq.Enumerable.Select(r.Places, RulebookPlaces.Describe))}: on entering, {ConsequenceText.Describe(r.Enter)}; on leaving, {ConsequenceText.Describe(r.Leave)}. Cooldown {r.CooldownSeconds}s.";

    public static string Presence(PresenceRule r, string owner) =>
        $"When {owner} comes within {r.RangeYalms:0} yalms: {ConsequenceText.Describe(r.Arrive)}; when they leave: {ConsequenceText.Describe(r.Depart)}. Cooldown {r.CooldownSeconds}s.";

    public static string Threshold(LedgerThreshold t)
    {
        var what = ConsequenceText.Describe(t.Consequence);
        if (t.DrawCard)
        {
            var draw = $"draw a {CardPiles.Word(t.Pile)} card";
            what = t.Consequence.Count == 0 ? draw : what + "; " + draw;
        }
        var reset = t.ResetTo is { } to ? $", then reset to {to}" : "";
        return $"When the ledger is {(t.Direction == ThresholdDirection.AtOrAbove ? "at or above" : "at or below")} {t.Score}: {what}{reset}.";
    }

    public static string Card(DeckCard c) =>
        $"{(c.Pile == CardPile.Reward ? "Reward" : "Punishment")}, weight {c.Weight}{(c.Once ? ", once" : "")}: {ConsequenceText.Describe(c.Consequence)}.";

    public static string DrawOn(DrawOnSettings d)
    {
        var bad = new System.Collections.Generic.List<string>();
        if (d.OathBroken) bad.Add("an oath is broken");
        if (d.Wipe) bad.Add("the party wipes");
        if (d.Death) bad.Add("your character dies");
        var parts = new System.Collections.Generic.List<string>();
        if (bad.Count > 0) parts.Add($"a punishment card when {string.Join(", ", bad)}");
        if (d.OathKept) parts.Add("a reward card when an oath is kept");
        return parts.Count == 0 ? "Never draws by itself." : $"Draws {string.Join("; ", parts)}. Cooldown {d.CooldownSeconds}s.";
    }

    public static string Oath(Oath o) => $"{OathText.Describe(o)} - {OathText.Outcomes(o)}";

    /// Every rule of a document by id, with a kind label and summary, for diffs and switch lists.
    public static System.Collections.Generic.List<(string Id, string Kind, string Name, string Summary)> All(RulebookDocument doc, string owner)
    {
        var list = new System.Collections.Generic.List<(string, string, string, string)>();
        foreach (var o in doc.Oaths) list.Add((o.Id, "Oath", Name(o.Name, "Oath"), Oath(o)));
        foreach (var c in doc.Deck) list.Add((c.Id, "Card", Name(c.Name, "Card"), Card(c)));
        foreach (var p in doc.Places) list.Add((p.Id, "Place rule", Name(p.Name, "Place rule"), Place(p)));
        foreach (var p in doc.Presence) list.Add((p.Id, "Presence rule", Name(p.Name, "Presence rule"), Presence(p, owner)));
        foreach (var t in doc.Thresholds) list.Add((t.Id, "Ledger threshold", Name(t.Name, "Threshold"), Threshold(t)));
        list.Add((DrawOnSettings.RuleId, "Deck", "Deck draws on events", DrawOn(doc.DrawOn)));
        return list;
    }

    private static string Name(string name, string fallback) => string.IsNullOrWhiteSpace(name) ? $"({fallback.ToLowerInvariant()} without a name)" : name;
}
