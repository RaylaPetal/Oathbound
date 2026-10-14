using System;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// The Owner's chat line for a command they sent: what went out, never whether it worked. Follows the same grammar
/// as ChatCommandListener.Resolve; anything only the Sub can resolve (an alias) is shown as sent.
public static class CommandDescriber
{
    /// `body` is the text after the trigger phrase.
    public static string Describe(string body)
    {
        var text = body.Trim();
        var (word, rest) = Split(text);
        var described = word.ToLowerInvariant() switch
        {
            "title" => Title(rest),
            "outfit" => Outfit(rest),
            "gesture" => Gesture(rest),
            "collar" => rest.ToLowerInvariant() switch { "lock" => "lock collar", "unlock" => "unlock collar", _ => null },
            "moodle" => Moodle(rest),
            "restraint" => Restraint(rest),
            "toy" => Toy(rest),
            "customtrigger" => CustomTrigger(rest),
            "teleport" => TeleportTarget.TryParse(rest, out var to) ? $"teleport to you on {to.World}" : null,
            "revert" when rest.Equals("all", StringComparison.OrdinalIgnoreCase) => "revert everything (not the collar)",
            Rulebook.ConsequenceValidator.DeckWord => $"draw a {Split(Split(rest).Remainder).First.ToLowerInvariant()} card",
            Rulebook.ConsequenceValidator.LedgerWord => $"ledger {rest}",
            ControlWords.Leash => Leash(text),
            _ => Control(text),
        };
        return described ?? $"\"{text}\"";
    }

    private static string? Title(string rest)
    {
        rest = LockTimerOption.StripSeconds(rest, out var seconds);
        var (verb, payload) = Split(rest);
        return verb.ToLowerInvariant() switch
        {
            "clear" => "clear title",
            "create" when payload.Length > 0 => $"set title \"{Name(payload)}\"{Locked(seconds)}",
            "style" when TitleCommand.TryParseStyleCommand(payload, out var title, out _, out _, out _) => $"set title \"{title}\"{Locked(seconds)}",
            _ => null,
        };
    }

    private static string? Outfit(string rest)
    {
        rest = MoodleOption.Strip(LockTimerOption.StripSeconds(rest, out var seconds), out _);
        var (verb, payload) = Split(rest);
        return verb.ToLowerInvariant() switch
        {
            "unlock" => "release outfit",
            "wear" when payload.Length > 0 => $"wear outfit {Name(payload)}{Locked(seconds)}",
            "lock" when payload.Length > 0 => $"lock outfit {Name(payload)}{Locked(seconds)}",
            _ => null,
        };
    }

    private static string? Gesture(string rest)
    {
        var name = LockTimerOption.StripSeconds(rest, out var seconds).Trim();
        if (name.Length == 0)
            return null;
        if (name.Equals(ChatComposer.StopGestureWord, StringComparison.OrdinalIgnoreCase))
            return "stop animation";
        return $"play {Name(name)}" + (seconds is { } s ? $" for {RestraintLock.Format(TimeSpan.FromSeconds(s))}" : "");
    }

    private static string? Moodle(string rest)
    {
        rest = LockTimerOption.StripSeconds(rest, out var seconds);
        var (verb, payload) = Split(rest);
        if (verb.Equals(CustomMoodle.CustomWord, StringComparison.OrdinalIgnoreCase))
            return CustomMoodle.TryDecode(payload, out var custom) ? $"apply moodle \"{MoodlesTextFormat.StripMarkup(custom.Title)}\"{Locked(seconds)}" : null;
        if (verb.Equals(CustomMoodle.RemoveWord, StringComparison.OrdinalIgnoreCase) && CustomMoodle.TryDecodeId(payload, out _))
            return "remove a moodle you wrote";
        return verb.ToLowerInvariant() switch
        {
            "clear" => "clear moodles",
            "apply" when payload.Length > 0 => $"apply moodle {Name(payload)}{Locked(seconds)}",
            "remove" when payload.Length > 0 => $"remove moodle {Name(payload)}",
            _ => null,
        };
    }

    private static string? Restraint(string rest)
    {
        rest = MoodleOption.Strip(LockTimerOption.Strip(rest, out var restraintLock), out _);
        var locked = Locked(restraintLock.Seconds) + (restraintLock.Key is not null ? " with a key" : "");
        var (verb, payload) = Split(rest);
        switch (verb.ToLowerInvariant())
        {
            case "unlock":
                return payload.Length == 0 ? "unlock all restraints" : $"unlock {Name(payload)}";
            case "timer" when RestraintCommand.TryParseTimerAdjust(payload, out var delta):
            {
                var amount = RestraintLock.Format(TimeSpan.FromSeconds(Math.Abs(delta)));
                return delta > 0 ? $"add {amount} to the restraint timer" : $"take {amount} off the restraint timer";
            }
            case "lock" when RestraintCommand.TryParseLockCommand(payload, out var device, out _) && device.Length > 0:
                return $"lock restraint {device}{locked}";
            case "catalog" when RestraintCommand.TryParseCatalogCommand(payload, out _, out var label, out _, out _):
                return $"lock restraint {label}{locked}";
            case "wear" when RestraintCommand.TryParseWearCommand(payload, out _, out _, out var label, out _):
                return $"lock restraint {label}{locked}";
            default:
                return null;
        }
    }

    private static string? Toy(string rest)
    {
        const string patternPrefix = "pattern:";
        if (rest.Equals("stop", StringComparison.OrdinalIgnoreCase))
            return "stop toy";
        if (rest.StartsWith(patternPrefix, StringComparison.OrdinalIgnoreCase))
            return $"toy pattern {rest[patternPrefix.Length..].Trim()}";
        var (verb, payload) = Split(rest);
        return verb.ToLowerInvariant() switch
        {
            "vibrate" when ToyControlCommand.TryParseVibrateCommand(payload, out var intensity, out _) => $"vibrate toy at {intensity}%",
            "sequence" when ToyControlCommand.TryParseCustomSequenceCommand(payload, out var steps, out _) => $"play a {steps.Count}-step toy pattern",
            _ => null,
        };
    }

    private static string? CustomTrigger(string rest)
    {
        rest = LockTimerOption.Strip(rest, out var restraintLock);
        if (rest.Trim().Equals("revert", StringComparison.OrdinalIgnoreCase))
            return "revert custom trigger effects";
        var (verb, payload) = Split(rest);
        return verb.Equals("cast", StringComparison.OrdinalIgnoreCase) && CustomTriggerCommand.TryParseCastCommand(payload, out var label, out _)
            ? $"cast \"{label}\"{Locked(restraintLock.Seconds)}"
            : null;
    }

    /// `leash travel <payload>` or `leash [duty:pause] [length:N] [moodle:"..."]`.
    private static string? Leash(string text)
    {
        var (_, rest) = Split(text);
        var (word, payload) = Split(rest);
        if (word.Equals(ChatComposer.LeashTravelWord, StringComparison.OrdinalIgnoreCase))
            return TeleportTarget.TryParse(payload, out var to) ? $"leash travel to {to.World}" : null;
        var bare = DutyPauseOption.Strip(LengthOption.Strip(MoodleOption.Strip(text, out _), out var yalms), out _);
        return bare.Trim().Equals(ControlWords.Leash, StringComparison.OrdinalIgnoreCase)
            ? $"leash ({yalms ?? LengthOption.DefaultYalms} yalms)"
            : null;
    }

    private static string? Control(string text) => text.ToLowerInvariant() switch
    {
        ControlWords.Unleash => "unleash",
        ControlWords.ClearTitle => "clear title",
        ControlWords.ClearMoodle => "clear moodles",
        ControlWords.Unlock => "unlock outfit",
        _ => null,
    };

    private static string Locked(int? seconds) =>
        RestraintLock.FromSeconds(seconds).Duration is { } d ? $" (locked {RestraintLock.Format(d)})" : "";

    private static string Name(string text) =>
        CommandSelector.TryRead(text, out var value, out _) ? value : text.Trim();

    private static (string First, string Remainder) Split(string text)
    {
        var trimmed = text.Trim();
        var space = trimmed.IndexOf(' ');
        return space < 0 ? (trimmed, "") : (trimmed[..space], trimmed[(space + 1)..].Trim());
    }
}
