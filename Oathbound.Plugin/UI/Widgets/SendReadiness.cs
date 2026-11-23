using System;
using Dalamud.Bindings.ImGui;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// The pairing an Owner command would go to, and the first reason it can't go there yet (null when it can).
public readonly record struct SendTarget(PairingState? Pairing, string? BlockedReason)
{
    public bool CanSend => BlockedReason is null;
}

/// The one readiness check every Owner send control uses, in the order the reasons are shown.
public static class SendReadiness
{
    public const string NoSubReason = "Not paired with a Sub yet - pick an Owner-side pairing in the header, or pair in Settings.";
    public const string ReconnectingReason = "Reconnecting to the Oathbound relay - sending works again once it's back.";

    /// A short drop shouldn't make every send button flicker.
    private static readonly TimeSpan RelayGrace = TimeSpan.FromSeconds(2);

    private static int cachedFrame = -1;
    private static SendTarget cached;
    private static DateTime? relayDownSinceUtc;
    private static int relayFrame = -1;
    private static bool relayDown;

    /// The active pairing. Cached per frame, since many buttons ask.
    public static SendTarget For(Plugin plugin)
    {
        var frame = ImGui.GetFrameCount();
        if (frame == cachedFrame)
            return cached;
        cachedFrame = frame;
        cached = Check(plugin, plugin.Configuration.ActivePairing);
        return cached;
    }

    /// A specific pairing, e.g. the one a game or rulebook belongs to.
    public static SendTarget For(Plugin plugin, PairingState? pairing) => Check(plugin, pairing);

    private static SendTarget Check(Plugin plugin, PairingState? pairing)
    {
        if (pairing is not { Direction: PairingDirection.OwnerSide })
            return new SendTarget(null, NoSubReason);
        if (!pairing.IsPaired)
            return new SendTarget(pairing, $"Your pairing with {pairing.PeerName ?? "your Sub"} isn't ready to send yet.");
        if (RelayDown(plugin))
            return new SendTarget(pairing, ReconnectingReason);
        if (pairing.PeerRelayState == PeerRelayState.NeedsUpdate)
            return new SendTarget(pairing, $"{pairing.PeerName} needs to update Oathbound before anything you send reaches them.");
        return new SendTarget(pairing, null);
    }

    private static bool RelayDown(Plugin plugin)
    {
        var frame = ImGui.GetFrameCount();
        if (frame == relayFrame)
            return relayDown;
        relayFrame = frame;
        if (plugin.PeerMessenger.Connected)
            relayDownSinceUtc = null;
        else
            relayDownSinceUtc ??= DateTime.UtcNow;
        relayDown = relayDownSinceUtc is { } since && DateTime.UtcNow - since >= RelayGrace;
        return relayDown;
    }
}
