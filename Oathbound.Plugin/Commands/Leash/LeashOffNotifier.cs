using System;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// Why a Sub's leash ended, or a `leash` was refused.
public enum LeashEnd
{
    OwnerUnleash,
    OwnerRevertAll,
    PairingEnded,
    Panic,
    // Travel and Timeout now pause the leash; kept only to read notices from older Sub builds.
    Travel,
    Timeout,
    Refused,
    /// The Sub's own accepted presence rule ended it.
    Rule,
    Other,
}

public static class LeashEndExtensions
{
    /// Owner-known reasons never reach a notice.
    public static string ToNoticeWord(this LeashEnd reason) => reason switch
    {
        LeashEnd.Panic => "panic",
        LeashEnd.Travel => "travel",
        LeashEnd.Timeout => "timeout",
        LeashEnd.Refused => "refused",
        LeashEnd.Rule => "rule",
        _ => "other",
    };

    public static LeashEnd FromNoticeWord(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "panic" => LeashEnd.Panic,
        "travel" => LeashEnd.Travel,
        "timeout" => LeashEnd.Timeout,
        "refused" => LeashEnd.Refused,
        "rule" => LeashEnd.Rule,
        _ => LeashEnd.Other,
    };

    /// Completes "{Sub}'s leash came off: ...".
    public static string ToOwnerText(this LeashEnd reason) => reason switch
    {
        LeashEnd.Panic => "they used their safeword.",
        LeashEnd.Travel => "they couldn't follow you.",
        LeashEnd.Timeout => "they waited too long for you to come back.",
        LeashEnd.Refused => "their client couldn't put the leash on.",
        LeashEnd.Rule => "your presence rule took it off when you left.",
        _ => "it came off on their side.",
    };
}

/// Sends the Sub's `collarleash` notices to the Owner: leash off, leash paused, and teleport refused. Owner-initiated
/// ends and ended pairings send nothing.
public sealed class LeashOffNotifier : IDisposable
{
    private readonly PluginConfig config;
    private readonly FollowCommand follow;
    private readonly TeleportCommand teleport;
    private readonly ChatComposer composer;
    private readonly ChatSender sender;

    public LeashOffNotifier(PluginConfig config, FollowCommand follow, TeleportCommand teleport, ChatComposer composer, ChatSender sender)
    {
        this.config = config;
        this.follow = follow;
        this.teleport = teleport;
        this.composer = composer;
        this.sender = sender;
        follow.LeashEnded += OnLeashEnded;
        follow.LeashPaused += OnLeashPaused;
        teleport.OwnerTeleportFailed += OnOwnerTeleportFailed;
    }

    public void Dispose()
    {
        follow.LeashEnded -= OnLeashEnded;
        follow.LeashPaused -= OnLeashPaused;
        teleport.OwnerTeleportFailed -= OnOwnerTeleportFailed;
    }

    public void NotifyRefused(PairingState pairing) => Send(pairing, LeashEnd.Refused);

    /// The sender's client still thinks this Sub is leashed to them.
    public void NotifyNotLeashed(PairingState pairing) => Send(pairing, LeashEnd.Other);

    /// An Owner's Teleport refused before it started.
    public void NotifyTeleportRefused(PairingState pairing, TeleportRefusal code) =>
        SendRefusal(pairing, ChatComposer.TeleportFailedWord, code);

    private void OnLeashEnded(Guid pairingId, LeashEnd reason)
    {
        if (reason is LeashEnd.OwnerUnleash or LeashEnd.OwnerRevertAll or LeashEnd.PairingEnded)
            return;
        if (config.FindPairingById(pairingId) is { } pairing)
            Send(pairing, reason);
    }

    private void OnLeashPaused(Guid pairingId, TeleportRefusal code)
    {
        if (config.FindPairingById(pairingId) is { } pairing)
            SendRefusal(pairing, ChatComposer.LeashPausedWord, code);
    }

    private void OnOwnerTeleportFailed(Guid pairingId, TeleportRefusal code)
    {
        if (config.FindPairingById(pairingId) is { } pairing)
            SendRefusal(pairing, ChatComposer.TeleportFailedWord, code);
    }

    private static bool TryGetOwner(PairingState pairing, out string name, out string world)
    {
        name = pairing.PeerName ?? "";
        world = pairing.PeerWorld ?? "";
        return pairing is { IsPaired: true, Direction: PairingDirection.SubSide }
            && !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(world);
    }

    private void Send(PairingState pairing, LeashEnd reason)
    {
        if (!TryGetOwner(pairing, out var name, out var world))
            return;
        Plugin.Log.Info($"Leash-off notice ({reason.ToNoticeWord()}) sent to the Owner.");
        sender.Send(composer.ComposeLeashOffNotice(name, world, reason));
    }

    private void SendRefusal(PairingState pairing, string word, TeleportRefusal code)
    {
        if (!TryGetOwner(pairing, out var name, out var world))
            return;
        Plugin.Log.Info($"Leash notice ({word} {code.ToNoticeWord()}) sent to the Owner.");
        sender.Send(composer.ComposeTeleportRefusalNotice(name, world, word, code));
    }
}
