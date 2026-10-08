using System;
using System.Globalization;
using System.Numerics;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.Commands;

/// Alias-triggered titles plus the Owner's title, which is locked: persisted in config, put back whenever something
/// else replaces it, and blocking the Sub's own aliases until it ends.
public sealed class TitleCommand : IDisposable
{
    private readonly PluginConfig config;
    private readonly HonorificIpc honorific;
    private readonly SubRuntimeState runtimeState;

    public TitleCommand(PluginConfig config, HonorificIpc honorific, SubRuntimeState runtimeState)
    {
        this.config = config;
        this.honorific = honorific;
        this.runtimeState = runtimeState;
        honorific.LocalTitleChanged += OnLocalTitleChanged;
        honorific.Ready += RequestReassert;
        if (config.OwnerLocks.Title is { } saved)
        {
            runtimeState.TitleApplied = true;
            runtimeState.TitleText = saved.Text;
        }
    }

    public void Dispose()
    {
        honorific.LocalTitleChanged -= OnLocalTitleChanged;
        honorific.Ready -= RequestReassert;
    }

    public TitleLock? Lock => config.OwnerLocks.Title;

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
        runtimeState.TitleText = alias.Text;
    }

    public void Clear()
    {
        if (runtimeState.TitleForceLocked)
            return;

        honorific.ClearTitle();
        runtimeState.TitleApplied = false;
        runtimeState.TitleText = null;
    }

    /// Plain white suffix; see the styled overload.
    public void ForceApply(string text, DateTime? expiresAtUtc = null, Guid? byPairingId = null) =>
        ForceApply(text, false, new Vector3(1, 1, 1), null, expiresAtUtc, byPairingId);

    /// `glow` is optional. `expiresAtUtc` null locks it until the Owner clears it.
    public void ForceApply(string text, bool isPrefix, Vector3 color, Vector3? glow = null, DateTime? expiresAtUtc = null, Guid? byPairingId = null)
    {
        config.OwnerLocks.Title = new TitleLock
        {
            Text = text, IsPrefix = isPrefix, Color = color, Glow = glow, ByPairingId = byPairingId, ExpiresAtUtc = expiresAtUtc,
        };
        config.Save();
        Set(config.OwnerLocks.Title);
        runtimeState.TitleApplied = true;
        runtimeState.TitleText = text;
    }

    public void ForceClear()
    {
        var hadLock = config.OwnerLocks.Title is not null;
        config.OwnerLocks.Title = null;
        if (hadLock)
            config.Save();
        honorific.ClearTitle();
        runtimeState.TitleApplied = false;
        runtimeState.TitleText = null;
    }

    /// Re-sent at once after a login or Honorific coming back.
    public void RequestReassert() => reassertPending = true;

    /// Honorific may raise this off the framework thread, so it only flags; our own set raises it too, so a title
    /// that already matches is left alone.
    private void OnLocalTitleChanged(HonorificTitleData? shown)
    {
        if (config.OwnerLocks.Title is { } locked && !Matches(shown, locked))
            reassertPending = true;
    }

    /// Honorific's change event is the fast path; the interval covers versions without it and missed events.
    public void OnFrameworkUpdate()
    {
        if (config.OwnerLocks.Title is not { } locked)
            return;

        var now = Environment.TickCount64;
        if (reassertPending && now >= nextSetAllowedTicks)
        {
            reassertPending = false;
            Set(locked);
            nextSetAllowedTicks = now + MinSetIntervalMs;
            nextReassertTicks = now + ReassertIntervalMs;
            return;
        }
        if (now < nextReassertTicks)
            return;
        nextReassertTicks = now + ReassertIntervalMs;
        if (honorific.TryGetLocalTitle(out var shown) && Matches(shown, locked))
            return;
        Set(locked);
        nextSetAllowedTicks = now + MinSetIntervalMs;
    }

    private void Set(TitleLock locked) =>
        honorific.SetTitle(new HonorificTitleData { Title = locked.Text, IsPrefix = locked.IsPrefix, Color = locked.Color, Glow = locked.Glow });

    private static bool Matches(HonorificTitleData? shown, TitleLock locked) =>
        shown is not null && shown.Title == locked.Text && shown.IsPrefix == locked.IsPrefix;

    private const long ReassertIntervalMs = 10_000;
    /// So two plugins both re-setting the title can't flip it every frame.
    private const long MinSetIntervalMs = 1_000;
    private long nextReassertTicks;
    private long nextSetAllowedTicks;
    private volatile bool reassertPending;

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
