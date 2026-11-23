using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// Activation and teardown of pairings. Pairing itself is by code (CodePairingService); nothing here activates a
/// pairing from relay state alone.
public sealed class PairingService
{
    private readonly PluginConfig config;
    private readonly DeviceIdentityService identity;
    private readonly CollarCommand collar;
    private readonly RevocationService revocation;

    /// Clears ChatCommandListener's stale "your peer unpaired" notice.
    public event Action? PairingActivated;
    /// Only for a pairing this install just completed (new or re-paired), not for other pairing changes.
    public event Action<PairingState>? PairingCompleted;
    public event Action? PairingEnded;

    /// Runs before the identity is replaced, while the old key can still sign, so the old backup can be deleted.
    public Func<CancellationToken, Task>? BeforeIdentityReset { get; set; }

    public PairingService(PluginConfig config, DeviceIdentityService identity, CollarCommand collar, RevocationService revocation)
    {
        this.config = config;
        this.identity = identity;
        this.collar = collar;
        this.revocation = revocation;
    }

    /// Returns whether a pairing was added. pairIdHash is symmetric, so a fetch can return the other direction's epoch;
    /// callers treat false as "not ready yet".
    internal bool ActivateLocally(PairEnvelope pair, PairingDirection direction, Guid pairingId, string peerName, string peerWorld, string peerDeviceKeyId, EcPublicKeyJwk peerPublicKey, string? peerTriggerPhrase)
    {
        var ownKeyId = identity.DeviceKeyId;
        var expectedOwner = direction == PairingDirection.OwnerSide ? ownKeyId : peerDeviceKeyId;
        var expectedSub = direction == PairingDirection.SubSide ? ownKeyId : peerDeviceKeyId;
        if (pair.Type != "pair" || pair.SchemaVersion != 1 || pair.OwnerDeviceKeyId != expectedOwner || pair.SubDeviceKeyId != expectedSub ||
            pair.PairIdHash != RelayCrypto.ComputePairIdHash(ownKeyId!, peerDeviceKeyId))
            return false;

        // Re-pairing the same peer device in the same direction updates in place, keeping Id and revocation counters.
        var existing = config.Pairings.FirstOrDefault(p => p.PeerDeviceKeyId == peerDeviceKeyId && p.Direction == direction);
        // An epoch older than ours for the same peer+direction is a stale fetch, not a new pairing - keep polling.
        if (existing is not null && pair.PairEpoch < existing.PairEpoch)
            return false;
        var pairing = existing ?? new PairingState { Id = pairingId, Direction = direction };
        pairing.PairIdHash = pair.PairIdHash;
        pairing.PairEpoch = pair.PairEpoch;
        pairing.PeerDeviceKeyId = peerDeviceKeyId;
        pairing.PeerPublicKeyX = peerPublicKey.X;
        pairing.PeerPublicKeyY = peerPublicKey.Y;
        pairing.PeerName = peerName;
        pairing.PeerWorld = peerWorld;
        pairing.PeerTriggerPhrase = peerTriggerPhrase;
        // A new epoch starts over with just the character that paired; alts are announced again by their records.
        pairing.ClearCharacters();
        pairing.EnsureCharactersMigrated();
        pairing.Paired = true;
        pairing.RecordPairedFrom = true;
        config.LinkCurrentCharacterRequested = true;
        if (existing is null)
            config.Pairings.Add(pairing);
        // Becomes active unless another live pairing already is; an id left on an ended pairing doesn't count.
        if (config.ActivePairingId is not { } activeId || config.FindPairingById(activeId) is not { IsPaired: true })
            config.ActivePairingId = pairing.Id;
        config.PendingRelayOperations.RemoveAll(o => o.Kind is "pair-invite" or "pair-accept");
        config.SaveNow();
        PairingActivated?.Invoke();
        PairingCompleted?.Invoke(pairing);
        return true;
    }

    public void EndFromVerifiedPeerNotice(PairingState pairing)
    {
        if (!pairing.IsPaired) return;
        pairing.Paired = false;
        pairing.Rulebook = new();
        pairing.CatalogDelivery = new();
        pairing.ClearCharacters();
        config.SaveNow();
        PairingEnded?.Invoke();
        // Ending an unrelated pairing never touches the collar.
        if (config.CollarOwningPairingId == pairing.Id)
            collar.ReleaseOnUnpair();
        PairingActivated?.Invoke();
    }

    /// Every ReleasePeer must finish before the identity is replaced: revocations must be signed by the old key.
    public async Task ResetDeviceIdentityAsync(CancellationToken ct)
    {
        foreach (var pairing in config.Pairings.Where(p => p.IsPaired).ToList())
        {
            var pairIdHash = pairing.PairIdHash;
            var pairEpoch = pairing.PairEpoch;
            ReleasePeer(pairing, publishRelayRevocation: false);
            if (pairIdHash is not null)
            {
                try { await revocation.PublishBestEffortAsync(pairing, pairIdHash, pairEpoch, "identity-reset", ct).ConfigureAwait(false); }
                catch (DeviceIdentityUnavailableException) { } // The old key can't sign; the reset is how the user recovers from that.
            }

            // A retry signed by the retired identity can't be authenticated anymore, so drop it.
            config.RevocationOutbox.RemoveAll(o => o.PairIdHash == pairIdHash && o.PairEpoch == pairEpoch);
        }
        if (BeforeIdentityReset is { } beforeReset)
            await beforeReset(ct).ConfigureAwait(false);
        identity.ResetIdentity();
    }

    /// Local teardown completes before the best-effort revocation is attempted. Never touches other pairings.
    public void ReleasePeer(PairingState pairing, bool publishRelayRevocation = true)
    {
        var pairIdHash = pairing.PairIdHash;
        var pairEpoch = pairing.PairEpoch;

        pairing.PeerName = null;
        pairing.PeerWorld = null;
        pairing.PeerDeviceKeyId = null;
        pairing.PeerPublicKeyX = null;
        pairing.PeerPublicKeyY = null;
        pairing.PairIdHash = null;
        pairing.Paired = false;
        pairing.Rulebook = new();
        pairing.CatalogDelivery = new();
        pairing.ClearCharacters();
        config.SaveNow();
        PairingEnded?.Invoke();
        if (config.CollarOwningPairingId == pairing.Id)
            collar.ReleaseOnUnpair();

        if (publishRelayRevocation && pairIdHash is not null)
            Plugin.FireAndForget(revocation.PublishBestEffortAsync(pairing, pairIdHash, pairEpoch, "unpair", CancellationToken.None));
    }
}
