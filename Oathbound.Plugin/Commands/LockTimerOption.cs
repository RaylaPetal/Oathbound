using System;

namespace Oathbound.Plugin.Commands;

/// collar/restraint-lock-timer "Lock option on the wire fails closed on older clients": the optional
/// `lockfor:<seconds>` sub-verb placed directly after the `restraint`/`customtrigger` category word. Unlike
/// MoodleOption it leads rather than trails: a trailing token would land inside an old client's `rules:`
/// list (silently dropped - a surprise Permanent lock) or a `chat=` tail (sent into game chat), whereas an
/// unknown leading sub-verb hits an old client's "Unrecognized override" branch and applies nothing.
public static class LockTimerOption
{
    private const string Token = "lockfor:";

    /// `rest` is everything after the category word. Returns it without a leading `lockfor:<seconds>`
    /// option and the lock it asked for (Permanent when there is none). A malformed option is left in
    /// place, so the normal sub-verb dispatch rejects the whole command rather than locking permanently.
    public static string Strip(string rest, out RestraintLock restraintLock)
    {
        restraintLock = RestraintLock.Permanent;
        var trimmed = rest.TrimStart();
        if (!trimmed.StartsWith(Token, StringComparison.OrdinalIgnoreCase))
            return rest;

        var end = trimmed.IndexOf(' ');
        var value = end < 0 ? trimmed[Token.Length..] : trimmed[Token.Length..end];
        if (end < 0 || !int.TryParse(value, out var seconds) || seconds <= 0)
            return rest;

        restraintLock = RestraintLock.FromSeconds(seconds);
        return trimmed[(end + 1)..].TrimStart();
    }

    /// Inserts the option after `command`'s first word (the category). A Permanent lock returns `command`
    /// unchanged, so an un-timed send stays byte-for-byte what older Sub clients already understand.
    public static string Insert(string command, RestraintLock restraintLock)
    {
        if (restraintLock.Seconds is not { } seconds)
            return command;

        var trimmed = command.TrimStart();
        var space = trimmed.IndexOf(' ');
        if (space < 0)
            return command;
        var rest = trimmed[(space + 1)..];
        // Never stack a second option onto a command that already carries one.
        if (rest.TrimStart().StartsWith(Token, StringComparison.OrdinalIgnoreCase))
            return command;
        return $"{trimmed[..space]} {Token}{seconds} {rest}";
    }
}
