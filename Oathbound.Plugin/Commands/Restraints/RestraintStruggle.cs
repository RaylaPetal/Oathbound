using System;
using System.Collections.Generic;

namespace Oathbound.Plugin.Commands;

/// Persisted as numbers; only ever appended.
public enum StruggleLevel
{
    None,
    Easy,
    Medium,
    Hard,
}

public readonly record struct StruggleSetting(StruggleLevel Level, int PenaltyMinutes)
{
    public bool Allowed => Level != StruggleLevel.None;
}

/// The Owner's struggle setting rides in a restraint command's `rules:` tokens, which an older Sub skips, so for it
/// the lock just can't be struggled against instead of the whole command being refused.
public static class RestraintStruggle
{
    private const string LevelToken = "struggle=";
    private const string PenaltyToken = "strugglepenalty=";
    private const string RulesToken = "rules:";
    public const int MaxPenaltyMinutes = 30;

    public static readonly string[] LevelNames = ["No struggling", "Easy", "Medium", "Hard"];

    public static double Chance(StruggleLevel level) => level switch
    {
        StruggleLevel.Easy => 0.25,
        StruggleLevel.Medium => 0.10,
        StruggleLevel.Hard => 0.03,
        _ => 0,
    };

    public static TimeSpan Wait(StruggleLevel level) => level switch
    {
        StruggleLevel.Easy => TimeSpan.FromMinutes(2),
        StruggleLevel.Medium => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(10),
    };

    public static string Describe(StruggleSetting s) => !s.Allowed ? "can't be struggled"
        : $"{LevelNames[(int)s.Level]} struggle ({Chance(s.Level):P0}, every {RestraintLock.Format(Wait(s.Level))})"
          + (s.PenaltyMinutes > 0 ? $", +{s.PenaltyMinutes}m per failed try" : "");

    public static IEnumerable<string> Tokens(StruggleSetting s)
    {
        if (!s.Allowed)
            yield break;
        yield return LevelToken + s.Level switch
        {
            StruggleLevel.Easy => "easy",
            StruggleLevel.Medium => "medium",
            _ => "hard",
        };
        if (s.PenaltyMinutes > 0)
            yield return PenaltyToken + Math.Min(s.PenaltyMinutes, MaxPenaltyMinutes);
    }

    /// From a decoded, comma-separated rule token list.
    public static StruggleSetting FromRuleTokens(string tokens)
    {
        var level = StruggleLevel.None;
        var penalty = 0;
        foreach (var token in tokens.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith(LevelToken, StringComparison.OrdinalIgnoreCase))
                level = token[LevelToken.Length..].ToLowerInvariant() switch
                {
                    "easy" => StruggleLevel.Easy,
                    "medium" => StruggleLevel.Medium,
                    "hard" => StruggleLevel.Hard,
                    _ => level,
                };
            else if (token.StartsWith(PenaltyToken, StringComparison.OrdinalIgnoreCase) && int.TryParse(token[PenaltyToken.Length..], out var minutes))
                penalty = Math.Clamp(minutes, 0, MaxPenaltyMinutes);
        }
        return new StruggleSetting(level, level == StruggleLevel.None ? 0 : penalty);
    }

    /// From a `restraint lock|catalog|wear ...` command's plain-text `rules:` list.
    public static StruggleSetting FromCommand(string rest)
    {
        var at = rest.IndexOf(RulesToken, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return default;
        var list = rest[(at + RulesToken.Length)..];
        var end = list.IndexOf(' ');
        return FromRuleTokens(end < 0 ? list : list[..end]);
    }

    /// Restraint forms that carry a `rules:` list (or a quoted device name one can follow), and Custom Trigger
    /// bundles with a restraint whose rules travel inline.
    public static bool Accepts(string command)
    {
        var t = command.Trim();
        if (t.StartsWith("restraint ", StringComparison.OrdinalIgnoreCase))
            return t.Contains(' ' + RulesToken, StringComparison.OrdinalIgnoreCase) || (t.EndsWith('"') && t.Contains(" lock \"", StringComparison.OrdinalIgnoreCase));
        return CustomTriggerCommand.HasInlineRestraintRules(t);
    }

    /// Adds the setting to a command about to be sent; anything else is returned unchanged.
    public static string Insert(string command, StruggleSetting s)
    {
        if (!s.Allowed || !Accepts(command))
            return command;
        var tokens = string.Join(',', Tokens(s));
        var t = command.Trim();
        if (!t.StartsWith("restraint ", StringComparison.OrdinalIgnoreCase))
            return CustomTriggerCommand.AppendRestraintRuleTokens(t, tokens);
        var at = t.IndexOf(' ' + RulesToken, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return $"{t} {RulesToken}{tokens}";
        var listStart = at + 1 + RulesToken.Length;
        var listEnd = t.IndexOf(' ', listStart);
        if (listEnd < 0)
            listEnd = t.Length;
        var separator = listEnd > listStart ? "," : "";
        return t[..listEnd] + separator + tokens + t[listEnd..];
    }
}
