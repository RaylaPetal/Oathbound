using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// Publishes/checks signed revocations and keeps the retry outbox. Never does local teardown itself, and never re-enables a pairing.
public sealed class RevocationService
{
    public event Action? PairingRevoked;

    /// Raised on the framework thread for each pairing whose relay row was read and is still active.
    public event Action<PairingState, PairEnvelope>? PairStatusFetched;

    /// Wired by Plugin to the verified unpair-notice teardown; not a constructor dependency because PairingService depends on this class.
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

    /// `pairIdHash`/`pairEpoch` are passed explicitly because ReleasePeer clears them first. Failures queue a retry; fire-and-forget.
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

        // Reserved and persisted even if the publish fails, so a retry never reuses a sequence the peer may have consumed.
        pairing.OutgoingRevocationSequence = sequence;
        SetDeliveryStatus(pairing, "pending");
        config.SaveNow();

        try
        {
            await relay.PublishRevocationAsync(envelope, ct).ConfigureAwait(false);
            SetDeliveryStatus(pairing, "delivered");
            config.SaveNow();
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
            config.SaveNow();
        }
    }

    /// The request itself is wrong, so retrying can never succeed.
    private static readonly HashSet<string> PermanentFailureCodes = ["unauthorized", "invalid_request", "payload_too_large"];

    /// Honors Retry-After, else jittered exponential backoff. Expired or permanently rejected entries are dropped with a warning.
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
                config.SaveNow();
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
                config.SaveNow();
            }
            catch (RelayException ex) when (PermanentFailureCodes.Contains(ex.Code))
            {
                Plugin.Log.Warning($"Revocation retry for pair {entry.PairIdHash} (sequence {entry.Sequence}) permanently rejected ({ex.Code}); giving up.");
                config.RevocationOutbox.Remove(entry);
                SetDeliveryStatus(entry, "failed");
                config.SaveNow();
            }
            catch (RelayException ex)
            {
                entry.Attempt++;
                var backoffSeconds = ex.RetryAfterSeconds ?? Math.Min(30 * (1 << Math.Min(entry.Attempt, 8)), 3600) + Random.Shared.Next(0, 15);
                entry.NextAttemptAtUnixSeconds = now + backoffSeconds;
                config.SaveNow();
            }
        }
    }

    private static void SetDeliveryStatus(PairingState pairing, string status)
    {
        pairing.LastRevocationDeliveryStatus = status;
        pairing.LastRevocationDeliveryUpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// If the pairing has since been released, the lookup fails and the display update is skipped.
    private void SetDeliveryStatus(RevocationRetryEntry entry, string status)
    {
        var pairing = config.Pairings.FirstOrDefault(p => p.PairIdHash == entry.PairIdHash && p.PairEpoch == entry.PairEpoch);
        if (pairing is not null)
            SetDeliveryStatus(pairing, status);
    }

    /// Only ever ends a pairing locally; the schema has no room for anything else.
    public async Task CheckForMissedRevocationAsync(CancellationToken ct)
    {
        // Each pairing is checked independently.
        foreach (var pairing in config.Pairings.Where(p => p.IsPaired).ToList())
            await CheckForMissedRevocationAsync(pairing, ct).ConfigureAwait(false);
    }

    private async Task CheckForMissedRevocationAsync(PairingState pairing, CancellationToken ct)
    {
        if (!pairing.IsPaired || pairing.PairIdHash is null || pairing.PeerPublicKeyX is null || pairing.PeerPublicKeyY is null)
            return;

        pairing.LastRevocationCheckUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        config.SaveNow();

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

    /// Pair rows are permanent tombstones, so this works however long the client was away (signed revocations expire
    /// after 7 days). Asks about each pairing's exact epoch. Any error leaves every pairing untouched.
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
                // "unauthorized" also covers a missing row (the relay doesn't distinguish), so leave the pairing alone.
                Plugin.Log.Information($"Pair status check skipped: {ex.Code}.");
                continue;
            }

            if (pair.PairIdHash != pairing.PairIdHash || pair.PairEpoch != pairing.PairEpoch || !pairing.IsPaired)
                continue;
            if (pair.RevokedAt is null)
            {
                if (PairStatusFetched is { } fetched)
                    await Plugin.Framework.RunOnFrameworkThread(() => fetched(pairing, pair)).ConfigureAwait(false);
                continue;
            }

            Plugin.Log.Information($"Pairing with {pairing.PeerName}@{pairing.PeerWorld} ended locally: the relay reports it was unpaired.");
            if (EndPairingLocally is { } end)
            {
                await Plugin.Framework.RunOnFrameworkThread(() => end(pairing)).ConfigureAwait(false);
            }
            else
            {
                pairing.Paired = false;
                config.SaveNow();
                PairingRevoked?.Invoke();
            }
            Plugin.ChatGui.Print($"[Oathbound] Your pairing with {pairing.PeerName}@{pairing.PeerWorld} has ended - it was unpaired.");
        }
    }

    /// Returns true if the pairing is still active afterwards.
    private bool ApplyIfValid(RevocationEnvelope revocation, EcPublicKeyJwk peerPublicKey, PairingState pairing)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (revocation.PairIdHash != pairing.PairIdHash) return true;
        // Exact match: a mutual pair shares one pairIdHash, so another epoch may belong to the other direction.
        if (revocation.PairEpoch != pairing.PairEpoch) return true;
        if (revocation.Sequence <= pairing.IncomingRevocationSequence) return true; // Replay.
        if (revocation.ExpiresAt <= now) return true;
        if (revocation.IssuedByDeviceKeyId != pairing.PeerDeviceKeyId) return true; // Wrong device.
        if (!RelayCrypto.VerifyRaw(peerPublicKey, revocation.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(revocation)))
            return true;

        pairing.IncomingRevocationSequence = revocation.Sequence;
        pairing.Paired = false;
        PairingRevoked?.Invoke();
        config.SaveNow();
        Plugin.Log.Information($"Pairing ended locally: a valid signed revocation (sequence {revocation.Sequence}, reason \"{revocation.Reason}\") was observed from the paired peer.");
        return false;
    }
}
