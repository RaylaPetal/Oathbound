using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// Where the receiving side's last item stands, from the sender's point of view.
public enum MailboxSentState
{
    /// Nothing sent yet.
    None,
    /// On the relay, not picked up.
    Waiting,
    /// Picked up by a receiver that posts receipts, whose receipt hasn't arrived.
    AwaitingConfirmation,
    /// Picked up by a receiver from before receipts; the best confirmation there is.
    PickedUp,
    /// The receiver's plugin acknowledged it.
    Delivered,
    /// The receiver's plugin couldn't take it; see the receipt code.
    Rejected,
    /// Neither waiting nor picked up: the receiver replaced its key, or it expired unread.
    Lost,
}

/// Receipts and sender statuses for every mailbox: builds and signs them, posts each at most once per change,
/// keeps an unposted receipt for the next check, and verifies the other side's against the paired device key.
public sealed class MailboxStatusService
{
    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly DeviceIdentityService identity;
    private readonly object gate = new();
    private readonly HashSet<(Guid, string, string)> inFlight = new();
    private readonly Dictionary<(Guid, string), DateTime> notBefore = new();
    /// When each status first came back "unauthorized". The relay says that for a clock off by minutes or a stale device
    /// key cache as well as for an ended pairing, so it's retried for a while before being given up.
    private readonly Dictionary<(Guid, string, string), DateTime> unauthorizedSince = new();
    private static readonly TimeSpan UnauthorizedRetryFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan UnauthorizedRetryEvery = TimeSpan.FromMinutes(5);

    public MailboxStatusService(PluginConfig config, RelayClient relay, DeviceIdentityService identity)
    {
        this.config = config;
        this.relay = relay;
        this.identity = identity;
    }

    /// The peer's status, only if it is a well-formed status for this pairing, mailbox and role, from the paired device.
    public static MailboxStatusEnvelope? VerifyPeer(PairingState pairing, MailboxStatusEnvelope? status, string mailbox, string role)
    {
        if (status is null || pairing.PeerDeviceKeyId is null || pairing.PeerPublicKeyX is null || pairing.PeerPublicKeyY is null)
            return null;
        if (status.Type != "mailbox-status" || status.SchemaVersion != 1 || status.PairIdHash != pairing.PairIdHash || status.PairEpoch != pairing.PairEpoch
            || status.Mailbox != mailbox || status.Role != role || status.DeviceKeyId != pairing.PeerDeviceKeyId)
            return null;
        var peerKey = new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = pairing.PeerPublicKeyX, Y = pairing.PeerPublicKeyY };
        return RelayCrypto.VerifyRaw(peerKey, status.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(status)) ? status : null;
    }

    /// True when the relay already holds a receipt of ours for this mailbox and epoch.
    public bool IsOurs(PairingState pairing, MailboxStatusEnvelope? status) =>
        status is not null && status.PairEpoch == pairing.PairEpoch && status.DeviceKeyId == identity.DeviceKeyId;

    /// Sender side: records what the pair status says about the item we last sent.
    public static void ObserveReceiver(PairingState pairing, MailboxDeliveryState state, string mailbox, MailboxStatusEnvelope? recipientStatus, int? waiting, int? consumed)
    {
        state.RelayWaiting = waiting ?? 0;
        state.RelayConsumed = consumed ?? 0;
        if (state.PeerSendsReceiptsEpoch != pairing.PairEpoch)
        {
            state.PeerSendsReceipts = false;
            state.PeerSendsReceiptsEpoch = pairing.PairEpoch;
            state.PeerReceiptCode = null;
            state.PeerReceiptSequence = 0;
            state.PeerReceiptAt = 0;
        }
        if (VerifyPeer(pairing, recipientStatus, mailbox, MailboxRoles.Recipient) is not { } receipt)
            return;
        state.PeerSendsReceipts = true;
        state.PeerReceiptCode = receipt.Code;
        state.PeerReceiptSequence = receipt.Sequence;
        state.PeerReceiptAt = receipt.At;
    }

    /// Receiver side: records the sender's latest can't-send status.
    public static void ObserveSender(PairingState pairing, MailboxDeliveryState state, string mailbox, MailboxStatusEnvelope? senderStatus)
    {
        if (VerifyPeer(pairing, senderStatus, mailbox, MailboxRoles.Sender) is { } status)
        {
            state.PeerSenderCode = status.Code;
            state.PeerSenderAt = status.At;
        }
        else if (senderStatus is null)
        {
            state.PeerSenderCode = null;
            state.PeerSenderAt = 0;
        }
    }

    /// Where `lastSent` stands. A receiver that posts receipts is trusted only through them.
    public static MailboxSentState Evaluate(MailboxDeliveryState state, int lastSent)
    {
        if (lastSent <= 0)
            return MailboxSentState.None;
        if (state.PeerSendsReceipts && state.PeerReceiptSequence >= lastSent && state.PeerReceiptCode is { } code)
            return code == MailboxStatusCodes.Ok ? MailboxSentState.Delivered : MailboxSentState.Rejected;
        if (state.RelayWaiting >= lastSent)
            return MailboxSentState.Waiting;
        if (state.RelayConsumed >= lastSent)
            return state.PeerSendsReceipts ? MailboxSentState.AwaitingConfirmation : MailboxSentState.PickedUp;
        return MailboxSentState.Lost;
    }

    /// A reject a fresh copy can fix, with automatic resends left for this content.
    public static bool ShouldResend(MailboxDeliveryState state, int lastSent) =>
        Evaluate(state, lastSent) == MailboxSentState.Rejected && MailboxStatusCodes.IsResendable(state.PeerReceiptCode!)
        && state.ConsecutiveResends < MailboxStatusCodes.MaxConsecutiveResends;

    /// Counts a successful send toward the resend limit (a resend of the same content) or resets it (new content).
    public static void RecordSent(MailboxDeliveryState state, string digest, bool resendAfterReject)
    {
        if (resendAfterReject && digest == state.ResendDigest)
        {
            state.ConsecutiveResends++;
            return;
        }
        state.ResendDigest = digest;
        state.ConsecutiveResends = 0;
    }

    // ---- Receipts ----

    /// Records the receipt, then posts it; an unposted one is retried by FlushReceiptAsync.
    public async Task PostReceiptAsync(PairingState pairing, MailboxDeliveryState state, string mailbox, int sequence, string code, CancellationToken ct)
    {
        state.PendingReceiptSequence = sequence;
        state.PendingReceiptCode = code;
        config.SaveNow();
        await FlushReceiptAsync(pairing, state, mailbox, ct).ConfigureAwait(false);
    }

    public async Task FlushReceiptAsync(PairingState pairing, MailboxDeliveryState state, string mailbox, CancellationToken ct)
    {
        if (state.PendingReceiptSequence is not { } sequence || state.PendingReceiptCode is not { } code)
            return;
        var outcome = await PostAsync(pairing, mailbox, MailboxRoles.Recipient, sequence, code, ct).ConfigureAwait(false);
        if (outcome == PostOutcome.Retry)
            return;
        if (state.PendingReceiptSequence == sequence && state.PendingReceiptCode == code)
        {
            state.PendingReceiptSequence = null;
            state.PendingReceiptCode = null;
            config.SaveNow();
        }
    }

    // ---- Sender statuses ----

    /// Posts `code` unless the relay already holds it; `ok` only clears an earlier problem.
    public async Task SetSenderStatusAsync(PairingState pairing, MailboxDeliveryState state, string mailbox, string code, int sequence, CancellationToken ct)
    {
        if (code == MailboxStatusCodes.Ok ? state.PostedSenderCode is null or MailboxStatusCodes.Ok
                                          : state.PostedSenderCode == code && state.PostedSenderSequence == sequence)
            return;
        if (await PostAsync(pairing, mailbox, MailboxRoles.Sender, sequence, code, ct).ConfigureAwait(false) == PostOutcome.Stored)
        {
            state.PostedSenderCode = code;
            state.PostedSenderSequence = sequence;
            config.Save();
        }
    }

    /// Fire-and-forget form for callers on the framework thread.
    public void SetSenderStatus(PairingState pairing, MailboxDeliveryState state, string mailbox, string code, int sequence, CancellationToken ct) =>
        Plugin.FireAndForget(SetSenderStatusAsync(pairing, state, mailbox, code, sequence, ct));

    private enum PostOutcome
    {
        Stored,
        /// The relay can't take it and never will (an older relay, or a status it refuses); dropped.
        Dropped,
        Retry,
    }

    private async Task<PostOutcome> PostAsync(PairingState pairing, string mailbox, string role, int sequence, string code, CancellationToken ct)
    {
        if (pairing is not { IsPaired: true, PairIdHash: { Length: > 0 } pairIdHash })
            return PostOutcome.Dropped;
        var key = (pairing.Id, mailbox, role);
        lock (gate)
        {
            if (notBefore.TryGetValue((pairing.Id, mailbox + role), out var wait) && DateTime.UtcNow < wait)
                return PostOutcome.Retry;
            if (!inFlight.Add(key))
                return PostOutcome.Retry;
        }
        try
        {
            identity.EnsureIdentity();
            var status = new MailboxStatusEnvelope
            {
                PairIdHash = pairIdHash,
                PairEpoch = pairing.PairEpoch,
                Mailbox = mailbox,
                Role = role,
                DeviceKeyId = identity.DeviceKeyId!,
                Sequence = sequence,
                Code = code,
                At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            status.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(status));
            await relay.PostMailboxStatusAsync(status, ct).ConfigureAwait(false);
            Plugin.Log.Debug($"Mailbox {mailbox} {role} status for {pairing.PeerName}: {code} #{sequence}.");
            lock (gate) unauthorizedSince.Remove(key);
            return PostOutcome.Stored;
        }
        catch (RelayException ex) when (ex.Code == "unauthorized")
        {
            var now = DateTime.UtcNow;
            lock (gate)
            {
                if (!unauthorizedSince.TryGetValue(key, out var since))
                    unauthorizedSince[key] = since = now;
                if (now - since < UnauthorizedRetryFor)
                {
                    notBefore[(pairing.Id, mailbox + role)] = now + UnauthorizedRetryEvery;
                    Plugin.Log.Information($"Mailbox {mailbox} {role} status for {pairing.PeerName} refused as unauthorized (check the PC clock); will retry.");
                    return PostOutcome.Retry;
                }
                unauthorizedSince.Remove(key);
            }
            Plugin.Log.Information($"Mailbox {mailbox} {role} status for {pairing.PeerName} still unauthorized after an hour; dropped.");
            return PostOutcome.Dropped;
        }
        catch (RelayException ex) when (ex.Code is "not_found" or "invalid_request")
        {
            Plugin.Log.Information($"Mailbox {mailbox} {role} status for {pairing.PeerName} not accepted ({ex.Code}); dropped.");
            return PostOutcome.Dropped;
        }
        catch (RelayException ex)
        {
            if (ex.Code == "rate_limited")
                lock (gate) notBefore[(pairing.Id, mailbox + role)] = DateTime.UtcNow.AddSeconds(Math.Max(ex.RetryAfterSeconds ?? 60, 1));
            Plugin.Log.Information($"Mailbox {mailbox} {role} status for {pairing.PeerName} not sent ({ex.Code}); will retry.");
            return PostOutcome.Retry;
        }
        catch (OperationCanceledException)
        {
            return PostOutcome.Retry;
        }
        finally
        {
            lock (gate) inFlight.Remove(key);
        }
    }

    // ---- Next receive key, held across a pickup ----

    public static void StoreNextKey(MailboxDeliveryState state, string envelopeJson, string keyId, EcPublicKeyJwk publicKey, RelayEcKeyPair key, byte[] entropy)
    {
        var privateD = RelayCrypto.ExportPrivateD(key);
        var (protectedD, wasProtected) = DeviceIdentityService.Protect(privateD, entropy);
        if (!ReferenceEquals(protectedD, privateD)) Array.Clear(privateD);
        state.ClearNextKey();
        state.NextKeyId = keyId;
        state.NextKeyX = publicKey.X;
        state.NextKeyY = publicKey.Y;
        state.NextKeyPrivate = protectedD;
        state.NextKeyPrivateProtected = wasProtected;
        state.NextKeyEnvelopeJson = envelopeJson;
    }

    public static T? LoadNextKeyEnvelope<T>(MailboxDeliveryState state) where T : class
    {
        if (state.NextKeyEnvelopeJson is not { } json)
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
