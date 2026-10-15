using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.Commands;

/// Discrete vibrate/pattern/stop commands against every connected Intiface device. Each command runs as a local step
/// sequence advanced per frame. Every run is capped by a ceiling the Sub's client enforces, whatever was requested.
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

    /// Ascending strength (pulse last). Custom patterns can't use these names.
    public static readonly IReadOnlyList<string> BuiltInPatternNames = ["weak", "medium", "strong", "intense", "pulse"];

    public static bool IsBuiltInPattern(string name) => BuiltInPatterns.ContainsKey(name);

    /// Null for a name that isn't built-in.
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

    /// Null while idle.
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

    /// Runs until its ceiling, an explicit stop, or panic. `source` only labels the status display.
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

    /// Built-in names first (they can't be shadowed), then the Sub's own patterns. Fails closed without changing state.
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

    /// The Owner's pattern travels inline since the Sub may not have it saved. Clamped again here regardless of the parser.
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

    /// Unspecified: the Sub's default ceiling. Bounded: clamped to MaxDurationSeconds. Permanent: the longer backstop.
    private int EffectiveCeilingSeconds(ToyDuration duration) => duration.Type switch
    {
        ToyDuration.Kind.Bounded => Math.Clamp(duration.Seconds, 0, MaxDurationSeconds),
        ToyDuration.Kind.Permanent => Math.Max(0, config.PermanentBackstopSeconds),
        _ => EffectiveDefaultCeilingSeconds(),
    };

    /// The Sub can lower the default ceiling but never raise it past MaxDurationSeconds.
    private int EffectiveDefaultCeilingSeconds() => Math.Clamp(config.DefaultMaxDurationSeconds, 1, MaxDurationSeconds);

    public bool ForceStop()
    {
        active = false;
        intiface.StopAll();
        runtimeState.ToyControlForceLocked = false;
        return true;
    }

    /// Unconditional, whether or not anything is tracked as active.
    public void ReleaseAllForPanic()
    {
        active = false;
        intiface.StopAll();
        runtimeState.ToyControlForceLocked = false;
    }

    /// `toy vibrate intensity:<0-100> [duration:<seconds>|duration:permanent]`. Order-independent; intensity is mandatory.
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

    /// `toy sequence steps:<intensity>=<ms>,... [loop:true]`. Capped at 400 characters.
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

public readonly record struct ToyStatus(string Description, string Source, int IntensityPercent, int StepIndex, int StepCount, bool Loop, long ElapsedMs, long RemainingMs);

/// Tri-state so "none given", "bounded" and "permanent" can't be confused the way a sentinel int could.
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
