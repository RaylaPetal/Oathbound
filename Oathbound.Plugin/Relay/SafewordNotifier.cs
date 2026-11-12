using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// Sub side: tells every Owner that panic was used, and nothing else - a tell so an online Owner knows at once, and a
/// relay report for one who isn't. Both carry the same `at`, which is how the Owner's client counts them as one.
public sealed class SafewordNotifier
{
    /// A Sub pressing the hotkey repeatedly sends one notice, not a stream of tells.
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    /// An older relay without the endpoint.
    private static readonly TimeSpan UnsupportedRetryDelay = TimeSpan.FromHours(1);

    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly ChatComposer composer;
    private readonly ChatSender sender;

    /// Written from relay continuations too. In memory only, so a report still unsent at unload is lost.
    private readonly ConcurrentDictionary<Guid, long> pending = new();
    private DateTime lastNoticeUtc = DateTime.MinValue;
    private DateTime retryNotBeforeUtc;
    private int busy;

    public SafewordNotifier(PluginConfig config, RelayClient relay, ChatComposer composer, ChatSender sender)
    {
        this.config = config;
        this.relay = relay;
        this.composer = composer;
        this.sender = sender;
    }

    /// Panic only, never unpair. Only queues work, so it can't hold up panic's reverts.
    public void OnPanic()
    {
        var now = DateTime.UtcNow;
        if (now - lastNoticeUtc < RepeatWindow)
            return;
        lastNoticeUtc = now;

        var at = new DateTimeOffset(now).ToUnixTimeSeconds();
        foreach (var pairing in config.Pairings.Where(p => p is { IsPaired: true, Direction: PairingDirection.SubSide }))
        {
            if (!string.IsNullOrWhiteSpace(pairing.PeerName) && !string.IsNullOrWhiteSpace(pairing.PeerWorld))
                sender.Send(composer.ComposeSafewordPing(pairing.PeerName, pairing.PeerWorld, at));
            if (pairing.PairIdHash is not null)
                pending[pairing.Id] = at;
        }
        retryNotBeforeUtc = DateTime.MinValue;
        Plugin.Log.Information("Safeword notice sent.");
    }

    public void OnFrameworkUpdate(CancellationToken ct)
    {
        if (pending.IsEmpty || DateTime.UtcNow < retryNotBeforeUtc || !Plugin.ClientState.IsLoggedIn)
            return;
        if (Interlocked.Exchange(ref busy, 1) == 1)
            return;
        var (pairingId, at) = pending.First();
        if (config.FindPairingById(pairingId) is not { IsPaired: true, Direction: PairingDirection.SubSide, PairIdHash: not null } pairing)
        {
            pending.TryRemove(pairingId, out _);
            Interlocked.Exchange(ref busy, 0);
            return;
        }
        Plugin.FireAndForget(SendAsync(pairing, at, ct));
    }

    private async Task SendAsync(PairingState pairing, long at, CancellationToken ct)
    {
        try
        {
            await relay.PutSafewordAsync(pairing.PairIdHash!, pairing.PairEpoch, at, ChatComposer.SafewordRelayMood, ct).ConfigureAwait(false);
            // A newer safeword queued meanwhile stays queued.
            pending.TryRemove(new(pairing.Id, at));
        }
        catch (RelayException ex)
        {
            retryNotBeforeUtc = DateTime.UtcNow + (ex.Code == "not_found" ? UnsupportedRetryDelay : RetryDelay);
            Plugin.Log.Information($"Safeword report failed ({ex.Code}); retrying later.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            retryNotBeforeUtc = DateTime.UtcNow + RetryDelay;
            Plugin.Log.Warning(ex, "Safeword report failed; retrying later.");
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }
}
