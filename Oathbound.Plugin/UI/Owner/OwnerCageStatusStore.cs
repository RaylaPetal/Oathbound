using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.UI;

/// Owner side: the cage state each Sub's client last reported to the relay, plus the cage this Owner last sent, which
/// decides when the pair status check runs faster. In memory only; the next pair status check refills it.
public sealed class OwnerCageStatusStore
{
    public sealed record Status(string State, DateTimeOffset StateAt, DateTimeOffset? EndsAt);
    /// `Post` is the unsaved hitching post this was, or the one an open opens; posts have no saved id. `Seq` is the
    /// command frame's, which its apply receipt names.
    public sealed record Sent(Guid? CageId, string Name, DateTimeOffset At, bool Open, SavedCage? Post = null, long Seq = 0);
    /// The Sub's client refused the cage command this Owner last sent; shown until the next send.
    public sealed record Refusal(string Name, bool Open, bool Post, RefusalCode Code, DateTimeOffset At);

    private static readonly TimeSpan FastInterval = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RefreshThrottle = TimeSpan.FromMinutes(1);

    private readonly PluginConfig config;
    private readonly Dictionary<Guid, Status> statuses = new();
    private readonly Dictionary<Guid, Sent> sent = new();
    /// What `sent` held before the latest send, so a refused send can be taken back.
    private readonly Dictionary<Guid, Sent?> beforeSent = new();
    private readonly Dictionary<Guid, Refusal> refusals = new();
    private DateTime lastCheckUtc = DateTime.MinValue;
    private bool refreshRequested;

    public OwnerCageStatusStore(PluginConfig config) => this.config = config;

    /// Call on the framework thread.
    public void Update(PairingState pairing, PairEnvelope pair)
    {
        if (pairing.Direction != PairingDirection.OwnerSide)
            return;
        if (pair.CageState is not { } state || pair.CageStateAt is not { } stateAt)
        {
            statuses.Remove(pairing.Id);
            return;
        }
        var status = new Status(state, DateTimeOffset.FromUnixTimeSeconds(stateAt), pair.CageEndsAt is { } end ? DateTimeOffset.FromUnixTimeSeconds(end) : null);
        statuses.TryGetValue(pairing.Id, out var previous);
        statuses[pairing.Id] = status;
        if (previous is not null && previous.State != CageStatusReporter.Caged && state == CageStatusReporter.Caged)
            Plugin.NotificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
            {
                Title = "Cage closed",
                Content = $"{pairing.PeerName} is in the cage now.",
                Type = Dalamud.Interface.ImGuiNotification.NotificationType.Info,
            });
    }

    public Status? For(PairingState pairing)
    {
        if (!pairing.IsPaired || config.FindPairingById(pairing.Id) is null)
        {
            statuses.Remove(pairing.Id);
            sent.Remove(pairing.Id);
            beforeSent.Remove(pairing.Id);
            refusals.Remove(pairing.Id);
            return null;
        }
        return statuses.GetValueOrDefault(pairing.Id);
    }

    public Sent? LastSent(PairingState pairing) => sent.GetValueOrDefault(pairing.Id);

    public void MarkSent(PairingState pairing, SavedCage? cage, bool open, long seq)
    {
        // An open keeps the cage it opens, so its bounds stop drawing once the Sub reports it gone.
        var previous = sent.GetValueOrDefault(pairing.Id);
        var post = cage is null ? previous?.Post : cage.Post ? cage : null;
        var cageId = cage is { Post: false } ? cage.Id : cage is null ? previous?.CageId : null;
        beforeSent[pairing.Id] = previous;
        sent[pairing.Id] = new Sent(cageId, cage?.Name ?? previous?.Name ?? "", DateTimeOffset.UtcNow, open, post, seq);
        refusals.Remove(pairing.Id);
        refreshRequested = true;
    }

    public Refusal? RefusalFor(PairingState pairing) => refusals.GetValueOrDefault(pairing.Id);

    /// A refused cage command was never applied: put back what was sent before it, which also ends the faster checks
    /// and the drawing that waited on it. An applied one changes nothing; the relay report stays the cage's source.
    public void OnCommandOutcome(CommandOutcome outcome)
    {
        if (outcome.Kind != CommandOutcomeKind.Refused || sent.GetValueOrDefault(outcome.PairingId) is not { } latest || latest.Seq != outcome.Seq)
            return;
        if (beforeSent.Remove(outcome.PairingId, out var previous) && previous is not null)
            sent[outcome.PairingId] = previous;
        else
            sent.Remove(outcome.PairingId);
        refusals[outcome.PairingId] = new Refusal(latest.Name, latest.Open, latest.Post is not null && !latest.Open, outcome.Code, DateTimeOffset.UtcNow);
    }

    /// The hitching post this Owner tied the Sub to, while it hasn't been opened or reported gone since.
    public SavedCage? HoldingPost(PairingState pairing)
    {
        if (LastSent(pairing) is not { Open: false, Post: { } post } s)
            return null;
        var status = For(pairing);
        if (status is null || status.State != CageStatusReporter.None)
            return post;
        // A refused post is never reported, so an older `none` only counts as "not yet" for a short while.
        return status.StateAt < s.At.AddSeconds(-5) && DateTimeOffset.UtcNow - s.At < FastInterval ? post : null;
    }

    /// The Owner opened that Sub's view.
    public void RequestRefresh() => refreshRequested = true;

    /// Whether the pair status check should run now, ahead of its regular interval.
    public bool WantsCheck(DateTime utcNow)
    {
        if (refreshRequested && utcNow - lastCheckUtc >= RefreshThrottle)
            return true;
        return utcNow - lastCheckUtc >= FastInterval && sent.Any(s => Unconfirmed(s.Key, s.Value));
    }

    public void MarkChecked(DateTime utcNow)
    {
        lastCheckUtc = utcNow;
        refreshRequested = false;
    }

    /// Sent, and not yet reported `caged` (or `none` for an open) since.
    private bool Unconfirmed(Guid pairingId, Sent s)
    {
        if (config.FindPairingById(pairingId) is not { IsPaired: true, Direction: PairingDirection.OwnerSide })
            return false;
        if (statuses.GetValueOrDefault(pairingId) is not { } status || status.StateAt < s.At.AddSeconds(-5))
            return true;
        return s.Open ? status.State != CageStatusReporter.None : status.State is not (CageStatusReporter.Caged or CageStatusReporter.None);
    }
}
