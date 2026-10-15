using System;

namespace Oathbound.Plugin.Commands;

/// `lockfor:<seconds>` and then `key:<salt>.<hash>` right after the category word. They lead rather than trail so an
/// older client hits its "unrecognized override" branch instead of silently locking without them or sending them into chat.
public static class LockTimerOption
{
    private const string Token = "lockfor:";
    private const string KeyToken = "key:";

    /// A malformed option is left in place so dispatch rejects the whole command.
    public static string Strip(string rest, out RestraintLock restraintLock)
    {
        var remainder = StripSeconds(rest, out var seconds);
        remainder = StripKey(remainder, out var key);
        restraintLock = (seconds is { } s ? RestraintLock.FromSeconds(s) : RestraintLock.Permanent).WithKey(key);
        return remainder;
    }

    private static string StripKey(string rest, out string? key)
    {
        key = null;
        var trimmed = rest.TrimStart();
        if (!trimmed.StartsWith(KeyToken, StringComparison.OrdinalIgnoreCase))
            return rest;
        var end = trimmed.IndexOf(' ');
        if (end < 0)
            return rest;
        var value = trimmed[KeyToken.Length..end];
        if (!RestraintKey.IsValidWire(value))
            return rest;
        key = value;
        return trimmed[(end + 1)..].TrimStart();
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

    /// A Permanent lock without a key returns `command` unchanged.
    public static string Insert(string command, RestraintLock restraintLock)
    {
        if (restraintLock.Key is { } key)
            command = InsertAfterCategory(command, KeyToken, key);
        return restraintLock.Seconds is { } seconds ? InsertSeconds(command, seconds) : command;
    }

    private static string InsertAfterCategory(string command, string token, string value)
    {
        var trimmed = command.TrimStart();
        var space = trimmed.IndexOf(' ');
        if (space < 0)
            return command;
        var rest = trimmed[(space + 1)..];
        if (rest.TrimStart().StartsWith(token, StringComparison.OrdinalIgnoreCase))
            return command;
        return $"{trimmed[..space]} {token}{value} {rest}";
    }

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
