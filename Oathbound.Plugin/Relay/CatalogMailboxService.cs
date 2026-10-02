using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
    /// Retried on the hourly pass.
    Failed,
}

public readonly record struct MailboxPublishResult(MailboxPublishOutcome Outcome, int RetryAfterSeconds = 0);

/// Automatic sync over the per-pair relay mailbox: the Sub pushes on change, the Owner collects hourly.
/// No chat on this path. Timing lives in CatalogAutoSync; this is only the relay/crypto work.
public sealed class CatalogMailboxService
{
    private static readonly byte[] ReceiveKeyEntropy = "oathbound-mailbox-receive-key-v1"u8.ToArray();

    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly DeviceIdentityService identity;
    private readonly CatalogSyncService catalogSync;

    private readonly object gate = new();
    private readonly HashSet<Guid> checksInFlight = new();
    private readonly HashSet<Guid> importing = new();
    private readonly HashSet<Guid> publishesInFlight = new();

    public CatalogMailboxService(PluginConfig config, RelayClient relay, DeviceIdentityService identity, CatalogSyncService catalogSync)
    {
        this.config = config;
        this.relay = relay;
        this.identity = identity;
        this.catalogSync = catalogSync;
    }

    public bool IsSyncing(Guid pairingId) { lock (gate) return importing.Contains(pairingId); }

    public bool IsChecking(Guid pairingId) { lock (gate) return checksInFlight.Contains(pairingId); }

    // ---- Sub side ----

    /// `force` skips the "digest unchanged" shortcut, for the hourly delivery check.
    public async Task<MailboxPublishResult> PublishAsync(PairingState pairing, string exportText, string digest, CancellationToken ct)
    {
        if (pairing is not { Direction: PairingDirection.SubSide, IsPaired: true, PairIdHash: { Length: > 0 } pairIdHash } ||
            pairing.PeerDeviceKeyId is null || pairing.PeerPublicKeyX is null || pairing.PeerPublicKeyY is null)
            return new(MailboxPublishOutcome.NotReady);
        if (!config.Permissions.RelayCatalogSync)
            return new(MailboxPublishOutcome.NotReady);

        lock (gate)
            if (!publishesInFlight.Add(pairing.Id))
                return new(MailboxPublishOutcome.NotReady);
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
                    return new(MailboxPublishOutcome.NotReady); // Owner hasn't published a receive key yet.
                }

                var key = info.Key;
                if (key.PairIdHash != pairIdHash || key.PairEpoch != pairing.PairEpoch || key.OwnerDeviceKeyId != pairing.PeerDeviceKeyId ||
                    !RelayCrypto.VerifyRaw(peerPublicKey, key.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(key)))
                {
                    // Never encrypt to a key the paired Owner didn't sign.
                    Plugin.Log.Warning($"Catalog mailbox for {pairing.PeerName}: receive key did not verify against the paired Owner - not publishing.");
                    return new(MailboxPublishOutcome.Failed);
                }

                // Same catalog as last time and the relay confirms delivery: nothing to do.
                var delivered = pairing.LastPublishedMailboxSnapshotId > 0 &&
                    (info.WaitingSnapshotId >= pairing.LastPublishedMailboxSnapshotId || info.LastConsumedSnapshotId >= pairing.LastPublishedMailboxSnapshotId);
                if (digest == pairing.LastPublishedCatalogDigest && delivered)
                    return new(MailboxPublishOutcome.Published);

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
                return new(MailboxPublishOutcome.Published);
            }
            return new(MailboxPublishOutcome.Failed);
        }
        catch (RelayException ex) when (ex.Code is "rate_limited" or "cooldown_active")
        {
            return new(MailboxPublishOutcome.RateLimited, Math.Max(ex.RetryAfterSeconds ?? 60, 1));
        }
        catch (RelayException ex)
        {
            Plugin.Log.Information($"Catalog mailbox publish for {pairing.PeerName} failed: {ex.Code}.");
            return new(MailboxPublishOutcome.Failed);
        }
        catch (InvalidDataException ex)
        {
            Plugin.Log.Warning(ex.Message);
            return new(MailboxPublishOutcome.Failed);
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
    public async Task CheckAsync(PairingState pairing, CancellationToken ct)
    {
        if (pairing is not { Direction: PairingDirection.OwnerSide, IsPaired: true, PairIdHash: { Length: > 0 } pairIdHash })
            return;
        lock (gate)
            if (!checksInFlight.Add(pairing.Id))
                return;
        try
        {
            identity.EnsureIdentity();

            CatalogMailboxStatus status;
            try
            {
                status = await relay.FetchMailboxStatusAsync(pairIdHash, pairing.PairEpoch, ct).ConfigureAwait(false);
            }
            catch (RelayException ex) when (ex.Code == "not_found")
            {
                RecordCheckFailure(pairing, "The relay doesn't support automatic catalog sync yet - use Request refresh.");
                return;
            }

            // Replacing the key discards anything encrypted to the old one; the Sub's delivery check republishes it.
            if (!status.HasKey || status.ReceiveKeyId != pairing.MailboxReceiveKeyId || !HasUsableReceiveKey(pairing))
            {
                await PublishFreshKeyAsync(pairing, ct).ConfigureAwait(false);
                pairing.SubLastPublishedUnixSeconds = status.LastUploadAt;
                pairing.LastMailboxCheckOkUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                pairing.LastMailboxCheckError = status.HasSnapshot && status.SnapshotId > pairing.LastImportedSnapshotId
                    ? "A newer catalog was waiting but couldn't be read with this device's key - your Sub's plugin will send it again within the hour."
                    : null;
                config.SaveNow();
                return;
            }

            pairing.SubLastPublishedUnixSeconds = status.LastUploadAt;
            string? importError = null;
            if (status is { HasSnapshot: true, SnapshotId: { } snapshotId } && snapshotId > pairing.LastImportedSnapshotId)
                importError = await RetrieveAndImportAsync(pairing, snapshotId, ct).ConfigureAwait(false);

            pairing.LastMailboxCheckOkUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            pairing.LastMailboxCheckError = importError;
            config.SaveNow();
        }
        catch (RelayException ex)
        {
            RecordCheckFailure(pairing, DescribeError(ex));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Catalog mailbox check failed unexpectedly.");
            RecordCheckFailure(pairing, "The automatic catalog check failed unexpectedly - see /xllog.");
        }
        finally
        {
            lock (gate)
            {
                checksInFlight.Remove(pairing.Id);
                importing.Remove(pairing.Id);
            }
        }
    }

    /// Null on success; otherwise the prior catalog is untouched.
    private async Task<string?> RetrieveAndImportAsync(PairingState pairing, int snapshotId, CancellationToken ct)
    {
        lock (gate) importing.Add(pairing.Id);

        if (!TryLoadReceiveKey(pairing, out var receiveKey))
            return "This device's mailbox key couldn't be read.";
        using var ownerReceive = receiveKey;
        var usedKeyId = pairing.MailboxReceiveKeyId!;

        // Rotation is atomic with the pickup on the relay, so persist the next key before any local verification.
        using var nextKey = RelayCrypto.GenerateEphemeralKeyPair();
        var nextEnvelope = BuildSignedKeyEnvelope(pairing, nextKey, RelayCrypto.RandomReceiveKeyId());
        var (envelope, ciphertext) = await relay.ConsumeMailboxSnapshotAsync(pairing.PairIdHash!, pairing.PairEpoch, snapshotId, nextEnvelope, ct).ConfigureAwait(false);
        StoreReceiveKey(pairing, nextEnvelope, nextKey);
        config.SaveNow();

        if (!TryDecryptPush(pairing, envelope, ciphertext, ownerReceive, usedKeyId, out var exportText, out var error))
            return error;

        // Imports touch the lists the UI draws from, so apply on the framework thread.
        var result = await Plugin.Framework.RunOnFrameworkThread(() => catalogSync.ApplyRelaySnapshot(exportText!, envelope.PairIdHash)).ConfigureAwait(false);
        if (result.Error is not null)
            return result.Error;

        pairing.LastImportedSnapshotId = envelope.SnapshotId;
        pairing.LastAcceptedCatalogSyncUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        config.SaveNow();
        LastAutoImport = (pairing.Id, result);
        if (result.Added + result.Updated + result.Removed > 0)
        {
            Plugin.NotificationManager.AddNotification(new Notification
            {
                Title = "Oathbound",
                Content = $"Synced {pairing.PeerName}'s catalog: {result.Added} added, {result.Updated} updated, {result.Removed} removed.",
                Type = NotificationType.Success,
                InitialDuration = TimeSpan.FromSeconds(6),
            });
        }
        return null;
    }

    public (Guid PairingId, CatalogSnapshotResult Result)? LastAutoImport { get; private set; }

    /// Every check passes before a single byte is decrypted into the catalog.
    private bool TryDecryptPush(PairingState pairing, CatalogPushEnvelope envelope, byte[] ciphertext, RelayEcKeyPair ownerReceive, string usedKeyId, out string? exportText, out string? error)
    {
        exportText = null;
        error = null;
        if (envelope.PairIdHash != pairing.PairIdHash || envelope.PairEpoch != pairing.PairEpoch)
            error = "Snapshot addressed to a different pair/epoch - ignored.";
        else if (envelope.RecipientDeviceKeyId != identity.DeviceKeyId || envelope.SenderDeviceKeyId != pairing.PeerDeviceKeyId)
            error = "Snapshot sender/recipient device keys did not match this pairing - ignored.";
        else if (envelope.ReceiveKeyId != usedKeyId)
            error = "Snapshot was encrypted to a different mailbox key - ignored.";
        else if (envelope.SnapshotId <= pairing.LastImportedSnapshotId)
            error = "Snapshot is not newer than the last one imported - ignored (stale or replayed).";
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
            return false;

        try
        {
            using var subEphemeralPublic = RelayCrypto.ImportEphemeralPublicKey(envelope.SenderEphemeralPublicKey);
            var sharedSecret = RelayCrypto.DeriveSharedSecret(ownerReceive, subEphemeralPublic);
            var salt = SHA256.HashData([.. RelayCrypto.ExportRawUncompressedPoint(ownerReceive), .. RelayCrypto.ExportRawUncompressedPoint(envelope.SenderEphemeralPublicKey)]);
            var aesKey = RelayCrypto.DeriveAesKey(sharedSecret, salt, RelayCrypto.BuildCatalogPushHkdfInfo(envelope.PairIdHash, envelope.ReceiveKeyId));
            var compressed = RelayCrypto.AesGcmDecrypt(aesKey, RelayCrypto.Base64UrlDecode(envelope.Nonce), ciphertext, CatalogPushAad.Build(envelope));
            exportText = System.Text.Encoding.UTF8.GetString(RelayCompression.Decompress(compressed, RelayProtocolConstants.CatalogPlaintextMaxBytes));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException)
        {
            error = $"Snapshot failed to decrypt/decompress - ignored ({ex.GetType().Name}).";
            return false;
        }
    }

    private async Task PublishFreshKeyAsync(PairingState pairing, CancellationToken ct)
    {
        using var key = RelayCrypto.GenerateEphemeralKeyPair();
        var envelope = BuildSignedKeyEnvelope(pairing, key, RelayCrypto.RandomReceiveKeyId());
        await relay.PublishMailboxKeyAsync(envelope, ct).ConfigureAwait(false);
        StoreReceiveKey(pairing, envelope, key);
        config.SaveNow();
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
