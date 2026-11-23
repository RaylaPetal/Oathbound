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
            Status.Line(StatusKind.Info, $"Vibrating at {s.IntensityPercent}% - {s.Description}{StepText(s)}");
            IconGlyph.WrappedDisabled($"Started by {s.Source} - running for {TimeText.Duration(Ms(s.ElapsedMs))}, auto-stops {TimeText.Until(Ms(s.RemainingMs))}");
        }
        else
            IconGlyph.WrappedDisabled(connected ? "Idle - nothing is playing." : "Idle - no toy connected.");
    }

    public static void DrawOwner(OwnerToyEstimate? estimate, CommandMark? mark = null, string who = "your Sub")
    {
        if (estimate is null)
            IconGlyph.WrappedDisabled("Nothing sent to this Sub yet this session.");
        else if (estimate.IsStop)
            IconGlyph.WrappedDisabled($"Last sent: stop, {TimeText.Ago(Ms(Environment.TickCount64 - estimate.SentTicks))}.");
        else if (estimate.LikelyRunning)
            Status.Line(StatusKind.Info, $"Likely running: {OwnerSummary(estimate)} - sent {TimeText.Ago(Ms(Environment.TickCount64 - estimate.SentTicks))}, {OwnerEnd(estimate)}");
        else
            IconGlyph.WrappedDisabled($"Likely finished: {OwnerSummary(estimate)} - sent {TimeText.Ago(Ms(Environment.TickCount64 - estimate.SentTicks))}.");
        Status.Line(CommandMarkText.Describe(mark, who, "toy"));
        IconGlyph.HelpMarker(mark is null or { State: CommandConfirmation.Legacy }
            ? "Based on what you sent - your client can't see your Sub's device."
            : "Based on what you sent that your Sub's client didn't refuse - your client can't see your Sub's device.");
    }

    /// Null hides the entry. The Sub's actual state wins when a client is both.
    public static (string Text, string Tooltip)? Dtr(ToyControlCommand toy, OwnerToyEstimate? estimate)
    {
        if (toy.CurrentStatus is { } s)
            return ($"Toy {s.IntensityPercent}%", $"{s.Description}{StepText(s)}\nStarted by {s.Source}\nAuto-stops {TimeText.Until(Ms(s.RemainingMs))}");
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
        var left = Ms(end - Environment.TickCount64);
        return e.EndIsExact ? $"ends {TimeText.Until(left)}" : $"stops within {TimeText.Duration(left)}";
    }

    private static TimeSpan Ms(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms));
}
