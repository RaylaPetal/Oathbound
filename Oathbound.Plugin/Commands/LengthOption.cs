using System;
using System.Globalization;

namespace Oathbound.Plugin.Commands;

/// collar/leash "Leash length": the optional `length:<yalms>` option on `leash`. It sits right before any
/// `moodle:"..."` option (which is always last), so the receiver strips the moodle first, then this. A
/// malformed or out-of-range value is left in place, so the exact `leash` match after it fails and the
/// command is refused - the same fail-closed shape as MoodleOption.
public static class LengthOption
{
    private const string Token = "length:";

    public const int MinYalms = 1;
    public const int MaxYalms = 15;
    public const int DefaultYalms = 3;

    /// Returns `text` without a trailing valid `length:N`, and N (null when there is none or it is invalid).
    public static string Strip(string text, out int? yalms)
    {
        yalms = null;
        var trimmed = text.TrimEnd();
        var start = trimmed.LastIndexOf(Token, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || (start > 0 && !char.IsWhiteSpace(trimmed[start - 1])))
            return text;

        var value = trimmed[(start + Token.Length)..];
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < MinYalms or > MaxYalms)
            return text;

        yalms = parsed;
        return trimmed[..start].TrimEnd();
    }

    public static string Append(string command, int yalms) =>
        $"{command} {Token}{Math.Clamp(yalms, MinYalms, MaxYalms).ToString(CultureInfo.InvariantCulture)}";
}
