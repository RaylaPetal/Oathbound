using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// One Owner send control. `Id` is stable per item, so later per-control results can be keyed by it.
/// With `Command`, its length is checked, it's shown on hover and a click sends it. With `Custom`, a click runs that
/// instead (Teleport, cage, rulebook actions...) and a non-null return is the error to report.
/// `Pairing` null means the active pairing.
public sealed record SendSpec(string Id, string Label)
{
    public string? Command { get; init; }
    public string? Help { get; init; }
    public string? BlockedReason { get; init; }
    public Func<string?>? Custom { get; init; }
    public PairingState? Pairing { get; init; }
    public bool AnchorQuickSend { get; init; }
    public bool Small { get; init; }
}

public readonly record struct SendGateResult(bool Enabled, string Tooltip);

/// For surfaces that draw their own control (menu items, Sub Control tiles, two-click buttons).
public static class SendGate
{
    public const string TooLongReason = "Command is too long to send.";

    public static SendGateResult Evaluate(Plugin plugin, SendSpec spec)
    {
        var target = spec.Pairing is null ? SendReadiness.For(plugin) : SendReadiness.For(plugin, spec.Pairing);
        var reason = target.BlockedReason
            ?? (spec.Command is { } command && !CommandSelector.Fits(command) ? TooLongReason : null)
            ?? spec.BlockedReason;
        var help = spec.Help is null ? "" : spec.Help + "\n";
        if (reason is not null)
            return new SendGateResult(false, help + reason);

        var tooltip = help + (spec.Command ?? "");
        if (target.Pairing is { PeerRelayState: PeerRelayState.Offline } offline)
            tooltip += $"\n{offline.PeerName} seems to be offline - this waits briefly for them to reconnect.";
        return new SendGateResult(true, tooltip.TrimEnd('\n'));
    }

    /// Runs the spec's send, for a control the caller drew and found clicked while enabled.
    public static void Run(Plugin plugin, SendSpec spec)
    {
        if (spec.Custom is { } custom)
        {
            if (custom() is { } error)
                SendNotice.Failed(error, error);
            return;
        }
        if (spec.Command is not { } command)
            return;
        if (spec.Pairing is { } pairing)
            plugin.SendCommand(pairing, command);
        else
            plugin.SendCommand(command);
    }

    public static void Tooltip(SendGateResult gate)
    {
        if (gate.Tooltip.Length > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(gate.Tooltip);
    }
}

public static class SendButton
{
    /// True when it was pressed and the send ran.
    public static bool Draw(Plugin plugin, SendSpec spec)
    {
        var gate = SendGate.Evaluate(plugin, spec);
        bool pressed;
        using (ImRaii.Disabled(!gate.Enabled))
        {
            var label = $"{spec.Label}##send_{spec.Id}";
            pressed = spec.Small ? ImGui.SmallButton(label) : ImGui.Button(label);
        }
        if (spec.AnchorQuickSend)
            TutorialService.Anchor(TutorialAnchors.QuickSend);
        SendGate.Tooltip(gate);
        if (!pressed || !gate.Enabled)
            return false;
        SendGate.Run(plugin, spec);
        return true;
    }
}

/// A send that couldn't leave this client, reported the same way from every window.
public static class SendNotice
{
    public const string TeleportFallback = "Teleport failed.";
    public const string CageFallback = "The cage command couldn't be sent.";

    public static void Failed(string? error, string fallback) =>
        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = "Oathbound",
            Content = error ?? fallback,
            Type = NotificationType.Warning,
            InitialDuration = TimeSpan.FromSeconds(5),
        });

    /// Null when it went out, else the error to report.
    public static string? ErrorOf((bool Success, string? Error) result, string fallback) =>
        result.Success ? null : result.Error ?? fallback;

    public static Func<string?> Teleport(Plugin plugin) =>
        () => ErrorOf(TeleportSendAction.TryResolveAndSend(plugin), TeleportFallback);

    public static Func<string?> Cage(Func<(bool Success, string? Error)> send) =>
        () => ErrorOf(send(), CageFallback);
}
