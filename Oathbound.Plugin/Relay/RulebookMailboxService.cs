using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Rulebook;

namespace Oathbound.Plugin.Relay;

/// The relay side of rulebooks, both directions: the Owner publishes rulebooks and collects reports, the Sub
/// collects rulebooks and publishes reports. Each recipient keeps one receive key per channel, rotated on every
/// pickup. Nothing here sends chat or applies anything; received items are handed to the events.
public sealed class RulebookMailboxService
{
    private static readonly byte[] ReceiveKeyEntropy = "oathbound-rulebook-receive-key-v1"u8.ToArray();
    private const int MaxReportPlaintextBytes = 512 * 1024;

    /// Framework thread. A version that passed every check and now waits for the Sub's review.
    public event Action<PairingState, RulebookDocument>? RulebookReceived;

    /// Framework thread. A version that decrypted but was refused (bad consequence, unknown schema).
    public event Action<PairingState, int, string>? RulebookRejected;

    /// Framework thread.
    public event Action<PairingState, RulebookReport>? ReportReceived;

    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly DeviceIdentityService identity;
    private readonly object gate = new();
    private readonly HashSet<(Guid, string)> inFlight = new();

    public RulebookMailboxService(PluginConfig config, RelayClient relay, DeviceIdentityService identity)
    {
        this.config = config;
        this.relay = relay;
        this.identity = identity;
    }

    /// The Rulebook window and the `collarrulebook` nudge: read this pairing's relay row now instead of waiting.
    public async Task CheckNowAsync(PairingState pairing, CancellationToken ct)
    {
        if (pairing is not { IsPaired: true, PairIdHash: { Length: > 0 } pairIdHash })
            return;
        try
        {
            var pair = await relay.FetchPairAtEpochAsync(pairIdHash, pairing.PairEpoch, ct).ConfigureAwait(false);
            if (pair.PairIdHash == pairIdHash && pair.PairEpoch == pairing.PairEpoch && pair.RevokedAt is null && pair.RulebookMailbox is { } summary)
                await ApplyPairStatusAsync(pairing, summary, ct).ConfigureAwait(false);
        }
        catch (RelayException ex)
        {
            Plugin.Log.Information($"Rulebook check for {pairing.PeerName} skipped: {ex.Code}.");
        }
    }

    /// From the pair status poll. Costs no extra request unless a key is missing or something is waiting.
    public async Task ApplyPairStatusAsync(PairingState pairing, RulebookMailboxSummary summary, CancellationToken ct)
    {
        if (pairing is not { IsPaired: true, PairIdHash: { Length: > 0 } })
            return;
        var state = pairing.Rulebook;
        if (pairing.Direction == PairingDirection.OwnerSide)
        {
            state.SubSupportsRulebook = summary.Rulebook.Exists;
            state.PickedUpVersion = Math.Max(state.PickedUpVersion, summary.Rulebook.LastConsumedSequence ?? 0);
            // A version the relay neither holds nor handed over was dropped when the Sub replaced its key.
            if (summary.Rulebook.Exists && state.PublishedVersion > 0 && state.PublishedDocument is not null
                && !Delivered(summary.Rulebook, state.PublishedVersion))
            {
                Plugin.Log.Information($"Rulebook version {state.PublishedVersion} for {pairing.PeerName} never arrived - sending it again.");
                if (await ResendPublishedAsync(pairing, ct).ConfigureAwait(false) is { } error)
                    Plugin.Log.Information($"Rulebook resend for {pairing.PeerName} failed: {error}");
            }
            await CollectAsync(pairing, RulebookChannels.Report, summary.Report, state.LastReportSequence, ct).ConfigureAwait(false);
        }
        else
        {
            // Same for a report: forgetting what was sent makes the next tick upload it again.
            if (summary.Report.Exists && state.NextReportSequence > 0 && !Delivered(summary.Report, state.NextReportSequence))
                state.LastUploadedReportDigest = null;
            await CollectAsync(pairing, RulebookChannels.Rulebook, summary.Rulebook, state.LastReceivedVersion, ct).ConfigureAwait(false);
        }
    }

    private static bool Delivered(RulebookChannelSummary summary, int sequence) =>
        (summary.WaitingSequence ?? 0) >= sequence || (summary.LastConsumedSequence ?? 0) >= sequence;

    /// The relay's current view of one channel, read again so a summary that's already out of date can't
    /// make this client replace its key and throw away something that just arrived.
    private async Task<RulebookChannelSummary?> FreshSummaryAsync(PairingState pairing, string channel, CancellationToken ct)
    {
        var pair = await relay.FetchPairAtEpochAsync(pairing.PairIdHash!, pairing.PairEpoch, ct).ConfigureAwait(false);
        if (pair.PairIdHash != pairing.PairIdHash || pair.PairEpoch != pairing.PairEpoch || pair.RevokedAt is not null || pair.RulebookMailbox is not { } fresh)
            return null;
        return channel == RulebookChannels.Rulebook ? fresh.Rulebook : fresh.Report;
    }

    // ---- Recipient side (shared) ----

    private async Task CollectAsync(PairingState pairing, string channel, RulebookChannelSummary summary, int lastSeen, CancellationToken ct)
    {
        lock (gate)
            if (!inFlight.Add((pairing.Id, channel)))
                return;
        try
        {
            identity.EnsureIdentity();
            var slot = KeySlot.For(pairing, channel);
            if (!slot.TryLoad(out var probe))
            {
                await PublishFreshKeyAsync(pairing, channel, slot, ct).ConfigureAwait(false);
                return;
            }
            probe.Dispose();
            if (!summary.Exists || summary.ReceiveKeyId != slot.Id)
            {
                // The summary may predate this client's own latest rotation; only a fresh mismatch means the key is gone.
                if (await FreshSummaryAsync(pairing, channel, ct).ConfigureAwait(false) is not { } fresh)
                    return;
                if (!fresh.Exists || fresh.ReceiveKeyId != slot.Id)
                {
                    // Anything waiting was encrypted to a key we no longer hold; the sender's delivery check resends it.
                    await PublishFreshKeyAsync(pairing, channel, slot, ct).ConfigureAwait(false);
                    return;
                }
                summary = fresh;
            }

            if (summary.WaitingSequence is not { } sequence || sequence <= lastSeen)
                return;
            await ConsumeAsync(pairing, channel, slot, sequence, ct).ConfigureAwait(false);
        }
        catch (RelayException ex)
        {
            Plugin.Log.Information($"Rulebook {channel} pickup for {pairing.PeerName} failed: {ex.Code}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Rulebook {channel} pickup failed unexpectedly.");
        }
        finally
        {
            lock (gate) inFlight.Remove((pairing.Id, channel));
        }
    }

    private async Task ConsumeAsync(PairingState pairing, string channel, KeySlot slot, int sequence, CancellationToken ct)
    {
        if (!slot.TryLoad(out var receiveKey))
            return;
        using var usedKey = receiveKey;
        var usedKeyId = slot.Id!;

        // Rotation is atomic with the pickup on the relay, so the next key is persisted before any local check.
        using var nextKey = RelayCrypto.GenerateEphemeralKeyPair();
        var nextEnvelope = BuildSignedKeyEnvelope(pairing, channel, nextKey);
        var (envelope, ciphertext) = await relay.ConsumeRulebookItemAsync(pairing.PairIdHash!, pairing.PairEpoch, channel, sequence, nextEnvelope, ct).ConfigureAwait(false);
        slot.Store(nextEnvelope, nextKey);
        config.SaveNow();

        var maxCiphertext = channel == RulebookChannels.Rulebook ? RelayProtocolConstants.RulebookCiphertextMaxBytes : RelayProtocolConstants.RulebookReportCiphertextMaxBytes;
        var lastSeen = channel == RulebookChannels.Rulebook ? pairing.Rulebook.LastReceivedVersion : pairing.Rulebook.LastReportSequence;
        var error = VerifyItem(pairing, channel, envelope, ciphertext, usedKeyId, lastSeen, maxCiphertext);
        if (error is not null)
        {
            Plugin.Log.Warning($"Rulebook {channel} item from {pairing.PeerName} ignored: {error}");
            return;
        }

        string json;
        try
        {
            var maxPlain = channel == RulebookChannels.Rulebook ? RulebookLimits.MaxPlaintextBytes : MaxReportPlaintextBytes;
            json = Encoding.UTF8.GetString(RulebookCrypto.Open(envelope, ciphertext, usedKey, channel, maxPlain));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException)
        {
            Plugin.Log.Warning($"Rulebook {channel} item from {pairing.PeerName} failed to decrypt ({ex.GetType().Name}).");
            return;
        }

        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (channel == RulebookChannels.Rulebook)
                AcceptIncomingRulebook(pairing, envelope.Sequence, json);
            else
                AcceptIncomingReport(pairing, envelope.Sequence, json);
        }).ConfigureAwait(false);
    }

    /// Every check passes before a single byte is decrypted.
    private string? VerifyItem(PairingState pairing, string channel, RulebookItemEnvelope envelope, byte[] ciphertext, string usedKeyId, int lastSeen, int maxCiphertext)
    {
        if (envelope.Type != RulebookChannels.ItemType(channel))
            return "wrong item type for this channel";
        if (envelope.PairIdHash != pairing.PairIdHash || envelope.PairEpoch != pairing.PairEpoch)
            return "addressed to a different pair/epoch";
        if (envelope.RecipientDeviceKeyId != identity.DeviceKeyId || envelope.SenderDeviceKeyId != pairing.PeerDeviceKeyId)
            return "sender/recipient device keys don't match this pairing";
        if (envelope.ReceiveKeyId != usedKeyId)
            return "encrypted to a different receive key";
        if (envelope.Sequence <= lastSeen)
            return "not newer than the last one received (stale or replayed)";
        if (envelope.CiphertextSizeBytes > maxCiphertext || ciphertext.Length != envelope.CiphertextSizeBytes)
            return "invalid ciphertext size";
        if (RelayCrypto.Sha256Hex(ciphertext) != envelope.CiphertextDigest)
            return "ciphertext digest mismatch";
        if (PeerKey(pairing) is not { } peerKey)
            return "no peer public key on file";
        if (!RelayCrypto.VerifyRaw(peerKey, envelope.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(envelope)))
            return "signature didn't verify against the paired peer's key";
        return null;
    }

    private void AcceptIncomingRulebook(PairingState pairing, int sequence, string json)
    {
        var state = pairing.Rulebook;
        state.LastReceivedVersion = sequence;
        RulebookDocument? doc = null;
        string? reason = null;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (!parsed.RootElement.TryGetProperty("schemaVersion", out var sv) || sv.GetInt32() != RulebookDocument.CurrentSchemaVersion)
                reason = "it needs a newer plugin version";
            else
                doc = RulebookJson.Deserialize(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            reason = "it couldn't be read";
        }

        if (doc is not null && doc.Version != sequence)
            reason = "its version didn't match the relay's";
        else if (doc is not null && RulebookValidation.Check(doc) is { } invalid)
            reason = invalid;

        if (reason is not null || doc is null)
        {
            config.SaveNow();
            RulebookRejected?.Invoke(pairing, sequence, reason ?? "it couldn't be read");
            return;
        }

        state.Pending = doc;
        state.PendingReceivedUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        config.SaveNow();
        RulebookReceived?.Invoke(pairing, doc);
    }

    private void AcceptIncomingReport(PairingState pairing, int sequence, string json)
    {
        RulebookReport? report;
        try
        {
            report = RulebookJson.Deserialize<RulebookReport>(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            report = null;
        }
        pairing.Rulebook.LastReportSequence = sequence;
        if (report is null)
        {
            config.SaveNow();
            return;
        }
        pairing.Rulebook.LastReport = report;
        pairing.Rulebook.LastReportReceivedUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        config.SaveNow();
        ReportReceived?.Invoke(pairing, report);
    }

    // ---- Sender side (shared) ----

    /// Owner: publish the current draft as the next version. Null on success, else a message for the editor.
    public async Task<string?> PublishRulebookAsync(PairingState pairing, CancellationToken ct)
    {
        if (pairing is not { Direction: PairingDirection.OwnerSide, IsPaired: true, PairIdHash: { Length: > 0 } })
            return "This pairing isn't active.";
        if (RulebookValidation.Check(pairing.Rulebook.Draft) is { } invalid)
            return invalid;

        // Sent from a copy, and fingerprinted from that copy: edits made while sending stay "unpublished".
        return await SendDocumentAsync(pairing, pairing.Rulebook.Draft.Clone(), updateDigest: true, ct).ConfigureAwait(false);
    }

    /// The last sent version again, under a new version number, unchanged.
    private Task<string?> ResendPublishedAsync(PairingState pairing, CancellationToken ct) =>
        pairing.Rulebook.PublishedDocument is { } published
            ? SendDocumentAsync(pairing, published.Clone(), updateDigest: false, ct)
            : Task.FromResult<string?>("nothing was sent before");

    private async Task<string?> SendDocumentAsync(PairingState pairing, RulebookDocument doc, bool updateDigest, CancellationToken ct)
    {
        var result = await SendAsync(pairing, RulebookChannels.Rulebook, pairing.Rulebook.PublishedVersion, sequence =>
        {
            doc.Version = sequence;
            return Encoding.UTF8.GetBytes(RulebookJson.Serialize(doc));
        }, ct).ConfigureAwait(false);

        if (result.Error is not null)
            return result.Error;
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var state = pairing.Rulebook;
            state.PublishedVersion = result.Sequence;
            state.PublishedDocument = doc;
            state.PublishedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (updateDigest)
                state.PublishedDigest = DraftDigest(doc);
            state.PublishError = null;
            config.SaveNow();
        }).ConfigureAwait(false);
        return null;
    }

    /// Sub: upload a report. False when it should be retried later (rate limit, no key yet, network).
    public async Task<bool> UploadReportAsync(PairingState pairing, RulebookReport report, CancellationToken ct)
    {
        if (pairing is not { Direction: PairingDirection.SubSide, IsPaired: true, PairIdHash: { Length: > 0 } })
            return false;
        var bytes = Encoding.UTF8.GetBytes(RulebookJson.Serialize(report));
        var result = await SendAsync(pairing, RulebookChannels.Report, pairing.Rulebook.NextReportSequence, _ => bytes, ct).ConfigureAwait(false);
        if (result.Error is not null)
            return false;
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            pairing.Rulebook.NextReportSequence = result.Sequence;
            pairing.Rulebook.LastReportUploadUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            config.SaveNow();
        }).ConfigureAwait(false);
        return true;
    }

    public static string DraftDigest(RulebookDocument draft)
    {
        var copy = draft.Clone();
        copy.Version = 0;
        return RelayCrypto.Sha256Hex(RulebookJson.Serialize(copy));
    }

    private readonly record struct SendResult(int Sequence, string? Error);

    private async Task<SendResult> SendAsync(PairingState pairing, string channel, int lastLocalSequence, Func<int, byte[]> buildPlaintext, CancellationToken ct)
    {
        lock (gate)
            if (!inFlight.Add((pairing.Id, "send-" + channel)))
                return new(0, "Already sending - try again in a moment.");
        try
        {
            identity.EnsureIdentity();
            if (PeerKey(pairing) is not { } peerKey || pairing.PeerDeviceKeyId is null)
                return new(0, "No peer key on file for this pairing.");

            // The second pass only if the recipient rotated its key between our fetch and upload.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                RulebookKeyInfo info;
                try
                {
                    info = await relay.FetchRulebookKeyAsync(pairing.PairIdHash!, pairing.PairEpoch, channel, ct).ConfigureAwait(false);
                }
                catch (RelayException ex) when (ex.Code == "not_found")
                {
                    return new(0, channel == RulebookChannels.Rulebook
                        ? $"{pairing.PeerName}'s plugin doesn't support rulebooks yet."
                        : "The Owner's plugin hasn't set up rulebook reports yet.");
                }

                var key = info.Key;
                if (key.Channel != channel || key.PairIdHash != pairing.PairIdHash || key.PairEpoch != pairing.PairEpoch ||
                    key.RecipientDeviceKeyId != pairing.PeerDeviceKeyId ||
                    !RelayCrypto.VerifyRaw(peerKey, key.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(key)))
                {
                    // Never encrypt to a key the paired peer didn't sign.
                    Plugin.Log.Warning($"Rulebook {channel} key for {pairing.PeerName} didn't verify against the paired peer - not sending.");
                    return new(0, "The relay returned a key your peer didn't sign - not sent.");
                }

                var sequence = Math.Max(lastLocalSequence, info.LastSequence) + 1;
                var plaintext = buildPlaintext(sequence);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var envelope = new RulebookItemEnvelope
                {
                    Type = RulebookChannels.ItemType(channel),
                    PairIdHash = key.PairIdHash,
                    PairEpoch = key.PairEpoch,
                    ReceiveKeyId = key.ReceiveKeyId,
                    Sequence = sequence,
                    SenderDeviceKeyId = identity.DeviceKeyId!,
                    RecipientDeviceKeyId = key.RecipientDeviceKeyId,
                    CreatedAt = now,
                    ExpiresAt = now + RelayProtocolConstants.RulebookMailboxExpirySeconds,
                };
                var maxCiphertext = channel == RulebookChannels.Rulebook ? RelayProtocolConstants.RulebookCiphertextMaxBytes : RelayProtocolConstants.RulebookReportCiphertextMaxBytes;
                byte[] ciphertext;
                try
                {
                    ciphertext = RulebookCrypto.Seal(envelope, key.ReceivePublicKey, channel, plaintext, maxCiphertext);
                }
                catch (InvalidDataException)
                {
                    return new(0, "Too large to send - remove some rules or cards.");
                }
                envelope.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(envelope));

                try
                {
                    await relay.UploadRulebookItemAsync(envelope, ciphertext, ct).ConfigureAwait(false);
                    return new(sequence, null);
                }
                catch (RelayException ex) when (ex.Code == "expired" && attempt == 0)
                {
                    continue;
                }
            }
            return new(0, "The key kept changing - try again.");
        }
        catch (RelayException ex)
        {
            Plugin.Log.Information($"Rulebook {channel} send to {pairing.PeerName} failed: {ex.Code}.");
            return new(0, ex.Code switch
            {
                "rate_limited" => $"Sent too recently - try again in {ex.RetryAfterSeconds ?? 60} seconds.",
                "network" => "Couldn't reach the relay - try again later.",
                "unauthorized" => "The relay rejected this (is the pairing still active?).",
                "payload_too_large" => "Too large to send - remove some rules or cards.",
                _ => "Sending failed - try again later.",
            });
        }
        catch (OperationCanceledException)
        {
            return new(0, "Cancelled.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Rulebook {channel} send failed unexpectedly.");
            return new(0, "Sending failed unexpectedly - see /xllog.");
        }
        finally
        {
            lock (gate) inFlight.Remove((pairing.Id, "send-" + channel));
        }
    }

    // ---- Keys ----

    private async Task PublishFreshKeyAsync(PairingState pairing, string channel, KeySlot slot, CancellationToken ct)
    {
        using var key = RelayCrypto.GenerateEphemeralKeyPair();
        var envelope = BuildSignedKeyEnvelope(pairing, channel, key);
        await relay.PublishRulebookKeyAsync(envelope, ct).ConfigureAwait(false);
        slot.Store(envelope, key);
        config.SaveNow();
        Plugin.Log.Debug($"Rulebook {channel} mailbox for {pairing.PeerName}: published a fresh receive key.");
    }

    private RulebookKeyEnvelope BuildSignedKeyEnvelope(PairingState pairing, string channel, RelayEcKeyPair key)
    {
        var envelope = new RulebookKeyEnvelope
        {
            Channel = channel,
            PairIdHash = pairing.PairIdHash!,
            PairEpoch = pairing.PairEpoch,
            ReceiveKeyId = RelayCrypto.RandomReceiveKeyId(),
            RecipientDeviceKeyId = identity.DeviceKeyId!,
            ReceivePublicKey = RelayCrypto.ExportPublicKeyJwk(key),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        envelope.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(envelope));
        return envelope;
    }

    private static EcPublicKeyJwk? PeerKey(PairingState pairing) =>
        pairing.PeerPublicKeyX is null || pairing.PeerPublicKeyY is null
            ? null
            : new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = pairing.PeerPublicKeyX, Y = pairing.PeerPublicKeyY };

    /// The Sub keeps its rulebook-channel key and the Owner its report-channel key in different fields.
    private sealed class KeySlot
    {
        private readonly RulebookPairingState state;
        private readonly bool rulebookChannel;

        private KeySlot(RulebookPairingState state, bool rulebookChannel)
        {
            this.state = state;
            this.rulebookChannel = rulebookChannel;
        }

        public static KeySlot For(PairingState pairing, string channel) => new(pairing.Rulebook, channel == RulebookChannels.Rulebook);

        public string? Id => rulebookChannel ? state.ReceiveKeyId : state.ReportKeyId;

        /// The previous private scalar is overwritten and gone for good.
        public void Store(RulebookKeyEnvelope envelope, RelayEcKeyPair key)
        {
            var privateD = RelayCrypto.ExportPrivateD(key);
            var (protectedD, wasProtected) = DeviceIdentityService.Protect(privateD, ReceiveKeyEntropy);
            if (!ReferenceEquals(protectedD, privateD)) Array.Clear(privateD);
            if (rulebookChannel)
            {
                state.ReceiveKeyId = envelope.ReceiveKeyId;
                state.ReceiveKeyX = envelope.ReceivePublicKey.X;
                state.ReceiveKeyY = envelope.ReceivePublicKey.Y;
                state.ReceiveKeyPrivate = protectedD;
                state.ReceiveKeyPrivateProtected = wasProtected;
            }
            else
            {
                state.ReportKeyId = envelope.ReceiveKeyId;
                state.ReportKeyX = envelope.ReceivePublicKey.X;
                state.ReportKeyY = envelope.ReceivePublicKey.Y;
                state.ReportKeyPrivate = protectedD;
                state.ReportKeyPrivateProtected = wasProtected;
            }
        }

        public bool TryLoad(out RelayEcKeyPair key)
        {
            key = null!;
            var (x, y, d, prot) = rulebookChannel
                ? (state.ReceiveKeyX, state.ReceiveKeyY, state.ReceiveKeyPrivate, state.ReceiveKeyPrivateProtected)
                : (state.ReportKeyX, state.ReportKeyY, state.ReportKeyPrivate, state.ReportKeyPrivateProtected);
            if (Id is null || x is null || y is null || d is null)
                return false;
            try
            {
                var privateD = DeviceIdentityService.Unprotect(d, prot ?? false, ReceiveKeyEntropy);
                key = RelayCrypto.ImportEphemeralPrivateKey(new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = x, Y = y }, privateD);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Rulebook receive key could not be loaded; a fresh one will be published.");
                return false;
            }
        }
    }
}
