using System;
using System.Globalization;
using System.Numerics;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.Commands;

/// Alias-triggered titles plus the Owner's force-apply, which locks out the Sub's aliases until cleared.
public sealed class TitleCommand
{
    private readonly HonorificIpc honorific;
    private readonly SubRuntimeState runtimeState;

    public TitleCommand(HonorificIpc honorific, SubRuntimeState runtimeState)
    {
        this.honorific = honorific;
        this.runtimeState = runtimeState;
    }

    public void Apply(TitleAliasDefinition alias)
    {
        if (runtimeState.TitleForceLocked)
            return;

        honorific.SetTitle(new HonorificTitleData
        {
            Title = alias.Text,
            IsPrefix = alias.IsPrefix,
            Color = alias.Color,
            Glow = alias.Glow,
        });
        runtimeState.TitleApplied = true;
    }

    public void Clear()
    {
        if (runtimeState.TitleForceLocked)
            return;

        honorific.ClearTitle();
        runtimeState.TitleApplied = false;
    }

    /// Plain white suffix; see the styled overload.
    public void ForceApply(string text)
    {
        honorific.SetTitle(new HonorificTitleData { Title = text, IsPrefix = false, Color = new(1, 1, 1) });
        runtimeState.TitleApplied = true;
        runtimeState.TitleForceLocked = true;
        runtimeState.TitleForceText = text;
        runtimeState.TitleForceIsPrefix = false;
        runtimeState.TitleForceColor = new(1, 1, 1);
        runtimeState.TitleForceGlow = null;
    }

    /// `glow` is optional.
    public void ForceApply(string text, bool isPrefix, Vector3 color, Vector3? glow = null)
    {
        honorific.SetTitle(new HonorificTitleData { Title = text, IsPrefix = isPrefix, Color = color, Glow = glow });
        runtimeState.TitleApplied = true;
        runtimeState.TitleForceLocked = true;
        runtimeState.TitleForceText = text;
        runtimeState.TitleForceIsPrefix = isPrefix;
        runtimeState.TitleForceColor = color;
        runtimeState.TitleForceGlow = glow;
    }

    public void ForceClear()
    {
        honorific.ClearTitle();
        runtimeState.TitleApplied = false;
        runtimeState.TitleForceLocked = false;
        runtimeState.TitleForceText = null;
    }

    /// Honorific has no change notification, so the forced style is re-sent on an interval, bypassing Apply.
    public void OnFrameworkUpdate()
    {
        if (!runtimeState.TitleForceLocked || runtimeState.TitleForceText is null)
            return;

        var now = Environment.TickCount64;
        if (now < nextReassertTicks)
            return;

        honorific.SetTitle(new HonorificTitleData
        {
            Title = runtimeState.TitleForceText,
            IsPrefix = runtimeState.TitleForceIsPrefix,
            Color = runtimeState.TitleForceColor,
            Glow = runtimeState.TitleForceGlow,
        });
        nextReassertTicks = now + ReassertIntervalMs;
    }

    private const long ReassertIntervalMs = 10_000;
    private long nextReassertTicks;

    /// A separate `style` verb, since free-form title text has nothing for an old client to fail closed against.
    /// `glow` is omitted when null.
    public static string BuildStyleCommand(string text, bool isPrefix, Vector3 color, Vector3? glow = null)
    {
        var command = $"title style \"{text}\" prefix:{(isPrefix ? 1 : 0)} color:{FormatVector(color)}";
        if (glow is { } g)
            command += $" glow:{FormatVector(g)}";
        return command;
    }

    private static string FormatVector(Vector3 v) =>
        $"{v.X.ToString(CultureInfo.InvariantCulture)},{v.Y.ToString(CultureInfo.InvariantCulture)},{v.Z.ToString(CultureInfo.InvariantCulture)}";

    /// Fails closed when text, prefix or color is missing. A missing glow token means no glow.
    public static bool TryParseStyleCommand(string remainder, out string text, out bool isPrefix, out Vector3 color, out Vector3? glow)
    {
        text = "";
        isPrefix = false;
        color = new Vector3(1, 1, 1);
        glow = null;

        var trimmed = remainder.Trim();
        if (!trimmed.StartsWith('"'))
            return false;

        var closing = trimmed.IndexOf('"', 1);
        if (closing < 0)
            return false;

        text = trimmed[1..closing];
        if (text.Length == 0)
            return false;

        var foundPrefix = false;
        var foundColor = false;
        foreach (var token in trimmed[(closing + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("prefix:", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(token["prefix:".Length..], out var p))
                    return false;
                isPrefix = p != 0;
                foundPrefix = true;
            }
            else if (token.StartsWith("color:", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseVector(token["color:".Length..], out var c))
                    return false;
                color = c;
                foundColor = true;
            }
            else if (token.StartsWith("glow:", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseVector(token["glow:".Length..], out var g))
                    return false;
                glow = g;
            }
        }

        return foundPrefix && foundColor;
    }

    private static bool TryParseVector(string encoded, out Vector3 value)
    {
        value = default;
        var parts = encoded.Split(',');
        if (parts.Length != 3
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return false;
        value = new Vector3(x, y, z);
        return true;
    }
}
