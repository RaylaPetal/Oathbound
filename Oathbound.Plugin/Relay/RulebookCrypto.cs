using System;
using System.IO;
using System.Security.Cryptography;

namespace Oathbound.Plugin.Relay;

/// Sealing and opening for both rulebook channels: ECDH(receive key, per-item ephemeral) + HKDF-SHA256 +
/// AES-256-GCM over gzip, the catalog mailbox scheme with per-channel HKDF labels.
public static class RulebookCrypto
{
    /// salt = SHA-256(receiveRaw || senderEphemeralRaw), the same order on both ends.
    public static byte[] DeriveItemKey(RelayEcKeyPair ownKey, EcPublicKeyJwk otherPublic, EcPublicKeyJwk receivePublic, EcPublicKeyJwk senderEphemeralPublic,
        string channel, string pairIdHash, string receiveKeyId)
    {
        using var other = RelayCrypto.ImportEphemeralPublicKey(otherPublic);
        var sharedSecret = RelayCrypto.DeriveSharedSecret(ownKey, other);
        var salt = SHA256.HashData([.. RelayCrypto.ExportRawUncompressedPoint(receivePublic), .. RelayCrypto.ExportRawUncompressedPoint(senderEphemeralPublic)]);
        try
        {
            return RelayCrypto.DeriveAesKey(sharedSecret, salt, RelayCrypto.BuildRulebookHkdfInfo(channel, pairIdHash, receiveKeyId));
        }
        finally
        {
            Array.Clear(sharedSecret);
        }
    }

    /// Fills the envelope's crypto fields (nonce, ephemeral key, size, digest) and returns the ciphertext.
    /// The caller has already set every AAD-bound field, including the sequence; signing happens afterwards.
    public static byte[] Seal(RulebookItemEnvelope envelope, EcPublicKeyJwk receivePublic, string channel, byte[] plaintext, int maxCiphertextBytes)
    {
        var compressed = RelayCompression.Compress(plaintext);
        try
        {
            // GCM output is always input + 16 bytes, so this is exact.
            if (compressed.Length + 16 > maxCiphertextBytes)
                throw new InvalidDataException("Too large to send through the relay.");

            using var ephemeral = RelayCrypto.GenerateEphemeralKeyPair();
            envelope.SenderEphemeralPublicKey = RelayCrypto.ExportPublicKeyJwk(ephemeral);
            var nonce = RelayCrypto.RandomBytes(RelayCrypto.AeadNonceLengthBytes);
            envelope.Nonce = RelayCrypto.Base64UrlEncode(nonce);

            var aesKey = DeriveItemKey(ephemeral, receivePublic, receivePublic, envelope.SenderEphemeralPublicKey, channel, envelope.PairIdHash, envelope.ReceiveKeyId);
            try
            {
                var ciphertext = RelayCrypto.AesGcmEncrypt(aesKey, nonce, compressed, RulebookItemAad.Build(envelope));
                envelope.CiphertextSizeBytes = ciphertext.Length;
                envelope.CiphertextDigest = RelayCrypto.Sha256Hex(ciphertext);
                return ciphertext;
            }
            finally
            {
                Array.Clear(aesKey);
            }
        }
        finally
        {
            Array.Clear(compressed);
        }
    }

    /// Decrypts an item the caller has already verified (pair, devices, key id, sequence, size, digest, signature).
    public static byte[] Open(RulebookItemEnvelope envelope, byte[] ciphertext, RelayEcKeyPair receiveKey, string channel, int maxPlaintextBytes)
    {
        var receivePublic = RelayCrypto.ExportPublicKeyJwk(receiveKey);
        var aesKey = DeriveItemKey(receiveKey, envelope.SenderEphemeralPublicKey, receivePublic, envelope.SenderEphemeralPublicKey, channel, envelope.PairIdHash, envelope.ReceiveKeyId);
        try
        {
            var compressed = RelayCrypto.AesGcmDecrypt(aesKey, RelayCrypto.Base64UrlDecode(envelope.Nonce), ciphertext, RulebookItemAad.Build(envelope));
            return RelayCompression.Decompress(compressed, maxPlaintextBytes);
        }
        finally
        {
            Array.Clear(aesKey);
        }
    }
}
