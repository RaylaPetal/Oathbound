using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Oathbound.Plugin.Relay;

/// The character fields a code invitation or acceptance carries encrypted (collar/pairing "Character identity
/// is hidden from the relay").
public sealed record PairingCharacter(string Name, string World, string? TriggerPhrase);

/// collar/pairing + collar/pairing-recovery: pairing codes and recovery codes, and everything derived from
/// them. Must agree byte-for-byte with protocol/constants.json `pairingCode`/`recoveryCode` and the
/// `pairingCode`/`recoveryCode` entries in protocol/vectors/crypto-vectors.json (worker/test/vectors.spec.ts
/// checks the Worker side; this side is checked by hand against the same vectors).
public static class PairingCodes
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32: no I, L, O, U

    public const int PairingCodeBytes = 10;  // 80 bits -> 16 characters
    public const int PairingCodeChars = 16;
    public const int RecoveryCodeBytes = 16; // 128 bits -> 26 characters
    public const int RecoveryCodeChars = 26;

    private static readonly byte[] PairLookupInfo = "oathbound-pair-code-lookup-v1"u8.ToArray();
    private static readonly byte[] PairEncryptionInfo = "oathbound-pair-code-enc-v1"u8.ToArray();
    private static readonly byte[] BackupIdInfo = "oathbound-backup-id-v1"u8.ToArray();
    private static readonly byte[] BackupEncryptionInfo = "oathbound-backup-enc-v1"u8.ToArray();

    public static string NewPairingCode() => Encode(RelayCrypto.RandomBytes(PairingCodeBytes));
    public static string NewRecoveryCode() => Encode(RelayCrypto.RandomBytes(RecoveryCodeBytes));

    /// Uppercase, dashes and whitespace removed, I/L -> 1 and O -> 0 (so a typed code survives the usual
    /// look-alike mistakes). Returns null if anything outside the alphabet remains or the length is wrong.
    public static string? Normalize(string? input, int expectedChars)
    {
        if (input is null) return null;
        var sb = new StringBuilder(input.Length);
        foreach (var raw in input.ToUpperInvariant())
        {
            if (raw == '-' || char.IsWhiteSpace(raw)) continue;
            var c = raw switch { 'I' or 'L' => '1', 'O' => '0', _ => raw };
            if (Alphabet.IndexOf(c) < 0) return null;
            sb.Append(c);
        }
        return sb.Length == expectedChars ? sb.ToString() : null;
    }

    /// Groups of four for display: K7QM-3XRP-9DTA-WV2E.
    public static string Format(string normalized)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < normalized.Length; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(normalized[i]);
        }
        return sb.ToString();
    }

    private static string Encode(byte[] bytes)
    {
        var sb = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    private static byte[] Hkdf(string normalized, byte[] info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(normalized), outputLength: 32, salt: [], info: info);

    /// The code invitation's invitationId (a 43-character capability); the relay stores only its SHA-256.
    public static string LookupId(string normalizedPairingCode) => RelayCrypto.Base64UrlEncode(Hkdf(normalizedPairingCode, PairLookupInfo));
    public static byte[] CharacterKey(string normalizedPairingCode) => Hkdf(normalizedPairingCode, PairEncryptionInfo);
    public static string BackupId(string normalizedRecoveryCode) => RelayCrypto.Base64UrlEncode(Hkdf(normalizedRecoveryCode, BackupIdInfo));
    public static byte[] BackupKey(string normalizedRecoveryCode) => Hkdf(normalizedRecoveryCode, BackupEncryptionInfo);

    private static byte[] CharacterAad(string invitationId, bool inviter) =>
        Encoding.UTF8.GetBytes($"oathbound-pair-code-v1|{invitationId}|{(inviter ? "inviter" : "accepter")}");

    public static EncryptedBlob EncryptCharacter(string normalizedPairingCode, string invitationId, bool inviter, PairingCharacter character)
    {
        var fields = new Dictionary<string, object?> { ["name"] = character.Name, ["world"] = character.World };
        if (!string.IsNullOrWhiteSpace(character.TriggerPhrase))
            fields["triggerPhrase"] = character.TriggerPhrase.Trim();
        var plaintext = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(fields));
        var nonce = RelayCrypto.RandomBytes(RelayCrypto.AeadNonceLengthBytes);
        var ciphertext = RelayCrypto.AesGcmEncrypt(CharacterKey(normalizedPairingCode), nonce, plaintext, CharacterAad(invitationId, inviter));
        return new EncryptedBlob { Nonce = RelayCrypto.Base64UrlEncode(nonce), Ciphertext = RelayCrypto.Base64UrlEncode(ciphertext) };
    }

    /// Null if the blob doesn't decrypt under this code (wrong code, tampered, or the wrong side's blob).
    public static PairingCharacter? DecryptCharacter(string normalizedPairingCode, string invitationId, bool inviter, EncryptedBlob? blob)
    {
        if (blob is null) return null;
        try
        {
            var plaintext = RelayCrypto.AesGcmDecrypt(CharacterKey(normalizedPairingCode), RelayCrypto.Base64UrlDecode(blob.Nonce),
                RelayCrypto.Base64UrlDecode(blob.Ciphertext), CharacterAad(invitationId, inviter));
            using var doc = JsonDocument.Parse(plaintext);
            var root = doc.RootElement;
            var name = root.GetProperty("name").GetString();
            var world = root.GetProperty("world").GetString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world)) return null;
            var trigger = root.TryGetProperty("triggerPhrase", out var t) ? t.GetString() : null;
            return new PairingCharacter(name, world, trigger);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public static (string Nonce, string Ciphertext) EncryptBackup(string normalizedRecoveryCode, byte[] plaintext)
    {
        var backupId = BackupId(normalizedRecoveryCode);
        var nonce = RelayCrypto.RandomBytes(RelayCrypto.AeadNonceLengthBytes);
        var ciphertext = RelayCrypto.AesGcmEncrypt(BackupKey(normalizedRecoveryCode), nonce, plaintext, Encoding.UTF8.GetBytes($"oathbound-backup-v1|{backupId}"));
        return (RelayCrypto.Base64UrlEncode(nonce), RelayCrypto.Base64UrlEncode(ciphertext));
    }

    public static byte[]? DecryptBackup(string normalizedRecoveryCode, string nonce, string ciphertext)
    {
        try
        {
            var backupId = BackupId(normalizedRecoveryCode);
            return RelayCrypto.AesGcmDecrypt(BackupKey(normalizedRecoveryCode), RelayCrypto.Base64UrlDecode(nonce),
                RelayCrypto.Base64UrlDecode(ciphertext), Encoding.UTF8.GetBytes($"oathbound-backup-v1|{backupId}"));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
