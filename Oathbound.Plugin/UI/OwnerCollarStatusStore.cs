using System;
using System.Collections.Generic;
using Dalamud.Interface.ImGuiNotification;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.UI;

/// Owner side: the collar state each Sub's client last reported to the relay. Real Sub state, unlike
/// OwnerStatusEstimateTracker. In memory only; the next pair status check refills it.
public sealed class OwnerCollarStatusStore
{
    public sealed record Status(string State, DateTimeOffset StateAt, DateTimeOffset? CheckinAt);

    private readonly PluginConfig config;
    private readonly Dictionary<Guid, Status> statuses = new();

    public OwnerCollarStatusStore(PluginConfig config) => this.config = config;

    /// Call on the framework thread.
    public void Update(PairingState pairing, PairEnvelope pair)
    {
        if (pairing.Direction != PairingDirection.OwnerSide)
            return;
        if (pair.CollarState is not { } state || pair.CollarStateAt is not { } stateAt)
        {
            statuses.Remove(pairing.Id);
            return;
        }

        var wasBroken = statuses.TryGetValue(pairing.Id, out var previous) && previous.State == CollarStatusReporter.Broken;
        statuses[pairing.Id] = new Status(state, DateTimeOffset.FromUnixTimeSeconds(stateAt),
            pair.CollarCheckinAt is { } checkin ? DateTimeOffset.FromUnixTimeSeconds(checkin) : null);

        if (state == CollarStatusReporter.Broken && !wasBroken)
            Plugin.NotificationManager.AddNotification(new Notification
            {
                Title = "Collar unlocked",
                Content = $"{pairing.PeerName}'s collar is unlocked - it came off without you unlocking it.",
                Type = NotificationType.Warning,
                Minimized = false,
            });
    }

    public Status? For(PairingState pairing)
    {
        if (!pairing.IsPaired || config.FindPairingById(pairing.Id) is null)
        {
            statuses.Remove(pairing.Id);
            return null;
        }
        return statuses.GetValueOrDefault(pairing.Id);
    }

    public static bool IsStale(Status status) =>
        status.State != CollarStatusReporter.Unlocked && status.CheckinAt is { } checkin
        && DateTimeOffset.UtcNow - checkin > TimeSpan.FromSeconds(RelayProtocolConstants.CollarStaleSeconds);
}
