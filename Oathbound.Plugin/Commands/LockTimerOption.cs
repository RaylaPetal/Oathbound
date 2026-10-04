using System;

namespace Oathbound.Plugin.Commands;

/// `lockfor:<seconds>` right after the category word. It leads rather than trails so an older client hits its
/// "unrecognized override" branch instead of silently locking permanently or sending it into chat.
public static class LockTimerOption
{
    private const string Token = "lockfor:";

    /// A malformed option is left in place so dispatch rejects the whole command.
    public static string Strip(string rest, out RestraintLock restraintLock)
    {
        var remainder = StripSeconds(rest, out var seconds);
        restraintLock = seconds is { } s ? RestraintLock.FromSeconds(s) : RestraintLock.Permanent;
        return remainder;
    }

    /// The raw seconds, unclamped: an animation hold counts in seconds, a restraint lock in minutes.
    public static string StripSeconds(string rest, out int? seconds)
    {
        seconds = null;
        var trimmed = rest.TrimStart();
        if (!trimmed.StartsWith(Token, StringComparison.OrdinalIgnoreCase))
            return rest;

        var end = trimmed.IndexOf(' ');
        var value = end < 0 ? trimmed[Token.Length..] : trimmed[Token.Length..end];
        if (end < 0 || !int.TryParse(value, out var parsed) || parsed <= 0)
            return rest;

        seconds = parsed;
        return trimmed[(end + 1)..].TrimStart();
    }

    /// A Permanent lock returns `command` unchanged.
    public static string Insert(string command, RestraintLock restraintLock) =>
        restraintLock.Seconds is { } seconds ? InsertSeconds(command, seconds) : command;

    public static string InsertSeconds(string command, int seconds)
    {
        var trimmed = command.TrimStart();
        var space = trimmed.IndexOf(' ');
        if (space < 0)
            return command;
        var rest = trimmed[(space + 1)..];
        // Never stack a second option.
        if (rest.TrimStart().StartsWith(Token, StringComparison.OrdinalIgnoreCase))
            return command;
        return $"{trimmed[..space]} {Token}{seconds} {rest}";
    }
}
