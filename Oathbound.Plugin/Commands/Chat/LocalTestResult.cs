namespace Oathbound.Plugin.Commands;

/// `Feedback` is the Sub's chat line, worded to follow the Owner's name ("locked your outfit: Maid").
/// `Unmatched` marks text that named no alias or command, which may be a Reactions phrase instead.
/// `Code` is why a failed command was refused, as the Owner's apply receipt reports it.
public readonly record struct LocalTestResult(bool Success, string Message, string? Feedback = null, bool Unmatched = false, RefusalCode Code = RefusalCode.Other)
{
    public static LocalTestResult Ok(string message, string? feedback = null) => new(true, message, feedback);
    public static LocalTestResult Fail(string message, string? feedback = null, RefusalCode code = RefusalCode.Other) => new(false, message, feedback, Code: code);

    public RefusalCode ReceiptCode => Unmatched ? RefusalCode.Unknown : Code;
}
