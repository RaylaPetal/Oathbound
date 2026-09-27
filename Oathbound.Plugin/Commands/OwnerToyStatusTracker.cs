using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// Owner-side toy status: an estimate built only from the toy commands this client itself sent. The Sub's
/// device state never travels back over the wire, so this can't see the Sub's own automatic triggers, a
/// Sub-side stop, panic, or the Sub's own shorter default max duration - the UI says so. Keyed by the
/// pairing the command went to, so switching the active pairing shows that Sub's own last command.
public sealed class OwnerToyStatusTracker : IDisposable
{
    private static readonly Regex ToyCommand = new(@"(?:^|\s)toy\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly PluginConfig config;
    private readonly ChatSender sender;
    private readonly Dictionary<Guid, OwnerToyEstimate> estimates = new();

    public OwnerToyStatusTracker(PluginConfig config, ChatSender sender)
    {
        this.config = config;
        this.sender = sender;
        sender.Sent += OnSent;
    }

    /// The last toy command sent to the active pairing, or null if none was sent this session.
    public OwnerToyEstimate? ForActivePairing =>
        config.GetActivePairing() is { Direction: PairingDirection.OwnerSide } pairing && estimates.TryGetValue(pairing.Id, out var estimate) ? estimate : null;

    private void OnSent(string text)
    {
        if (config.GetActivePairing() is not { Direction: PairingDirection.OwnerSide } pairing) return;
        var match = ToyCommand.Match(text.Trim());
        if (!match.Success) return;

        if (Parse(match.Groups[1].Value.Trim()) is { } estimate)
            estimates[pairing.Id] = estimate;
    }

    private static OwnerToyEstimate? Parse(string rest)
    {
        var now = Environment.TickCount64;
        const long defaultCeilingMs = ToyControlCommand.MaxDurationSeconds * 1000L;

        if (rest.Equals("stop", StringComparison.OrdinalIgnoreCase))
            return new OwnerToyEstimate("Stop", null, now, now, true);

        if (rest.StartsWith("pattern:", StringComparison.OrdinalIgnoreCase))
        {
            var name = rest["pattern:".Length..].Trim();
            int? intensity = ToyControlCommand.BuiltInIntensity(name);
            return new OwnerToyEstimate($"Pattern \"{name}\"", intensity, now, now + defaultCeilingMs, false);
        }

        if (rest.StartsWith("vibrate ", StringComparison.OrdinalIgnoreCase) &&
            ToyControlCommand.TryParseVibrateCommand(rest["vibrate ".Length..], out var percent, out var duration))
        {
            var clamped = Math.Clamp(percent, 0, 100);
            return duration.Type switch
            {
                ToyDuration.Kind.Bounded => new OwnerToyEstimate($"Vibrate {clamped}%", clamped, now, now + Math.Clamp(duration.Seconds, 0, ToyControlCommand.MaxDurationSeconds) * 1000L, true),
                ToyDuration.Kind.Permanent => new OwnerToyEstimate($"Vibrate {clamped}% (permanent)", clamped, now, null, false),
                _ => new OwnerToyEstimate($"Vibrate {clamped}%", clamped, now, now + defaultCeilingMs, false),
            };
        }

        if (rest.StartsWith("sequence ", StringComparison.OrdinalIgnoreCase) &&
            ToyControlCommand.TryParseCustomSequenceCommand(rest["sequence ".Length..], out var steps, out var loop))
        {
            // A non-looping sequence whose every step is timed ends on its own; anything else runs to the ceiling.
            var natural = !loop && steps.All(s => s.DurationMs > 0);
            var endMs = natural ? Math.Min(steps.Sum(s => (long)s.DurationMs), defaultCeilingMs) : defaultCeilingMs;
            return new OwnerToyEstimate($"Custom sequence ({steps.Count} steps)", steps.Max(s => s.IntensityPercent), now, now + endMs, natural);
        }

        return null;
    }

    public void Dispose() => sender.Sent -= OnSent;
}

/// What the Owner last sent: `EndTicks` null means permanent (runs until stopped or the Sub's backstop);
/// `EndIsExact` false means the end is only an upper bound (the Sub's own default max may be shorter).
/// `IntensityPercent` is the peak intensity when known (null for a Sub-side custom pattern by name).
public sealed record OwnerToyEstimate(string Description, int? IntensityPercent, long SentTicks, long? EndTicks, bool EndIsExact)
{
    public bool IsStop => Description == "Stop";

    public bool LikelyRunning => !IsStop && (EndTicks is null || Environment.TickCount64 < EndTicks);
}
