using System;

namespace Oathbound.Plugin.Rulebook;

/// Ritual repeats follow the Sub's local calendar days, so they reset at local midnight whatever DST does.
public static class RitualCalendar
{
    public static long StartOfLocalDay(long unixSeconds) => DayStart(ToLocal(unixSeconds).Date);

    /// A repeat starting at a day start ends `days` calendar days later. One starting mid-day (stored by an older
    /// build, or shifted by a time zone change) ends at the first midnight after that, so it's never shortened.
    public static long RepeatEnd(long startUnixSeconds, int days)
    {
        var date = ToLocal(startUnixSeconds).Date;
        var end = date.AddDays(Math.Max(1, days));
        return startUnixSeconds <= DayStart(date) ? DayStart(end) : DayStart(end.AddDays(1));
    }

    public static long ScopeEnd(long dayStartUnixSeconds, int durationMinutes) =>
        DayStart(ToLocal(dayStartUnixSeconds).Date.AddDays(DurationDays(durationMinutes)));

    public static int DurationDays(int durationMinutes) => Math.Max(1, (durationMinutes + 1439) / 1440);

    public static DateTime LocalDate(long unixSeconds) => ToLocal(unixSeconds).Date;

    private static DateTime ToLocal(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;

    private static long DayStart(DateTime date)
    {
        var local = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        // A zone that changes its clocks at 00:00 skips midnight; that day starts at its first valid minute.
        while (TimeZoneInfo.Local.IsInvalidTime(local))
            local = local.AddMinutes(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local)).ToUnixTimeSeconds();
    }
}
