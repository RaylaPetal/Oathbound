using System;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// The P-256 key match chat is sealed with (protocol/constants.json matchChat). Kept apart from the device key, so a
/// match partner never holds anything that names this install's pairings. Stored like the device identity.
public sealed class MatchKeyService
{
    private static readonly byte[] Entropy = "oathbound-match-key-v1"u8.ToArray();
    private readonly PluginConfig config;
    private RelayEcKeyPair? cached;

    public MatchKeyService(PluginConfig config) => this.config = config;

    private LookingForPairState State => config.LookingForPair;

    public bool HasKey => State.MatchKeyX is not null && State.MatchKeyY is not null && State.ProtectedMatchKey is not null;

    public EcPublicKeyJwk EnsurePublicKey()
    {
        if (!HasKey) Generate();
        return new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = State.MatchKeyX!, Y = State.MatchKeyY! };
    }

    public RelayEcKeyPair GetKey()
    {
        if (cached is not null) return cached;
        if (!HasKey) throw new InvalidOperationException("No match key yet.");
        var privateD = DeviceIdentityService.Unprotect(State.ProtectedMatchKey!, State.MatchKeyProtected, Entropy);
        try
        {
            cached = RelayCrypto.ImportSigningPrivateKey(new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = State.MatchKeyX!, Y = State.MatchKeyY! }, privateD);
        }
        finally
        {
            Array.Clear(privateD);
        }
        return cached;
    }

    /// Only when there's no profile and no match left that could still need it.
    public void DropIfUnused()
    {
        if (!HasKey || State.Published || State.HasActiveMatch) return;
        cached?.Dispose();
        cached = null;
        State.MatchKeyX = State.MatchKeyY = null;
        State.ProtectedMatchKey = null;
        config.Save();
    }

    private void Generate()
    {
        using var key = RelayCrypto.GenerateSigningKeyPair();
        var jwk = RelayCrypto.ExportPublicKeyJwk(key);
        var privateD = RelayCrypto.ExportPrivateD(key);
        var (protectedKey, wasProtected) = DeviceIdentityService.Protect(privateD, Entropy);
        State.MatchKeyX = jwk.X;
        State.MatchKeyY = jwk.Y;
        State.ProtectedMatchKey = protectedKey;
        State.MatchKeyProtected = wasProtected;
        if (!ReferenceEquals(protectedKey, privateD)) Array.Clear(privateD);
        cached?.Dispose();
        cached = null;
        config.SaveNow();
    }
}
