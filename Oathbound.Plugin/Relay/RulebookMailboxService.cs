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
    private readonly MailboxStatusService statuses;
    private readonly object gate = new();
    private readonly HashSet<(Guid, string)> inFlight = new();
    /// Pairings, channels and epochs this session already told the sender it posts receipts.
    private readonly HashSet<(Guid, string, int)> announced = new();

    public RulebookMailboxService(PluginConfig config, RelayClient relay, DeviceIdentityService identity, MailboxStatusService statuses)
    {
        this.config = config;
        this.relay = relay;
        this.identity = identity;
        this.statuses = statuses;
    }

    public static MailboxDeliveryState DeliveryFor(PairingState pairing, string channel) =>
        channel == RulebookChannels.Rulebook ? pairing.Rulebook.RulebookDelivery : pairing.Rulebook.ReportDelivery;

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

    /// From the pair status poll. Costs no extra request unless a key is missing, something is waiting, a receipt is
    /// unposted or the last item has to go again.
    public async Task ApplyPairStatusAsync(PairingState pairing, RulebookMailboxSummary summary, CancellationToken ct)
    {
        if (pairing is not { IsPaired: true, PairIdHash: { Length: > 0 } })
            return;
        var state = pairing.Rulebook;
        if (pairing.Direction == PairingDirection.OwnerSide)
        {
            state.SubSupportsRulebook = summary.Rulebook.Exists;
            state.PickedUpVersion = Math.Max(state.PickedUpVersion, summary.Rulebook.LastConsumedSequence ?? 0);
            var delivery = state.RulebookDelivery;
            MailboxStatusService.ObserveReceiver(pairing, delivery, RulebookChannels.Rulebook, summary.Rulebook.RecipientStatus,
                summary.Rulebook.WaitingSequence, summary.Rulebook.LastConsumedSequence);
            // A version the relay neither holds nor handed over was dropped when the Sub replaced its key; one the Sub's
            // plugin couldn't read goes again only when a fresh copy can help.
            if (summary.Rulebook.Exists && state.PublishedVersion > 0 && state.PublishedDocument is not null)
            {
                var sent = MailboxStatusService.Evaluate(delivery, state.PublishedVersion);
                var afterReject = sent == MailboxSentState.Rejected;
                if (sent == MailboxSentState.Lost || afterReject && MailboxStatusService.ShouldResend(delivery, state.PublishedVersion))
                {
                    Plugin.Log.Information(afterReject
                        ? $"Rulebook version {state.PublishedVersion} for {pairing.PeerName} couldn't be read ({delivery.PeerReceiptCode}) - sending it again."
                        : $"Rulebook version {state.PublishedVersion} for {pairing.PeerName} never arrived - sending it again.");
                    if (await ResendPublishedAsync(pairing, afterReject, ct).ConfigureAwait(false) is { } error)
                        Plugin.Log.Warning($"Rulebook resend for {pairing.PeerName} failed: {error}");
                }
            }
            await CollectAsync(pairing, RulebookChannels.Report, summary.Report, state.LastReportSequence, ct).ConfigureAwait(false);
        }
        else
        {
            var delivery = state.ReportDelivery;
            MailboxStatusService.ObserveReceiver(pairing, delivery, RulebookChannels.Report, summary.Report.RecipientStatus,
                summary.Report.WaitingSequence, summary.Report.LastConsumedSequence);
            // Same for a report: forgetting what was sent makes the next tick upload it again.
            if (summary.Report.Exists && state.NextReportSequence > 0)
            {
                var sent = MailboxStatusService.Evaluate(delivery, state.NextReportSequence);
                if (sent == MailboxSentState.Lost || MailboxStatusService.ShouldResend(delivery, state.NextReportSequence))
                {
                    state.LastUploadedReportDigest = null;
                    delivery.ResendAfterReject = sent == MailboxSentState.Rejected;
                }
            }
            await CollectAsync(pairing, RulebookChannels.Rulebook, summary.Rulebook, state.LastReceivedVersion, ct).ConfigureAwait(false);
        }
        config.SaveDisplayState();
    }

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
        var delivery = DeliveryFor(pairing, channel);
        try
        {
            identity.EnsureIdentity();
            MailboxStatusService.ObserveSender(pairing, delivery, channel, summary.SenderStatus);
            await statuses.FlushReceiptAsync(pairing, delivery, channel, ct).ConfigureAwait(false);

            var slot = KeySlot.For(pairing, channel);
            bool Ours(RulebookChannelSummary s) => s.Exists && (s.ReceiveKeyId == slot.Id || s.ReceiveKeyId == delivery.NextKeyId);
            if (!Ours(summary))
            {
                // The summary may predate this client's own latest rotation; only a fresh mismatch means the key is gone.
                if (await FreshSummaryAsync(pairing, channel, ct).ConfigureAwait(false) is not { } fresh)
                    return;
                summary = fresh;
            }

            if (delivery.NextKeyId is not null && summary.Exists && summary.ReceiveKeyId == delivery.NextKeyId && delivery.NextKeyItem > 0)
            {
                // The relay rotated to our stored next key, so the last pickup's response never arrived.
                await RetryLostPickupAsync(pairing, channel, slot, delivery.NextKeyItem, ct).ConfigureAwait(false);
                return;
            }
            if (!summary.Exists || summary.ReceiveKeyId != slot.Id || !slot.TryLoad(out var probe))
            {
                // Anything waiting was encrypted to a key we no longer hold; the sender's delivery check resends it.
                await PublishFreshKeyAsync(pairing, channel, slot, ct).ConfigureAwait(false);
                return;
            }
            probe.Dispose();

            if (summary.WaitingSequence is { } sequence && sequence > lastSeen)
            {
                await ConsumeAsync(pairing, channel, slot, sequence, ct).ConfigureAwait(false);
                return;
            }
            await AnnounceReceiptsAsync(pairing, channel, summary, lastSeen, ct).ConfigureAwait(false);
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

    /// Lets the sender know this client posts receipts, once per pair epoch, naming only what it already took.
    private async Task AnnounceReceiptsAsync(PairingState pairing, string channel, RulebookChannelSummary summary, int lastSeen, CancellationToken ct)
    {
        var delivery = DeliveryFor(pairing, channel);
        if (statuses.IsOurs(pairing, summary.RecipientStatus) || delivery.PendingReceiptSequence is not null)
            return;
        lock (gate)
            if (!announced.Add((pairing.Id, channel, pairing.PairEpoch)))
                return;
        var sequence = Math.Min(lastSeen, summary.LastConsumedSequence ?? 0);
        await statuses.PostReceiptAsync(pairing, delivery, channel, sequence, MailboxStatusCodes.Ok, ct).ConfigureAwait(false);
    }

    private async Task ConsumeAsync(PairingState pairing, string channel, KeySlot slot, int sequence, CancellationToken ct)
    {
        if (!slot.TryLoad(out var receiveKey))
            return;
        using var usedKey = receiveKey;
        var delivery = DeliveryFor(pairing, channel);
        var nextEnvelope = EnsureNextKey(pairing, channel);
        delivery.NextKeyItem = sequence;
        config.SaveNow();
        var (envelope, ciphertext) = await relay.ConsumeRulebookItemAsync(pairing.PairIdHash!, pairing.PairEpoch, channel, sequence, nextEnvelope, ct).ConfigureAwait(false);
        await SettleAsync(pairing, channel, slot, usedKey, sequence, envelope, ciphertext, ct).ConfigureAwait(false);
    }

    /// Repeats the pickup with the stored next key; an item the relay no longer holds gets a key_lost receipt, so the
    /// sender sends it again.
    private async Task RetryLostPickupAsync(PairingState pairing, string channel, KeySlot slot, int sequence, CancellationToken ct)
    {
        var delivery = DeliveryFor(pairing, channel);
        var nextEnvelope = MailboxStatusService.LoadNextKeyEnvelope<RulebookKeyEnvelope>(delivery);
        if (nextEnvelope is not null && slot.TryLoad(out var receiveKey))
        {
            using var usedKey = receiveKey;
            try
            {
                var (envelope, ciphertext) = await relay.ConsumeRulebookItemAsync(pairing.PairIdHash!, pairing.PairEpoch, channel, sequence, nextEnvelope, ct).ConfigureAwait(false);
                Plugin.Log.Information($"Rulebook {channel} from {pairing.PeerName}: finished an interrupted pickup of #{sequence}.");
                await SettleAsync(pairing, channel, slot, usedKey, sequence, envelope, ciphertext, ct).ConfigureAwait(false);
                return;
            }
            catch (RelayException ex) when (ex.Code == "not_found")
            {
            }
        }
        Plugin.Log.Warning($"Rulebook {channel} from {pairing.PeerName}: #{sequence} was lost with an interrupted pickup - asking for it again.");
        slot.Promote(delivery);
        RecordFailure(delivery, MailboxStatusCodes.KeyLost, sequence);
        config.SaveNow();
        await statuses.PostReceiptAsync(pairing, delivery, channel, sequence, MailboxStatusCodes.KeyLost, ct).ConfigureAwait(false);
    }

    /// Checks, decrypts and hands over one picked-up item, moves to the next key, and posts the receipt.
    private async Task SettleAsync(PairingState pairing, string channel, KeySlot slot, RelayEcKeyPair usedKey, int sequence, RulebookItemEnvelope envelope, byte[] ciphertext, CancellationToken ct)
    {
        var delivery = DeliveryFor(pairing, channel);
        var usedKeyId = slot.Id!;
        var maxCiphertext = channel == RulebookChannels.Rulebook ? RelayProtocolConstants.RulebookCiphertextMaxBytes : RelayProtocolConstants.RulebookReportCiphertextMaxBytes;
        var lastSeen = channel == RulebookChannels.Rulebook ? pairing.Rulebook.LastReceivedVersion : pairing.Rulebook.LastReportSequence;
        var (code, error) = VerifyItem(pairing, channel, envelope, ciphertext, usedKeyId, lastSeen, maxCiphertext);
        string? json = null;
        if (error is null)
        {
            try
            {
                var maxPlain = channel == RulebookChannels.Rulebook ? RulebookLimits.MaxPlaintextBytes : MaxReportPlaintextBytes;
                json = Encoding.UTF8.GetString(RulebookCrypto.Open(envelope, ciphertext, usedKey, channel, maxPlain));
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException)
            {
                (code, error) = (MailboxStatusCodes.DecryptFailed, $"failed to decrypt ({ex.GetType().Name})");
            }
        }
        if (json is not null)
        {
            code = await Plugin.Framework.RunOnFrameworkThread(() => channel == RulebookChannels.Rulebook
                ? AcceptIncomingRulebook(pairing, envelope.Sequence, json)
                : AcceptIncomingReport(pairing, envelope.Sequence, json)).ConfigureAwait(false);
        }
        else
        {
            Plugin.Log.Warning($"Rulebook {channel} item from {pairing.PeerName} ignored: {error}");
        }

        slot.Promote(delivery);
        if (code == MailboxStatusCodes.Ok)
            delivery.LastFailureCode = null;
        else
            RecordFailure(delivery, code, sequence);
        config.SaveNow();
        await statuses.PostReceiptAsync(pairing, delivery, channel, sequence, code, ct).ConfigureAwait(false);
    }

    private static void RecordFailure(MailboxDeliveryState delivery, string code, int sequence)
    {
        delivery.LastFailureCode = code;
        delivery.LastFailureSequence = sequence;
        delivery.LastFailureAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// Every check passes before a single byte is decrypted. The code is the receipt to post when one fails.
    private (string Code, string? Error) VerifyItem(PairingState pairing, string channel, RulebookItemEnvelope envelope, byte[] ciphertext, string usedKeyId, int lastSeen, int maxCiphertext)
    {
        const string failed = MailboxStatusCodes.VerifyFailed;
        if (envelope.Type != RulebookChannels.ItemType(channel))
            return (failed, "wrong item type for this channel");
        if (envelope.PairIdHash != pairing.PairIdHash || envelope.PairEpoch != pairing.PairEpoch)
            return (failed, "addressed to a different pair/epoch");
        if (envelope.RecipientDeviceKeyId != identity.DeviceKeyId || envelope.SenderDeviceKeyId != pairing.PeerDeviceKeyId)
            return (failed, "sender/recipient device keys don't match this pairing");
        if (envelope.ReceiveKeyId != usedKeyId)
            return (failed, "encrypted to a different receive key");
        if (envelope.Sequence <= lastSeen)
            return (MailboxStatusCodes.Stale, "not newer than the last one received (stale or replayed)");
        if (envelope.CiphertextSizeBytes > maxCiphertext || ciphertext.Length != envelope.CiphertextSizeBytes)
            return (failed, "invalid ciphertext size");
        if (RelayCrypto.Sha256Hex(ciphertext) != envelope.CiphertextDigest)
            return (failed, "ciphertext digest mismatch");
        if (PeerKey(pairing) is not { } peerKey)
            return (failed, "no peer public key on file");
        if (!RelayCrypto.VerifyRaw(peerKey, envelope.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(envelope)))
            return (failed, "signature didn't verify against the paired peer's key");
        return (MailboxStatusCodes.Ok, null);
    }

    /// A version that can't be offered is refused here with its receipt code; "declined" stays the Sub's own decision.
    private string AcceptIncomingRulebook(PairingState pairing, int sequence, string json)
    {
        var state = pairing.Rulebook;
        state.LastReceivedVersion = sequence;
        RulebookDocument? doc = null;
        string? reason = null;
        var code = MailboxStatusCodes.ImportFailed;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (!parsed.RootElement.TryGetProperty("schemaVersion", out var sv) || sv.GetInt32() < RulebookDocument.MinSchemaVersion)
                reason = "it couldn't be read";
            else if (sv.GetInt32() > RulebookDocument.CurrentSchemaVersion)
                (reason, code) = ("it needs a newer plugin version", MailboxStatusCodes.NeedsUpdate);
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
            return code;
        }

        state.Pending = doc;
        state.PendingReceivedUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        config.SaveNow();
        RulebookReceived?.Invoke(pairing, doc);
        return MailboxStatusCodes.Ok;
    }

    private string AcceptIncomingReport(PairingState pairing, int sequence, string json)
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
            Plugin.Log.Warning($"Rulebook report #{sequence} from {pairing.PeerName} couldn't be read.");
            config.SaveNow();
            return MailboxStatusCodes.ImportFailed;
        }
        pairing.Rulebook.LastReport = report;
        pairing.Rulebook.LastReportReceivedUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        config.SaveNow();
        ReportReceived?.Invoke(pairing, report);
        return MailboxStatusCodes.Ok;
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
        return await SendDocumentAsync(pairing, pairing.Rulebook.Draft.Clone(), updateDigest: true, resendAfterReject: false, ct).ConfigureAwait(false);
    }

    /// The last sent version again, under a new version number, unchanged.
    private Task<string?> ResendPublishedAsync(PairingState pairing, bool afterReject, CancellationToken ct) =>
        pairing.Rulebook.PublishedDocument is { } published
            ? SendDocumentAsync(pairing, published.Clone(), updateDigest: false, afterReject, ct)
            : Task.FromResult<string?>("nothing was sent before");

    private async Task<string?> SendDocumentAsync(PairingState pairing, RulebookDocument doc, bool updateDigest, bool resendAfterReject, CancellationToken ct)
    {
        var result = await SendAsync(pairing, RulebookChannels.Rulebook, pairing.Rulebook.PublishedVersion, sequence =>
        {
            doc.Version = sequence;
            doc.SchemaVersion = doc.RequiredSchemaVersion;
            return Encoding.UTF8.GetBytes(RulebookJson.Serialize(doc));
        }, ct).ConfigureAwait(false);

        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var state = pairing.Rulebook;
            if (result.Error is not null)
            {
                state.PublishError = updateDigest ? result.Error : $"Sending version {state.PublishedVersion} again failed: {result.Error}";
            }
            else
            {
                state.PublishedVersion = result.Sequence;
                state.PublishedDocument = doc;
                state.PublishedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (updateDigest)
                    state.PublishedDigest = DraftDigest(doc);
                state.PublishError = null;
                MailboxStatusService.RecordSent(state.RulebookDelivery, DraftDigest(doc), resendAfterReject);
            }
            config.SaveNow();
        }).ConfigureAwait(false);
        return result.Error;
    }

    /// Sub: upload a report. Null on success, else why it couldn't be sent (rate limit, no key yet, network...).
    public async Task<string?> UploadReportAsync(PairingState pairing, RulebookReport report, CancellationToken ct)
    {
        if (pairing is not { Direction: PairingDirection.SubSide, IsPaired: true, PairIdHash: { Length: > 0 } })
            return "This pairing isn't active.";
        var bytes = Encoding.UTF8.GetBytes(RulebookJson.Serialize(report));
        var result = await SendAsync(pairing, RulebookChannels.Report, pairing.Rulebook.NextReportSequence, _ => bytes, ct).ConfigureAwait(false);
        if (result.Error is not null)
            return result.Error;
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var state = pairing.Rulebook;
            state.NextReportSequence = result.Sequence;
            state.LastReportUploadUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            MailboxStatusService.RecordSent(state.ReportDelivery, ContentDigest(report), state.ReportDelivery.ResendAfterReject);
            state.ReportDelivery.ResendAfterReject = false;
            config.SaveNow();
        }).ConfigureAwait(false);
        return null;
    }

    /// The report without its timestamp, so the same content resent later counts as the same content.
    private static string ContentDigest(RulebookReport report)
    {
        var generated = report.GeneratedAt;
        report.GeneratedAt = 0;
        var digest = RelayCrypto.Sha256Hex(RulebookJson.Serialize(report));
        report.GeneratedAt = generated;
        return digest;
    }

    public static string DraftDigest(RulebookDocument draft)
    {
        var copy = draft.Clone();
        copy.Version = 0;
        copy.SchemaVersion = 0;
        return RelayCrypto.Sha256Hex(RulebookJson.Serialize(copy));
    }

    private readonly record struct SendResult(int Sequence, string? Error);

    /// Sends, then tells the recipient's plugin whether this side can send (a retry-worthy rate limit or a missing key
    /// isn't a problem to report).
    private async Task<SendResult> SendAsync(PairingState pairing, string channel, int lastLocalSequence, Func<int, byte[]> buildPlaintext, CancellationToken ct)
    {
        var (result, code) = await TrySendAsync(pairing, channel, lastLocalSequence, buildPlaintext, ct).ConfigureAwait(false);
        if (code is not null)
            await statuses.SetSenderStatusAsync(pairing, DeliveryFor(pairing, channel), channel, code, code == MailboxStatusCodes.Ok ? result.Sequence : 0, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<(SendResult Result, string? StatusCode)> TrySendAsync(PairingState pairing, string channel, int lastLocalSequence, Func<int, byte[]> buildPlaintext, CancellationToken ct)
    {
        lock (gate)
            if (!inFlight.Add((pairing.Id, "send-" + channel)))
                return (new(0, "Already sending - try again in a moment."), null);
        try
        {
            identity.EnsureIdentity();
            if (PeerKey(pairing) is not { } peerKey || pairing.PeerDeviceKeyId is null)
                return (new(0, "No peer key on file for this pairing."), null);

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
                    return (new(0, channel == RulebookChannels.Rulebook
                        ? $"{pairing.PeerName}'s plugin doesn't support rulebooks yet."
                        : "The Owner's plugin hasn't set up rulebook reports yet."), null);
                }

                var key = info.Key;
                if (key.Channel != channel || key.PairIdHash != pairing.PairIdHash || key.PairEpoch != pairing.PairEpoch ||
                    key.RecipientDeviceKeyId != pairing.PeerDeviceKeyId ||
                    !RelayCrypto.VerifyRaw(peerKey, key.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(key)))
                {
                    // Never encrypt to a key the paired peer didn't sign.
                    Plugin.Log.Warning($"Rulebook {channel} key for {pairing.PeerName} didn't verify against the paired peer - not sending.");
                    return (new(0, "The relay returned a key your peer didn't sign - not sent."), MailboxStatusCodes.KeyUnverified);
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
                    return (new(0, "Too large to send - remove some rules or cards."), MailboxStatusCodes.TooLarge);
                }
                envelope.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(envelope));

                try
                {
                    await relay.UploadRulebookItemAsync(envelope, ciphertext, ct).ConfigureAwait(false);
                    return (new(sequence, null), MailboxStatusCodes.Ok);
                }
                catch (RelayException ex) when (ex.Code == "expired" && attempt == 0)
                {
                    continue;
                }
            }
            return (new(0, "The key kept changing - try again."), null);
        }
        catch (RelayException ex)
        {
            Plugin.Log.Warning($"Rulebook {channel} send to {pairing.PeerName} failed: {ex.Code}.");
            var message = ex.Code switch
            {
                "rate_limited" => $"Sent too recently - try again in {ex.RetryAfterSeconds ?? 60} seconds.",
                "network" => "Couldn't reach the relay - try again later.",
                "unauthorized" => "The relay rejected this (is the pairing still active?).",
                "payload_too_large" => "Too large to send - remove some rules or cards.",
                _ => "Sending failed - try again later.",
            };
            var code = ex.Code switch
            {
                "rate_limited" => null,
                "payload_too_large" => MailboxStatusCodes.TooLarge,
                _ => MailboxStatusCodes.UploadFailed,
            };
            return (new(0, message), code);
        }
        catch (OperationCanceledException)
        {
            return (new(0, "Cancelled."), null);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Rulebook {channel} send failed unexpectedly.");
            return (new(0, "Sending failed unexpectedly - see /xllog."), MailboxStatusCodes.UploadFailed);
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
        DeliveryFor(pairing, channel).ClearNextKey();
        config.SaveNow();
        Plugin.Log.Debug($"Rulebook {channel} mailbox for {pairing.PeerName}: published a fresh receive key.");
    }

    /// The next key is generated once and reused until a pickup with it settles.
    private RulebookKeyEnvelope EnsureNextKey(PairingState pairing, string channel)
    {
        var delivery = DeliveryFor(pairing, channel);
        if (delivery.NextKeyId is not null && MailboxStatusService.LoadNextKeyEnvelope<RulebookKeyEnvelope>(delivery) is { } stored
            && stored.ReceiveKeyId == delivery.NextKeyId && stored.PairEpoch == pairing.PairEpoch && stored.Channel == channel)
            return stored;
        using var nextKey = RelayCrypto.GenerateEphemeralKeyPair();
        var envelope = BuildSignedKeyEnvelope(pairing, channel, nextKey);
        MailboxStatusService.StoreNextKey(delivery, JsonSerializer.Serialize(envelope), envelope.ReceiveKeyId, envelope.ReceivePublicKey, nextKey, ReceiveKeyEntropy);
        config.SaveNow();
        return envelope;
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

        /// The relay is on the stored next key now; it becomes this slot's key and the old private scalar is cleared.
        public void Promote(MailboxDeliveryState delivery)
        {
            if (delivery.NextKeyId is null || delivery.NextKeyPrivate is null)
                return;
            if (rulebookChannel)
            {
                if (state.ReceiveKeyPrivate is { } old) Array.Clear(old);
                state.ReceiveKeyId = delivery.NextKeyId;
                state.ReceiveKeyX = delivery.NextKeyX;
                state.ReceiveKeyY = delivery.NextKeyY;
                state.ReceiveKeyPrivate = delivery.NextKeyPrivate;
                state.ReceiveKeyPrivateProtected = delivery.NextKeyPrivateProtected;
            }
            else
            {
                if (state.ReportKeyPrivate is { } old) Array.Clear(old);
                state.ReportKeyId = delivery.NextKeyId;
                state.ReportKeyX = delivery.NextKeyX;
                state.ReportKeyY = delivery.NextKeyY;
                state.ReportKeyPrivate = delivery.NextKeyPrivate;
                state.ReportKeyPrivateProtected = delivery.NextKeyPrivateProtected;
            }
            delivery.NextKeyPrivate = null;
            delivery.ClearNextKey();
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
