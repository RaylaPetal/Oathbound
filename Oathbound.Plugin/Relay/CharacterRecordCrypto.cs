using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// The decrypted characters record one side of a pair publishes. Short property names keep it small; the relay never
/// sees it. Must match protocol/schemas/pair-characters.schema.json#/$defs/plaintext.
public sealed class CharacterRecord
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("seq")] public long Seq { get; set; }
    [JsonPropertyName("at")] public long At { get; set; }
    [JsonPropertyName("linked")] public List<CharacterRecordEntry> Linked { get; set; } = new();
    [JsonPropertyName("unlinked")] public List<CharacterRecordEntry> Unlinked { get; set; } = new();
    [JsonPropertyName("current"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? Current { get; set; }
    [JsonPropertyName("accepted"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<CharacterRecordEntry>? Accepted { get; set; }
}

public sealed class CharacterRecordEntry
{
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    [JsonPropertyName("w")] public string World { get; set; } = "";

    public static CharacterRecordEntry From(CharacterRef c) => new() { Name = c.Name, World = c.World };
}

public sealed class EncryptedCharacterRecord
{
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("ciphertext")] public string Ciphertext { get; set; } = "";
}

/// Static ECDH between the two devices' identity keys, so both derive the same key with no exchange; the AAD binds a
/// record to its pair, epoch, side and seq, so the relay can't move, swap or roll one back.
/// protocol/vectors/crypto-vectors.json `pairCharacters` is the cross-runtime reference.
public static class CharacterRecordCrypto
{
    public const string OwnerSide = "owner";
    public const string SubSide = "sub";
    public const int MaxCiphertextBytes = 4096;
    private const string InfoLabel = "oathbound-relay-characters-v1";

    public static string SideOf(PairingDirection ownDirection, bool own) =>
        (ownDirection == PairingDirection.OwnerSide) == own ? OwnerSide : SubSide;

    public static byte[] DeriveKey(RelayEcKeyPair ownKey, EcPublicKeyJwk peerPublic, string pairIdHash, int pairEpoch)
    {
        using var peer = RelayCrypto.ImportEphemeralPublicKey(peerPublic);
        var shared = RelayCrypto.DeriveSharedSecret(ownKey, peer);
        try
        {
            var epoch = pairEpoch.ToString(CultureInfo.InvariantCulture);
            var salt = SHA256.HashData(Encoding.UTF8.GetBytes(pairIdHash + "|" + epoch));
            return RelayCrypto.DeriveAesKey(shared, salt, Encoding.UTF8.GetBytes(InfoLabel + pairIdHash + epoch));
        }
        finally
        {
            Array.Clear(shared);
        }
    }

    public static byte[] Aad(string pairIdHash, int pairEpoch, string side, long seq) =>
        Encoding.UTF8.GetBytes(CanonicalJson.Serialize(new Dictionary<string, object?>
        {
            ["pairIdHash"] = pairIdHash,
            ["pairEpoch"] = pairEpoch,
            ["side"] = side,
            ["seq"] = seq,
        }));

    public static EncryptedCharacterRecord Seal(CharacterRecord record, byte[] key, string pairIdHash, int pairEpoch, string side)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(record);
        var nonce = RelayCrypto.RandomBytes(RelayCrypto.AeadNonceLengthBytes);
        var ciphertext = RelayCrypto.AesGcmEncrypt(key, nonce, plaintext, Aad(pairIdHash, pairEpoch, side, record.Seq));
        if (ciphertext.Length > MaxCiphertextBytes)
            throw new InvalidOperationException("Too many characters to publish.");
        return new EncryptedCharacterRecord { Nonce = RelayCrypto.Base64UrlEncode(nonce), Ciphertext = RelayCrypto.Base64UrlEncode(ciphertext) };
    }

    /// Null when it doesn't decrypt under this pair/epoch/side/seq, or isn't a well-formed record for that seq.
    public static CharacterRecord? Open(EncryptedCharacterRecord sealedRecord, byte[] key, string pairIdHash, int pairEpoch, string side, long seq)
    {
        try
        {
            var plaintext = RelayCrypto.AesGcmDecrypt(key, RelayCrypto.Base64UrlDecode(sealedRecord.Nonce), RelayCrypto.Base64UrlDecode(sealedRecord.Ciphertext), Aad(pairIdHash, pairEpoch, side, seq));
            var record = JsonSerializer.Deserialize<CharacterRecord>(plaintext);
            return record is { Version: 1 } && record.Seq == seq ? record : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
