using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// collar/pairing "Device-key lifecycle is recoverable and explicit". Generates and protects this
/// installation's persistent ECDSA P-256 signing identity. See protocol/docs/threat-model.md for why DPAPI
/// provides no real guarantee under Wine - this class still calls it unconditionally (it costs nothing and
/// helps on native Windows), but never claims the key is "protected" in any user-facing text; that
/// disclosure lives in Settings, not here.
public sealed class DeviceIdentityService
{
    private readonly PluginConfig config;
    private RelayEcKeyPair? cachedKey;

    public DeviceIdentityService(PluginConfig config)
    {
        this.config = config;
    }

    private static readonly TimeSpan ResetCooldown = TimeSpan.FromMinutes(5);

    public string? DeviceKeyId => config.DeviceIdentity.DeviceKeyId;
    public bool HasIdentity => config.DeviceIdentity.HasIdentity;

    /// collar/pairing "Device identity reset has a short client-side cooldown" - a UI-friction guard
    /// against an accidental repeat reset, not an abuse control (see the change's proposal.md for why).
    public TimeSpan? CooldownRemaining
    {
        get
        {
            if (config.DeviceIdentity.LastResetUtc is not { } lastReset) return null;
            var remaining = lastReset + ResetCooldown - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : null;
        }
    }

    public bool CanReset => CooldownRemaining is null;

    /// Generates a fresh identity if none exists yet; a no-op otherwise. Called once at plugin startup.
    public void EnsureIdentity()
    {
        if (config.DeviceIdentity.HasIdentity) return;
        GenerateAndPersist();
    }

    /// collar/pairing "User resets the device identity": generates a brand-new identity, invalidating every
    /// relay-assisted pairing this side held (the old device key id no longer matches anything server-side
    /// or in the peer's own PeerDeviceKeyId). Callers are responsible for locally ending any active pairing
    /// as part of the same user-confirmed action - this method only replaces the key.
    public void ResetIdentity()
    {
        cachedKey?.Dispose();
        cachedKey = null;
        GenerateAndPersist();
        config.DeviceIdentity.LastResetUtc = DateTime.UtcNow;
        config.Save();
    }

    /// Returns the live signing key, importing the protected private scalar on first use. Throws if no
    /// identity exists yet (callers must EnsureIdentity() at startup) or if the protected blob cannot be
    /// unprotected (e.g. it was written on a different Windows user profile) - in that case the caller
    /// should surface a reset prompt, never silently regenerate out from under an existing pairing.
    public RelayEcKeyPair GetSigningKey()
    {
        if (cachedKey is not null) return cachedKey;

        var identity = config.DeviceIdentity;
        if (!identity.HasIdentity)
            throw new InvalidOperationException("No device identity exists yet; call EnsureIdentity() first.");

        var privateD = Unprotect(identity.ProtectedPrivateKey!, identity.IsProtected);
        var publicKeyJwk = new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = identity.PublicKeyX!, Y = identity.PublicKeyY! };
        cachedKey = RelayCrypto.ImportSigningPrivateKey(publicKeyJwk, privateD);
        return cachedKey;
    }

    public EcPublicKeyJwk GetPublicKeyJwk()
    {
        var identity = config.DeviceIdentity;
        if (!identity.HasIdentity)
            throw new InvalidOperationException("No device identity exists yet; call EnsureIdentity() first.");
        return new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = identity.PublicKeyX!, Y = identity.PublicKeyY! };
    }

    /// collar/pairing-recovery: the raw identity for the encrypted backup - public key plus private scalar.
    /// Only ever handed to BackupService, which encrypts it under the recovery code before it leaves memory.
    internal (string PublicKeyX, string PublicKeyY, byte[] PrivateD) ExportForBackup()
    {
        var identity = config.DeviceIdentity;
        if (!identity.HasIdentity)
            throw new InvalidOperationException("No device identity exists yet; call EnsureIdentity() first.");
        return (identity.PublicKeyX!, identity.PublicKeyY!, Unprotect(identity.ProtectedPrivateKey!, identity.IsProtected));
    }

    /// collar/pairing-recovery: replaces this install's identity with a restored one. Validates the key pair
    /// first (the private scalar must actually belong to the public key), then re-protects it for this
    /// machine. Every relay pairing keyed to that identity works again immediately, since pairIdHash and the
    /// peer's own records depend only on the public key.
    internal void ImportFromBackup(string publicKeyX, string publicKeyY, byte[] privateD)
    {
        var publicKeyJwk = new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = publicKeyX, Y = publicKeyY };
        using (var key = RelayCrypto.ImportSigningPrivateKey(publicKeyJwk, privateD))
        {
            const string probe = "oathbound-restore-probe";
            if (!RelayCrypto.VerifyRaw(publicKeyJwk, RelayCrypto.SignRaw(key, probe), probe))
                throw new CryptographicException("The restored private key doesn't match its public key.");
        }

        var (protectedPrivateKey, wasProtected) = Protect(privateD);
        config.DeviceIdentity.PublicKeyX = publicKeyX;
        config.DeviceIdentity.PublicKeyY = publicKeyY;
        config.DeviceIdentity.ProtectedPrivateKey = protectedPrivateKey;
        config.DeviceIdentity.IsProtected = wasProtected;
        config.DeviceIdentity.DeviceKeyId = RelayCrypto.DeviceKeyId(publicKeyJwk);
        config.Save();
        cachedKey?.Dispose();
        cachedKey = null;
    }

    private void GenerateAndPersist()
    {
        using var key = RelayCrypto.GenerateSigningKeyPair();
        var publicKeyJwk = RelayCrypto.ExportPublicKeyJwk(key);
        var privateD = RelayCrypto.ExportPrivateD(key);

        var (protectedPrivateKey, wasProtected) = Protect(privateD);
        config.DeviceIdentity.PublicKeyX = publicKeyJwk.X;
        config.DeviceIdentity.PublicKeyY = publicKeyJwk.Y;
        config.DeviceIdentity.ProtectedPrivateKey = protectedPrivateKey;
        config.DeviceIdentity.IsProtected = wasProtected;
        config.DeviceIdentity.DeviceKeyId = RelayCrypto.DeviceKeyId(publicKeyJwk);
        config.Save();

        Array.Clear(privateD);
        cachedKey?.Dispose();
        cachedKey = null;
    }

    /// Also used for each Owner-side pairing's catalog-mailbox receive key (CatalogSyncRelayService), with
    /// its own entropy so a blob from one purpose can never be unprotected as the other.
    internal static (byte[] Data, bool WasProtected) Protect(byte[] plaintext, byte[]? entropy = null)
    {
        if (!OperatingSystem.IsWindows()) return (plaintext, false);
        try
        {
            return (ProtectedData.Protect(plaintext, entropy ?? s_entropy, DataProtectionScope.CurrentUser), true);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            // Best-available protection only: under Wine, or if the profile's DPAPI master key is
            // unavailable, fall back to storing the plain scalar rather than failing to create an identity
            // at all. See protocol/docs/threat-model.md - this is documented, not a silent weakening.
            Plugin.Log.Warning(ex, "DPAPI protection unavailable; storing the device private key without OS-level protection.");
            return (plaintext, false);
        }
    }

    /// `isProtected` is `DeviceIdentityState.IsProtected` - null for an identity generated before that field
    /// existed, in which case this keeps the old exception-based guess (try DPAPI, treat any
    /// `CryptographicException` as "was never protected") rather than risk misclassifying a legacy identity
    /// whose actual history isn't recorded. For a known value, there's no guessing: `false` skips DPAPI
    /// entirely, and `true` treats a decrypt failure as what it actually is - a genuinely unrecoverable
    /// identity (wrong Windows profile, rotated DPAPI master key, etc.) - by throwing
    /// `DeviceIdentityUnavailableException` instead of silently returning the still-encrypted ciphertext as
    /// if it were the plaintext scalar (which is what produced the confusing BouncyCastle "Scalar is not in
    /// the interval [1, n-1]" crash this replaces).
    internal static byte[] Unprotect(byte[] stored, bool? isProtected, byte[]? entropy = null)
    {
        if (!OperatingSystem.IsWindows() || isProtected == false) return stored;
        try
        {
            return ProtectedData.Unprotect(stored, entropy ?? s_entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            if (isProtected is null) return stored; // Legacy identity, unknown history - old behavior.
            throw new DeviceIdentityUnavailableException(
                "This device's identity key could not be unprotected (it may have been written on a different Windows user profile, or the OS-level protection key changed). Reset the device identity in Settings to recover.", ex);
        }
    }

    private static readonly byte[] s_entropy = "oathbound-device-identity-v1"u8.ToArray();
}

/// See DeviceIdentityService.Unprotect - a device identity whose protected private key can no longer be
/// unprotected is unrecoverable; the only way forward is an explicit reset (Settings), never a silent
/// regeneration out from under an existing pairing.
public sealed class DeviceIdentityUnavailableException(string message, Exception inner) : Exception(message, inner);
