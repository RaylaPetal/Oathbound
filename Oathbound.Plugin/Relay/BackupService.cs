using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

public enum RestoreOutcome
{
    Restored,
    NeedsConfirmation,
    InvalidCode,
    NotFound,
    Failed,
}

/// Encrypted relay backup of the identity and pairings under the recovery code; the relay stores only ciphertext
/// under a hashed id derived from the code. Restoring brings back the identity itself, so no re-pairing is needed.
public sealed class BackupService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UnsupportedRetryDelay = TimeSpan.FromHours(1);
    private static readonly byte[] CodeEntropy = "oathbound-recovery-code-v1"u8.ToArray();
    private const int MaxBackupPlaintextBytes = 256 * 1024;

    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly DeviceIdentityService identity;
    private readonly RevocationService revocation;

    private DateTime nextCheckUtc = DateTime.MinValue;
    private DateTime retryNotBeforeUtc = DateTime.MinValue;
    private int busy;

    public BackupService(PluginConfig config, RelayClient relay, DeviceIdentityService identity, RevocationService revocation)
    {
        this.config = config;
        this.relay = relay;
        this.identity = identity;
        this.revocation = revocation;
    }

    public bool HasCode => config.Recovery.HasCode;

    public bool ShouldShowCodeDialog => config.Recovery.HasCode && !config.Recovery.CodeAcknowledged;

    public bool IsUpToDate => config.Recovery.UploadedFingerprint is not null && config.Recovery.UploadedFingerprint == Fingerprint();
    public DateTimeOffset? LastUploadAt => config.Recovery.LastUploadAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(config.Recovery.LastUploadAt) : null;

    public string? LastError { get; private set; }

    public void AcknowledgeCode()
    {
        config.Recovery.CodeAcknowledged = true;
        config.SaveNow();
    }

    /// Groups of four.
    public string? GetFormattedCode() => ReadCode() is { } code ? PairingCodes.Format(code) : null;

    private string? ReadCode()
    {
        if (!config.Recovery.HasCode) return null;
        try
        {
            var bytes = DeviceIdentityService.Unprotect(config.Recovery.ProtectedCode!, config.Recovery.IsProtected, CodeEntropy);
            return PairingCodes.Normalize(Encoding.UTF8.GetString(bytes), PairingCodes.RecoveryCodeChars);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Recovery code could not be read on this machine.");
            return null;
        }
    }

    private void StoreCode(string normalizedCode, bool acknowledged)
    {
        var (data, wasProtected) = DeviceIdentityService.Protect(Encoding.UTF8.GetBytes(normalizedCode), CodeEntropy);
        config.Recovery.ProtectedCode = data;
        config.Recovery.IsProtected = wasProtected;
        config.Recovery.CodeAcknowledged = acknowledged;
        config.Recovery.UploadedFingerprint = null;
        config.SaveNow();
    }

    /// Issues a code once the first pairing exists, and keeps the relay copy current.
    public void OnFrameworkUpdate(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now < nextCheckUtc) return;
        nextCheckUtc = now + CheckInterval;

        if (!config.Recovery.HasCode && config.Pairings.Any(p => p.IsPaired) && identity.HasIdentity)
            StoreCode(PairingCodes.NewRecoveryCode(), acknowledged: false);

        if (!config.Recovery.HasCode || now < retryNotBeforeUtc) return;
        if (IsUpToDate && config.Recovery.PendingDeletes.Count == 0) return;
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        Plugin.FireAndForget(SyncAsync(ct));
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        try
        {
            if (ReadCode() is not { } code) return;
            var plaintext = BuildPlaintext();
            var fingerprint = Fingerprint();
            if (config.Recovery.UploadedFingerprint != fingerprint)
            {
                var (nonce, ciphertext) = PairingCodes.EncryptBackup(code, RelayCompression.Compress(plaintext));
                await relay.PutBackupAsync(PairingCodes.BackupId(code), nonce, ciphertext, ct).ConfigureAwait(false);
                config.Recovery.UploadedFingerprint = fingerprint;
                config.Recovery.LastUploadAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                config.SaveNow();
            }

            foreach (var oldBackupId in config.Recovery.PendingDeletes.ToList())
            {
                await relay.DeleteBackupAsync(oldBackupId, ct).ConfigureAwait(false);
                config.Recovery.PendingDeletes.Remove(oldBackupId);
                config.SaveNow();
            }
            LastError = null;
        }
        catch (RelayException ex) when (ex.Code == "not_found")
        {
            // The relay predates backups - not the player's fault, so say so and check back rarely.
            LastError = "Backups aren't available on the relay yet - your recovery code will be used once they are.";
            retryNotBeforeUtc = DateTime.UtcNow + UnsupportedRetryDelay;
        }
        catch (RelayException ex)
        {
            LastError = ex.Code == "network" ? "Couldn't reach the relay to update your backup - it will retry." : $"Backup update failed ({ex.Code}) - it will retry.";
            retryNotBeforeUtc = DateTime.UtcNow + RetryDelay;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or DeviceIdentityUnavailableException)
        {
            LastError = "Your backup couldn't be prepared on this machine.";
            Plugin.Log.Warning(ex, "Backup preparation failed.");
            retryNotBeforeUtc = DateTime.UtcNow + RetryDelay;
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }

    /// The old copy is deleted from the relay.
    public void Regenerate()
    {
        if (ReadCode() is { } old)
            config.Recovery.PendingDeletes.Add(PairingCodes.BackupId(old));
        StoreCode(PairingCodes.NewRecoveryCode(), acknowledged: false);
        nextCheckUtc = DateTime.MinValue;
        retryNotBeforeUtc = DateTime.MinValue;
    }

    /// Runs while the old key can still sign. Best effort.
    public async Task DeleteForIdentityResetAsync(CancellationToken ct)
    {
        if (ReadCode() is { } code)
        {
            try { await relay.DeleteBackupAsync(PairingCodes.BackupId(code), ct).ConfigureAwait(false); }
            catch (RelayException ex) { Plugin.Log.Information($"Backup delete on identity reset failed: {ex.Code}."); }
        }
        config.Recovery = new RecoveryState();
        config.SaveNow();
    }

    /// NeedsConfirmation, changing nothing, when this install holds pairings under a different identity.
    public async Task<(RestoreOutcome Outcome, int Restored, int Dropped)> RestoreAsync(string typedCode, bool replaceConfirmed, CancellationToken ct)
    {
        var code = PairingCodes.Normalize(typedCode, PairingCodes.RecoveryCodeChars);
        if (code is null) return (RestoreOutcome.InvalidCode, 0, 0);

        StoredBackup stored;
        try
        {
            stored = await relay.FetchBackupAsync(PairingCodes.BackupId(code), ct).ConfigureAwait(false);
        }
        catch (RelayException ex) when (ex.Code == "not_found")
        {
            return (RestoreOutcome.NotFound, 0, 0);
        }
        catch (RelayException ex)
        {
            LastError = ex.Code == "rate_limited" ? "Too many attempts - try again later." : "Couldn't reach the relay - try again.";
            return (RestoreOutcome.Failed, 0, 0);
        }

        BackupPayload? payload;
        try
        {
            var compressed = PairingCodes.DecryptBackup(code, stored.Nonce, stored.Ciphertext);
            if (compressed is null) return (RestoreOutcome.NotFound, 0, 0);
            payload = JsonSerializer.Deserialize<BackupPayload>(RelayCompression.Decompress(compressed, MaxBackupPlaintextBytes));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or InvalidDataException)
        {
            payload = null;
        }
        if (payload is not { SchemaVersion: 1, Identity: { } restoredIdentity })
        {
            LastError = "That backup couldn't be read.";
            return (RestoreOutcome.Failed, 0, 0);
        }

        var restoredKeyId = RelayCrypto.DeviceKeyId(new EcPublicKeyJwk { X = restoredIdentity.PublicKeyX, Y = restoredIdentity.PublicKeyY });
        if (restoredKeyId != stored.DeviceKeyId)
        {
            LastError = "That backup doesn't match the identity it was stored for.";
            return (RestoreOutcome.Failed, 0, 0);
        }

        var replacingOtherPairings = identity.DeviceKeyId != restoredKeyId && config.Pairings.Any(p => p.IsPaired);
        if (replacingOtherPairings && !replaceConfirmed)
            return (RestoreOutcome.NeedsConfirmation, 0, 0);

        try
        {
            identity.ImportFromBackup(restoredIdentity.PublicKeyX, restoredIdentity.PublicKeyY, RelayCrypto.Base64UrlDecode(restoredIdentity.PrivateD));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            LastError = "The identity in that backup is invalid.";
            Plugin.Log.Warning(ex, "Restore: identity import failed.");
            return (RestoreOutcome.Failed, 0, 0);
        }

        config.Pairings = payload.Pairings.Select(p => p.ToState()).ToList();
        config.ActivePairingId = payload.ActivePairingId is { } active && config.Pairings.Any(p => p.Id == active)
            ? active
            : config.Pairings.FirstOrDefault()?.Id;
        config.CodeInvitations.Clear();
        config.PendingRelayOperations.Clear();
        config.RevocationOutbox.Clear();
        StoreCode(code, acknowledged: true);

        // Drop anything a partner ended while this install was gone.
        var before = config.Pairings.Count(p => p.IsPaired);
        await revocation.CheckPairStatusAsync(ct).ConfigureAwait(false);
        var after = config.Pairings.Count(p => p.IsPaired);
        config.Pairings.RemoveAll(p => !p.IsPaired);
        config.SaveNow();

        nextCheckUtc = DateTime.MinValue;
        LastError = null;
        return (RestoreOutcome.Restored, after, before - after);
    }

    private byte[] BuildPlaintext()
    {
        var (x, y, d) = identity.ExportForBackup();
        try
        {
            var payload = new BackupPayload
            {
                SchemaVersion = 1,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Identity = new BackupIdentity { PublicKeyX = x, PublicKeyY = y, PrivateD = RelayCrypto.Base64UrlEncode(d) },
                Pairings = config.Pairings.Where(p => p.IsPaired).Select(BackupPairing.From).ToList(),
                ActivePairingId = config.ActivePairingId,
            };
            return JsonSerializer.SerializeToUtf8Bytes(payload);
        }
        finally
        {
            Array.Clear(d);
        }
    }

    /// Minus the timestamp - a change means the relay copy is stale.
    private string Fingerprint()
    {
        var sb = new StringBuilder(identity.DeviceKeyId ?? "");
        sb.Append('|').Append(config.ActivePairingId);
        foreach (var p in config.Pairings.Where(p => p.IsPaired).OrderBy(p => p.Id))
            sb.Append('|').Append(JsonSerializer.Serialize(BackupPairing.From(p)));
        return RelayCrypto.Sha256Hex(sb.ToString());
    }

    private sealed class BackupPayload
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
        [JsonPropertyName("identity")] public BackupIdentity? Identity { get; set; }
        [JsonPropertyName("pairings")] public List<BackupPairing> Pairings { get; set; } = new();
        [JsonPropertyName("activePairingId")] public Guid? ActivePairingId { get; set; }
    }

    private sealed class BackupIdentity
    {
        [JsonPropertyName("x")] public string PublicKeyX { get; set; } = "";
        [JsonPropertyName("y")] public string PublicKeyY { get; set; } = "";
        [JsonPropertyName("d")] public string PrivateD { get; set; } = "";
    }

    /// Catalog/sync bookkeeping is left out; it rebuilds on the next sync.
    private sealed class BackupPairing
    {
        [JsonPropertyName("id")] public Guid Id { get; set; }
        [JsonPropertyName("direction")] public PairingDirection Direction { get; set; }
        [JsonPropertyName("pairIdHash")] public string? PairIdHash { get; set; }
        [JsonPropertyName("pairEpoch")] public int PairEpoch { get; set; }
        [JsonPropertyName("peerDeviceKeyId")] public string? PeerDeviceKeyId { get; set; }
        [JsonPropertyName("peerPublicKeyX")] public string? PeerPublicKeyX { get; set; }
        [JsonPropertyName("peerPublicKeyY")] public string? PeerPublicKeyY { get; set; }
        [JsonPropertyName("peerName")] public string? PeerName { get; set; }
        [JsonPropertyName("peerWorld")] public string? PeerWorld { get; set; }
        [JsonPropertyName("peerTriggerPhrase")] public string? PeerTriggerPhrase { get; set; }
        [JsonPropertyName("outgoingRevocationSequence")] public int OutgoingRevocationSequence { get; set; }
        [JsonPropertyName("incomingRevocationSequence")] public int IncomingRevocationSequence { get; set; }

        public static BackupPairing From(PairingState p) => new()
        {
            Id = p.Id,
            Direction = p.Direction,
            PairIdHash = p.PairIdHash,
            PairEpoch = p.PairEpoch,
            PeerDeviceKeyId = p.PeerDeviceKeyId,
            PeerPublicKeyX = p.PeerPublicKeyX,
            PeerPublicKeyY = p.PeerPublicKeyY,
            PeerName = p.PeerName,
            PeerWorld = p.PeerWorld,
            PeerTriggerPhrase = p.PeerTriggerPhrase,
            OutgoingRevocationSequence = p.OutgoingRevocationSequence,
            IncomingRevocationSequence = p.IncomingRevocationSequence,
        };

        public PairingState ToState() => new()
        {
            Id = Id,
            Direction = Direction,
            PairIdHash = PairIdHash,
            PairEpoch = PairEpoch,
            PeerDeviceKeyId = PeerDeviceKeyId,
            PeerPublicKeyX = PeerPublicKeyX,
            PeerPublicKeyY = PeerPublicKeyY,
            PeerName = PeerName,
            PeerWorld = PeerWorld,
            PeerTriggerPhrase = PeerTriggerPhrase,
            OutgoingRevocationSequence = OutgoingRevocationSequence,
            IncomingRevocationSequence = IncomingRevocationSequence,
            Paired = true,
        };
    }
}
