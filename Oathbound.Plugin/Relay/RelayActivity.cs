using System;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// How many installs recently checked in with the relay, as last read from a pair status check. Arrives with
/// the check the client already makes, so it costs no extra request. In memory only.
public sealed class RelayActivity
{
    /// Older than this and the number is hidden rather than shown as current.
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);

    private long computedAt;

    public int? ActiveDevices { get; private set; }

    /// Framework thread.
    public void Update(PairingState pairing, PairEnvelope pair)
    {
        if (pair.RelayActiveDevices is not { } count || pair.RelayActiveDevicesAt is not { } at || at < computedAt)
            return;
        ActiveDevices = count;
        computedAt = at;
    }

    public int? Current =>
        ActiveDevices is { } count && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(computedAt) < MaxAge ? count : null;
}
