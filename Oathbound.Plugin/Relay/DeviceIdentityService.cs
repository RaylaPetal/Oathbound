using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// This install's persistent ECDSA P-256 signing identity. DPAPI gives no real guarantee under Wine, so the key
/// is never described as "protected" to the user.
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

    /// A guard against an accidental repeat reset, not an abuse control.
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

    public void EnsureIdentity()
    {
        if (config.DeviceIdentity.HasIdentity) return;
        GenerateAndPersist();
    }

    /// Invalidates every relay pairing this side held. Callers end those pairings locally; this only replaces the key.
    public void ResetIdentity()
    {
        cachedKey?.Dispose();
        cachedKey = null;
        GenerateAndPersist();
        config.DeviceIdentity.LastResetUtc = DateTime.UtcNow;
        config.SaveNow();
    }

    /// Throws if there's no identity or the blob can't be unprotected - callers prompt for a reset, never silently regenerate.
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

    /// Only ever handed to BackupService, which encrypts it under the recovery code.
    internal (string PublicKeyX, string PublicKeyY, byte[] PrivateD) ExportForBackup()
    {
        var identity = config.DeviceIdentity;
        if (!identity.HasIdentity)
            throw new InvalidOperationException("No device identity exists yet; call EnsureIdentity() first.");
        return (identity.PublicKeyX!, identity.PublicKeyY!, Unprotect(identity.ProtectedPrivateKey!, identity.IsProtected));
    }

    /// Validates that the scalar belongs to the public key, then re-protects it for this machine.
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
        config.SaveNow();
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
        config.SaveNow();

        Array.Clear(privateD);
        cachedKey?.Dispose();
        cachedKey = null;
    }

    /// Also used for catalog-mailbox receive keys, with their own entropy so blobs can't be swapped across purposes.
    internal static (byte[] Data, bool WasProtected) Protect(byte[] plaintext, byte[]? entropy = null)
    {
        if (!OperatingSystem.IsWindows()) return (plaintext, false);
        try
        {
            return (ProtectedData.Protect(plaintext, entropy ?? s_entropy, DataProtectionScope.CurrentUser), true);
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            // Under Wine or without a DPAPI master key, store the plain scalar rather than fail to create an identity.
            Plugin.Log.Warning(ex, "DPAPI protection unavailable; storing the device private key without OS-level protection.");
            return (plaintext, false);
        }
    }

    /// Null `isProtected` (legacy) keeps the old guess. `true` with a decrypt failure throws, rather than returning
    /// ciphertext as if it were the scalar.
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

/// The only way forward is an explicit reset, never a silent regeneration.
public sealed class DeviceIdentityUnavailableException(string message, Exception inner) : Exception(message, inner);
