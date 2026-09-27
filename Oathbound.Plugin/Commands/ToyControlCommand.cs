using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.Commands;

/// collar/toy-control: Owner-initiated (or, per collar/toy-control's local-trigger requirements, Sub-local-
/// trigger-initiated) discrete vibrate/pattern/stop commands against every device the Sub currently has
/// connected via Intiface (IntifaceIpc) - no per-device targeting. Every command runs as a discrete,
/// locally-executed step sequence (see `PatternStep`) advanced from `OnFrameworkUpdate` (the same per-frame
/// polling shape `RestraintCommand.OnFrameworkUpdate` already uses for delayed bound-animation triggers)
/// rather than any wire round-trip - a plain vibrate is a degenerate one-step sequence, `weak`/`medium`/
/// `strong`/`intense` are single-step presets, `pulse` is a two-step loop, and a Sub-authored custom pattern (see
/// `PluginConfig.ToyPatterns`) is the same shape with more steps. Every command is capped to either the
/// Sub's own configured `PluginConfig.DefaultMaxDurationSeconds` (itself never above the fixed compiled
/// `MaxDurationSeconds`), an explicit bounded duration clamped straight to `MaxDurationSeconds`, or (only
/// when explicitly requested) the much longer `PluginConfig.PermanentBackstopSeconds` - the ceiling is
/// enforced entirely by the Sub's own client and cannot be bypassed by what the Owner (or a local trigger)
/// requested.
public sealed class ToyControlCommand
{
    public const int MaxDurationSeconds = 120;

    private readonly IntifaceIpc intiface;
    private readonly SubRuntimeState runtimeState;
    private readonly PluginConfig config;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<PatternStep>> BuiltInPatterns = new Dictionary<string, IReadOnlyList<PatternStep>>(StringComparer.OrdinalIgnoreCase)
    {
        ["weak"] = new[] { new PatternStep { IntensityPercent = 15, DurationMs = 0 } },
        ["medium"] = new[] { new PatternStep { IntensityPercent = 35, DurationMs = 0 } },
        ["strong"] = new[] { new PatternStep { IntensityPercent = 60, DurationMs = 0 } },
        ["intense"] = new[] { new PatternStep { IntensityPercent = 90, DurationMs = 0 } },
        ["pulse"] = new[]
        {
            new PatternStep { IntensityPercent = 80, DurationMs = 500 },
            new PatternStep { IntensityPercent = 0, DurationMs = 500 },
        },
    };

    private static readonly IReadOnlyDictionary<string, bool> BuiltInLoop = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
    {
        ["weak"] = false, ["medium"] = false, ["strong"] = false, ["intense"] = false, ["pulse"] = true,
    };

    /// The reserved built-in pattern names, in ascending-strength order (pulse last) - what every UI lists
    /// and what a custom pattern may not be named.
    public static readonly IReadOnlyList<string> BuiltInPatternNames = ["weak", "medium", "strong", "intense", "pulse"];

    public static bool IsBuiltInPattern(string name) => BuiltInPatterns.ContainsKey(name);

    /// A built-in pattern's peak intensity, or null for a name that isn't built-in.
    public static int? BuiltInIntensity(string name) =>
        BuiltInPatterns.TryGetValue(name, out var s) ? s.Max(x => x.IntensityPercent) : null;

    private bool active;
    private IReadOnlyList<PatternStep> steps = Array.Empty<PatternStep>();
    private bool loop;
    private int stepIndex;
    private long stepEndTicks;
    private long stopAtTicks;
    private long startedTicks;
    private string activeDescription = "";
    private string activeSource = "";

    /// What the device is doing right now, for the Sub's own live status display - null while idle.
    public ToyStatus? CurrentStatus => active
        ? new ToyStatus(activeDescription, activeSource, steps[stepIndex].IntensityPercent, stepIndex, steps.Count, loop,
            Environment.TickCount64 - startedTicks, Math.Max(0, stopAtTicks - Environment.TickCount64))
        : null;

    public ToyControlCommand(IntifaceIpc intiface, SubRuntimeState runtimeState, PluginConfig config)
    {
        this.intiface = intiface;
        this.runtimeState = runtimeState;
        this.config = config;
    }

    /// collar/toy-control "Owner-initiated vibration command"/"Locally enforced maximum duration": a plain
    /// vibrate is a single-step, non-looping sequence at a fixed intensity with no natural end of its own -
    /// it runs until the overall stop-at ceiling (per `duration`), an explicit stop, or panic.
    /// `source` is only a label for the live status display ("Owner command", "Trigger: ...").
    public bool ForceApplyVibrate(int intensityPercent, ToyDuration duration, string source = OwnerSource)
    {
        if (!intiface.IsConnected) return false;

        var intensity = Math.Clamp(intensityPercent, 0, 100);
        var step = new PatternStep { IntensityPercent = intensity, DurationMs = 0 };
        var description = duration.Type == ToyDuration.Kind.Permanent ? $"Vibrate {intensity}% (permanent)" : $"Vibrate {intensity}%";
        StartSequence(new[] { step }, loop: false, EffectiveCeilingSeconds(duration), description, source);
        return true;
    }

    public const string OwnerSource = "Owner command";

    /// collar/toy-control "Named pattern commands": checks the fixed built-in set first (weak/medium/strong/
    /// pulse - these names are reserved and cannot be shadowed by a custom pattern), then the Sub's own
    /// `PluginConfig.ToyPatterns` by name. Returns false for a name recognized in neither set, or if no toy
    /// is connected, without changing any state - the "fail closed" behavior a wire command and a local
    /// trigger both rely on.
    public bool ForceApplyPattern(string patternName, string source = OwnerSource)
    {
        if (!intiface.IsConnected) return false;

        if (BuiltInPatterns.TryGetValue(patternName, out var builtInSteps))
        {
            StartSequence(builtInSteps, BuiltInLoop[patternName], EffectiveDefaultCeilingSeconds(), $"Pattern \"{patternName.ToLowerInvariant()}\"", source);
            return true;
        }

        var custom = config.ToyPatterns.FirstOrDefault(p => string.Equals(p.Name, patternName, StringComparison.OrdinalIgnoreCase));
        if (custom is null || custom.Steps.Count == 0)
            return false;

        StartSequence(custom.Steps, custom.Loop, EffectiveDefaultCeilingSeconds(), $"Pattern \"{custom.Name}\"", source);
        return true;
    }

    /// collar/toy-control: an Owner-authored pattern isn't necessarily saved on the Sub's own client at
    /// all, so it can't be invoked by name the way a built-in or Sub-authored pattern is - the whole step
    /// sequence travels inline in the wire command itself (see `TryParseCustomSequenceCommand`/
    /// `BuildCustomSequenceCommand`) and plays immediately, one-shot, the same as any other pattern. Every
    /// step's intensity/duration is clamped defensively here too, even though the parser already clamps -
    /// this method has no way to know a caller went through that parser.
    public bool ForceApplyCustomSequence(IReadOnlyList<PatternStep> sequence, bool loop, string source = OwnerSource)
    {
        if (!intiface.IsConnected || sequence.Count == 0) return false;

        var clamped = sequence.Select(s => new PatternStep { IntensityPercent = Math.Clamp(s.IntensityPercent, 0, 100), DurationMs = Math.Max(0, s.DurationMs) }).ToList();
        StartSequence(clamped, loop, EffectiveDefaultCeilingSeconds(), $"Custom sequence ({clamped.Count} steps)", source);
        return true;
    }

    private void StartSequence(IReadOnlyList<PatternStep> sequence, bool loop, int ceilingSeconds, string description, string source)
    {
        var now = Environment.TickCount64;
        active = true;
        steps = sequence;
        this.loop = loop;
        stepIndex = 0;
        startedTicks = now;
        activeDescription = description;
        activeSource = source;
        stopAtTicks = now + ceilingSeconds * 1000L;
        stepEndTicks = steps[0].DurationMs > 0 ? now + steps[0].DurationMs : long.MaxValue;
        intiface.VibrateAll(steps[0].IntensityPercent / 100.0);
        runtimeState.ToyControlForceLocked = true;
    }

    /// collar/toy-control "Locally enforced maximum duration": `Unspecified` uses the Sub's own configured
    /// default ceiling (`EffectiveDefaultCeilingSeconds()`) exactly as an untimed command always has;
    /// `Bounded` clamps into `[0, MaxDurationSeconds]` against the fixed compiled ceiling directly,
    /// independent of the Sub's own default setting, exactly as a timed command always has; `Permanent` is
    /// the one case that uses neither - it is never truly unbounded either, using the separate, much longer
    /// `PermanentBackstopSeconds` ceiling instead, so a hard local stop always exists regardless of which
    /// mode was requested.
    private int EffectiveCeilingSeconds(ToyDuration duration) => duration.Type switch
    {
        ToyDuration.Kind.Bounded => Math.Clamp(duration.Seconds, 0, MaxDurationSeconds),
        ToyDuration.Kind.Permanent => Math.Max(0, config.PermanentBackstopSeconds),
        _ => EffectiveDefaultCeilingSeconds(),
    };

    /// collar/toy-control: the Sub's own `DefaultMaxDurationSeconds`, clamped so it can shorten the default
    /// ceiling below `MaxDurationSeconds` but never raise it past the fixed compiled cap.
    private int EffectiveDefaultCeilingSeconds() => Math.Clamp(config.DefaultMaxDurationSeconds, 1, MaxDurationSeconds);

    /// collar/toy-control "Explicit stop command".
    public bool ForceStop()
    {
        active = false;
        intiface.StopAll();
        runtimeState.ToyControlForceLocked = false;
        return true;
    }

    /// collar/toy-control "Panic immediately stops every device": unconditional, independent of whether
    /// anything is currently tracked as active - mirrors RestraintCommand.ReleaseAllBoundAnimationsForPanic's
    /// "drop bookkeeping unconditionally" shape.
    public void ReleaseAllForPanic()
    {
        active = false;
        intiface.StopAll();
        runtimeState.ToyControlForceLocked = false;
    }

    /// collar/toy-control wire grammar: `toy vibrate intensity:<0-100> [duration:<seconds>|duration:permanent]`.
    /// Additive, space-separated `key:value` tokens after the `vibrate` keyword - order-independent, matching
    /// the tolerant style of every other wire parser in this plugin. `intensity:` is mandatory; a remainder
    /// missing it fails to parse rather than defaulting to some intensity. `duration:permanent` (case-
    /// insensitive) parses to `ToyDuration.Permanent`; a numeric `duration:<n>` parses to `ToyDuration.
    /// Bounded(n)`; no duration token at all leaves `ToyDuration.Unspecified`.
    public static bool TryParseVibrateCommand(string remainder, out int intensityPercent, out ToyDuration duration)
    {
        intensityPercent = 0;
        duration = ToyDuration.Unspecified;
        var foundIntensity = false;
        foreach (var token in remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("intensity:", StringComparison.OrdinalIgnoreCase) && int.TryParse(token["intensity:".Length..], out var intensity))
            {
                intensityPercent = intensity;
                foundIntensity = true;
            }
            else if (token.StartsWith("duration:", StringComparison.OrdinalIgnoreCase))
            {
                var value = token["duration:".Length..];
                if (value.Equals("permanent", StringComparison.OrdinalIgnoreCase))
                    duration = ToyDuration.Permanent;
                else if (int.TryParse(value, out var seconds))
                    duration = ToyDuration.Bounded(seconds);
            }
        }
        return foundIntensity;
    }

    public static string BuildVibrateCommand(int intensityPercent, ToyDuration duration) => duration.Type switch
    {
        ToyDuration.Kind.Bounded => $"toy vibrate intensity:{intensityPercent} duration:{duration.Seconds}",
        ToyDuration.Kind.Permanent => $"toy vibrate intensity:{intensityPercent} duration:permanent",
        _ => $"toy vibrate intensity:{intensityPercent}",
    };

    public static string BuildPatternCommand(string patternName) => $"toy pattern:{patternName}";

    public static string BuildStopCommand() => "toy stop";

    /// collar/toy-control wire grammar: `toy sequence steps:<intensity>=<ms>,<intensity>=<ms>,... [loop:true]`.
    /// Lets an Owner author and send a whole pattern that the Sub's client has never saved anywhere - the
    /// full step list travels inline in this one command, matching `RestraintCommand.BuildLockCommand`'s
    /// established comma-joined-tokens shape for embedding a small structured list in a single wire
    /// command. `steps:` is mandatory and needs at least one valid pair; `loop:` defaults to false when
    /// absent. A 400-character length guard (matching `RestraintCommand.TryParseCatalogCommand`'s own
    /// defensive cap) rejects a pathologically oversized command outright rather than trying to partially
    /// parse it.
    public static bool TryParseCustomSequenceCommand(string remainder, out List<PatternStep> steps, out bool loop)
    {
        steps = new List<PatternStep>();
        loop = false;
        if (remainder.Length > 400) return false;

        foreach (var token in remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("steps:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var pair in token["steps:".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = pair.Split('=', 2);
                    if (parts.Length == 2 && int.TryParse(parts[0], out var intensity) && int.TryParse(parts[1], out var durationMs))
                        steps.Add(new PatternStep { IntensityPercent = Math.Clamp(intensity, 0, 100), DurationMs = Math.Max(0, durationMs) });
                }
            }
            else if (token.StartsWith("loop:", StringComparison.OrdinalIgnoreCase))
            {
                loop = token["loop:".Length..].Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }
        return steps.Count > 0;
    }

    public static string BuildCustomSequenceCommand(IReadOnlyList<PatternStep> steps, bool loop)
    {
        var stepsText = string.Join(',', steps.Select(s => $"{s.IntensityPercent}={s.DurationMs}"));
        return loop ? $"toy sequence steps:{stepsText} loop:true" : $"toy sequence steps:{stepsText}";
    }

    public void OnFrameworkUpdate()
    {
        if (!active) return;

        var now = Environment.TickCount64;
        if (now >= stopAtTicks)
        {
            ForceStop();
            return;
        }

        if (now >= stepEndTicks)
        {
            stepIndex++;
            if (stepIndex >= steps.Count)
            {
                if (!loop) { ForceStop(); return; }
                stepIndex = 0;
            }
            var step = steps[stepIndex];
            stepEndTicks = step.DurationMs > 0 ? now + step.DurationMs : long.MaxValue;
            intiface.VibrateAll(step.IntensityPercent / 100.0);
        }
    }
}

/// A snapshot of what the device is doing right now: what's playing, what started it, the current step's
/// intensity, and how long it has run / has left before the local ceiling stops it.
public readonly record struct ToyStatus(string Description, string Source, int IntensityPercent, int StepIndex, int StepCount, bool Loop, long ElapsedMs, long RemainingMs);

/// collar/toy-control "Locally enforced maximum duration": a small tri-state in place of a plain `int?`,
/// so "no duration given" (Unspecified - default ceiling), "an explicit bounded duration" (Bounded - clamped
/// to the default ceiling), and "explicit permanent mode" (Permanent - the separate, longer backstop
/// ceiling) can never be confused with each other, unlike a magic sentinel value would risk.
public readonly struct ToyDuration
{
    public enum Kind { Unspecified, Bounded, Permanent }

    public Kind Type { get; }
    public int Seconds { get; }

    private ToyDuration(Kind type, int seconds)
    {
        Type = type;
        Seconds = seconds;
    }

    public static readonly ToyDuration Unspecified = new(Kind.Unspecified, 0);
    public static readonly ToyDuration Permanent = new(Kind.Permanent, 0);
    public static ToyDuration Bounded(int seconds) => new(Kind.Bounded, seconds);
}
