using System;
using Dalamud.Bindings.ImGui;
using Oathbound.Plugin.Commands;

namespace Oathbound.Plugin.UI;

/// The Sub sees what the device is actually doing; the Owner sees an estimate from what they sent.
public static class ToyStatusView
{
    public static void DrawSub(ToyControlCommand toy, bool connected)
    {
        if (toy.CurrentStatus is { } s)
        {
            IconGlyph.WrappedColored(Theme.Success, $"Vibrating at {s.IntensityPercent}% - {s.Description}{StepText(s)}");
            IconGlyph.WrappedDisabled($"Started by {s.Source} - running {Clock(s.ElapsedMs)}, auto-stops in {Clock(s.RemainingMs)}");
        }
        else
            IconGlyph.WrappedDisabled(connected ? "Idle - nothing is playing." : "Idle - no toy connected.");
    }

    public static void DrawOwner(OwnerToyEstimate? estimate)
    {
        if (estimate is null)
            IconGlyph.WrappedDisabled("Nothing sent to this Sub yet this session.");
        else if (estimate.IsStop)
            IconGlyph.WrappedDisabled($"Last sent: stop, {Clock(Environment.TickCount64 - estimate.SentTicks)} ago.");
        else if (estimate.LikelyRunning)
            IconGlyph.WrappedColored(Theme.Success, $"Likely running: {OwnerSummary(estimate)} - sent {Clock(Environment.TickCount64 - estimate.SentTicks)} ago, {OwnerEnd(estimate)}");
        else
            IconGlyph.WrappedDisabled($"Likely finished: {OwnerSummary(estimate)} - sent {Clock(Environment.TickCount64 - estimate.SentTicks)} ago.");
        IconGlyph.HelpMarker("Based on what you sent - your client can't see your Sub's device.");
    }

    /// Null hides the entry. The Sub's actual state wins when a client is both.
    public static (string Text, string Tooltip)? Dtr(ToyControlCommand toy, OwnerToyEstimate? estimate)
    {
        if (toy.CurrentStatus is { } s)
            return ($"Toy {s.IntensityPercent}%", $"{s.Description}{StepText(s)}\nStarted by {s.Source}\nAuto-stops in {Clock(s.RemainingMs)}");
        if (estimate is { LikelyRunning: true })
            return ($"Sub toy {(estimate.IntensityPercent is { } i ? $"~{i}%" : "on")}",
                $"Estimate from what you sent: {estimate.Description}\n{OwnerEnd(estimate)}\nYour client can't see your Sub's device.");
        return null;
    }

    private static string StepText(ToyStatus s) =>
        s.StepCount > 1 ? $" (step {s.StepIndex + 1}/{s.StepCount}{(s.Loop ? ", looping" : "")})" : "";

    private static string OwnerSummary(OwnerToyEstimate e) =>
        e.IntensityPercent is { } i && !e.Description.StartsWith("Vibrate", StringComparison.Ordinal) ? $"{e.Description} ({i}%)" : e.Description;

    private static string OwnerEnd(OwnerToyEstimate e)
    {
        if (e.EndTicks is not { } end) return "runs until stopped";
        var left = Clock(Math.Max(0, end - Environment.TickCount64));
        return e.EndIsExact ? $"ends in {left}" : $"stops within {left}";
    }

    private static string Clock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }
}
