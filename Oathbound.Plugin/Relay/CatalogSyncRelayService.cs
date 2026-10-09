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

/// An Owner's request for a fresh encrypted catalog snapshot, end to end. Both sides live here; Role decides which runs.
public sealed class CatalogSyncRelayService
{
    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly DeviceIdentityService identity;
    private readonly ChatComposer composer;
    private readonly ChatSender sender;
    private readonly CatalogSyncService catalogSync;
    private readonly CatalogPictureService pictures;

    /// Memory-only by design; an interrupted request is cleared on startup rather than persisted or resumed.
    private readonly Dictionary<string, RelayEcKeyPair> pendingOwnerRequests = new();

    /// So the poll loop stops instead of overwriting the denial with a timeout error.
    private readonly HashSet<string> deniedRequestIds = new();

    public string? LastError { get; private set; }
    public event Action? LastErrorChanged;

    public bool RequestInFlight { get; private set; }
    public event Action? RequestInFlightChanged;
    public string Phase { get; private set; } = "Idle";

    /// Successful or not, for display.
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public CatalogSnapshotResult? LastImportResult { get; private set; }

    public CatalogSyncRelayService(PluginConfig config, RelayClient relay, DeviceIdentityService identity, ChatComposer composer, ChatSender sender, CatalogSyncService catalogSync, CatalogPictureService pictures)
    {
        this.pictures = pictures;
        this.config = config;
        this.relay = relay;
        this.identity = identity;
        this.composer = composer;
        this.sender = sender;
        this.catalogSync = catalogSync;

        if (config.PendingRelayOperations.RemoveAll(o => o.Kind == "catalog-request") > 0)
        {
            config.SaveNow();
            LastError = "A catalog refresh was interrupted by a plugin restart. Request a fresh snapshot.";
        }
    }

    private void SetError(string? message)
    {
        LastError = message;
        LastErrorChanged?.Invoke();
    }

    private void SetInFlight(bool value)
    {
        RequestInFlight = value;
        RequestInFlightChanged?.Invoke();
    }

    private void SetPhase(string value)
    {
        Phase = value;
        RequestInFlightChanged?.Invoke();
    }

    /// The manual fallback to automatic mailbox sync. The Worker's one-active-request slot is the only gate.
    public async Task<bool> RequestRefreshAsync(PairingState pairing, CancellationToken ct)
    {
        if (!pairing.IsPaired)
        {
            SetError("Not paired.");
            return false;
        }
        if (RequestInFlight)
        {
            SetError("A request is already in flight.");
            return false;
        }

        SetInFlight(true);
        SetPhase("Creating secure request");
        var pollStarted = false;
        RelayEcKeyPair? unownedEphemeral = null;
        try
        {
            identity.EnsureIdentity();
            var ownerEphemeral = unownedEphemeral = RelayCrypto.GenerateEphemeralKeyPair();
            var requestId = RelayCrypto.RandomCapabilityId();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var envelope = new CatalogRequestEnvelope
            {
                PairIdHash = pairing.PairIdHash!,
                PairEpoch = pairing.PairEpoch,
                RequestId = requestId,
                RequesterDeviceKeyId = identity.DeviceKeyId!,
                OwnerEphemeralPublicKey = RelayCrypto.ExportPublicKeyJwk(ownerEphemeral),
                CreatedAt = now,
                ExpiresAt = now + RelayProtocolConstants.CatalogRequestExpirySeconds,
            };
            envelope.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(envelope));

            await relay.CreateCatalogRequestAsync(envelope, ct).ConfigureAwait(false);

            // Ownership moves into the map; disposed wherever it's later removed.
            pendingOwnerRequests[requestId] = ownerEphemeral;
            unownedEphemeral = null;
            config.PendingRelayOperations.RemoveAll(o => o.Kind == "catalog-request");
            config.PendingRelayOperations.Add(new PendingRelayOperationState { Kind = "catalog-request", OperationId = requestId, ExpiresAt = envelope.ExpiresAt });
            config.SaveNow();

            var tell = composer.ComposeCatalogRequestNotice(pairing.PeerName!, pairing.PeerWorld!, requestId);
            sender.Send(tell);
            SetError(null);

            SetPhase("Waiting for Sub upload");
            _ = PollAndImportAsync(requestId, envelope.ExpiresAt, pairing, ct);
            pollStarted = true;
            return true;
        }
        catch (RelayException ex)
        {
            SetError(DescribeError(ex));
            return false;
        }
        finally
        {
            unownedEphemeral?.Dispose();
            if (!pollStarted)
            {
                SetPhase("Idle");
                SetInFlight(false);
            }
        }
    }

    /// The prior imported snapshot is untouched unless every check passes.
    private async Task PollAndImportAsync(string requestId, long requestExpiresAt, PairingState pairing, CancellationToken ct)
    {
        try
        {
            // Bounded by the request's own expiresAt; giving up sooner would destroy the key for a request the Sub could still answer.
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (deniedRequestIds.Remove(requestId)) return;
                if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= requestExpiresAt)
                {
                    SetError("Timed out waiting for your Sub to respond.");
                    return;
                }

                CatalogRequestEnvelope status;
                try
                {
                    status = await relay.FetchCatalogRequestAsync(requestId, ct).ConfigureAwait(false);
                }
                catch (RelayException ex) when (ex.Code is "network" or "service_unavailable")
                {
                    // A single transient failure doesn't end the wait.
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    continue;
                }
                catch (RelayException ex)
                {
                    SetError(DescribeError(ex));
                    return;
                }

                if (status.Status == "uploaded") break;
                if (status.Status is "consumed" or "expired") { SetError("The request expired or was already consumed."); return; }
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }

            if (!pendingOwnerRequests.TryGetValue(requestId, out var ownerEphemeral))
            {
                SetError("Lost track of this request's key material (plugin restarted mid-request?) - request again.");
                return;
            }

            var (envelope, ciphertext) = await relay.ConsumeCatalogResponseAsync(requestId, ct).ConfigureAwait(false);
            SetPhase("Decrypting and validating");
            LastAttemptAt = DateTimeOffset.UtcNow;

            if (!ImportSnapshot(envelope, ciphertext, ownerEphemeral, pairing, out var result, out var error))
            {
                SetError(error);
                LastImportResult = new CatalogSnapshotResult(0, 0, 0, 0, error);
                return;
            }

            pairing.LastAcceptedCatalogSyncUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            pairing.LastImportedSnapshotId = envelope.SnapshotId;
            config.SaveNow();
            LastImportResult = result;
            SetPhase("Complete");
            SetError(null);
            Plugin.FireAndForget(pictures.FetchMissingAsync(pairing, ct));
        }
        catch (RelayException ex)
        {
            SetError(DescribeError(ex));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (pendingOwnerRequests.Remove(requestId, out var key)) key.Dispose();
            if (config.PendingRelayOperations.RemoveAll(o => o.Kind == "catalog-request" && o.OperationId == requestId) > 0)
                config.SaveNow();
            SetInFlight(false);
            if (Phase != "Complete") SetPhase("Idle");
        }
    }

    /// Isolated so a failure anywhere leaves no partial state.
    private bool ImportSnapshot(CatalogResponseEnvelope envelope, byte[] ciphertext, RelayEcKeyPair ownerEphemeral, PairingState pairing, out CatalogSnapshotResult result, out string? error)
    {
        result = default;
        error = null;

        if (envelope.PairIdHash != pairing.PairIdHash || envelope.PairEpoch != pairing.PairEpoch)
        {
            error = "Snapshot addressed to a different pair/epoch - ignored.";
            return false;
        }
        if (envelope.RecipientDeviceKeyId != identity.DeviceKeyId || envelope.SenderDeviceKeyId != pairing.PeerDeviceKeyId)
        {
            error = "Snapshot sender/recipient device keys did not match this pairing - ignored.";
            return false;
        }
        if (envelope.SnapshotId <= pairing.LastImportedSnapshotId)
        {
            error = "Snapshot is not newer than the last one imported - ignored (stale or replayed).";
            return false;
        }
        if (envelope.CiphertextSizeBytes > RelayProtocolConstants.CatalogCiphertextMaxBytes || ciphertext.Length != envelope.CiphertextSizeBytes)
        {
            error = "Snapshot ciphertext size was invalid - ignored.";
            return false;
        }
        if (RelayCrypto.Sha256Hex(ciphertext) != envelope.CiphertextDigest)
        {
            error = "Snapshot ciphertext digest did not match - ignored (corrupt or tampered).";
            return false;
        }
        if (pairing.PeerPublicKeyX is null || pairing.PeerPublicKeyY is null)
        {
            error = "No peer public key on file - cannot verify this snapshot.";
            return false;
        }
        var peerPublicKey = new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = pairing.PeerPublicKeyX, Y = pairing.PeerPublicKeyY };
        if (!RelayCrypto.VerifyRaw(peerPublicKey, envelope.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(envelope)))
        {
            error = "Snapshot signature did not verify against the paired peer's key - ignored.";
            return false;
        }

        byte[] plaintext;
        try
        {
            using var subEphemeralPublic = RelayCrypto.ImportEphemeralPublicKey(envelope.SenderEphemeralPublicKey);
            var sharedSecret = RelayCrypto.DeriveSharedSecret(ownerEphemeral, subEphemeralPublic);
            var ownerRaw = RelayCrypto.ExportRawUncompressedPoint(ownerEphemeral);
            var subRaw = RelayCrypto.ExportRawUncompressedPoint(envelope.SenderEphemeralPublicKey);
            var combined = new byte[ownerRaw.Length + subRaw.Length];
            Buffer.BlockCopy(ownerRaw, 0, combined, 0, ownerRaw.Length);
            Buffer.BlockCopy(subRaw, 0, combined, ownerRaw.Length, subRaw.Length);
            var salt = SHA256.HashData(combined);
            var info = RelayCrypto.BuildCatalogHkdfInfo(envelope.PairIdHash, envelope.RequestId);
            var aesKey = RelayCrypto.DeriveAesKey(sharedSecret, salt, info);
            var nonce = RelayCrypto.Base64UrlDecode(envelope.Nonce);
            var aad = CatalogResponseAad.Build(envelope);
            var compressed = RelayCrypto.AesGcmDecrypt(aesKey, nonce, ciphertext, aad);
            plaintext = RelayCompression.Decompress(compressed, RelayProtocolConstants.CatalogPlaintextMaxBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidDataException)
        {
            error = $"Snapshot failed to decrypt/decompress - ignored ({ex.GetType().Name}).";
            return false;
        }

        var exportText = System.Text.Encoding.UTF8.GetString(plaintext);
        var applyResult = catalogSync.ApplyRelaySnapshot(exportText, envelope.PairIdHash);
        if (applyResult.Error is not null)
        {
            error = applyResult.Error;
            return false;
        }
        result = applyResult;
        return true;
    }

    // ---- Sub side ----

    /// Fails closed silently, except for "permission not enabled", which gets its own notice tell.
    public async Task HandleCatalogRequestTellAsync(string requestId, string senderName, string senderWorld, CancellationToken ct)
    {
        // Only the Sub-side pairing with this sender is a valid source for a catalog request.
        var pairing = config.FindPairing(senderName, senderWorld, PairingDirection.SubSide);
        if (pairing is null)
        {
            Plugin.Log.Warning($"Catalog request from {senderName}@{senderWorld} ignored: no Sub-side pairing with that sender.");
            return;
        }

        CatalogRequestEnvelope request;
        try
        {
            request = await relay.FetchCatalogRequestAsync(requestId, ct).ConfigureAwait(false);
        }
        catch (RelayException ex)
        {
            Plugin.Log.Warning($"Catalog request {requestId} ignored: could not fetch it from the relay ({DescribeError(ex)}).");
            return;
        }

        if (request.PairIdHash != pairing.PairIdHash || request.PairEpoch != pairing.PairEpoch)
        {
            Plugin.Log.Warning($"Catalog request {requestId} ignored: pair id/epoch didn't match this pairing (stale pairing or re-pair since?).");
            return;
        }
        if (request.RequesterDeviceKeyId != pairing.PeerDeviceKeyId)
        {
            Plugin.Log.Warning($"Catalog request {requestId} ignored: requester device key didn't match the paired Owner's device (re-paired since?).");
            return; // Not from the actual paired Owner's device.
        }
        if (pairing.PeerPublicKeyX is null || pairing.PeerPublicKeyY is null)
        {
            Plugin.Log.Warning($"Catalog request {requestId} ignored: no peer public key on file for this pairing.");
            return;
        }
        var peerPublicKey = new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = pairing.PeerPublicKeyX, Y = pairing.PeerPublicKeyY };
        if (!RelayCrypto.VerifyRaw(peerPublicKey, request.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(request)))
        {
            Plugin.Log.Warning($"Catalog request {requestId} ignored: signature did not verify against the paired peer's key.");
            return;
        }

        if (!config.Permissions.RelayCatalogSync)
        {
            sender.Send(composer.ComposeCatalogPermissionDenied(senderName, senderWorld, requestId));
            return;
        }

        try
        {
            identity.EnsureIdentity();
            if (!catalogSync.TryBuildBoundedExport(out var exportText, out var exportError))
            {
                Plugin.Log.Warning(exportError ?? "Catalog snapshot exceeded a local size limit.");
                return;
            }
            await pictures.SyncBeforePublishAsync(pairing, ct).ConfigureAwait(false);
            var plaintext = System.Text.Encoding.UTF8.GetBytes(exportText);
            var compressed = RelayCompression.Compress(plaintext);
            if (compressed.Length > RelayProtocolConstants.CatalogCiphertextMaxBytes)
            {
                Plugin.Log.Warning("Catalog snapshot too large to upload even compressed; request left unanswered.");
                return;
            }

            using var subEphemeral = RelayCrypto.GenerateEphemeralKeyPair();
            using var ownerEphemeralPublic = RelayCrypto.ImportEphemeralPublicKey(request.OwnerEphemeralPublicKey);
            var sharedSecret = RelayCrypto.DeriveSharedSecret(subEphemeral, ownerEphemeralPublic);
            var ownerRaw = RelayCrypto.ExportRawUncompressedPoint(request.OwnerEphemeralPublicKey);
            var subRaw = RelayCrypto.ExportRawUncompressedPoint(subEphemeral);
            var combined = new byte[ownerRaw.Length + subRaw.Length];
            Buffer.BlockCopy(ownerRaw, 0, combined, 0, ownerRaw.Length);
            Buffer.BlockCopy(subRaw, 0, combined, ownerRaw.Length, subRaw.Length);
            var salt = SHA256.HashData(combined);
            var info = RelayCrypto.BuildCatalogHkdfInfo(request.PairIdHash, requestId);
            var aesKey = RelayCrypto.DeriveAesKey(sharedSecret, salt, info);
            var nonceBytes = RelayCrypto.RandomBytes(RelayCrypto.AeadNonceLengthBytes);

            var snapshotId = ++pairing.NextOutgoingSnapshotId;
            config.SaveNow();

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var envelope = new CatalogResponseEnvelope
            {
                PairIdHash = request.PairIdHash,
                PairEpoch = request.PairEpoch,
                RequestId = requestId,
                SnapshotId = snapshotId,
                SenderDeviceKeyId = identity.DeviceKeyId!,
                RecipientDeviceKeyId = request.RequesterDeviceKeyId,
                CreatedAt = now,
                ExpiresAt = now + RelayProtocolConstants.CatalogObjectExpirySeconds,
                CiphertextSizeBytes = 0,
                Nonce = RelayCrypto.Base64UrlEncode(nonceBytes),
                SenderEphemeralPublicKey = RelayCrypto.ExportPublicKeyJwk(subEphemeral),
            };
            var aad = CatalogResponseAad.Build(envelope);
            var ciphertext = RelayCrypto.AesGcmEncrypt(aesKey, nonceBytes, compressed, aad);
            if (ciphertext.Length > RelayProtocolConstants.CatalogCiphertextMaxBytes)
            {
                Plugin.Log.Warning("Catalog snapshot exceeds the encrypted upload limit; request left unanswered.");
                return;
            }
            envelope.CiphertextSizeBytes = ciphertext.Length;
            envelope.CiphertextDigest = RelayCrypto.Sha256Hex(ciphertext);
            envelope.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(envelope));

            await relay.UploadCatalogResponseAsync(requestId, envelope, ciphertext, ct).ConfigureAwait(false);

            // Everything else here is silent by design; a successful upload gets a brief notice.
            Plugin.NotificationManager.AddNotification(new Notification
            {
                Title = "Oathbound",
                Content = $"Sent an updated catalog to {senderName}.",
                Type = NotificationType.Success,
                InitialDuration = TimeSpan.FromSeconds(5),
            });

            // Best-effort scrub; managed arrays can still leave GC copies.
            Array.Clear(compressed);
            Array.Clear(ciphertext);
        }
        catch (RelayException ex)
        {
            Plugin.Log.Information($"Catalog snapshot upload failed: {DescribeError(ex)}");
        }
    }

    public void HandlePermissionDeniedTell(string requestId)
    {
        if (pendingOwnerRequests.Remove(requestId, out var key)) key.Dispose();
        deniedRequestIds.Add(requestId);
        config.PendingRelayOperations.RemoveAll(o => o.Kind == "catalog-request" && o.OperationId == requestId);
        config.SaveNow();
        SetError("Your Sub has not enabled catalog synchronization.");
        SetPhase("Idle");
        SetInFlight(false);
    }

    private static string DescribeError(RelayException ex) => ex.Code switch
    {
        "not_configured" => "No relay endpoint is configured.",
        "network" => "Could not reach the relay - check your connection and try again.",
        "cooldown_active" => "A refresh request is already waiting on your Sub - try again once it finishes or expires.",
        "rate_limited" => "Too many attempts - try again shortly.",
        "expired" => "That request is no longer valid.",
        "unauthorized" => "The relay rejected this request.",
        "service_unavailable" => "The relay is temporarily unavailable - try again shortly.",
        _ => "The relay request failed.",
    };
}
