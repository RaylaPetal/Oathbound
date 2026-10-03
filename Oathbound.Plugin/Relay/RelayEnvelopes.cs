using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Oathbound.Plugin.Relay;

public sealed class EcPublicKeyJwk
{
    [JsonPropertyName("kty")] public string Kty { get; set; } = "EC";
    [JsonPropertyName("crv")] public string Crv { get; set; } = "P-256";
    [JsonPropertyName("x")] public string X { get; set; } = "";
    [JsonPropertyName("y")] public string Y { get; set; } = "";
}

/// The relay only ever sees this opaque shape.
public sealed class EncryptedBlob
{
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("ciphertext")] public string Ciphertext { get; set; } = "";
}

public sealed class InvitationEnvelope
{
    [JsonPropertyName("type")] public string Type { get; set; } = "invitation";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("invitationId")] public string InvitationId { get; set; } = "";
    [JsonPropertyName("inviterDeviceKeyId")] public string InviterDeviceKeyId { get; set; } = "";
    [JsonPropertyName("inviterPublicKey")] public EcPublicKeyJwk InviterPublicKey { get; set; } = new();
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("triggerPhrase")] public string? TriggerPhrase { get; set; }
    // Only on a code invitation; null stays absent so a tell invitation's canonical form is unchanged.
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("encryptedCharacter")] public EncryptedBlob? EncryptedCharacter { get; set; }
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
    [JsonPropertyName("signature")] public string? Signature { get; set; }

    // Fetch responses only; never sent.
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("acceptance")] public AcceptanceEnvelope? Acceptance { get; set; }
}

public sealed class AcceptanceEnvelope
{
    [JsonPropertyName("type")] public string Type { get; set; } = "acceptance";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("invitationId")] public string InvitationId { get; set; } = "";
    [JsonPropertyName("accepterDeviceKeyId")] public string AccepterDeviceKeyId { get; set; } = "";
    [JsonPropertyName("accepterPublicKey")] public EcPublicKeyJwk AccepterPublicKey { get; set; } = new();
    [JsonPropertyName("proofDigest")] public string ProofDigest { get; set; } = "";
    [JsonPropertyName("role")] public string? Role { get; set; }
    [JsonPropertyName("triggerPhrase")] public string? TriggerPhrase { get; set; }
    [JsonPropertyName("encryptedCharacter")] public EncryptedBlob? EncryptedCharacter { get; set; }
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
    [JsonPropertyName("signature")] public string? Signature { get; set; }
}

public sealed class PairEnvelope
{
    [JsonPropertyName("type")] public string Type { get; set; } = "pair";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("pairIdHash")] public string PairIdHash { get; set; } = "";
    [JsonPropertyName("pairEpoch")] public int PairEpoch { get; set; }
    [JsonPropertyName("ownerDeviceKeyId")] public string OwnerDeviceKeyId { get; set; } = "";
    [JsonPropertyName("subDeviceKeyId")] public string SubDeviceKeyId { get; set; } = "";
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
    [JsonPropertyName("revokedAt")] public long? RevokedAt { get; set; }
    [JsonPropertyName("collarState")] public string? CollarState { get; set; }
    [JsonPropertyName("collarStateAt")] public long? CollarStateAt { get; set; }
    [JsonPropertyName("collarCheckinAt")] public long? CollarCheckinAt { get; set; }
    /// Null only from a relay that predates the field, which puts automatic catalog sync back on its hourly checks.
    [JsonPropertyName("catalogMailbox")] public CatalogMailboxSummary? CatalogMailbox { get; set; }
}

public sealed class CatalogMailboxSummary
{
    [JsonPropertyName("exists")] public bool Exists { get; set; }
    [JsonPropertyName("receiveKeyId")] public string? ReceiveKeyId { get; set; }
    [JsonPropertyName("waitingSnapshotId")] public int? WaitingSnapshotId { get; set; }
    [JsonPropertyName("lastConsumedSnapshotId")] public int? LastConsumedSnapshotId { get; set; }
    [JsonPropertyName("lastUploadAt")] public long? LastUploadAt { get; set; }
}

public sealed class RevocationEnvelope
{
    [JsonPropertyName("type")] public string Type { get; set; } = "revocation";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("pairIdHash")] public string PairIdHash { get; set; } = "";
    [JsonPropertyName("pairEpoch")] public int PairEpoch { get; set; }
    [JsonPropertyName("sequence")] public int Sequence { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("issuedByDeviceKeyId")] public string IssuedByDeviceKeyId { get; set; } = "";
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
    [JsonPropertyName("signature")] public string? Signature { get; set; }
}

public sealed class CatalogRequestEnvelope
{
    [JsonPropertyName("type")] public string Type { get; set; } = "catalog-request";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("pairIdHash")] public string PairIdHash { get; set; } = "";
    [JsonPropertyName("pairEpoch")] public int PairEpoch { get; set; }
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
    [JsonPropertyName("requesterDeviceKeyId")] public string RequesterDeviceKeyId { get; set; } = "";
    [JsonPropertyName("ownerEphemeralPublicKey")] public EcPublicKeyJwk OwnerEphemeralPublicKey { get; set; } = new();
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
    [JsonPropertyName("signature")] public string? Signature { get; set; }

    // Fetch responses only; never sent.
    [JsonPropertyName("status")] public string? Status { get; set; }
}

public sealed class CatalogResponseEnvelope
{
    [JsonPropertyName("type")] public string Type { get; set; } = "catalog-response";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("pairIdHash")] public string PairIdHash { get; set; } = "";
    [JsonPropertyName("pairEpoch")] public int PairEpoch { get; set; }
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = "";
    [JsonPropertyName("snapshotId")] public int SnapshotId { get; set; }
    [JsonPropertyName("senderDeviceKeyId")] public string SenderDeviceKeyId { get; set; } = "";
    [JsonPropertyName("recipientDeviceKeyId")] public string RecipientDeviceKeyId { get; set; } = "";
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "ECDH-P256+HKDF-SHA256+AES-256-GCM";
    [JsonPropertyName("ciphertextDigest")] public string CiphertextDigest { get; set; } = "";
    [JsonPropertyName("ciphertextSizeBytes")] public int CiphertextSizeBytes { get; set; }
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("senderEphemeralPublicKey")] public EcPublicKeyJwk SenderEphemeralPublicKey { get; set; } = new();
    [JsonPropertyName("signature")] public string? Signature { get; set; }
}

/// Signed with the Owner's device key so the Sub can verify it before encrypting to it.
public sealed class CatalogMailboxKeyEnvelope
{
    [JsonPropertyName("type")] public string Type { get; set; } = "catalog-mailbox-key";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("pairIdHash")] public string PairIdHash { get; set; } = "";
    [JsonPropertyName("pairEpoch")] public int PairEpoch { get; set; }
    [JsonPropertyName("receiveKeyId")] public string ReceiveKeyId { get; set; } = "";
    [JsonPropertyName("ownerDeviceKeyId")] public string OwnerDeviceKeyId { get; set; } = "";
    [JsonPropertyName("receivePublicKey")] public EcPublicKeyJwk ReceivePublicKey { get; set; } = new();
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
    [JsonPropertyName("signature")] public string? Signature { get; set; }
}

/// catalog-response's shape, with the receive key in place of a request id.
public sealed class CatalogPushEnvelope
{
    [JsonPropertyName("type")] public string Type { get; set; } = "catalog-push";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("pairIdHash")] public string PairIdHash { get; set; } = "";
    [JsonPropertyName("pairEpoch")] public int PairEpoch { get; set; }
    [JsonPropertyName("receiveKeyId")] public string ReceiveKeyId { get; set; } = "";
    [JsonPropertyName("snapshotId")] public int SnapshotId { get; set; }
    [JsonPropertyName("senderDeviceKeyId")] public string SenderDeviceKeyId { get; set; } = "";
    [JsonPropertyName("recipientDeviceKeyId")] public string RecipientDeviceKeyId { get; set; } = "";
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "ECDH-P256+HKDF-SHA256+AES-256-GCM";
    [JsonPropertyName("ciphertextDigest")] public string CiphertextDigest { get; set; } = "";
    [JsonPropertyName("ciphertextSizeBytes")] public int CiphertextSizeBytes { get; set; }
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("senderEphemeralPublicKey")] public EcPublicKeyJwk SenderEphemeralPublicKey { get; set; } = new();
    [JsonPropertyName("signature")] public string? Signature { get; set; }
}

/// Built like CatalogResponseAad (vector: ecdhHkdfAesGcmCatalogPush).
public static class CatalogPushAad
{
    public static byte[] Build(CatalogPushEnvelope envelope)
    {
        var dict = new Dictionary<string, object?>
        {
            ["type"] = envelope.Type,
            ["schemaVersion"] = envelope.SchemaVersion,
            ["pairIdHash"] = envelope.PairIdHash,
            ["pairEpoch"] = envelope.PairEpoch,
            ["receiveKeyId"] = envelope.ReceiveKeyId,
            ["snapshotId"] = envelope.SnapshotId,
            ["senderDeviceKeyId"] = envelope.SenderDeviceKeyId,
            ["recipientDeviceKeyId"] = envelope.RecipientDeviceKeyId,
            ["createdAt"] = envelope.CreatedAt,
            ["expiresAt"] = envelope.ExpiresAt,
            ["algorithm"] = envelope.Algorithm,
            ["ciphertextSizeBytes"] = 0,
            ["senderEphemeralPublicKey"] = new Dictionary<string, object?>
            {
                ["kty"] = envelope.SenderEphemeralPublicKey.Kty,
                ["crv"] = envelope.SenderEphemeralPublicKey.Crv,
                ["x"] = envelope.SenderEphemeralPublicKey.X,
                ["y"] = envelope.SenderEphemeralPublicKey.Y,
            },
        };
        return System.Text.Encoding.UTF8.GetBytes(CanonicalJson.Serialize(dict));
    }
}

/// Everything except ciphertextDigest, nonce and signature, with ciphertextSizeBytes forced to 0 - binds the
/// ciphertext to its envelope without depending on its own size/digest.
public static class CatalogResponseAad
{
    public static byte[] Build(CatalogResponseEnvelope envelope)
    {
        var dict = new Dictionary<string, object?>
        {
            ["type"] = envelope.Type,
            ["schemaVersion"] = envelope.SchemaVersion,
            ["pairIdHash"] = envelope.PairIdHash,
            ["pairEpoch"] = envelope.PairEpoch,
            ["requestId"] = envelope.RequestId,
            ["snapshotId"] = envelope.SnapshotId,
            ["senderDeviceKeyId"] = envelope.SenderDeviceKeyId,
            ["recipientDeviceKeyId"] = envelope.RecipientDeviceKeyId,
            ["createdAt"] = envelope.CreatedAt,
            ["expiresAt"] = envelope.ExpiresAt,
            ["algorithm"] = envelope.Algorithm,
            ["ciphertextSizeBytes"] = 0,
            ["senderEphemeralPublicKey"] = new Dictionary<string, object?>
            {
                ["kty"] = envelope.SenderEphemeralPublicKey.Kty,
                ["crv"] = envelope.SenderEphemeralPublicKey.Crv,
                ["x"] = envelope.SenderEphemeralPublicKey.X,
                ["y"] = envelope.SenderEphemeralPublicKey.Y,
            },
        };
        return System.Text.Encoding.UTF8.GetBytes(CanonicalJson.Serialize(dict));
    }
}

/// RFC 8785 form of an envelope without its Signature - exactly what the signature covers. Reflection-driven,
/// so a new field never needs a hand-written mapping.
public static class EnvelopeCanonical
{
    public static string SerializeExcludingSignature(object envelope) => CanonicalJson.Serialize(ToCanonicalValue(envelope, isRoot: true, excludeSignature: true));

    /// Nothing excluded: the request-signing digest covers the literal wire body.
    public static string SerializeFull(object? value) => CanonicalJson.Serialize(ToCanonicalValue(value, isRoot: false, excludeSignature: false));

    private static object? ToCanonicalValue(object? value, bool isRoot, bool excludeSignature)
    {
        switch (value)
        {
            case null:
                return null;
            case string or int or long or short or byte or bool:
                return value;
            case EcPublicKeyJwk jwk:
                return new Dictionary<string, object?> { ["kty"] = jwk.Kty, ["crv"] = jwk.Crv, ["x"] = jwk.X, ["y"] = jwk.Y };
            default:
                var dict = new Dictionary<string, object?>();
                foreach (var prop in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (excludeSignature && prop.Name == nameof(InvitationEnvelope.Signature)) continue;
                    // Only the root envelope's metadata is excluded; a nested envelope keeps its shape.
                    if (isRoot && (prop.Name == nameof(InvitationEnvelope.Status) || prop.Name == nameof(InvitationEnvelope.Acceptance) || prop.Name == nameof(CatalogRequestEnvelope.Status)))
                        continue;

                    var propValue = prop.GetValue(value);
                    if (propValue is null) continue; // Absent, not null -- matches the wire schemas' optional fields.

                    var jsonName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? prop.Name;
                    dict[jsonName] = ToCanonicalValue(propValue, isRoot: false, excludeSignature);
                }
                return dict;
        }
    }
}
