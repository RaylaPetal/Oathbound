using System.Text.RegularExpressions;

namespace Oathbound.Plugin.Config;

/// Moodles markup is stripped for display, never rendered. Each tag is stripped independently so a malformed
/// tag can't leave a stray bracket.
public static class MoodlesTextFormat
{
    private static readonly Regex MarkupTags = new(
        @"\[(?:color=[^\]]*|/color|glow=[^\]]*|/glow|i|/i)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// Display only - never before storing or matching, which use the exact name Moodles reports.
    public static string StripMarkup(string text) => MarkupTags.Replace(text, "");
}
