using System;

namespace Oathbound.Plugin.Commands;

/// `leash duty:pause`: the Sub's client pauses this leash while the Sub is in a duty. Sits right after the word
/// `leash`, before `length:` and `moodle:`, which are stripped from the end first.
public static class DutyPauseOption
{
    private const string Token = "duty:pause";

    public static string Strip(string text, out bool pauseInDuties)
    {
        var trimmed = text.TrimEnd();
        pauseInDuties = trimmed.EndsWith(Token, StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == Token.Length || char.IsWhiteSpace(trimmed[^(Token.Length + 1)]));
        return pauseInDuties ? trimmed[..^Token.Length].TrimEnd() : text;
    }

    public static string Append(string command) => $"{command} {Token}";
}
