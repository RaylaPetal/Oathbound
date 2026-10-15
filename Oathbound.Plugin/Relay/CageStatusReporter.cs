using System;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// Sub side: tells the relay the state of the cage a pairing holds, so that Owner sees when an armed cage closes or a
/// cage re-arms. Only the state and its end time travel, never the cage's name, place or position.
public sealed class CageStatusReporter
{
    public const string None = "none";
    public const string Armed = "armed";
    public const string Caged = "caged";

    private static readonly TimeSpan EvaluateInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    /// An older relay without the endpoint: back off for a long time instead of retrying every few minutes.
    private static readonly TimeSpan UnsupportedRetryDelay = TimeSpan.FromHours(1);

    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly CageService cage;

    private DateTime nextEvaluateUtc;
    private DateTime retryNotBeforeUtc;
    private int busy;
    private bool wasLoggedIn;

    private Guid? reportedPairingId;
    private string? reportedState;
    private long? reportedEndsAt;

    public CageStatusReporter(PluginConfig config, RelayClient relay, CageService cage)
    {
        this.config = config;
        this.relay = relay;
        this.cage = cage;
        cage.StateChanged += () => nextEvaluateUtc = DateTime.MinValue;
    }

    public void OnFrameworkUpdate(CancellationToken ct)
    {
        var loggedIn = Plugin.ClientState.IsLoggedIn;
        if (loggedIn && !wasLoggedIn)
            reportedState = null; // Report again at login.
        wasLoggedIn = loggedIn;

        var now = DateTime.UtcNow;
        if (!loggedIn || now < nextEvaluateUtc || now < retryNotBeforeUtc)
            return;
        nextEvaluateUtc = now + EvaluateInterval;

        if (Desired() is not { } desired)
            return;
        if (desired.Pairing.Id == reportedPairingId && desired.State == reportedState && desired.EndsAt == reportedEndsAt)
            return;
        if (Interlocked.Exchange(ref busy, 1) == 1)
            return;
        Plugin.FireAndForget(SendAsync(desired.Pairing, desired.State, desired.EndsAt, ct));
    }

    /// The holding pairing's state, or `none` once for the pairing last reported after its cage ended or moved on.
    private (PairingState Pairing, string State, long? EndsAt)? Desired()
    {
        var current = cage.Cage;
        if (current?.PairingId is { } id && Reportable(id) is { } holder)
        {
            // A cage held by a different pairing than the one last reported: clear the old one first.
            if (reportedPairingId is { } old && old != id && reportedState is not (null or None) && Reportable(old) is { } previous)
                return (previous, None, null);
            var end = current.Phase == CagePhase.Caged || current.HasClosed ? current.EndsAtUtc : current.ArmExpiresAtUtc;
            return (holder, current.Phase == CagePhase.Caged ? Caged : Armed, end is { } e ? new DateTimeOffset(e, TimeSpan.Zero).ToUnixTimeSeconds() : null);
        }
        if (reportedPairingId is { } last && reportedState is not None && Reportable(last) is { } lastPairing)
            return (lastPairing, None, null);
        return null;
    }

    private PairingState? Reportable(Guid id) =>
        config.FindPairingById(id) is { IsPaired: true, Direction: PairingDirection.SubSide, PairIdHash: not null } pairing ? pairing : null;

    private async Task SendAsync(PairingState pairing, string state, long? endsAt, CancellationToken ct)
    {
        try
        {
            await relay.PutCageStatusAsync(pairing.PairIdHash!, pairing.PairEpoch, state, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), endsAt, ct).ConfigureAwait(false);
            reportedPairingId = pairing.Id;
            reportedState = state;
            reportedEndsAt = endsAt;
            Plugin.Log.Information($"Cage status reported: {state}.");
        }
        catch (RelayException ex)
        {
            retryNotBeforeUtc = DateTime.UtcNow + (ex.Code == "not_found" ? UnsupportedRetryDelay : RetryDelay);
            Plugin.Log.Information($"Cage status report failed ({ex.Code}); retrying later.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            retryNotBeforeUtc = DateTime.UtcNow + RetryDelay;
            Plugin.Log.Warning(ex, "Cage status report failed; retrying later.");
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }
}
