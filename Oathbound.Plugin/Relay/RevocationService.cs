using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// collar/pairing "Unpair and panic publish authenticated revocation" and "Pairing" 's replay/epoch
/// isolation requirements. This class only ever publishes/checks signed revocations and maintains the
/// retry outbox - it never performs local teardown itself (PanicHandler/PairingService already did that,
/// synchronously, before this is ever called) and never re-enables a pairing.
public sealed class RevocationService
{
    public event Action? PairingRevoked;

    /// collar/pairing: how a pairing the relay reports as over is ended locally - wired by Plugin to the same
    /// teardown a verified unpair-notice tell gets (collar release, and restraints unlocked for a Sub-side
    /// pairing). It can't be a constructor dependency: PairingService itself depends on this class.
    public Action<PairingState>? EndPairingLocally { get; set; }
    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly DeviceIdentityService identity;

    public RevocationService(PluginConfig config, RelayClient relay, DeviceIdentityService identity)
    {
        this.config = config;
        this.relay = relay;
        this.identity = identity;
    }

    /// Best-effort publish of a revocation for the given pairing (captured by the caller *before* it
    /// cleared its identity fields, since ReleasePeer clears identity but not this pairing's own sequence
    /// bookkeeping - see PairingService). `pairIdHash`/
    /// `pairEpoch` are passed explicitly (rather than read from `pairing`) because ReleasePeer clears
    /// `pairing.PairIdHash` before this runs; `pairing` itself is only used for its own sequence counter and
    /// delivery-status display, which survive that clearing. On any failure, queues a retry entry rather
    /// than throwing; callers should treat this as fire-and-forget.
    public async Task PublishBestEffortAsync(PairingState pairing, string pairIdHash, int pairEpoch, string reason, CancellationToken ct)
    {
        var sequence = pairing.OutgoingRevocationSequence + 1;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var envelope = new RevocationEnvelope
        {
            PairIdHash = pairIdHash,
            PairEpoch = pairEpoch,
            Sequence = sequence,
            Reason = reason,
            IssuedByDeviceKeyId = identity.DeviceKeyId ?? "",
            CreatedAt = now,
            ExpiresAt = now + 604800,
        };
        envelope.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(envelope));

        // The sequence is reserved locally (and persisted) whether or not the publish itself succeeds, so a
        // retried attempt never reuses a sequence number a peer might already be tracking as consumed.
        pairing.OutgoingRevocationSequence = sequence;
        SetDeliveryStatus(pairing, "pending");
        config.Save();

        try
        {
            await relay.PublishRevocationAsync(envelope, ct).ConfigureAwait(false);
            SetDeliveryStatus(pairing, "delivered");
            config.Save();
        }
        catch (RelayException)
        {
            config.RevocationOutbox.Add(new RevocationRetryEntry
            {
                PairIdHash = pairIdHash,
                PairEpoch = pairEpoch,
                Sequence = sequence,
                Reason = reason,
                CreatedAt = envelope.CreatedAt,
                ExpiresAt = envelope.ExpiresAt,
                Signature = envelope.Signature!,
                Attempt = 0,
                NextAttemptAtUnixSeconds = now + 30,
            });
            SetDeliveryStatus(pairing, "pending");
            config.Save();
        }
    }

    /// Codes a retry can never succeed by simply trying again - the request itself is wrong (signature
    /// no longer matches on-file state, malformed, or the relay has permanently rejected it) rather than
    /// the relay being temporarily unavailable. Task 3.4 "no retry of permanent failures".
    private static readonly HashSet<string> PermanentFailureCodes = ["unauthorized", "invalid_request", "payload_too_large"];

    /// Called periodically (see Plugin.OnFrameworkUpdate, throttled) to retry anything still pending.
    /// Honors the relay's own Retry-After when it gives one; otherwise backs off exponentially with jitter
    /// (task 3.4 "jittered exponential backoff"). An entry past its ExpiresAt, or one the relay has
    /// permanently rejected, is dropped with a visible warning logged - it never restores pairing and never
    /// blocks anything else.
    public async Task RetryOutboxAsync(CancellationToken ct)
    {
        if (config.RevocationOutbox.Count == 0) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var entry in config.RevocationOutbox.ToArray())
        {
            if (now >= entry.ExpiresAt)
            {
                Plugin.Log.Warning($"Revocation retry for pair {entry.PairIdHash} (sequence {entry.Sequence}) expired without confirmed delivery.");
                config.RevocationOutbox.Remove(entry);
                SetDeliveryStatus(entry, "expired");
                config.Save();
                continue;
            }
            if (now < entry.NextAttemptAtUnixSeconds) continue;

            var envelope = new RevocationEnvelope
            {
                PairIdHash = entry.PairIdHash,
                PairEpoch = entry.PairEpoch,
                Sequence = entry.Sequence,
                Reason = entry.Reason,
                IssuedByDeviceKeyId = identity.DeviceKeyId ?? "",
                CreatedAt = entry.CreatedAt,
                ExpiresAt = entry.ExpiresAt,
                Signature = entry.Signature,
            };

            try
            {
                await relay.PublishRevocationAsync(envelope, ct).ConfigureAwait(false);
                config.RevocationOutbox.Remove(entry);
                SetDeliveryStatus(entry, "delivered");
                config.Save();
            }
            catch (RelayException ex) when (PermanentFailureCodes.Contains(ex.Code))
            {
                Plugin.Log.Warning($"Revocation retry for pair {entry.PairIdHash} (sequence {entry.Sequence}) permanently rejected ({ex.Code}); giving up.");
                config.RevocationOutbox.Remove(entry);
                SetDeliveryStatus(entry, "failed");
                config.Save();
            }
            catch (RelayException ex)
            {
                entry.Attempt++;
                var backoffSeconds = ex.RetryAfterSeconds ?? Math.Min(30 * (1 << Math.Min(entry.Attempt, 8)), 3600) + Random.Shared.Next(0, 15);
                entry.NextAttemptAtUnixSeconds = now + backoffSeconds;
                config.Save();
            }
        }
    }

    private static void SetDeliveryStatus(PairingState pairing, string status)
    {
        pairing.LastRevocationDeliveryStatus = status;
        pairing.LastRevocationDeliveryUpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// Retry-outbox entries no longer carry a reference to the `PairingState` that created them (the caller
    /// only had `pairIdHash`/`pairEpoch` by the time a retry entry is queued - see PublishBestEffortAsync).
    /// If that pairing has since been released, its identity fields (and so this lookup) are gone; the
    /// status update is then simply skipped - display-only staleness, never a correctness issue.
    private void SetDeliveryStatus(RevocationRetryEntry entry, string status)
    {
        var pairing = config.Pairings.FirstOrDefault(p => p.PairIdHash == entry.PairIdHash && p.PairEpoch == entry.PairEpoch);
        if (pairing is not null)
            SetDeliveryStatus(pairing, status);
    }

    /// collar/pairing "Peer missed the notification tell" / "Old revocation is replayed after re-pairing".
    /// Called at login and on a low-frequency bounded schedule (Plugin wires the interval). Only ever ends
    /// pairing locally; never executes any other command a peer's revocation might (in principle) try to
    /// smuggle in - there is nothing else to execute, the schema has no room for it.
    public async Task CheckForMissedRevocationAsync(CancellationToken ct)
    {
        // collar/multi-pairing: every active pairing is checked independently - one pairing's revocation
        // ends only that pairing, never any other this device holds.
        foreach (var pairing in config.Pairings.Where(p => p.IsPaired).ToList())
            await CheckForMissedRevocationAsync(pairing, ct).ConfigureAwait(false);
    }

    private async Task CheckForMissedRevocationAsync(PairingState pairing, CancellationToken ct)
    {
        if (!pairing.IsPaired || pairing.PairIdHash is null || pairing.PeerPublicKeyX is null || pairing.PeerPublicKeyY is null)
            return;

        pairing.LastRevocationCheckUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        config.Save();

        RevocationEnvelope[] revocations;
        try
        {
            revocations = await relay.CheckRevocationsAsync(pairing.PairIdHash, pairing.IncomingRevocationSequence, ct).ConfigureAwait(false);
        }
        catch (RelayException ex)
        {
            Plugin.Log.Information($"Revocation check skipped: {ex.Code}.");
            return;
        }

        var peerPublicKey = new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = pairing.PeerPublicKeyX, Y = pairing.PeerPublicKeyY };

        foreach (var revocation in revocations)
        {
            if (!ApplyIfValid(revocation, peerPublicKey, pairing))
                return; // Once this pairing has ended locally, later entries in this batch (if any) no longer apply.
        }
    }

    /// collar/pairing "Unpairing reaches the other person even when they are offline". The relay keeps every
    /// revoked pair row forever as a tombstone, so this answers "is my pairing over?" no matter how long this
    /// client was away - unlike the signed revocations above, which the relay deletes after at most 7 days.
    /// Asks about each pairing's own exact epoch (a mutual pair shares one pairIdHash across two epochs, so
    /// "the latest epoch" could be the other direction's). Any error leaves every pairing untouched: a pairing
    /// is never ended just because the relay couldn't be reached.
    public async Task CheckPairStatusAsync(CancellationToken ct)
    {
        foreach (var pairing in config.Pairings.Where(p => p.IsPaired && p.PairIdHash is not null).ToList())
        {
            PairEnvelope pair;
            try
            {
                pair = await relay.FetchPairAtEpochAsync(pairing.PairIdHash!, pairing.PairEpoch, ct).ConfigureAwait(false);
            }
            catch (RelayException ex)
            {
                // "unauthorized" also covers a row that doesn't exist - the relay doesn't distinguish, by design -
                // so it's treated like any other failure: leave the pairing alone.
                Plugin.Log.Information($"Pair status check skipped: {ex.Code}.");
                continue;
            }

            if (pair.PairIdHash != pairing.PairIdHash || pair.PairEpoch != pairing.PairEpoch || pair.RevokedAt is null || !pairing.IsPaired)
                continue;

            Plugin.Log.Information($"Pairing with {pairing.PeerName}@{pairing.PeerWorld} ended locally: the relay reports it was unpaired.");
            if (EndPairingLocally is { } end)
            {
                await Plugin.Framework.RunOnFrameworkThread(() => end(pairing)).ConfigureAwait(false);
            }
            else
            {
                pairing.Paired = false;
                config.Save();
                PairingRevoked?.Invoke();
            }
            Plugin.ChatGui.Print($"[Oathbound] Your pairing with {pairing.PeerName}@{pairing.PeerWorld} has ended - it was unpaired.");
        }
    }

    /// Returns true if pairing is still active after processing this revocation (false means it just ended).
    /// Task 5.4: rejects wrong-device, wrong-pair, stale-sequence, expired, and old-epoch revocations.
    private bool ApplyIfValid(RevocationEnvelope revocation, EcPublicKeyJwk peerPublicKey, PairingState pairing)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (revocation.PairIdHash != pairing.PairIdHash) return true;
        // collar/multi-pairing: exact match, not just "not older" - pairIdHash is symmetric over just the
        // two device key ids (no direction), so the same two devices paired in both directions share one
        // pairIdHash family; fetching "revocations since IncomingRevocationSequence" for THIS pairing can
        // come back carrying a DIFFERENT epoch that belongs to the OTHER direction's independent pairing
        // between the same two devices, not a stale notice for this one. Only this pairing's own exact
        // epoch can end it.
        if (revocation.PairEpoch != pairing.PairEpoch) return true;
        if (revocation.Sequence <= pairing.IncomingRevocationSequence) return true; // Replay.
        if (revocation.ExpiresAt <= now) return true;
        if (revocation.IssuedByDeviceKeyId != pairing.PeerDeviceKeyId) return true; // Wrong device.
        if (!RelayCrypto.VerifyRaw(peerPublicKey, revocation.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(revocation)))
            return true;

        pairing.IncomingRevocationSequence = revocation.Sequence;
        pairing.Paired = false;
        PairingRevoked?.Invoke();
        config.Save();
        Plugin.Log.Information($"Pairing ended locally: a valid signed revocation (sequence {revocation.Sequence}, reason \"{revocation.Reason}\") was observed from the paired peer.");
        return false;
    }
}
