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
                {
                    var name = AnimationNames.Short(Unquote(LockTimerOption.StripSeconds(rest, out var hold)));
                    return hold is { } seconds
                        ? $"play {name}, held {RestraintLock.Format(TimeSpan.FromSeconds(seconds))}"
                        : $"play {name}";
                }
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

/// Catalog labels read "mod — group — option — /trigger"; people only need the option, without pack numbering,
/// [author] tags or (emote list) suffixes.
public static partial class AnimationNames
{
    public static string Short(string label)
    {
        var parts = label.Split(" — ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count > 1 && parts[^1].StartsWith('/'))
            parts.RemoveAt(parts.Count - 1);
        var name = parts.Count > 1 ? parts[^1] : parts.FirstOrDefault() ?? label;
        var cleaned = Whitespace().Replace(Noise().Replace(LeadingNumber().Replace(name, ""), ""), " ").Trim(' ', '-', '*');
        return cleaned.Length > 0 ? cleaned : label.Trim();
    }

    [GeneratedRegex(@"^\s*\d+\s*[.)]\s*")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)")]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Whitespace();
}

/// An oath's condition and scope as one phrase, and its outcomes as labeled lines.
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
            OathCondition.GreetOwner => $"greet your Owner with {Greeting(oath)}{(string.IsNullOrEmpty(oath.AnimationId) ? "" : $" (held {Hold(oath)})")} {Repeat(oath)}",
            OathCondition.MessageOwner => $"send your Owner a tell{Containing(oath.Phrase)} {Repeat(oath)}",
            OathCondition.CheckIn => $"log in at least once {Every(oath.PeriodDays)}",
            OathCondition.SayGoodnight => $"tell your Owner goodnight{Containing(oath.Phrase)} before you log off",
            OathCondition.AddressOwner => $"call your Owner \"{oath.Phrase.Trim()}\" in every tell to them",
            OathCondition.ForbiddenWord => $"never say \"{oath.Phrase.Trim()}\" in chat",
            OathCondition.QuietInPublic => "stay quiet in /say, /shout and /yell",
            _ => "?",
        };
        var scope = oath.Scope == OathScope.NextDuty ? "during the next duty" : $"for {Duration(oath)}";
        return $"{condition} {scope}";
    }

    private static string Duration(Oath oath) => RestraintLock.Format(TimeSpan.FromMinutes(oath.DurationMinutes));

    public static string Hold(Oath oath) => RestraintLock.Format(TimeSpan.FromSeconds(oath.HoldSeconds));

    /// "day" for daily rituals, "stretch" for longer repeats.
    public static string Unit(Oath oath) => oath.PeriodDays == 1 ? "day" : "stretch";

    /// The emote by its command, or a modded animation by its short name.
    public static string Greeting(Oath oath) =>
        string.IsNullOrEmpty(oath.AnimationId) ? EmoteName(oath.EmoteId) : AnimationNames.Short(oath.AnimationLabel);

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

    /// "Swear to", then when each outcome applies and what it does, then per-time scoring.
    public static List<(string Label, string Text)> Lines(Oath oath)
    {
        static string Part(IReadOnlyList<string> commands, int ledger)
        {
            var parts = commands.Select(ConsequenceText.Describe).ToList();
            if (ledger != 0)
                parts.Add($"{Signed(ledger)} ledger");
            return parts.Count == 0 ? "nothing" : string.Join("; ", parts);
        }

        var lines = new List<(string, string)> { ("Swear to", RuleText.Cap(Describe(oath))) };
        var kept = Part(oath.Kept, oath.KeptLedger);
        var broken = Part(oath.Broken, oath.BrokenLedger);
        if (OathConditions.IsRitual(oath.Condition))
        {
            var unit = Unit(oath);
            lines.Add(("Kept", $"After {Duration(oath)} with no {unit} missed: {kept}"));
            lines.Add(("Broken", $"At the end, if any {unit} was missed: {broken}"));
            var each = new List<string>();
            if (oath.LedgerPerDone != 0)
                each.Add($"{Signed(oath.LedgerPerDone)} ledger when done");
            if (oath.LedgerPerMissed != 0)
                each.Add($"{Signed(oath.LedgerPerMissed)} ledger for each one missed");
            if (each.Count > 0)
                lines.Add(("Each time", RuleText.Cap(string.Join(", ", each))));
        }
        else
        {
            lines.Add(("Kept", oath.Scope == OathScope.NextDuty ? $"When the duty is completed: {kept}" : $"After the full {Duration(oath)}: {kept}"));
            lines.Add(("Broken", $"Right away: {broken}"));
        }
        return lines;
    }

    private static string Signed(int v) => $"{(v > 0 ? "+" : "")}{v}";

    private static string Places(Oath oath) =>
        oath.Places.Count == 0 ? "(no places)" : string.Join(", ", oath.Places.Select(RulebookPlaces.Describe));

    public static string Clock(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";
}

/// Each rule as a few labeled lines; the Sub's rules list, the review diff and the Owner's lists all use these.
public static class RuleText
{
    public static List<(string Label, string Text)> Place(PlaceRule r) =>
    [
        ("Where", Cap(string.Join(", ", r.Places.Select(RulebookPlaces.Describe)))),
        ("On entering", Cap(ConsequenceText.Describe(r.Enter))),
        ("On leaving", Cap(ConsequenceText.Describe(r.Leave))),
        ("Cooldown", $"{r.CooldownSeconds}s"),
    ];

    public static List<(string Label, string Text)> Presence(PresenceRule r, string owner)
    {
        var arrive = r.Arrive.Select(ConsequenceText.Describe).ToList();
        if (r.LeashOnArrive)
            arrive.Add($"leash you to {owner} ({r.LeashLengthYalms} yalms)");
        if (!string.IsNullOrWhiteSpace(r.ArriveTell))
            arrive.Add($"you /tell {owner} \"{r.ArriveTell.Trim()}\"");
        var depart = r.Depart.Select(ConsequenceText.Describe).ToList();
        if (r.UnleashOnDepart)
            depart.Add("take the leash off");
        if (!string.IsNullOrWhiteSpace(r.DepartTell))
            depart.Add($"you /tell {owner} \"{r.DepartTell.Trim()}\"");
        return
        [
            ("When", $"{Cap(owner)} comes within {r.RangeYalms:0} yalms"),
            ("Arrives", Cap(arrive.Count == 0 ? "nothing" : string.Join("; ", arrive))),
            ("Leaves", Cap(depart.Count == 0 ? "nothing" : string.Join("; ", depart))),
            ("Cooldown", $"{r.CooldownSeconds}s"),
        ];
    }

    public static List<(string Label, string Text)> Threshold(LedgerThreshold t)
    {
        var parts = t.Consequence.Select(ConsequenceText.Describe).ToList();
        if (t.DrawCard)
            parts.Add($"draw a {CardPiles.Word(t.Pile)} card");
        var lines = new List<(string, string)>
        {
            ("When", $"The ledger is {(t.Direction == ThresholdDirection.AtOrAbove ? "at or above" : "at or below")} {t.Score}"),
            ("Does", Cap(parts.Count == 0 ? "nothing" : string.Join("; ", parts))),
        };
        if (t.ResetTo is { } to)
            lines.Add(("Then", $"Reset the ledger to {to}"));
        return lines;
    }

    public static List<(string Label, string Text)> Card(DeckCard c) =>
    [
        ("Does", Cap(ConsequenceText.Describe(c.Consequence))),
        ("Pile", $"{(c.Pile == CardPile.Reward ? "Reward" : "Punishment")}, weight {c.Weight}{(c.Once ? ", only once" : "")}"),
    ];

    public static List<(string Label, string Text)> DrawOn(DrawOnSettings d)
    {
        var bad = new List<string>();
        if (d.OathBroken) bad.Add("an oath is broken");
        if (d.Wipe) bad.Add("the party wipes");
        if (d.Death) bad.Add("your character dies");
        var lines = new List<(string, string)>();
        if (bad.Count > 0) lines.Add(("Punishment", $"When {string.Join(", ", bad)}"));
        if (d.OathKept) lines.Add(("Reward", "When an oath is kept"));
        if (lines.Count == 0)
            return [("", "Never draws by itself.")];
        lines.Add(("Cooldown", $"{d.CooldownSeconds}s"));
        return lines;
    }

    public static List<(string Label, string Text)> Oath(Oath o) => OathText.Lines(o);

    /// Every rule of a document by id, with a kind label and its lines; Summary joins them for change detection.
    public static List<(string Id, string Kind, string Name, string Summary, List<(string Label, string Text)> Lines)> All(RulebookDocument doc, string owner)
    {
        var list = new List<(string, string, string, string, List<(string, string)>)>();
        void Add(string id, string kind, string name, List<(string Label, string Text)> lines) =>
            list.Add((id, kind, name, string.Join("\n", lines.Select(l => $"{l.Label}: {l.Text}")), lines));
        foreach (var o in doc.Oaths) Add(o.Id, "Oath", Name(o.Name, "Oath"), Oath(o));
        foreach (var c in doc.Deck) Add(c.Id, "Card", Name(c.Name, "Card"), Card(c));
        foreach (var p in doc.Places) Add(p.Id, "Place rule", Name(p.Name, "Place rule"), Place(p));
        foreach (var p in doc.Presence) Add(p.Id, "Presence rule", Name(p.Name, "Presence rule"), Presence(p, owner));
        foreach (var t in doc.Thresholds) Add(t.Id, "Ledger threshold", Name(t.Name, "Threshold"), Threshold(t));
        Add(DrawOnSettings.RuleId, "Deck", "Deck draws on events", DrawOn(doc.DrawOn));
        return list;
    }

    public static string Cap(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Name(string name, string fallback) => string.IsNullOrWhiteSpace(name) ? $"({fallback.ToLowerInvariant()} without a name)" : name;
}
