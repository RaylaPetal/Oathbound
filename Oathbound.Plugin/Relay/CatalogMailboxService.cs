using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.ImGuiNotification;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// Drives CatalogAutoSync's retry timing.
public enum MailboxPublishOutcome
{
    /// Uploaded, or already delivered.
    Published,
    /// No Owner receive key yet, or sync permission off. Retried later, never surfaced as an error.
    NotReady,
    /// Retry after RetryAfterSeconds.
    RateLimited,
    /// Retried after the next pair status poll (or the hourly fallback pass).
    Failed,
}

/// `Error` is a short reason shown on the Sub's Sync module, for NotReady and Failed.
public readonly record struct MailboxPublishResult(MailboxPublishOutcome Outcome, int RetryAfterSeconds = 0, string? Error = null);

/// Automatic sync over the per-pair relay mailbox: the Sub pushes on change, the Owner collects what the pair status poll reports waiting.
/// No chat on this path. Timing lives in CatalogAutoSync; this is only the relay/crypto work.
public sealed class CatalogMailboxService
{
    private static readonly byte[] ReceiveKeyEntropy = "oathbound-mailbox-receive-key-v1"u8.ToArray();

    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly DeviceIdentityService identity;
    private readonly CatalogSyncService catalogSync;
    private readonly CatalogPictureService pictures;
    private readonly MailboxStatusService statuses;

    private readonly object gate = new();
    /// Pairings and epochs this session already told the Sub's plugin it posts receipts.
    private readonly HashSet<(Guid, int)> announced = new();
    private readonly HashSet<Guid> checksInFlight = new();
    private readonly HashSet<Guid> importing = new();
    private readonly HashSet<Guid> publishesInFlight = new();

    public CatalogMailboxService(PluginConfig config, RelayClient relay, DeviceIdentityService identity, CatalogSyncService catalogSync, CatalogPictureService pictures, MailboxStatusService statuses)
    {
        this.statuses = statuses;
        this.config = config;
        this.relay = relay;
        this.identity = identity;
        this.catalogSync = catalogSync;
        this.pictures = pictures;
    }

    public bool IsSyncing(Guid pairingId) { lock (gate) return importing.Contains(pairingId); }

    public bool IsChecking(Guid pairingId) { lock (gate) return checksInFlight.Contains(pairingId); }

    // ---- Sub side ----

    /// `force` publishes even an unchanged catalog the relay shows as delivered (a redelivery or a Get latest).
    public async Task<MailboxPublishResult> PublishAsync(PairingState pairing, string exportText, string digest, bool force, CancellationToken ct)
    {
        if (pairing is not { Direction: PairingDirection.SubSide, IsPaired: true, PairIdHash: { Length: > 0 } pairIdHash } ||
            pairing.PeerDeviceKeyId is null || pairing.PeerPublicKeyX is null || pairing.PeerPublicKeyY is null)
            return new(MailboxPublishOutcome.Failed, Error: "This pairing has no Owner device key on file - pair again to sync automatically.");
        lock (gate)
            if (!publishesInFlight.Add(pairing.Id))
                return new(MailboxPublishOutcome.NotReady);
        var delivery = pairing.CatalogDelivery;
        try
        {
            identity.EnsureIdentity();
            var peerPublicKey = new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = pairing.PeerPublicKeyX, Y = pairing.PeerPublicKeyY };

            // The second pass only if the Owner rotated its key between our fetch and upload.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                CatalogMailboxKeyInfo info;
                try
                {
                    info = await relay.FetchMailboxKeyAsync(pairIdHash, pairing.PairEpoch, ct).ConfigureAwait(false);
                }
                catch (RelayException ex) when (ex.Code == "not_found")
                {
                    return new(MailboxPublishOutcome.NotReady, Error: "Waiting for your Owner's plugin to set up automatic sync.");
                }

                var key = info.Key;
                if (key.PairIdHash != pairIdHash || key.PairEpoch != pairing.PairEpoch || key.OwnerDeviceKeyId != pairing.PeerDeviceKeyId ||
                    !RelayCrypto.VerifyRaw(peerPublicKey, key.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(key)))
                {
                    // Never encrypt to a key the paired Owner didn't sign.
                    Plugin.Log.Warning($"Catalog mailbox for {pairing.PeerName}: receive key did not verify against the paired Owner - not publishing.");
                    await statuses.SetSenderStatusAsync(pairing, delivery, MailboxNames.Catalog, MailboxStatusCodes.KeyUnverified, 0, ct).ConfigureAwait(false);
                    return new(MailboxPublishOutcome.Failed, Error: "Your Owner's sync key didn't verify - ask them to use Get latest.");
                }

                // Same catalog as last time and the relay confirms delivery: nothing to do.
                var delivered = pairing.LastPublishedMailboxSnapshotId > 0 &&
                    (info.WaitingSnapshotId >= pairing.LastPublishedMailboxSnapshotId || info.LastConsumedSnapshotId >= pairing.LastPublishedMailboxSnapshotId);
                if (!force && digest == pairing.LastPublishedCatalogDigest && delivered)
                    return new(MailboxPublishOutcome.Published);

                // Pictures first, so the catalog never points at one the Owner can't fetch yet.
                if (attempt == 0)
                    await pictures.SyncBeforePublishAsync(pairing, ct).ConfigureAwait(false);

                try
                {
                    await EncryptAndUploadAsync(pairing, key, exportText, ct).ConfigureAwait(false);
                }
                catch (RelayException ex) when (ex.Code == "expired" && attempt == 0)
                {
                    continue; // Rotated under us - fetch the new key and publish again.
                }

                pairing.LastPublishedCatalogDigest = digest;
                pairing.LastPublishedCatalogUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                config.SaveNow();
                await statuses.SetSenderStatusAsync(pairing, delivery, MailboxNames.Catalog, MailboxStatusCodes.Ok, pairing.LastPublishedMailboxSnapshotId, ct).ConfigureAwait(false);
                return new(MailboxPublishOutcome.Published);
            }
            return new(MailboxPublishOutcome.Failed, Error: "Your Owner's sync key changed twice during the upload.");
        }
        catch (RelayException ex) when (ex.Code is "rate_limited" or "cooldown_active")
        {
            return new(MailboxPublishOutcome.RateLimited, Math.Max(ex.RetryAfterSeconds ?? 60, 1));
        }
        catch (RelayException ex)
        {
            Plugin.Log.Warning($"Catalog mailbox publish for {pairing.PeerName} failed: {ex.Code}.");
            var code = ex.Code == "payload_too_large" ? MailboxStatusCodes.TooLarge : MailboxStatusCodes.UploadFailed;
            await statuses.SetSenderStatusAsync(pairing, delivery, MailboxNames.Catalog, code, 0, ct).ConfigureAwait(false);
            return new(MailboxPublishOutcome.Failed, Error: DescribePublishError(ex));
        }
        catch (InvalidDataException ex)
        {
            Plugin.Log.Warning(ex.Message);
            await statuses.SetSenderStatusAsync(pairing, delivery, MailboxNames.Catalog, MailboxStatusCodes.TooLarge, 0, ct).ConfigureAwait(false);
            return new(MailboxPublishOutcome.Failed, Error: ex.Message);
        }
        finally
        {
            lock (gate) publishesInFlight.Remove(pairing.Id);
        }
    }

    private async Task EncryptAndUploadAsync(PairingState pairing, CatalogMailboxKeyEnvelope key, string exportText, CancellationToken ct)
    {
        var plaintext = System.Text.Encoding.UTF8.GetBytes(exportText);
        if (plaintext.Length > RelayProtocolConstants.CatalogPlaintextMaxBytes)
            throw new InvalidDataException("Catalog exceeds the local plaintext limit and was not published.");
        var compressed = RelayCompression.Compress(plaintext);
        byte[]? ciphertext = null;
        try
        {
            using var subEphemeral = RelayCrypto.GenerateEphemeralKeyPair();
            using var ownerReceivePublic = RelayCrypto.ImportEphemeralPublicKey(key.ReceivePublicKey);
            var sharedSecret = RelayCrypto.DeriveSharedSecret(subEphemeral, ownerReceivePublic);
            var salt = SHA256.HashData([.. RelayCrypto.ExportRawUncompressedPoint(key.ReceivePublicKey), .. RelayCrypto.ExportRawUncompressedPoint(subEphemeral)]);
            var aesKey = RelayCrypto.DeriveAesKey(sharedSecret, salt, RelayCrypto.BuildCatalogPushHkdfInfo(key.PairIdHash, key.ReceiveKeyId));
            var nonceBytes = RelayCrypto.RandomBytes(RelayCrypto.AeadNonceLengthBytes);

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var envelope = new CatalogPushEnvelope
            {
                PairIdHash = key.PairIdHash,
                PairEpoch = key.PairEpoch,
                ReceiveKeyId = key.ReceiveKeyId,
                SenderDeviceKeyId = identity.DeviceKeyId!,
                RecipientDeviceKeyId = key.OwnerDeviceKeyId,
                CreatedAt = now,
                ExpiresAt = now + RelayProtocolConstants.CatalogMailboxExpirySeconds,
                Nonce = RelayCrypto.Base64UrlEncode(nonceBytes),
                SenderEphemeralPublicKey = RelayCrypto.ExportPublicKeyJwk(subEphemeral),
            };

            // Checked before a snapshot id is spent. GCM output is always input + 16 bytes, so this is exact.
            if (compressed.Length + 16 > RelayProtocolConstants.CatalogCiphertextMaxBytes)
                throw new InvalidDataException("Catalog exceeds the encrypted upload limit and was not published.");

            // The id is bound into the AAD, so it's settled before the one encryption.
            envelope.SnapshotId = ++pairing.NextOutgoingSnapshotId;
            config.SaveNow();
            ciphertext = RelayCrypto.AesGcmEncrypt(aesKey, nonceBytes, compressed, CatalogPushAad.Build(envelope));
            envelope.CiphertextSizeBytes = ciphertext.Length;
            envelope.CiphertextDigest = RelayCrypto.Sha256Hex(ciphertext);
            envelope.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(envelope));

            await relay.UploadMailboxSnapshotAsync(envelope, ciphertext, ct).ConfigureAwait(false);
            pairing.LastPublishedMailboxSnapshotId = envelope.SnapshotId;
        }
        finally
        {
            // Best-effort scrub.
            Array.Clear(compressed);
            if (ciphertext is not null) Array.Clear(ciphertext);
        }
    }

    // ---- Owner side ----

    /// Publishes a receive key if needed, and imports a newer snapshot (then rotates the key). Never throws.
    /// Also retries any of the Sub's pictures that didn't download with the last import.
    public async Task CheckAsync(PairingState pairing, CancellationToken ct)
    {
        await RunOwnerCheckAsync(pairing, null, getLatest: false, ct).ConfigureAwait(false);
        await pictures.FetchMissingAsync(pairing, ct).ConfigureAwait(false);
    }

    /// Get latest: collects anything waiting, then publishes a fresh key marked as a refresh request, which makes the
    /// Sub's plugin publish its catalog now. Null when asked; otherwise why it couldn't ask.
    public async Task<string?> GetLatestAsync(PairingState pairing, CancellationToken ct)
    {
        var result = await RunOwnerCheckAsync(pairing, null, getLatest: true, ct).ConfigureAwait(false);
        await pictures.FetchMissingAsync(pairing, ct).ConfigureAwait(false);
        return result;
    }

    /// From the pair status poll: no mailbox request at all unless a key needs publishing, a pickup needs finishing,
    /// a receipt needs posting or a newer snapshot is waiting.
    public Task ApplyPairStatusAsync(PairingState pairing, CatalogMailboxSummary state, CancellationToken ct) =>
        pairing.Direction == PairingDirection.OwnerSide ? RunOwnerCheckAsync(pairing, state, getLatest: false, ct) : Task.CompletedTask;

    /// `summary` is mailbox state already read from the pair status poll; it is used only while its key is one of ours.
    private async Task<string?> RunOwnerCheckAsync(PairingState pairing, CatalogMailboxSummary? summary, bool getLatest, CancellationToken ct)
    {
        if (pairing is not { Direction: PairingDirection.OwnerSide, IsPaired: true, PairIdHash: { Length: > 0 } pairIdHash })
            return "This pairing isn't active.";
        lock (gate)
            if (!checksInFlight.Add(pairing.Id))
                return getLatest ? "A check is already running - try again in a moment." : null;
        var delivery = pairing.CatalogDelivery;
        var known = false;
        var recheck = false;
        string? result = null;
        try
        {
            identity.EnsureIdentity();
            if (summary is not null)
            {
                MailboxStatusService.ObserveSender(pairing, delivery, MailboxNames.Catalog, summary.SenderStatus);
                pairing.CatalogRefreshRequestedUnixSeconds = summary.RefreshRequestedAt ?? pairing.CatalogRefreshRequestedUnixSeconds;
            }
            await statuses.FlushReceiptAsync(pairing, delivery, MailboxNames.Catalog, ct).ConfigureAwait(false);
            // A refresh resets what the relay accepts receipts for, so one still unsent would then be refused for good.
            if (getLatest && delivery.PendingReceiptSequence is not null)
            {
                result = "The last catalog's receipt hasn't reached the relay yet - try Get latest again in a moment.";
                getLatest = false;
            }

            // A summary can predate this client's own latest rotation, so only a key we hold is taken from it.
            known = !getLatest && summary is { Exists: true } && summary.ReceiveKeyId is { } summaryKey
                && (summaryKey == pairing.MailboxReceiveKeyId || summaryKey == delivery.NextKeyId);
            string? relayKeyId;
            int? waiting;
            if (known)
            {
                relayKeyId = summary!.ReceiveKeyId;
                waiting = summary.WaitingSnapshotId;
                pairing.SubLastPublishedUnixSeconds = summary.LastUploadAt;
            }
            else
            {
                CatalogMailboxStatus status;
                try
                {
                    status = await relay.FetchMailboxStatusAsync(pairIdHash, pairing.PairEpoch, ct).ConfigureAwait(false);
                }
                catch (RelayException ex) when (ex.Code == "not_found")
                {
                    RecordCheckFailure(pairing, "The relay doesn't support automatic catalog sync yet.");
                    return "The relay doesn't support automatic catalog sync yet.";
                }
                relayKeyId = status.HasKey ? status.ReceiveKeyId : null;
                waiting = status.HasSnapshot ? status.SnapshotId : null;
                pairing.SubLastPublishedUnixSeconds = status.LastUploadAt;
            }

            string? note = null;
            var keyPublished = false;
            var pickedUp = false;
            if (delivery.NextKeyId is not null && relayKeyId == delivery.NextKeyId && delivery.NextKeyItem > 0)
            {
                // The relay rotated to our stored next key, so the last pickup's response never arrived.
                await RetryLostPickupAsync(pairing, delivery.NextKeyItem, ct).ConfigureAwait(false);
                pickedUp = true;
            }
            else if (relayKeyId is null || relayKeyId != pairing.MailboxReceiveKeyId || !HasUsableReceiveKey(pairing))
            {
                // Replacing the key discards anything encrypted to the old one; the Sub's delivery check republishes it.
                result = await PublishFreshKeyAsync(pairing, getLatest, ct).ConfigureAwait(false);
                keyPublished = true;
                if (waiting > pairing.LastImportedSnapshotId)
                    note = "A newer catalog was waiting but couldn't be read with this device's key - your Sub's plugin will send it again shortly.";
                Plugin.Log.Debug($"Catalog mailbox for {pairing.PeerName}: published a fresh receive key.");
            }
            else if (waiting is { } snapshotId && snapshotId > pairing.LastImportedSnapshotId)
            {
                await PickUpAsync(pairing, snapshotId, ct).ConfigureAwait(false);
                pickedUp = true;
            }
            else
            {
                Plugin.Log.Debug($"Catalog mailbox for {pairing.PeerName}: nothing new ({(known ? "pair status" : "status check")}).");
            }

            // A pickup just posted its own receipt; announcing from the older summary would post a stale "ok" over it.
            if (pickedUp)
                lock (gate) announced.Add((pairing.Id, pairing.PairEpoch));
            else
                await AnnounceReceiptsAsync(pairing, summary, ct).ConfigureAwait(false);
            if (getLatest && !keyPublished)
                result = await PublishFreshKeyAsync(pairing, refresh: true, ct).ConfigureAwait(false);

            pairing.LastMailboxCheckOkUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            pairing.LastMailboxCheckError = note;
            config.SaveNow();
        }
        catch (RelayException ex) when (known && ex.Code == "not_found")
        {
            // The snapshot the poll saw was replaced or expired before pickup; ask the mailbox directly.
            recheck = true;
        }
        catch (RelayException ex)
        {
            RecordCheckFailure(pairing, DescribeError(ex));
            result ??= DescribeError(ex);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Catalog mailbox check failed unexpectedly.");
            RecordCheckFailure(pairing, "The automatic catalog check failed unexpectedly - see /xllog.");
            result ??= "The check failed unexpectedly - see /xllog.";
        }
        finally
        {
            lock (gate)
            {
                checksInFlight.Remove(pairing.Id);
                importing.Remove(pairing.Id);
            }
        }
        if (recheck)
            return await RunOwnerCheckAsync(pairing, null, getLatest, ct).ConfigureAwait(false);
        return result;
    }

    /// Lets the Sub's plugin know this Owner posts receipts, once per pair epoch, naming only what it already took.
    private async Task AnnounceReceiptsAsync(PairingState pairing, CatalogMailboxSummary? summary, CancellationToken ct)
    {
        var delivery = pairing.CatalogDelivery;
        if (summary is not { Exists: true } || statuses.IsOurs(pairing, summary.RecipientStatus) || delivery.PendingReceiptSequence is not null)
            return;
        lock (gate)
            if (!announced.Add((pairing.Id, pairing.PairEpoch)))
                return;
        var sequence = Math.Min(pairing.LastImportedSnapshotId, summary.LastConsumedSnapshotId ?? 0);
        await statuses.PostReceiptAsync(pairing, delivery, MailboxNames.Catalog, sequence, MailboxStatusCodes.Ok, ct).ConfigureAwait(false);
    }

    private async Task PickUpAsync(PairingState pairing, int snapshotId, CancellationToken ct)
    {
        lock (gate) importing.Add(pairing.Id);
        if (!TryLoadReceiveKey(pairing, out var receiveKey))
            return;
        using var current = receiveKey;
        var delivery = pairing.CatalogDelivery;
        var nextEnvelope = EnsureNextKey(pairing);
        delivery.NextKeyItem = snapshotId;
        config.SaveNow();
        var (envelope, ciphertext) = await relay.ConsumeMailboxSnapshotAsync(pairing.PairIdHash!, pairing.PairEpoch, snapshotId, nextEnvelope, ct).ConfigureAwait(false);
        await SettleAsync(pairing, current, snapshotId, envelope, ciphertext, ct).ConfigureAwait(false);
    }

    /// Repeats the pickup with the stored next key; a snapshot the relay no longer holds gets a key_lost receipt, so the
    /// Sub's plugin sends it again.
    private async Task RetryLostPickupAsync(PairingState pairing, int snapshotId, CancellationToken ct)
    {
        lock (gate) importing.Add(pairing.Id);
        var delivery = pairing.CatalogDelivery;
        var nextEnvelope = MailboxStatusService.LoadNextKeyEnvelope<CatalogMailboxKeyEnvelope>(delivery);
        if (nextEnvelope is not null && TryLoadReceiveKey(pairing, out var receiveKey))
        {
            using var current = receiveKey;
            try
            {
                var (envelope, ciphertext) = await relay.ConsumeMailboxSnapshotAsync(pairing.PairIdHash!, pairing.PairEpoch, snapshotId, nextEnvelope, ct).ConfigureAwait(false);
                Plugin.Log.Information($"Catalog mailbox for {pairing.PeerName}: finished an interrupted pickup of snapshot #{snapshotId}.");
                await SettleAsync(pairing, current, snapshotId, envelope, ciphertext, ct).ConfigureAwait(false);
                return;
            }
            catch (RelayException ex) when (ex.Code == "not_found")
            {
            }
        }
        Plugin.Log.Warning($"Catalog mailbox for {pairing.PeerName}: snapshot #{snapshotId} was lost with an interrupted pickup - asking for it again.");
        PromoteNextKey(pairing);
        RecordSyncFailure(pairing, MailboxStatusCodes.KeyLost, snapshotId, "A catalog was lost when picking it up was interrupted - your Sub's plugin will send it again.");
        config.SaveNow();
        await statuses.PostReceiptAsync(pairing, delivery, MailboxNames.Catalog, snapshotId, MailboxStatusCodes.KeyLost, ct).ConfigureAwait(false);
    }

    /// Checks, decrypts and imports one picked-up snapshot, moves to the next key, and posts the receipt.
    private async Task SettleAsync(PairingState pairing, RelayEcKeyPair current, int snapshotId, CatalogPushEnvelope envelope, byte[] ciphertext, CancellationToken ct)
    {
        var usedKeyId = pairing.MailboxReceiveKeyId!;
        var (code, error, exportText) = TryDecryptPush(pairing, envelope, ciphertext, current, usedKeyId);
        CatalogSnapshotResult? result = null;
        if (exportText is not null)
        {
            // Imports touch the lists the UI draws from, so apply on the framework thread.
            result = await Plugin.Framework.RunOnFrameworkThread(() => catalogSync.ApplyRelaySnapshot(exportText, envelope.PairIdHash)).ConfigureAwait(false);
            if (result.Value.Error is { } importError)
                (code, error) = (MailboxStatusCodes.ImportFailed, importError);
        }

        PromoteNextKey(pairing);
        if (code == MailboxStatusCodes.Ok && result is { } imported)
        {
            pairing.LastImportedSnapshotId = envelope.SnapshotId;
            pairing.LastAcceptedCatalogSyncUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            pairing.LastSyncFailure = null;
            pairing.CatalogDelivery.LastFailureCode = null;
            LastAutoImport = (pairing.Id, imported);
            Plugin.Log.Debug($"Catalog mailbox for {pairing.PeerName}: snapshot #{snapshotId} imported.");
        }
        else
        {
            RecordSyncFailure(pairing, code, snapshotId, error ?? "The catalog couldn't be read.");
            Plugin.Log.Warning($"Catalog mailbox for {pairing.PeerName}: snapshot #{snapshotId} rejected ({code}): {error}");
        }
        config.SaveNow();
        await statuses.PostReceiptAsync(pairing, pairing.CatalogDelivery, MailboxNames.Catalog, snapshotId, code, ct).ConfigureAwait(false);

        if (code != MailboxStatusCodes.Ok || result is not { } done)
            return;
        Plugin.FireAndForget(pictures.FetchMissingAsync(pairing, ct));
        if (done.Added + done.Updated + done.Removed > 0)
        {
            Plugin.NotificationManager.AddNotification(new Notification
            {
                Title = "Oathbound",
                Content = $"Synced {pairing.PeerName}'s catalog: {done.Added} added, {done.Updated} updated, {done.Removed} removed.",
                Type = NotificationType.Success,
                InitialDuration = TimeSpan.FromSeconds(6),
            });
        }
    }

    public (Guid PairingId, CatalogSnapshotResult Result)? LastAutoImport { get; private set; }

    /// Every check passes before a single byte is decrypted into the catalog. The code is the receipt to post.
    private (string Code, string? Error, string? ExportText) TryDecryptPush(PairingState pairing, CatalogPushEnvelope envelope, byte[] ciphertext, RelayEcKeyPair ownerReceive, string usedKeyId)
    {
        string? error = null;
        var code = MailboxStatusCodes.VerifyFailed;
        if (envelope.PairIdHash != pairing.PairIdHash || envelope.PairEpoch != pairing.PairEpoch)
            error = "Snapshot addressed to a different pair/epoch - ignored.";
        else if (envelope.RecipientDeviceKeyId != identity.DeviceKeyId || envelope.SenderDeviceKeyId != pairing.PeerDeviceKeyId)
            error = "Snapshot sender/recipient device keys did not match this pairing - ignored.";
        else if (envelope.ReceiveKeyId != usedKeyId)
            error = "Snapshot was encrypted to a different mailbox key - ignored.";
        else if (envelope.SnapshotId <= pairing.LastImportedSnapshotId)
            (code, error) = (MailboxStatusCodes.Stale, "Snapshot is not newer than the last one imported - ignored (stale or replayed).");
        else if (envelope.CiphertextSizeBytes > RelayProtocolConstants.CatalogCiphertextMaxBytes || ciphertext.Length != envelope.CiphertextSizeBytes)
            error = "Snapshot ciphertext size was invalid - ignored.";
        else if (RelayCrypto.Sha256Hex(ciphertext) != envelope.CiphertextDigest)
            error = "Snapshot ciphertext digest did not match - ignored (corrupt or tampered).";
        else if (pairing.PeerPublicKeyX is null || pairing.PeerPublicKeyY is null)
            error = "No peer public key on file - cannot verify this snapshot.";
        else if (!RelayCrypto.VerifyRaw(new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = pairing.PeerPublicKeyX, Y = pairing.PeerPublicKeyY },
                     envelope.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(envelope)))
            error = "Snapshot signature did not verify against the paired Sub's key - ignored.";
        if (error is not null)
            return (code, error, null);

        try
        {
            using var subEphemeralPublic = RelayCrypto.ImportEphemeralPublicKey(envelope.SenderEphemeralPublicKey);
            var sharedSecret = RelayCrypto.DeriveSharedSecret(ownerReceive, subEphemeralPublic);
            var salt = SHA256.HashData([.. RelayCrypto.ExportRawUncompressedPoint(ownerReceive), .. RelayCrypto.ExportRawUncompressedPoint(envelope.SenderEphemeralPublicKey)]);
            var aesKey = RelayCrypto.DeriveAesKey(sharedSecret, salt, RelayCrypto.BuildCatalogPushHkdfInfo(envelope.PairIdHash, envelope.ReceiveKeyId));
            var compressed = RelayCrypto.AesGcmDecrypt(aesKey, RelayCrypto.Base64UrlDecode(envelope.Nonce), ciphertext, CatalogPushAad.Build(envelope));
            var exportText = System.Text.Encoding.UTF8.GetString(RelayCompression.Decompress(compressed, RelayProtocolConstants.CatalogPlaintextMaxBytes));
            return (MailboxStatusCodes.Ok, null, exportText);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException)
        {
            return (MailboxStatusCodes.DecryptFailed, $"Snapshot failed to decrypt/decompress - ignored ({ex.GetType().Name}).", null);
        }
    }

    /// Null on success; with `refresh`, why the relay wouldn't take it as a Get latest (the key is published anyway).
    private async Task<string?> PublishFreshKeyAsync(PairingState pairing, bool refresh, CancellationToken ct)
    {
        using var key = RelayCrypto.GenerateEphemeralKeyPair();
        var envelope = BuildSignedKeyEnvelope(pairing, key, RelayCrypto.RandomReceiveKeyId());
        string? result = null;
        try
        {
            await relay.PublishMailboxKeyAsync(envelope, ct, refresh).ConfigureAwait(false);
        }
        catch (RelayException ex) when (refresh && ex.Code == "rate_limited")
        {
            result = $"You asked less than a minute ago - you can ask again in {Math.Max(ex.RetryAfterSeconds ?? 60, 1)} seconds.";
            if (pairing.MailboxReceiveKeyId is not null && HasUsableReceiveKey(pairing))
                return result;
            await relay.PublishMailboxKeyAsync(envelope, ct).ConfigureAwait(false);
        }
        StoreReceiveKey(pairing, envelope, key);
        pairing.CatalogDelivery.ClearNextKey();
        if (refresh && result is null)
            pairing.CatalogRefreshRequestedUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        config.SaveNow();
        return result;
    }

    /// The next key is generated once and reused until a pickup with it settles.
    private CatalogMailboxKeyEnvelope EnsureNextKey(PairingState pairing)
    {
        var delivery = pairing.CatalogDelivery;
        if (delivery.NextKeyId is not null && MailboxStatusService.LoadNextKeyEnvelope<CatalogMailboxKeyEnvelope>(delivery) is { } stored
            && stored.ReceiveKeyId == delivery.NextKeyId && stored.PairEpoch == pairing.PairEpoch)
            return stored;
        using var nextKey = RelayCrypto.GenerateEphemeralKeyPair();
        var envelope = BuildSignedKeyEnvelope(pairing, nextKey, RelayCrypto.RandomReceiveKeyId());
        MailboxStatusService.StoreNextKey(delivery, JsonSerializer.Serialize(envelope), envelope.ReceiveKeyId, envelope.ReceivePublicKey, nextKey, ReceiveKeyEntropy);
        config.SaveNow();
        return envelope;
    }

    /// The relay is on the next key now; the previous private scalar is overwritten and gone for good.
    private static void PromoteNextKey(PairingState pairing)
    {
        var delivery = pairing.CatalogDelivery;
        if (delivery.NextKeyId is null || delivery.NextKeyPrivate is null)
            return;
        if (pairing.MailboxReceivePrivateKey is { } old)
            Array.Clear(old);
        pairing.MailboxReceiveKeyId = delivery.NextKeyId;
        pairing.MailboxReceivePublicKeyX = delivery.NextKeyX;
        pairing.MailboxReceivePublicKeyY = delivery.NextKeyY;
        pairing.MailboxReceivePrivateKey = delivery.NextKeyPrivate;
        pairing.MailboxReceivePrivateKeyProtected = delivery.NextKeyPrivateProtected;
        delivery.NextKeyPrivate = null;
        delivery.ClearNextKey();
    }

    private static void RecordSyncFailure(PairingState pairing, string code, int snapshotId, string message)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        pairing.LastSyncFailure = message;
        pairing.LastSyncFailureUnixSeconds = now;
        pairing.CatalogDelivery.LastFailureCode = code;
        pairing.CatalogDelivery.LastFailureSequence = snapshotId;
        pairing.CatalogDelivery.LastFailureAt = now;
    }

    private CatalogMailboxKeyEnvelope BuildSignedKeyEnvelope(PairingState pairing, RelayEcKeyPair key, string receiveKeyId)
    {
        var envelope = new CatalogMailboxKeyEnvelope
        {
            PairIdHash = pairing.PairIdHash!,
            PairEpoch = pairing.PairEpoch,
            ReceiveKeyId = receiveKeyId,
            OwnerDeviceKeyId = identity.DeviceKeyId!,
            ReceivePublicKey = RelayCrypto.ExportPublicKeyJwk(key),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        envelope.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(envelope));
        return envelope;
    }

    /// The previous private scalar is overwritten and gone for good.
    private static void StoreReceiveKey(PairingState pairing, CatalogMailboxKeyEnvelope envelope, RelayEcKeyPair key)
    {
        var privateD = RelayCrypto.ExportPrivateD(key);
        var (protectedD, wasProtected) = DeviceIdentityService.Protect(privateD, ReceiveKeyEntropy);
        if (!ReferenceEquals(protectedD, privateD)) Array.Clear(privateD);
        pairing.MailboxReceiveKeyId = envelope.ReceiveKeyId;
        pairing.MailboxReceivePublicKeyX = envelope.ReceivePublicKey.X;
        pairing.MailboxReceivePublicKeyY = envelope.ReceivePublicKey.Y;
        pairing.MailboxReceivePrivateKey = protectedD;
        pairing.MailboxReceivePrivateKeyProtected = wasProtected;
    }

    private static bool TryLoadReceiveKey(PairingState pairing, out RelayEcKeyPair key)
    {
        key = null!;
        if (pairing.MailboxReceivePrivateKey is null || pairing.MailboxReceivePublicKeyX is null || pairing.MailboxReceivePublicKeyY is null)
            return false;
        try
        {
            var privateD = DeviceIdentityService.Unprotect(pairing.MailboxReceivePrivateKey, pairing.MailboxReceivePrivateKeyProtected ?? false, ReceiveKeyEntropy);
            key = RelayCrypto.ImportEphemeralPrivateKey(
                new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = pairing.MailboxReceivePublicKeyX, Y = pairing.MailboxReceivePublicKeyY }, privateD);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Catalog mailbox receive key could not be loaded; a fresh one will be published.");
            return false;
        }
    }

    private static bool HasUsableReceiveKey(PairingState pairing)
    {
        if (pairing.MailboxReceiveKeyId is null || !TryLoadReceiveKey(pairing, out var key))
            return false;
        key.Dispose();
        return true;
    }

    private void RecordCheckFailure(PairingState pairing, string message)
    {
        pairing.LastMailboxCheckError = message;
        config.SaveNow();
    }

    private static string DescribePublishError(RelayException ex) => ex.Code switch
    {
        "network" => "Could not reach the relay.",
        "service_unavailable" => "The relay is temporarily unavailable.",
        "unauthorized" => "The relay rejected the upload (pairing no longer active on the relay?).",
        "payload_too_large" => "The catalog is too large to upload.",
        _ => $"The relay rejected the upload ({ex.Code}).",
    };

    private static string DescribeError(RelayException ex) => ex.Code switch
    {
        "network" => "Could not reach the relay - will check again later.",
        "rate_limited" => "The relay asked to slow down - will check again later.",
        "service_unavailable" => "The relay is temporarily unavailable - will check again later.",
        "unauthorized" => "The relay rejected the check (pairing no longer active on the relay?).",
        "not_found" => "The waiting catalog was already collected or replaced - will check again later.",
        _ => "The automatic catalog check failed - will try again later.",
    };
}
