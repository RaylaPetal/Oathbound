using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Rulebook;

/// Rulebook consequences run with no click from either person, so only categories that neither send chat, move
/// the character nor touch the collar are allowed. Checked when the Owner saves and again on the Sub after
/// decryption. Aliases are refused too: an alias can expand to anything, including a chat action.
public static class ConsequenceValidator
{
    public const string DeckWord = "deck";
    public const string LedgerWord = "ledger";
    public const string LadderWord = "ladder";

    private static readonly HashSet<string> AllowedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "outfit", "gesture", "moodle", "restraint", "toy", "revert", "customtrigger", DeckWord, LedgerWord, LadderWord,
    };

    /// Null when allowed, else why not, in words the editor can show.
    public static string? Check(string command)
    {
        var text = command.Trim();
        if (text.Length == 0)
            return "Empty command.";
        if (!CommandSelector.Fits(text))
            return $"Longer than {CommandSelector.MaxCommandLength} characters.";

        var space = text.IndexOf(' ');
        var word = space < 0 ? text : text[..space];
        var rest = space < 0 ? "" : text[(space + 1)..].Trim();
        if (!AllowedWords.Contains(word))
            return word.ToLowerInvariant() switch
            {
                "teleport" => "Teleport can't be a rulebook consequence.",
                "leash" or "follow" or ControlWords.Unleash => "Follow and leash can't be rulebook consequences.",
                "collar" => "The collar can't be a rulebook consequence.",
                _ => $"\"{word}\" isn't allowed in a rulebook. Use title, outfit, gesture, moodle, restraint, toy, revert all, deck draw reward/punishment, ledger +n/-n (points) or ladder up/down.",
            };

        switch (word.ToLowerInvariant())
        {
            case "revert":
                return rest.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : "Only \"revert all\" is allowed.";
            case DeckWord:
                return CardPiles.TryParseDraw(rest, out _) ? null : "Use \"deck draw reward\" or \"deck draw punishment\".";
            case LedgerWord:
                return LedgerCommand.TryParse(rest, out _, out _) ? null : "Use \"ledger +n\" or \"ledger -n\" to give or take n points, with n from 1 to 99.";
            case LadderWord:
                return LadderCommand.TryParse(rest, allowSet: false, out _, out _) ? null : "Use \"ladder up\" or \"ladder down\".";
            case "customtrigger":
                return CheckCustomTrigger(rest);
            default:
                return null;
        }
    }

    public static string? CheckAll(IEnumerable<string> commands) => commands.Select(Check).FirstOrDefault(e => e is not null);

    private static string? CheckCustomTrigger(string rest)
    {
        var afterLock = LockTimerOption.Strip(rest, out _).TrimStart();
        const string castWord = "cast ";
        if (!afterLock.StartsWith(castWord, StringComparison.OrdinalIgnoreCase))
            return "Only a Custom Trigger \"cast\" bundle is allowed.";
        if (!CustomTriggerCommand.TryParseCastCommand(afterLock[castWord.Length..].Trim(), out _, out var actions))
            return "That Custom Trigger bundle couldn't be read.";
        return actions.Any(a => a.Kind == CustomTriggerActionKind.Chat)
            ? "A Custom Trigger with a chat action can't be a rulebook consequence."
            : null;
    }

    /// Every consequence in a document, for the Sub's whole-version check.
    public static string? CheckDocument(RulebookDocument doc) =>
        AllConsequences(doc).Select(CheckAll).FirstOrDefault(e => e is not null) ?? CheckLadder(doc.Ladder);

    /// Each stage puts one restraint on, locked only by the ladder: no unlock or timer form, no lockfor:, no key.
    public static string? CheckLadder(Ladder? ladder)
    {
        if (ladder is null || ladder.Stages.Count == 0)
            return null;
        if (ladder.Stages.Count > RulebookLimits.MaxLadderStages)
            return $"A ladder has at most {RulebookLimits.MaxLadderStages} stages.";
        if (ladder.DecayHours is < 0 or > RulebookLimits.MaxLadderDecayHours || ladder.FailedStruggles is < 0 or > RulebookLimits.MaxLadderFailedStruggles)
            return "The ladder's decay or failed-struggle setting is out of range.";
        foreach (var stage in ladder.Stages)
        {
            if (stage.Struggle is < StruggleLevel.None or > StruggleLevel.Hard)
                return $"Stage \"{stage.Name}\" has an unknown struggle setting.";
            if (CheckStageCommand(stage.Command) is { } problem)
                return $"Stage \"{stage.Name}\": {problem}";
        }
        return null;
    }

    public static string? CheckStageCommand(string command)
    {
        var text = command.Trim();
        if (!text.StartsWith("restraint ", StringComparison.OrdinalIgnoreCase))
            return "a stage must be a restraint.";
        if (Check(text) is { } refused)
            return refused;
        var rest = LockTimerOption.Strip(text["restraint ".Length..], out var restraintLock).Trim();
        if (restraintLock.IsTimed || restraintLock.Key is not null)
            return "the ladder is the lock, so a stage can't have a timer or a key.";
        if (rest.StartsWith("unlock", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("timer", StringComparison.OrdinalIgnoreCase))
            return "a stage must put a restraint on.";
        return null;
    }

    public static bool IsLadderMove(string command) =>
        command.TrimStart().StartsWith(LadderWord + " ", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<List<string>> AllConsequences(RulebookDocument doc)
    {
        foreach (var o in doc.Oaths) { yield return o.Kept; yield return o.Broken; }
        foreach (var c in doc.Deck) yield return c.Consequence;
        foreach (var p in doc.Places) { yield return p.Enter; yield return p.Leave; }
        foreach (var p in doc.Presence) { yield return p.Arrive; yield return p.Depart; }
        foreach (var t in doc.Thresholds) yield return t.Consequence;
        foreach (var t in doc.Times) yield return t.Consequence;
        foreach (var i in doc.Shop) yield return i.Consequence;
    }

    /// A title, outfit or moodle with a timer, which a Sub from before timed locks would refuse when it ran.
    public static bool IsTimedOwnerLock(string command)
    {
        var text = command.Trim();
        var space = text.IndexOf(' ');
        if (space < 0)
            return false;
        var word = text[..space];
        return (word.Equals("title", StringComparison.OrdinalIgnoreCase) || word.Equals("outfit", StringComparison.OrdinalIgnoreCase)
                || word.Equals("moodle", StringComparison.OrdinalIgnoreCase))
            && text[(space + 1)..].TrimStart().StartsWith("lockfor:", StringComparison.OrdinalIgnoreCase);
    }
}

public static class LedgerCommand
{
    /// `+n` or `-n` (n 1..99), then an optional reason.
    public static bool TryParse(string rest, out int delta, out string reason)
    {
        delta = 0;
        reason = "";
        var text = rest.Trim();
        if (text.Length < 2 || (text[0] != '+' && text[0] != '-'))
            return false;
        var end = 1;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        if (end == 1 || !int.TryParse(text[1..end], out var n) || n < 1 || n > RulebookLimits.MaxLedgerChange)
            return false;
        if (end < text.Length && text[end] != ' ')
            return false;
        delta = text[0] == '-' ? -n : n;
        reason = text[end..].Trim();
        return true;
    }
}
