using System;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using BigInteger = Org.BouncyCastle.Math.BigInteger;

namespace Oathbound.Plugin.Relay;

/// BouncyCastle parameters only; Dispose() is a no-op kept for `using` call sites.
public sealed class RelayEcKeyPair : IDisposable
{
    internal ECPrivateKeyParameters? Private;
    internal ECPublicKeyParameters Public = null!;
    public void Dispose() { }
}

/// Algorithms are fixed by protocol/constants.json and must interoperate exactly with the Worker
/// (protocol/vectors/crypto-vectors.json). Private keys are never logged, exported or put in an envelope.
/// BouncyCastle, not the BCL's ECDsa/ECDiffieHellman: those are CNG-backed and Wine's CNG can't generate EC keys,
/// which would crash the plugin on load. AES-GCM and SHA-256 use BCrypt and are unaffected.
public static class RelayCrypto
{
    private static readonly Org.BouncyCastle.Asn1.X9.X9ECParameters CurveParams = Org.BouncyCastle.Asn1.Nist.NistNamedCurves.GetByName("P-256");
    private static readonly ECDomainParameters DomainParams = new(CurveParams.Curve, CurveParams.G, CurveParams.N, CurveParams.H);

    public static string Sha256Hex(string utf8Text) => Sha256Hex(Encoding.UTF8.GetBytes(utf8Text));

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256HexOfCanonicalJson(object value) => Sha256Hex(EnvelopeCanonical.SerializeExcludingSignature(value));

    public static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - padded.Length % 4) % 4;
        return Convert.FromBase64String(padded + new string('=', padding));
    }

    public static byte[] RandomBytes(int count)
    {
        var bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    /// 256-bit capability id, base64url without padding (43 characters).
    public static string RandomCapabilityId() => Base64UrlEncode(RandomBytes(32));

    /// 128-bit, base64url without padding (22 characters).
    public static string RandomNonce() => Base64UrlEncode(RandomBytes(16));

    /// 128 bits is enough for a single-use, short-lived, rate-limited invitation, and keeps the tell short.
    public static string RandomInvitationId() => Base64UrlEncode(RandomBytes(16));

    /// Compared for exact equality only, never used as a hash, so plain random bytes are fine.
    public static string RandomProofDigestHex() => Convert.ToHexStringLower(RandomBytes(16));

    private static byte[] FixedLength32(BigInteger value) => BigIntegers.AsUnsignedByteArray(32, value);

    // ---- ECDSA P-256 (device signing identity) ----

    public static RelayEcKeyPair GenerateSigningKeyPair()
    {
        var generator = new ECKeyPairGenerator("ECDSA");
        generator.Init(new ECKeyGenerationParameters(DomainParams, new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        return new RelayEcKeyPair { Private = (ECPrivateKeyParameters)pair.Private, Public = (ECPublicKeyParameters)pair.Public };
    }

    public static RelayEcKeyPair ImportSigningPrivateKey(EcPublicKeyJwk publicKeyJwk, byte[] privateD) => ImportPrivateKey(publicKeyJwk, privateD);

    public static RelayEcKeyPair ImportSigningPublicKey(EcPublicKeyJwk publicKeyJwk) => ImportPublicKey(publicKeyJwk);

    public static EcPublicKeyJwk ExportPublicKeyJwk(RelayEcKeyPair key)
    {
        var point = key.Public.Q.Normalize();
        return new EcPublicKeyJwk
        {
            Kty = "EC",
            Crv = "P-256",
            X = Base64UrlEncode(FixedLength32(point.AffineXCoord.ToBigInteger())),
            Y = Base64UrlEncode(FixedLength32(point.AffineYCoord.ToBigInteger())),
        };
    }

    public static byte[] ExportPrivateD(RelayEcKeyPair key) =>
        FixedLength32(key.Private?.D ?? throw new InvalidOperationException("Key pair has no private component to export."));

    /// Must match the Worker's computePairIdHash: SHA-256 of both key ids, sorted ordinally like JS's default sort.
    public static string ComputePairIdHash(string deviceKeyIdA, string deviceKeyIdB)
    {
        var (a, b) = string.CompareOrdinal(deviceKeyIdA, deviceKeyIdB) <= 0 ? (deviceKeyIdA, deviceKeyIdB) : (deviceKeyIdB, deviceKeyIdA);
        return Sha256Hex(CanonicalJson.Serialize(new System.Collections.Generic.Dictionary<string, object?> { ["a"] = a, ["b"] = b }));
    }

    /// SHA-256 of the JCS-canonicalized public key JWK; matches the Worker's deviceKeyIdForPublicKey.
    public static string DeviceKeyId(EcPublicKeyJwk publicKeyJwk) =>
        Sha256Hex(CanonicalJson.Serialize(new System.Collections.Generic.Dictionary<string, object?>
        {
            ["kty"] = publicKeyJwk.Kty,
            ["crv"] = publicKeyJwk.Crv,
            ["x"] = publicKeyJwk.X,
            ["y"] = publicKeyJwk.Y,
        }));

    /// Raw r||s (64 bytes), base64url without padding - never DER.
    public static string SignRaw(RelayEcKeyPair privateKey, string message)
    {
        if (privateKey.Private is null) throw new InvalidOperationException("Key pair has no private component to sign with.");
        var signer = new ECDsaSigner(new HMacDsaKCalculator(new Sha256Digest()));
        signer.Init(true, privateKey.Private);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(message));
        var rs = signer.GenerateSignature(digest);

        var signatureBytes = new byte[64];
        Buffer.BlockCopy(FixedLength32(rs[0]), 0, signatureBytes, 0, 32);
        Buffer.BlockCopy(FixedLength32(rs[1]), 0, signatureBytes, 32, 32);
        return Base64UrlEncode(signatureBytes);
    }

    public static bool VerifyRaw(EcPublicKeyJwk publicKeyJwk, string signatureBase64Url, string message)
    {
        byte[] signatureBytes;
        try
        {
            signatureBytes = Base64UrlDecode(signatureBase64Url);
        }
        catch (FormatException)
        {
            return false;
        }
        if (signatureBytes.Length != 64) return false;

        RelayEcKeyPair publicKey;
        try
        {
            publicKey = ImportSigningPublicKey(publicKeyJwk);
        }
        catch (Exception ex) when (ex is FormatException or ArithmeticException or ArgumentException)
        {
            return false;
        }

        var r = new BigInteger(1, signatureBytes, 0, 32);
        var s = new BigInteger(1, signatureBytes, 32, 32);
        var signer = new ECDsaSigner();
        signer.Init(false, publicKey.Public);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(message));
        return signer.VerifySignature(digest, r, s);
    }

    // ---- ECDH P-256 (ephemeral catalog key agreement) ----

    public static RelayEcKeyPair GenerateEphemeralKeyPair()
    {
        var generator = new ECKeyPairGenerator("ECDH");
        generator.Init(new ECKeyGenerationParameters(DomainParams, new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        return new RelayEcKeyPair { Private = (ECPrivateKeyParameters)pair.Private, Public = (ECPublicKeyParameters)pair.Public };
    }

    public static RelayEcKeyPair ImportEphemeralPrivateKey(EcPublicKeyJwk publicKeyJwk, byte[] privateD) => ImportPrivateKey(publicKeyJwk, privateD);

    public static RelayEcKeyPair ImportEphemeralPublicKey(EcPublicKeyJwk publicKeyJwk) => ImportPublicKey(publicKeyJwk);

    /// 0x04 || X || Y, byte-for-byte what WebCrypto's exportKey("raw") gives.
    public static byte[] ExportRawUncompressedPoint(RelayEcKeyPair key)
    {
        var point = key.Public.Q.Normalize();
        var raw = new byte[65];
        raw[0] = 0x04;
        Buffer.BlockCopy(FixedLength32(point.AffineXCoord.ToBigInteger()), 0, raw, 1, 32);
        Buffer.BlockCopy(FixedLength32(point.AffineYCoord.ToBigInteger()), 0, raw, 33, 32);
        return raw;
    }

    public static byte[] ExportRawUncompressedPoint(EcPublicKeyJwk publicKeyJwk)
    {
        var raw = new byte[65];
        raw[0] = 0x04;
        var x = Base64UrlDecode(publicKeyJwk.X);
        var y = Base64UrlDecode(publicKeyJwk.Y);
        Buffer.BlockCopy(x, 0, raw, 1, 32);
        Buffer.BlockCopy(y, 0, raw, 33, 32);
        return raw;
    }

    public static byte[] DeriveSharedSecret(RelayEcKeyPair privateKey, RelayEcKeyPair otherPartyPublicKey)
    {
        if (privateKey.Private is null) throw new InvalidOperationException("Key pair has no private component to derive with.");
        var agreement = new ECDHBasicAgreement();
        agreement.Init(privateKey.Private);
        var z = agreement.CalculateAgreement(otherPartyPublicKey.Public);
        return FixedLength32(z);
    }

    /// HKDF-SHA256 to 32 bytes. salt = SHA-256(ownerEphemeral || subEphemeral),
    /// info = "oathbound-relay-catalog-v1" || pairIdHash || requestId - both must match protocol/constants.json.
    public static byte[] DeriveAesKey(byte[] sharedSecret, byte[] salt, byte[] info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, outputLength: 32, salt: salt, info: info);

    public static byte[] BuildCatalogHkdfInfo(string pairIdHash, string requestId) =>
        Encoding.UTF8.GetBytes("oathbound-relay-catalog-v1" + pairIdHash + requestId);

    /// A distinct label so a mailbox key can never be confused with a request key.
    public static byte[] BuildCatalogPushHkdfInfo(string pairIdHash, string receiveKeyId) =>
        Encoding.UTF8.GetBytes("oathbound-relay-catalog-push-v1" + pairIdHash + receiveKeyId);

    /// Per-channel labels so a rulebook ciphertext can't be moved to the report channel or the catalog mailbox.
    public static byte[] BuildRulebookHkdfInfo(string channel, string pairIdHash, string receiveKeyId) =>
        Encoding.UTF8.GetBytes((channel == RulebookChannels.Rulebook ? "oathbound-relay-rulebook-push-v1" : "oathbound-relay-rulebook-report-v1") + pairIdHash + receiveKeyId);

    /// 128-bit mailbox receive key id.
    public static string RandomReceiveKeyId() => Base64UrlEncode(RandomBytes(16));

    private static RelayEcKeyPair ImportPrivateKey(EcPublicKeyJwk publicKeyJwk, byte[] privateD)
    {
        var d = new BigInteger(1, privateD);
        var priv = new ECPrivateKeyParameters("ECDSA", d, DomainParams);
        return new RelayEcKeyPair { Private = priv, Public = ImportPublicKey(publicKeyJwk).Public };
    }

    private static RelayEcKeyPair ImportPublicKey(EcPublicKeyJwk publicKeyJwk)
    {
        var x = new BigInteger(1, Base64UrlDecode(publicKeyJwk.X));
        var y = new BigInteger(1, Base64UrlDecode(publicKeyJwk.Y));
        var point = CurveParams.Curve.ValidatePoint(x, y);
        return new RelayEcKeyPair { Public = new ECPublicKeyParameters("ECDSA", point, DomainParams) };
    }

    // ---- AES-256-GCM (catalog payload) ----

    public const int AeadNonceLengthBytes = 12;
    public const int AeadTagLengthBytes = 16;

    /// Tag appended, matching WebCrypto's encrypt output.
    public static byte[] AesGcmEncrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[] associatedData)
    {
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AeadTagLengthBytes];
        using var aes = new AesGcm(key, AeadTagLengthBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        var combined = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, combined, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, ciphertext.Length, tag.Length);
        return combined;
    }

    public static byte[] AesGcmDecrypt(byte[] key, byte[] nonce, byte[] ciphertextWithTag, byte[] associatedData)
    {
        if (ciphertextWithTag.Length < AeadTagLengthBytes)
            throw new CryptographicException("Ciphertext shorter than the GCM tag; cannot decrypt.");

        var ciphertextLength = ciphertextWithTag.Length - AeadTagLengthBytes;
        var ciphertext = new byte[ciphertextLength];
        var tag = new byte[AeadTagLengthBytes];
        Buffer.BlockCopy(ciphertextWithTag, 0, ciphertext, 0, ciphertextLength);
        Buffer.BlockCopy(ciphertextWithTag, ciphertextLength, tag, 0, AeadTagLengthBytes);

        var plaintext = new byte[ciphertextLength];
        using var aes = new AesGcm(key, AeadTagLengthBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }

    // ---- Restraint pictures (catalog-pictures) ----

    /// Binds a blob to its id, so the relay can't serve one picture's bytes under another reference.
    private static byte[] PictureAad(string pictureId) => Encoding.UTF8.GetBytes($"oathbound-picture-v1|{pictureId}");

    /// nonce(12) || ciphertext+tag, as stored by the relay.
    public static byte[] EncryptPicture(byte[] key, string pictureId, byte[] jpeg)
    {
        var nonce = RandomBytes(AeadNonceLengthBytes);
        var sealedBytes = AesGcmEncrypt(key, nonce, jpeg, PictureAad(pictureId));
        var blob = new byte[nonce.Length + sealedBytes.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, nonce.Length);
        Buffer.BlockCopy(sealedBytes, 0, blob, nonce.Length, sealedBytes.Length);
        return blob;
    }

    public static byte[] DecryptPicture(byte[] key, string pictureId, byte[] blob)
    {
        if (blob.Length < AeadNonceLengthBytes + AeadTagLengthBytes)
            throw new CryptographicException("Picture blob too short.");
        return AesGcmDecrypt(key, blob[..AeadNonceLengthBytes], blob[AeadNonceLengthBytes..], PictureAad(pictureId));
    }
}
