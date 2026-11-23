using System;
using Oathbound.Plugin.Commands;

namespace Oathbound.Plugin.UI;

/// Every time shown in the UI: "1h 30m", "1h 30m left", "in 1h 30m", "5m ago", or a local short date and time.
public static class TimeText
{
    public static string Duration(TimeSpan span) => RestraintLock.Format(span);

    public static string Duration(long seconds) => RestraintLock.Format(TimeSpan.FromSeconds(Math.Max(0, seconds)));

    public static string Left(TimeSpan span) => $"{Duration(span)} left";

    public static string Left(DateTime endsAtUtc) => Left(endsAtUtc - DateTime.UtcNow);

    /// Null when there's no end, so callers can fall back with `??`.
    public static string? Left(DateTime? endsAtUtc) => endsAtUtc is { } end ? Left(end) : null;

    public static string Left(long endsAtUnixSeconds) => Left(DateTimeOffset.FromUnixTimeSeconds(endsAtUnixSeconds) - DateTimeOffset.UtcNow);

    public static string Until(TimeSpan span) => span <= TimeSpan.Zero ? "now" : $"in {Duration(span)}";

    public static string Until(DateTime atUtc) => Until(atUtc - DateTime.UtcNow);

    public static string Until(long atUnixSeconds) => Until(DateTimeOffset.FromUnixTimeSeconds(atUnixSeconds) - DateTimeOffset.UtcNow);

    /// Whole minutes: seconds only add noise to something in the past.
    public static string Ago(TimeSpan span) =>
        span < TimeSpan.FromMinutes(1) ? "just now" : $"{Duration(TimeSpan.FromMinutes(Math.Floor(span.TotalMinutes)))} ago";

    public static string Ago(DateTimeOffset at) => Ago(DateTimeOffset.UtcNow - at);

    public static string Ago(DateTime atUtc) => Ago(DateTime.UtcNow - atUtc);

    /// 0 is the unset value.
    public static string Ago(long unixSeconds) => unixSeconds <= 0 ? "never" : Ago(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

    public static string Date(DateTimeOffset at) => at.ToLocalTime().ToString("g");

    public static string Date(DateTime utc) => Date(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));

    public static string Date(long unixSeconds) => Date(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
}
