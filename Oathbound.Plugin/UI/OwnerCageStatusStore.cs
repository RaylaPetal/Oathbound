using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.UI;

/// Owner side: the cage state each Sub's client last reported to the relay, plus the cage this Owner last sent, which
/// decides when the pair status check runs faster. In memory only; the next pair status check refills it.
public sealed class OwnerCageStatusStore
{
    public sealed record Status(string State, DateTimeOffset StateAt, DateTimeOffset? EndsAt);
    public sealed record Sent(Guid? CageId, string Name, DateTimeOffset At, bool Open);

    private static readonly TimeSpan FastInterval = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan RefreshThrottle = TimeSpan.FromMinutes(1);

    private readonly PluginConfig config;
    private readonly Dictionary<Guid, Status> statuses = new();
    private readonly Dictionary<Guid, Sent> sent = new();
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
            return null;
        }
        return statuses.GetValueOrDefault(pairing.Id);
    }

    public Sent? LastSent(PairingState pairing) => sent.GetValueOrDefault(pairing.Id);

    public void MarkSent(PairingState pairing, SavedCage? cage, bool open)
    {
        // An open keeps the cage it opens, so its bounds stop drawing once the Sub reports it gone.
        var cageId = cage?.Id ?? sent.GetValueOrDefault(pairing.Id)?.CageId;
        sent[pairing.Id] = new Sent(cageId, cage?.Name ?? "", DateTimeOffset.UtcNow, open);
        refreshRequested = true;
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
