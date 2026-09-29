using System;

namespace Oathbound.Plugin.Commands;

/// collar/restraint-lock-timer: how long an Owner's restraint command locks the Restraints category -
/// Permanent (until `restraint unlock`/panic/pairing end, the pre-existing behavior) or Timed, which
/// RestraintCommand releases on its own once the duration has passed.
public readonly record struct RestraintLock(TimeSpan? Duration)
{
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(7);

    public static RestraintLock Permanent => new(null);

    /// Clamped to [MinDuration, MaxDuration] - an out-of-range request is honored at the nearest bound
    /// rather than rejected.
    public static RestraintLock Timed(TimeSpan duration) =>
        new(duration < MinDuration ? MinDuration : duration > MaxDuration ? MaxDuration : duration);

    public static RestraintLock FromSeconds(int? seconds) =>
        seconds is { } s ? Timed(TimeSpan.FromSeconds(s)) : Permanent;

    public bool IsTimed => Duration is not null;
    public int? Seconds => Duration is { } d ? (int)d.TotalSeconds : null;

    /// Compact human-readable duration ("2d 3h", "1h 30m", "12m 5s") for the Owner's picker and the Sub's
    /// remaining-time line.
    public static string Format(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        if (span.TotalDays >= 1)
            return span.Hours > 0 ? $"{(int)span.TotalDays}d {span.Hours}h" : $"{(int)span.TotalDays}d";
        if (span.TotalHours >= 1)
            return span.Minutes > 0 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{(int)span.TotalHours}h";
        if (span.TotalMinutes >= 1)
            return span.Seconds > 0 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{(int)span.TotalMinutes}m";
        return $"{span.Seconds}s";
    }
}
