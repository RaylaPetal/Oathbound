using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// Sub side: tells every Owner that panic was used - a tell for an Owner who's online, and a relay report for one
/// who isn't. Both carry the same `at`, which is how the Owner's client counts them as one safeword.
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

    /// Written from relay continuations too. In memory only: the tell already went out, the relay copy is the backup.
    private readonly ConcurrentDictionary<Guid, (long At, SafewordMood Mood)> pending = new();
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
        if (!config.NotifyOwnersOnSafeword)
            return;
        var now = DateTime.UtcNow;
        if (now - lastNoticeUtc < RepeatWindow)
            return;
        lastNoticeUtc = now;

        var at = new DateTimeOffset(now).ToUnixTimeSeconds();
        var mood = config.SafewordMood;
        foreach (var pairing in config.Pairings.Where(p => p is { IsPaired: true, Direction: PairingDirection.SubSide }))
        {
            if (!string.IsNullOrWhiteSpace(pairing.PeerName) && !string.IsNullOrWhiteSpace(pairing.PeerWorld))
                sender.Send(composer.ComposeSafewordNotice(pairing.PeerName, pairing.PeerWorld, mood, at));
            if (pairing.PairIdHash is not null)
                pending[pairing.Id] = (at, mood);
        }
        retryNotBeforeUtc = DateTime.MinValue;
        Plugin.Log.Information($"Safeword notice sent ({ChatComposer.SafewordMoodWord(mood)}).");
    }

    public void OnFrameworkUpdate(CancellationToken ct)
    {
        if (pending.IsEmpty || DateTime.UtcNow < retryNotBeforeUtc || !Plugin.ClientState.IsLoggedIn)
            return;
        if (Interlocked.Exchange(ref busy, 1) == 1)
            return;
        var (pairingId, report) = pending.First();
        if (config.FindPairingById(pairingId) is not { IsPaired: true, Direction: PairingDirection.SubSide, PairIdHash: not null } pairing)
        {
            pending.TryRemove(pairingId, out _);
            Interlocked.Exchange(ref busy, 0);
            return;
        }
        Plugin.FireAndForget(SendAsync(pairing, report.At, report.Mood, ct));
    }

    private async Task SendAsync(PairingState pairing, long at, SafewordMood mood, CancellationToken ct)
    {
        try
        {
            await relay.PutSafewordAsync(pairing.PairIdHash!, pairing.PairEpoch, at, ChatComposer.SafewordMoodWord(mood), ct).ConfigureAwait(false);
            // A newer safeword queued meanwhile stays queued.
            pending.TryRemove(new(pairing.Id, (at, mood)));
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
