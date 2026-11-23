using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// Owner-side estimate built only from the toy commands this client sent that the Sub's client didn't refuse, keyed
/// by pairing. The Sub's device state never comes back, so the UI says it's an estimate.
public sealed class OwnerToyStatusTracker : IDisposable
{
    private static readonly Regex ToyCommand = new(@"(?:^|\s)toy\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly PluginConfig config;
    private readonly OwnerReceiptTracker receipts;
    private readonly Dictionary<Guid, OwnerToyEstimate> estimates = new();
    /// The command whose estimate is shown and what was shown before it, so a late refusal can put that back.
    private readonly Dictionary<Guid, (long Seq, OwnerToyEstimate? Previous)> lastApplied = new();
    private readonly Dictionary<Guid, CommandMark> marks = new();

    public OwnerToyStatusTracker(PluginConfig config, OwnerReceiptTracker receipts)
    {
        this.config = config;
        this.receipts = receipts;
        receipts.CommandOutcome += OnOutcome;
    }

    /// Null if none was sent this session.
    public OwnerToyEstimate? ForActivePairing =>
        config.GetActivePairing() is { Direction: PairingDirection.OwnerSide } pairing && estimates.TryGetValue(pairing.Id, out var estimate) ? estimate : null;

    /// The latest toy command's confirmation for the active pairing.
    public CommandMark? MarkForActivePairing =>
        config.GetActivePairing() is { Direction: PairingDirection.OwnerSide } pairing ? marks.GetValueOrDefault(pairing.Id) : null;

    private void OnOutcome(CommandOutcome outcome)
    {
        if (config.FindPairingById(outcome.PairingId) is not { Direction: PairingDirection.OwnerSide } pairing) return;
        var match = ToyCommand.Match(outcome.Text.Trim());
        if (!match.Success) return;
        var id = pairing.Id;

        switch (outcome.Kind)
        {
            case CommandOutcomeKind.Waiting:
                SetMark(id, new CommandMark(outcome.Seq, CommandConfirmation.Waiting));
                break;
            case CommandOutcomeKind.Applied or CommandOutcomeKind.NotConfirmed or CommandOutcomeKind.Legacy when !outcome.Late:
                if (Parse(match.Groups[1].Value.Trim()) is { } estimate)
                {
                    lastApplied[id] = (outcome.Seq, estimates.GetValueOrDefault(id));
                    estimates[id] = estimate;
                }
                SetMark(id, new CommandMark(outcome.Seq, outcome.Kind switch
                {
                    CommandOutcomeKind.Applied => CommandConfirmation.Confirmed,
                    CommandOutcomeKind.NotConfirmed => CommandConfirmation.NotConfirmed,
                    _ => CommandConfirmation.Legacy,
                }));
                break;
            case CommandOutcomeKind.Applied:
                if (marks.GetValueOrDefault(id) is { } current && current.Seq == outcome.Seq)
                    marks[id] = current with { State = CommandConfirmation.Confirmed };
                break;
            case CommandOutcomeKind.Refused:
                if (outcome.Late && lastApplied.GetValueOrDefault(id) is { } applied && applied.Seq == outcome.Seq)
                {
                    if (applied.Previous is { } previous)
                        estimates[id] = previous;
                    else
                        estimates.Remove(id);
                    lastApplied.Remove(id);
                }
                SetMark(id, new CommandMark(outcome.Seq, CommandConfirmation.Refused, outcome.Code));
                break;
        }
    }

    private void SetMark(Guid pairingId, CommandMark mark)
    {
        if (marks.GetValueOrDefault(pairingId) is not { } current || current.Seq <= mark.Seq)
            marks[pairingId] = mark;
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
            // A non-looping sequence with every step timed ends on its own; anything else runs to the ceiling.
            var natural = !loop && steps.All(s => s.DurationMs > 0);
            var endMs = natural ? Math.Min(steps.Sum(s => (long)s.DurationMs), defaultCeilingMs) : defaultCeilingMs;
            return new OwnerToyEstimate($"Custom sequence ({steps.Count} steps)", steps.Max(s => s.IntensityPercent), now, now + endMs, natural);
        }

        return null;
    }

    public void Dispose() => receipts.CommandOutcome -= OnOutcome;
}

/// EndTicks null = permanent. EndIsExact false = an upper bound (the Sub's default may be shorter).
/// IntensityPercent is null when unknown (a Sub-side pattern by name).
public sealed record OwnerToyEstimate(string Description, int? IntensityPercent, long SentTicks, long? EndTicks, bool EndIsExact)
{
    public bool IsStop => Description == "Stop";

    public bool LikelyRunning => !IsStop && (EndTicks is null || Environment.TickCount64 < EndTicks);
}
