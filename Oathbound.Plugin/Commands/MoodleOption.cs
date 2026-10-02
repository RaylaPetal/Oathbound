using System;

namespace Oathbound.Plugin.Commands;

/// The optional trailing `moodle:"<status>"` option. Always last, so it's stripped before the other parsers run.
/// An older Sub reads it as part of the name, matches nothing, and fails closed.
public static class MoodleOption
{
    private const string Token = "moodle:";

    /// A malformed option is left in place for the normal parser to reject.
    public static string Strip(string text, out string? moodleName)
    {
        moodleName = null;
        var trimmed = text.TrimEnd();
        if (!trimmed.EndsWith('"'))
            return text;

        var start = trimmed.LastIndexOf(Token + "\"", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || (start > 0 && !char.IsWhiteSpace(trimmed[start - 1])))
            return text;

        var name = trimmed[(start + Token.Length + 1)..^1].Trim();
        if (name.Length == 0 || name.Contains('"'))
            return text;

        moodleName = name;
        return trimmed[..start].TrimEnd();
    }

    /// Nothing is appended when no override was picked.
    public static string Append(string command, string? moodleName) =>
        string.IsNullOrWhiteSpace(moodleName) ? command : $"{command} {Token}\"{moodleName.Trim()}\"";
}
