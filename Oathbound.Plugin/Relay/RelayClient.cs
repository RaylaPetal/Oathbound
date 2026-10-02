using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// The only exception RelayClient throws. Codes match error.schema.json, plus local "not_configured"/"network".
public sealed class RelayException : Exception
{
    public string Code { get; }
    public int? RetryAfterSeconds { get; }

    public RelayException(string code, int? retryAfterSeconds, string message) : base(message)
    {
        Code = code;
        RetryAfterSeconds = retryAfterSeconds;
    }
}

internal sealed class ErrorBody
{
    [JsonPropertyName("code")] public string Code { get; set; } = "invalid_request";
    [JsonPropertyName("retryAfterSeconds")] public int? RetryAfterSeconds { get; set; }
}

internal sealed class InvitationStatusBody
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
}
internal sealed class BackupBody
{
    [JsonPropertyName("type")] public string Type { get; set; } = "backup";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("ciphertext")] public string Ciphertext { get; set; } = "";
}
public sealed class StoredBackup
{
    [JsonPropertyName("deviceKeyId")] public string DeviceKeyId { get; set; } = "";
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("ciphertext")] public string Ciphertext { get; set; } = "";
    [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }
}
internal sealed class CollarStatusBody
{
    [JsonPropertyName("type")] public string Type { get; set; } = "collar-status";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("pairEpoch")] public int PairEpoch { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("stateAt")] public long StateAt { get; set; }
}

internal sealed class CollarStatusAck
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}

internal sealed class BackupStatusBody
{
    [JsonPropertyName("updatedAt")] public long? UpdatedAt { get; set; }
    [JsonPropertyName("deleted")] public bool? Deleted { get; set; }
}
internal sealed class RevocationListBody
{
    [JsonPropertyName("revocations")] public RevocationEnvelope[] Revocations { get; set; } = [];
}

internal sealed class CatalogUploadRequestBody
{
    [JsonPropertyName("envelope")] public CatalogResponseEnvelope Envelope { get; set; } = new();
    [JsonPropertyName("ciphertextBase64Url")] public string CiphertextBase64Url { get; set; } = "";
}

internal sealed class CatalogConsumeResponseBody
{
    [JsonPropertyName("envelope")] public CatalogResponseEnvelope Envelope { get; set; } = new();
    [JsonPropertyName("ciphertextBase64Url")] public string CiphertextBase64Url { get; set; } = "";
}

internal sealed class MailboxPairRefBody
{
    [JsonPropertyName("pairIdHash")] public string PairIdHash { get; set; } = "";
    [JsonPropertyName("pairEpoch")] public int PairEpoch { get; set; }
}

internal sealed class MailboxPublishKeyBody
{
    [JsonPropertyName("pairIdHash")] public string PairIdHash { get; set; } = "";
    [JsonPropertyName("pairEpoch")] public int PairEpoch { get; set; }
    [JsonPropertyName("key")] public CatalogMailboxKeyEnvelope Key { get; set; } = new();
}

internal sealed class MailboxUploadBody
{
    [JsonPropertyName("envelope")] public CatalogPushEnvelope Envelope { get; set; } = new();
    [JsonPropertyName("ciphertextBase64Url")] public string CiphertextBase64Url { get; set; } = "";
}

internal sealed class MailboxConsumeBody
{
    [JsonPropertyName("pairIdHash")] public string PairIdHash { get; set; } = "";
    [JsonPropertyName("pairEpoch")] public int PairEpoch { get; set; }
    [JsonPropertyName("snapshotId")] public int SnapshotId { get; set; }
    [JsonPropertyName("nextKey")] public CatalogMailboxKeyEnvelope NextKey { get; set; } = new();
}

internal sealed class MailboxConsumeResponseBody
{
    [JsonPropertyName("envelope")] public CatalogPushEnvelope Envelope { get; set; } = new();
    [JsonPropertyName("ciphertextBase64Url")] public string CiphertextBase64Url { get; set; } = "";
}

/// The Owner's current receive key plus the delivery receipt.
public sealed class CatalogMailboxKeyInfo
{
    [JsonPropertyName("key")] public CatalogMailboxKeyEnvelope Key { get; set; } = new();
    [JsonPropertyName("waitingSnapshotId")] public int? WaitingSnapshotId { get; set; }
    [JsonPropertyName("lastConsumedSnapshotId")] public int? LastConsumedSnapshotId { get; set; }
}

public sealed class CatalogMailboxStatus
{
    [JsonPropertyName("hasKey")] public bool HasKey { get; set; }
    [JsonPropertyName("receiveKeyId")] public string? ReceiveKeyId { get; set; }
    [JsonPropertyName("hasSnapshot")] public bool HasSnapshot { get; set; }
    [JsonPropertyName("snapshotId")] public int? SnapshotId { get; set; }
    [JsonPropertyName("snapshotCreatedAt")] public long? SnapshotCreatedAt { get; set; }
    [JsonPropertyName("lastUploadAt")] public long? LastUploadAt { get; set; }
}

/// The plugin's one HTTP boundary to the relay. Mutating calls are signed with the device key; reads are
/// capability-only. Never retries here - callers own retry/backoff.
public sealed class RelayClient : IDisposable
{
    public const string RelayOrigin = "https://oathbound-relay-staging.oathbound.workers.dev";
    private static readonly Uri RelayBaseUri = new(RelayOrigin, UriKind.Absolute);
    private const int MaxJsonResponseBytes = RelayProtocolConstants.CatalogCiphertextMaxBytes * 2;
    /// Omitting nulls is load-bearing: the server would canonicalize `"x":null` as a present key and every signature would fail.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient http;
    private readonly DeviceIdentityService identity;
    public bool? LastReachable { get; private set; }
    public DateTimeOffset? LastReachabilityCheck { get; private set; }

    public RelayClient(PluginConfig config, DeviceIdentityService identity, HttpMessageHandler? testHandler = null)
    {
        _ = config; // Kept in the constructor for DI compatibility; relay routing is release-pinned.
        this.identity = identity;
        http = new HttpClient(testHandler ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
            BaseAddress = RelayBaseUri,
            MaxResponseContentBufferSize = MaxJsonResponseBytes,
        };
    }

    public bool IsConfigured => true;

    public void Dispose() => http.Dispose();
    public void CancelPendingRequests() => http.CancelPendingRequests();

    // ---- Invitations ----

    public Task<InvitationEnvelope> CreateInvitationAsync(InvitationEnvelope envelope, CancellationToken ct) =>
        SendSignedAsync<InvitationEnvelope>(HttpMethod.Post, "/v1/invitations", envelope, ct);

    public Task<InvitationEnvelope> FetchInvitationAsync(string invitationId, CancellationToken ct) =>
        SendUnsignedAsync<InvitationEnvelope>(HttpMethod.Get, $"/v1/invitations/{invitationId}", ct);

    public Task<AcceptanceEnvelope> AcceptInvitationAsync(string invitationId, AcceptanceEnvelope envelope, CancellationToken ct) =>
        SendSignedAsync<AcceptanceEnvelope>(HttpMethod.Post, $"/v1/invitations/{invitationId}/accept", envelope, ct);

    public Task<PairEnvelope> ConsumeInvitationAsync(string invitationId, CancellationToken ct) =>
        SendSignedAsync<PairEnvelope>(HttpMethod.Post, $"/v1/invitations/{invitationId}/consume", null, ct);
    /// "cancelled" if unused, "rejected" if it was already accepted.
    public async Task<string> CancelInvitationAsync(string invitationId, CancellationToken ct) =>
        (await SendSignedAsync<InvitationStatusBody>(HttpMethod.Post, $"/v1/invitations/{invitationId}/cancel", null, ct).ConfigureAwait(false)).Status;

    // ---- Pairs ----

    public Task<PairEnvelope> FetchPairAsync(string pairIdHash, CancellationToken ct) =>
        SendSignedAsync<PairEnvelope>(HttpMethod.Get, $"/v1/pairs/{pairIdHash}", null, ct);
    /// One exact pairing's row, so a mutual pair's other epoch is never mistaken for it.
    public Task<PairEnvelope> FetchPairAtEpochAsync(string pairIdHash, int pairEpoch, CancellationToken ct) =>
        SendSignedAsync<PairEnvelope>(HttpMethod.Get, $"/v1/pairs/{pairIdHash}?epoch={pairEpoch}", null, ct);
    // ---- Backups ----
    /// Sub only: the relay accepts it from this pair epoch's Sub device alone.
    public Task PutCollarStatusAsync(string pairIdHash, int pairEpoch, string state, long stateAt, CancellationToken ct) =>
        SendSignedAsync<CollarStatusAck>(HttpMethod.Post, $"/v1/pairs/{pairIdHash}/collar-status",
            new CollarStatusBody { PairEpoch = pairEpoch, State = state, StateAt = stateAt }, ct);

    public Task PutBackupAsync(string backupId, string nonce, string ciphertext, CancellationToken ct) =>
        SendSignedAsync<BackupStatusBody>(HttpMethod.Put, $"/v1/backups/{backupId}", new BackupBody { Nonce = nonce, Ciphertext = ciphertext }, ct);
    public Task<StoredBackup> FetchBackupAsync(string backupId, CancellationToken ct) =>
        SendUnsignedAsync<StoredBackup>(HttpMethod.Get, $"/v1/backups/{backupId}", ct);
    public Task DeleteBackupAsync(string backupId, CancellationToken ct) =>
        SendSignedAsync<BackupStatusBody>(HttpMethod.Delete, $"/v1/backups/{backupId}", null, ct);

    // ---- Revocations ----

    public Task<RevocationEnvelope> PublishRevocationAsync(RevocationEnvelope envelope, CancellationToken ct) =>
        SendSignedAsync<RevocationEnvelope>(HttpMethod.Post, "/v1/revocations", envelope, ct);

    public async Task<RevocationEnvelope[]> CheckRevocationsAsync(string pairIdHash, int sinceSequence, CancellationToken ct)
    {
        var body = await SendSignedAsync<RevocationListBody>(HttpMethod.Get, $"/v1/revocations/{pairIdHash}?sinceSequence={sinceSequence}", null, ct).ConfigureAwait(false);
        return body.Revocations;
    }

    // ---- Catalog sync ----

    public Task<CatalogRequestEnvelope> CreateCatalogRequestAsync(CatalogRequestEnvelope envelope, CancellationToken ct) =>
        SendSignedAsync<CatalogRequestEnvelope>(HttpMethod.Post, "/v1/catalog/requests", envelope, ct);

    public Task<CatalogRequestEnvelope> FetchCatalogRequestAsync(string requestId, CancellationToken ct) =>
        SendUnsignedAsync<CatalogRequestEnvelope>(HttpMethod.Get, $"/v1/catalog/requests/{requestId}", ct);

    public Task<CatalogResponseEnvelope> UploadCatalogResponseAsync(string requestId, CatalogResponseEnvelope envelope, byte[] ciphertext, CancellationToken ct)
    {
        var body = new CatalogUploadRequestBody { Envelope = envelope, CiphertextBase64Url = RelayCrypto.Base64UrlEncode(ciphertext) };
        return SendSignedAsync<CatalogResponseEnvelope>(HttpMethod.Post, $"/v1/catalog/requests/{requestId}/upload", body, ct);
    }

    public async Task<(CatalogResponseEnvelope Envelope, byte[] Ciphertext)> ConsumeCatalogResponseAsync(string requestId, CancellationToken ct)
    {
        var body = await SendSignedAsync<CatalogConsumeResponseBody>(HttpMethod.Post, $"/v1/catalog/requests/{requestId}/consume", null, ct).ConfigureAwait(false);
        return (body.Envelope, RelayCrypto.Base64UrlDecode(body.CiphertextBase64Url));
    }

    // ---- Catalog mailbox (automatic sync) ----
    // `not_found` from Fetch/Upload means no receive key was published; from Status it means a relay without the
    // mailbox, which callers treat as "automatic sync unsupported".

    public Task<CatalogMailboxKeyEnvelope> PublishMailboxKeyAsync(CatalogMailboxKeyEnvelope key, CancellationToken ct) =>
        SendSignedAsync<CatalogMailboxKeyEnvelope>(HttpMethod.Post, "/v1/catalog/mailbox/key",
            new MailboxPublishKeyBody { PairIdHash = key.PairIdHash, PairEpoch = key.PairEpoch, Key = key }, ct);

    public Task<CatalogMailboxKeyInfo> FetchMailboxKeyAsync(string pairIdHash, int pairEpoch, CancellationToken ct) =>
        SendSignedAsync<CatalogMailboxKeyInfo>(HttpMethod.Post, "/v1/catalog/mailbox/key/fetch",
            new MailboxPairRefBody { PairIdHash = pairIdHash, PairEpoch = pairEpoch }, ct);

    public Task<CatalogPushEnvelope> UploadMailboxSnapshotAsync(CatalogPushEnvelope envelope, byte[] ciphertext, CancellationToken ct) =>
        SendSignedAsync<CatalogPushEnvelope>(HttpMethod.Post, "/v1/catalog/mailbox/upload",
            new MailboxUploadBody { Envelope = envelope, CiphertextBase64Url = RelayCrypto.Base64UrlEncode(ciphertext) }, ct);

    public Task<CatalogMailboxStatus> FetchMailboxStatusAsync(string pairIdHash, int pairEpoch, CancellationToken ct) =>
        SendSignedAsync<CatalogMailboxStatus>(HttpMethod.Post, "/v1/catalog/mailbox/status",
            new MailboxPairRefBody { PairIdHash = pairIdHash, PairEpoch = pairEpoch }, ct);

    public async Task<(CatalogPushEnvelope Envelope, byte[] Ciphertext)> ConsumeMailboxSnapshotAsync(string pairIdHash, int pairEpoch, int snapshotId, CatalogMailboxKeyEnvelope nextKey, CancellationToken ct)
    {
        var body = await SendSignedAsync<MailboxConsumeResponseBody>(HttpMethod.Post, "/v1/catalog/mailbox/consume",
            new MailboxConsumeBody { PairIdHash = pairIdHash, PairEpoch = pairEpoch, SnapshotId = snapshotId, NextKey = nextKey }, ct).ConfigureAwait(false);
        return (body.Envelope, RelayCrypto.Base64UrlDecode(body.CiphertextBase64Url));
    }

    // ---- Transport ----

    private async Task<TResponse> SendSignedAsync<TResponse>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var deviceKeyId = identity.DeviceKeyId ?? throw new RelayException("not_configured", null, "No device identity exists yet.");
        var signingKey = identity.GetSigningKey();

        var bodyJson = body is null ? "{}" : JsonSerializer.Serialize(body, body.GetType(), JsonOptions);
        if (Encoding.UTF8.GetByteCount(bodyJson) > MaxJsonResponseBytes)
            throw new RelayException("payload_too_large", null, "Relay request exceeded the local payload limit.");
        // The Worker treats an absent body as {}, so the digest must be computed over the same value.
        var bodyCanonical = body is null ? CanonicalJson.Serialize(new Dictionary<string, object?>()) : EnvelopeCanonical.SerializeFull(body);
        var bodyDigest = RelayCrypto.Sha256Hex(bodyCanonical);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = RelayCrypto.RandomNonce();
        var baseString = string.Join('\n', method.Method, path.Split('?')[0], bodyDigest, timestamp.ToString(), nonce);
        var signature = RelayCrypto.SignRaw(signingKey, baseString);

        using var request = new HttpRequestMessage(method, new Uri(RelayBaseUri, path));
        request.Headers.Add("x-relay-device-key-id", deviceKeyId);
        request.Headers.Add("x-relay-timestamp", timestamp.ToString());
        request.Headers.Add("x-relay-nonce", nonce);
        request.Headers.Add("x-relay-signature", signature);
        if (method != HttpMethod.Get && method != HttpMethod.Head)
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        return await SendAsync<TResponse>(request, ct).ConfigureAwait(false);
    }

    /// The capability id in the path is the proof of possession, so no signature.
    private async Task<TResponse> SendUnsignedAsync<TResponse>(HttpMethod method, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(RelayBaseUri, path));
        return await SendAsync<TResponse>(request, ct).ConfigureAwait(false);
    }

    private async Task<TResponse> SendAsync<TResponse>(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            RecordReachability(false);
            throw new RelayException("network", null, "Relay request timed out.");
        }
        catch (HttpRequestException ex)
        {
            RecordReachability(false);
            throw new RelayException("network", null, $"Relay request failed: {ex.Message}");
        }

        using (response)
        {
            RecordReachability(true);
            if (response.Content.Headers.ContentLength is > MaxJsonResponseBytes)
                throw new RelayException("payload_too_large", null, "Relay response exceeded the local payload limit.");
            var text = await ReadBoundedStringAsync(response.Content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                int? retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)delta.TotalSeconds : null;
                string code;
                try
                {
                    var error = JsonSerializer.Deserialize<ErrorBody>(text, JsonOptions);
                    code = error?.Code ?? "invalid_request";
                    retryAfter ??= error?.RetryAfterSeconds;
                }
                catch (JsonException)
                {
                    code = "invalid_request";
                }
                throw new RelayException(code, retryAfter, $"Relay returned {(int)response.StatusCode} ({code}).");
            }

            try
            {
                return JsonSerializer.Deserialize<TResponse>(text, JsonOptions) ?? throw new RelayException("invalid_request", null, "Relay returned an empty body.");
            }
            catch (JsonException ex)
            {
                throw new RelayException("invalid_request", null, $"Relay returned an unparseable body: {ex.Message}");
            }
        }
    }

    private void RecordReachability(bool reachable)
    {
        LastReachable = reachable;
        LastReachabilityCheck = DateTimeOffset.UtcNow;
    }

    private static async Task<string> ReadBoundedStringAsync(HttpContent content, CancellationToken ct)
    {
        await using var source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0) break;
            if (target.Length + read > MaxJsonResponseBytes)
                throw new RelayException("payload_too_large", null, "Relay response exceeded the local payload limit.");
            target.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(target.ToArray());
    }

}
