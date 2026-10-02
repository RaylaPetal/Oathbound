using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// Persisted so a matching acknowledgement can still complete after a restart. Direction is explicit because a Switch can invite either way.
public readonly record struct OutgoingInvitation(string InvitationId, PairingDirection Direction, string Target, long ExpiresAt);

/// What CreateAndSendInvitationAsync would replace, so the UI can confirm first.
public readonly record struct OutstandingInvitation(string Target, long ExpiresAt);

public readonly record struct PendingPairingRequest(string InvitationId, string Name, string World, PluginRole SenderRole, string? TriggerPhrase, long ExpiresAt);

/// The tell-referenced relay pairing state machine, both roles. Nothing here ever activates a pairing from relay state alone.
public sealed class PairingService
{
    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly DeviceIdentityService identity;
    private readonly ChatComposer composer;
    private readonly ChatSender sender;
    private readonly CollarCommand collar;
    private readonly RevocationService revocation;

    private OutgoingInvitation? outgoingInvitation;
    public long? OutgoingInvitationExpiresAt => outgoingInvitation?.ExpiresAt;
    public string? OutgoingInvitationTarget => outgoingInvitation?.Target;
    public string Phase => Pending is not null ? "Invitation received" : AwaitingActivation ? "Waiting for peer confirmation" : outgoingInvitation is not null ? "Invitation sent" : config.Pairings.Any(p => p.IsPaired) ? "Paired" : "Not paired";

    public PendingPairingRequest? Pending { get; private set; }
    public event Action? PendingChanged;

    /// Clears ChatCommandListener's stale "your peer unpaired" notice.
    public event Action? PairingActivated;
    public event Action? PairingEnded;

    /// Runs before the identity is replaced, while the old key can still sign, so the old backup can be deleted.
    public Func<CancellationToken, Task>? BeforeIdentityReset { get; set; }

    public string? LastError { get; private set; }
    public event Action? LastErrorChanged;

    /// True from Accept until the activation poll succeeds or gives up.
    public bool AwaitingActivation { get; private set; }
    public event Action? AwaitingActivationChanged;

    public PairingService(PluginConfig config, RelayClient relay, DeviceIdentityService identity, ChatComposer composer, ChatSender sender, CollarCommand collar, RevocationService revocation)
    {
        this.config = config;
        this.relay = relay;
        this.identity = identity;
        this.composer = composer;
        this.sender = sender;
        this.collar = collar;
        this.revocation = revocation;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var interrupted = config.PendingRelayOperations.FindLast(o =>
            o.Kind == "pair-invite" && o.ExpiresAt > now && !string.IsNullOrWhiteSpace(o.OperationId));
        if (interrupted is not null)
            outgoingInvitation = new OutgoingInvitation(interrupted.OperationId, interrupted.Direction, interrupted.Target ?? "", interrupted.ExpiresAt);
    }

    private void SetError(string? message)
    {
        LastError = message;
        LastErrorChanged?.Invoke();
    }

    /// Never mutates state.
    public OutstandingInvitation? DescribeOutstandingInvitation()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return outgoingInvitation is { } o && o.ExpiresAt > now ? new OutstandingInvitation(o.Target, o.ExpiresAt) : null;
    }

    /// Always replaces any prior outgoing invitation - check DescribeOutstandingInvitation() first.
    public async Task<bool> CreateAndSendInvitationAsync(string targetTellAddress, PairingDirection direction, CancellationToken ct)
    {
        if (config.Role == PluginRole.Owner && direction != PairingDirection.OwnerSide ||
            config.Role == PluginRole.Sub && direction != PairingDirection.SubSide)
        {
            SetError("The current Role does not support that pairing direction.");
            return false;
        }
        // A blank trigger phrase could never match incoming commands, so refuse up front.
        if (direction == PairingDirection.SubSide && string.IsNullOrWhiteSpace(config.TriggerPhrase))
        {
            SetError("Set a trigger phrase in Settings before pairing as Sub - without one, incoming commands can never apply.");
            return false;
        }
        if (!ChatComposer.TryValidateTellTarget(targetTellAddress, out var targetError))
        {
            SetError(targetError);
            return false;
        }
        try
        {
            identity.EnsureIdentity();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var envelope = new InvitationEnvelope
            {
                InvitationId = RelayCrypto.RandomInvitationId(),
                InviterDeviceKeyId = identity.DeviceKeyId!,
                InviterPublicKey = identity.GetPublicKeyJwk(),
                Role = direction == PairingDirection.OwnerSide ? "owner" : "sub",
                // Empty must become null: the Worker treats an empty triggerPhrase as absent, so the signed canonical form must agree.
                TriggerPhrase = string.IsNullOrWhiteSpace(config.TriggerPhrase) ? null : config.TriggerPhrase.Trim(),
                CreatedAt = now,
                ExpiresAt = now + 900,
            };
            envelope.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(envelope));

            var created = await relay.CreateInvitationAsync(envelope, ct).ConfigureAwait(false);
            outgoingInvitation = new OutgoingInvitation(created.InvitationId, direction, targetTellAddress.Trim(), created.ExpiresAt);
            config.PendingRelayOperations.RemoveAll(o => o.Kind == "pair-invite");
            config.PendingRelayOperations.Add(new PendingRelayOperationState { Kind = "pair-invite", OperationId = created.InvitationId, Target = targetTellAddress.Trim(), ExpiresAt = created.ExpiresAt, Direction = direction });
            config.SaveNow();

            var tell = composer.ComposeRelayInvitation(targetTellAddress, created.InvitationId);
            sender.Send(tell);
            SetError(null);
            return true;
        }
        catch (RelayException ex)
        {
            SetError(DescribeError(ex));
            return false;
        }
    }

    /// The invitation's signature is verified before it's ever shown; a forged reference is silently dropped.
    public async Task HandleInvitationTellAsync(string invitationId, string senderName, string senderWorld, CancellationToken ct)
    {
        // Only one incoming request can be Pending; a second is dropped, not queued.
        if (Pending is not null)
        {
            Plugin.Log.Information("Relay invitation tell ignored: another pairing request is already pending.");
            return;
        }
        try
        {
            var invitation = await relay.FetchInvitationAsync(invitationId, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (invitation.Type != "invitation" || invitation.SchemaVersion != 1 || invitation.ExpiresAt <= now || invitation.CreatedAt > now + 300)
            {
                Plugin.Log.Warning("Relay invitation tell ignored: invitation version or lifetime was invalid.");
                return;
            }
            if (!RelayCrypto.VerifyRaw(invitation.InviterPublicKey, invitation.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(invitation)))
            {
                Plugin.Log.Warning("Relay invitation tell ignored: invitation signature did not verify.");
                return;
            }
            if (invitation.InviterDeviceKeyId != RelayCrypto.DeviceKeyId(invitation.InviterPublicKey))
            {
                Plugin.Log.Warning("Relay invitation tell ignored: declared inviterDeviceKeyId did not match the inviter's own public key.");
                return;
            }

            if (invitation.Role is not ("owner" or "sub")) return;
            var senderRole = invitation.Role == "owner" ? PluginRole.Owner : PluginRole.Sub;
            if (senderRole == config.Role)
            {
                SetError("Pairing requires one Owner and one Sub. Change Role before accepting this invitation.");
                return;
            }
            Pending = new PendingPairingRequest(invitationId, senderName, senderWorld, senderRole, invitation.TriggerPhrase, invitation.ExpiresAt);
            PendingChanged?.Invoke();
        }
        catch (RelayException ex)
        {
            Plugin.Log.Information($"Relay invitation tell ignored: {DescribeError(ex)}");
        }
    }

    public void DismissPending()
    {
        Pending = null;
        PendingChanged?.Invoke();
    }

    /// This side isn't paired yet - the inviter activates after re-verifying the proof.
    public async Task<bool> AcceptPendingAsync(CancellationToken ct)
    {
        if (Pending is not { } request) return false;
        if (request.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            DismissPending();
            SetError("That invitation expired - ask for a fresh one.");
            return false;
        }

        // The opposite of what the inviter declared, since a Switch's Role doesn't say which direction this is.
        var direction = request.SenderRole == PluginRole.Owner ? PairingDirection.SubSide : PairingDirection.OwnerSide;

        if (direction == PairingDirection.SubSide && string.IsNullOrWhiteSpace(config.TriggerPhrase))
        {
            SetError("Set a trigger phrase in Settings before accepting this - without one, incoming commands can never apply.");
            return false;
        }

        // Pre-generated so the collar applied below records the id this pairing will carry.
        var pairingId = Guid.NewGuid();

        try
        {
            identity.EnsureIdentity();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var proofDigest = RelayCrypto.RandomProofDigestHex();
            var envelope = new AcceptanceEnvelope
            {
                InvitationId = request.InvitationId,
                AccepterDeviceKeyId = identity.DeviceKeyId!,
                AccepterPublicKey = identity.GetPublicKeyJwk(),
                ProofDigest = proofDigest,
                Role = direction == PairingDirection.OwnerSide ? "owner" : "sub",
                // Empty must become null: the Worker treats an empty triggerPhrase as absent, so the signed canonical form must agree.
                TriggerPhrase = string.IsNullOrWhiteSpace(config.TriggerPhrase) ? null : config.TriggerPhrase.Trim(),
                CreatedAt = now,
                ExpiresAt = now + 900,
            };
            envelope.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(envelope));

            await relay.AcceptInvitationAsync(request.InvitationId, envelope, ct).ConfigureAwait(false);

            var ack = composer.ComposePairingAck(request.Name, request.World, request.InvitationId, proofDigest);
            sender.Send(ack);

            // Must run on the main thread: we're past a ConfigureAwait(false), and the Moodles apply touches LocalPlayer.
            if (direction == PairingDirection.SubSide && config.Permissions.Collar && config.Collar.IsConfigured)
                await Plugin.Framework.RunOnFrameworkThread(() => collar.ForceApply(pairingId)).ConfigureAwait(false);

            Pending = null;
            PendingChanged?.Invoke();
            SetError(null);

            // The accepter never calls consume, so it polls to learn the epoch the inviter assigned.
            _ = AwaitActivationAsync(request, direction, pairingId, ct);
            return true;
        }
        catch (RelayException ex)
        {
            SetError(DescribeError(ex));
            return false;
        }
    }

    /// Checks every few seconds for up to two minutes. Consent was already given; this only learns the epoch.
    private async Task AwaitActivationAsync(PendingPairingRequest request, PairingDirection direction, Guid pairingId, CancellationToken ct)
    {
        AwaitingActivation = true;
        AwaitingActivationChanged?.Invoke();
        try
        {
            var ownDeviceKeyId = identity.DeviceKeyId!;
            var inviterInvitation = await relay.FetchInvitationAsync(request.InvitationId, ct).ConfigureAwait(false);
            var pairIdHash = RelayCrypto.ComputePairIdHash(ownDeviceKeyId, inviterInvitation.InviterDeviceKeyId);

            for (var attempt = 0; attempt < 40; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var pair = await relay.FetchPairAsync(pairIdHash, ct).ConfigureAwait(false);
                    // A poll can land on this pair's other-direction epoch before the inviter's consume - a race, so keep polling.
                    if (ActivateLocally(pair, direction, pairingId, request.Name, request.World, inviterInvitation.InviterDeviceKeyId, inviterInvitation.InviterPublicKey, request.TriggerPhrase))
                    {
                        SetError(null);
                        return;
                    }
                }
                catch (RelayException ex) when (ex.Code is "unauthorized" or "not_found")
                {
                }
                await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }

            SetError("The other side hasn't confirmed yet - ask them to check their pending invitation.");
        }
        catch (RelayException ex)
        {
            SetError(DescribeError(ex));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            AwaitingActivation = false;
            AwaitingActivationChanged?.Invoke();
        }
    }

    /// Bounded retries for a transient relay failure (~46 s total, well inside the 15-minute expiry).
    private static readonly int[] AcknowledgementRetryDelaysSeconds = [2, 4, 8, 16, 16];

    /// Activates only when the fetched acceptance's proof digest matches this tell. The tell's verified sender is what
    /// binds the relay's claim to a real character; relay state alone is never enough.
    public async Task HandleAcknowledgementTellAsync(string invitationId, string proofDigestHex, string senderName, string senderWorld, CancellationToken ct)
    {
        if (outgoingInvitation is not { } outgoing || outgoing.InvitationId != invitationId)
        {
            // Logged because, from the accepter's side, this looks the same as a lost ack.
            Plugin.Log.Information($"Relay acknowledgement tell ignored: invitationId {invitationId} does not match the current outstanding invitation ({outgoingInvitation?.InvitationId ?? "none"}).");
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var invitation = await relay.FetchInvitationAsync(invitationId, ct).ConfigureAwait(false);
                if (invitation.Acceptance is not { } acceptance) return;
                if (acceptance.Role is { } acceptedRole &&
                    acceptedRole != (outgoing.Direction == PairingDirection.OwnerSide ? "sub" : "owner")) return;
                if (!string.Equals(acceptance.ProofDigest, proofDigestHex, StringComparison.OrdinalIgnoreCase)) return;
                if (!RelayCrypto.VerifyRaw(acceptance.AccepterPublicKey, acceptance.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(acceptance)))
                {
                    Plugin.Log.Warning("Relay acknowledgement tell ignored: acceptance signature did not verify.");
                    return;
                }

                var pair = await relay.ConsumeInvitationAsync(invitationId, ct).ConfigureAwait(false);
                outgoingInvitation = null;
                config.PendingRelayOperations.RemoveAll(o => o.Kind == "pair-invite" && o.OperationId == invitationId);

                // consume() returns this exact invitation's envelope, so a mismatch is a protocol violation, not a race.
                if (!ActivateLocally(pair, outgoing.Direction, Guid.NewGuid(), senderName, senderWorld, acceptance.AccepterDeviceKeyId, acceptance.AccepterPublicKey, acceptance.TriggerPhrase))
                    SetError("The relay returned pairing data that did not match the verified devices and roles.");
                else
                    SetError(null);
                return;
            }
            catch (RelayException ex) when (ex.Code is "network" or "service_unavailable" or "rate_limited" && attempt < AcknowledgementRetryDelaysSeconds.Length)
            {
                Plugin.Log.Information($"Relay pairing activation attempt {attempt + 1} failed transiently ({ex.Code}); retrying.");
                try { await Task.Delay(TimeSpan.FromSeconds(AcknowledgementRetryDelaysSeconds[attempt]), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            catch (RelayException ex)
            {
                Plugin.Log.Information($"Relay pairing activation failed: {DescribeError(ex)}");
                SetError($"Could not finish pairing after your peer accepted: {DescribeError(ex)}");
                return;
            }
        }
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
        pairing.Paired = true;
        // The first pairing becomes active by default.
        if (existing is null)
        {
            config.Pairings.Add(pairing);
            config.ActivePairingId ??= pairing.Id;
        }
        config.PendingRelayOperations.RemoveAll(o => o.Kind is "pair-invite" or "pair-accept");
        config.SaveNow();
        PairingActivated?.Invoke();
        return true;
    }

    public void EndFromVerifiedPeerNotice(PairingState pairing)
    {
        if (!pairing.IsPaired) return;
        pairing.Paired = false;
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
                await revocation.PublishBestEffortAsync(pairing, pairIdHash, pairEpoch, "identity-reset", ct).ConfigureAwait(false);

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
        var peerName = pairing.PeerName;
        var peerWorld = pairing.PeerWorld;
        var pairIdHash = pairing.PairIdHash;
        var pairEpoch = pairing.PairEpoch;

        pairing.PeerName = null;
        pairing.PeerWorld = null;
        pairing.PeerDeviceKeyId = null;
        pairing.PeerPublicKeyX = null;
        pairing.PeerPublicKeyY = null;
        pairing.PairIdHash = null;
        pairing.Paired = false;
        config.SaveNow();
        PairingEnded?.Invoke();
        if (config.CollarOwningPairingId == pairing.Id)
            collar.ReleaseOnUnpair();

        if (!string.IsNullOrWhiteSpace(peerName) && !string.IsNullOrWhiteSpace(peerWorld))
        {
            try { sender.Send(composer.ComposeUnpairNotice(peerName, peerWorld, pairing.Direction)); }
            catch (Exception ex) { Plugin.Log.Warning(ex, "Could not send the peer unpair notification tell; relay revocation will still be attempted."); }
        }

        if (publishRelayRevocation && pairIdHash is not null)
            Plugin.FireAndForget(revocation.PublishBestEffortAsync(pairing, pairIdHash, pairEpoch, "unpair", CancellationToken.None));
    }

    private static string DescribeError(RelayException ex) => ex.Code switch
    {
        "not_configured" => "No relay endpoint is configured.",
        "network" => "Could not reach the relay - check your connection and try again.",
        "cooldown_active" => "Still cooling down - try again shortly.",
        "rate_limited" => "Too many attempts - try again shortly.",
        "expired" => "That invitation is no longer valid - create a fresh one.",
        "unauthorized" => "The relay rejected this request.",
        "service_unavailable" => "The relay is temporarily unavailable - try again shortly.",
        _ => "The relay request failed.",
    };
}
