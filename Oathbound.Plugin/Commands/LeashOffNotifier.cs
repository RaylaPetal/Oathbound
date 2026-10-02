using System;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// Why a Sub's leash ended (or a `leash` was refused) - decides whether the Owner gets a leash-off notice and
/// which reason word it carries (collar/leash "Sub tells the Owner when the leash comes off").
public enum LeashEnd
{
    /// The Owner's own `unleash` - their client already knows.
    OwnerUnleash,
    /// The Owner's own revert all - their client already knows.
    OwnerRevertAll,
    /// The leash's pairing ended - there's no Owner left to tell.
    PairingEnded,
    Panic,
    Travel,
    Timeout,
    Refused,
    Other,
}

public static class LeashEndExtensions
{
    /// The notice's reason word. The three Owner-known reasons never reach a notice; they map to `other`.
    public static string ToNoticeWord(this LeashEnd reason) => reason switch
    {
        LeashEnd.Panic => "panic",
        LeashEnd.Travel => "travel",
        LeashEnd.Timeout => "timeout",
        LeashEnd.Refused => "refused",
        _ => "other",
    };

    /// The Owner side's reading of a notice's reason word: missing or unknown is `Other`.
    public static LeashEnd FromNoticeWord(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "panic" => LeashEnd.Panic,
        "travel" => LeashEnd.Travel,
        "timeout" => LeashEnd.Timeout,
        "refused" => LeashEnd.Refused,
        _ => LeashEnd.Other,
    };

    /// Owner-facing wording, completing "{Sub}'s leash came off: ...".
    public static string ToOwnerText(this LeashEnd reason) => reason switch
    {
        LeashEnd.Panic => "they used their safeword.",
        LeashEnd.Travel => "they couldn't follow you.",
        LeashEnd.Timeout => "they waited too long for you to come back.",
        LeashEnd.Refused => "their client couldn't put the leash on.",
        _ => "it came off on their side.",
    };
}

/// collar/leash "Sub tells the Owner when the leash comes off" (design D2-D3): the Sub side's one place that
/// sends the leash-off notice tell, so the movement code never touches chat. Every leash end FollowCommand
/// raises comes through here; the Owner-known reasons and ended pairings send nothing.
public sealed class LeashOffNotifier : IDisposable
{
    private readonly PluginConfig config;
    private readonly FollowCommand follow;
    private readonly ChatComposer composer;
    private readonly ChatSender sender;

    public LeashOffNotifier(PluginConfig config, FollowCommand follow, ChatComposer composer, ChatSender sender)
    {
        this.config = config;
        this.follow = follow;
        this.composer = composer;
        this.sender = sender;
        follow.LeashEnded += OnLeashEnded;
    }

    public void Dispose() => follow.LeashEnded -= OnLeashEnded;

    /// A `leash` from this pairing that the Sub's client refused.
    public void NotifyRefused(PairingState pairing) => Send(pairing, LeashEnd.Refused);

    /// collar/leash-travel "Sub travels to the Owner on leash travel": a `leash travel` from an Owner this Sub
    /// isn't leashed to - their client still thinks it is.
    public void NotifyNotLeashed(PairingState pairing) => Send(pairing, LeashEnd.Other);

    private void OnLeashEnded(Guid pairingId, LeashEnd reason)
    {
        if (reason is LeashEnd.OwnerUnleash or LeashEnd.OwnerRevertAll or LeashEnd.PairingEnded)
            return;
        if (config.FindPairingById(pairingId) is { } pairing)
            Send(pairing, reason);
    }

    private void Send(PairingState pairing, LeashEnd reason)
    {
        if (!pairing.IsPaired || pairing.Direction != PairingDirection.SubSide)
            return;
        if (string.IsNullOrWhiteSpace(pairing.PeerName) || string.IsNullOrWhiteSpace(pairing.PeerWorld))
            return;
        Plugin.Log.Info($"Leash-off notice ({reason.ToNoticeWord()}) sent to the Owner.");
        sender.Send(composer.ComposeLeashOffNotice(pairing.PeerName, pairing.PeerWorld, reason));
    }
}
