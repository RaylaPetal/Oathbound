using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Oathbound.Plugin.Relay;

/// ECDSA/ECDH P-256 public key as a JWK - the wire format every relay envelope embeds a device or ephemeral
/// public key in (protocol/schemas/common.schema.json `ecPublicKeyJwk`).
public sealed class EcPublicKeyJwk
{
    [JsonPropertyName("kty")] public string Kty { get; set; } = "EC";
    [JsonPropertyName("crv")] public string Crv { get; set; } = "P-256";
    [JsonPropertyName("x")] public string X { get; set; } = "";
    [JsonPropertyName("y")] public string Y { get; set; } = "";
}

/// collar/pairing: AES-GCM ciphertext of a code invitation's character fields (see PairingCodes). The relay
/// only ever sees this opaque shape.
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
    // collar/pairing: set only on a code invitation (null -> absent, so a tell invitation's signed canonical
    // form is exactly what it always was).
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("encryptedCharacter")] public EncryptedBlob? EncryptedCharacter { get; set; }
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
    [JsonPropertyName("expiresAt")] public long ExpiresAt { get; set; }
    [JsonPropertyName("signature")] public string? Signature { get; set; }

    // Present only on a fetch response once the invitation has been accepted; never sent by this client.
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

    // Present only on a fetch response; never sent by this client.
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

/// collar/catalog-sync automatic sync: the Owner's current receive key for one pair/epoch's relay mailbox
/// (protocol/schemas/catalog-mailbox-key.schema.json), signed with the Owner's device key so the Sub verifies
/// it against the paired Owner before encrypting anything to it.
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

/// collar/catalog-sync automatic sync: a Sub-initiated snapshot left in the pair's mailbox
/// (protocol/schemas/catalog-push.schema.json) - catalog-response's shape with the receive key it was
/// encrypted to in place of a request id.
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

/// AES-GCM AAD for a catalog-push, built exactly like CatalogResponseAad (digest/nonce/signature removed,
/// ciphertextSizeBytes forced to 0) - protocol/vectors/crypto-vectors.json `ecdhHkdfAesGcmCatalogPush`.
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

/// Builds the AES-GCM additional authenticated data for a catalog-response envelope: everything except
/// ciphertextDigest, nonce, and signature (none of which are knowable before encryption happens), with
/// ciphertextSizeBytes forced to 0 as the same placeholder the Worker's own reference vectors use
/// (protocol/vectors/crypto-vectors.json `ecdhHkdfAesGcmCatalogEnvelope`) - binds the ciphertext to the
/// envelope metadata it will be uploaded alongside, without a chicken-and-egg dependency on the ciphertext's
/// own size/digest.
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

/// Builds the canonical (RFC 8785) form of any envelope DTO above, always excluding its own `Signature`
/// property - this is exactly the content every envelope's own signature covers, and exactly what a
/// verifier re-derives from a fetched envelope to check it. Reflection-driven so adding a new envelope
/// type or field never requires touching a hand-written mapping in two places.
public static class EnvelopeCanonical
{
    public static string SerializeExcludingSignature(object envelope) => CanonicalJson.Serialize(ToCanonicalValue(envelope, isRoot: true, excludeSignature: true));

    /// Canonicalizes an object's full shape with nothing excluded - used for the request-signing body
    /// digest (protocol/constants.json `requestSigning`), which covers the literal wire body verbatim,
    /// signature field included, unlike an envelope's own content signature.
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
                    // Only the root envelope's own metadata (status/acceptance) is excluded from what it
                    // signs; a nested envelope (e.g. an acceptance embedded for context) keeps its own shape.
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
