namespace Oathbound.Plugin.Commands;

/// `Feedback` is the Sub's chat line, worded to follow the Owner's name ("locked your outfit: Maid").
/// `Unmatched` marks text that named no alias or command, which may be a Reactions phrase instead.
public readonly record struct LocalTestResult(bool Success, string Message, string? Feedback = null, bool Unmatched = false)
{
    public static LocalTestResult Ok(string message, string? feedback = null) => new(true, message, feedback);
    public static LocalTestResult Fail(string message, string? feedback = null) => new(false, message, feedback);
}
