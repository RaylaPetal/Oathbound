using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// A relay invitation this side created and is waiting on (inviter role). Its non-secret reference,
/// target, and expiry are persisted so a matching acknowledgement can still complete after a restart.
/// `Direction` is which side of the resulting pairing *this* device will be on (OwnerSide = this device
/// will command the peer) - required explicitly rather than derived from Role, since a Switch can send an
/// invite establishing either direction.
public readonly record struct OutgoingInvitation(string InvitationId, PairingDirection Direction, string Target, long ExpiresAt);

/// What `CreateAndSendInvitationAsync` would silently replace if called right now - surfaced so the UI can
/// ask for explicit confirmation instead of orphaning an invitation the peer might still accept (see
/// "Sending a new invite while one is already outstanding" in collar/pairing).
public readonly record struct OutstandingInvitation(string Target, long ExpiresAt);

/// Mirrors the old `PendingPairingRequest`/`PeerUnpairedNotice` shape ChatCommandListener/CollarWindow/
/// SettingsWindow already know how to render, but populated from a fetched-and-verified relay invitation
/// instead of a code match.
public readonly record struct PendingPairingRequest(string InvitationId, string Name, string World, PluginRole SenderRole, string? TriggerPhrase, long ExpiresAt);

/// collar/pairing's relay-assisted handshake (Relay-assisted pairing binds device proof to verified game
/// identity). Owns every step of both roles' state machine; ChatCommandListener only recognizes the two
/// short lifecycle tells (`collarinvite`, `collarpairack`) and a
/// verified sender, then delegates here. Nothing in this class ever activates a pairing from relay state
/// alone - see HandleAcknowledgementTellAsync's comment.
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

    /// Fired once a pairing actually activates (inviter side, after consume succeeds). CollarWindow's
    /// stale "your peer unpaired" notice is superseded by a freshly-completed pairing - most relevant when
    /// re-pairing with the same person after they unpaired - but that notice lives in ChatCommandListener,
    /// not here, so this is an event rather than a direct call.
    public event Action? PairingActivated;
    public event Action? PairingEnded;

    /// collar/pairing-recovery: runs just before the identity is replaced, while the old key can still sign -
    /// Plugin wires it to BackupService so the old identity's backup is deleted from the relay.
    public Func<CancellationToken, Task>? BeforeIdentityReset { get; set; }

    /// Set after CreateAndSendInvitationAsync/AcceptPendingAsync/HandleAcknowledgementTellAsync fail, so
    /// Settings can show *why* without the caller needing its own try/catch around every button click.
    public string? LastError { get; private set; }
    public event Action? LastErrorChanged;

    /// True from a successful Accept until this side's own activation poll (see AwaitActivationAsync)
    /// either succeeds or gives up - lets Settings show "waiting for confirmation" instead of looking stuck.
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

    /// Query-only: what an immediate call to `CreateAndSendInvitationAsync` would replace, if anything.
    /// Never mutates state - the UI calls this first so it can ask for explicit confirmation before an
    /// unconfirmed, unexpired invitation is silently discarded (collar/pairing "Sending a new invite while
    /// one is already outstanding").
    public OutstandingInvitation? DescribeOutstandingInvitation()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return outgoingInvitation is { } o && o.ExpiresAt > now ? new OutstandingInvitation(o.Target, o.ExpiresAt) : null;
    }

    /// Inviter side, step 1: create a single-use invitation via the relay and send its reference in one
    /// tell. One click, one invitation, one tell (task 4.1). Callers should check
    /// `DescribeOutstandingInvitation()` first and get explicit confirmation before calling through if it
    /// returns non-null, since this always replaces any prior outgoing invitation without asking.
    /// `direction` is which side of the resulting pairing this device will be on - OwnerSide for an Owner
    /// or a Switch inviting a prospective Sub, SubSide for a Sub or a Switch inviting a prospective Owner.
    /// collar/multi-pairing: holding other active pairings never blocks sending another invitation - only a
    /// direction this device's Role doesn't support does.
    public async Task<bool> CreateAndSendInvitationAsync(string targetTellAddress, PairingDirection direction, CancellationToken ct)
    {
        if (config.Role == PluginRole.Owner && direction != PairingDirection.OwnerSide ||
            config.Role == PluginRole.Sub && direction != PairingDirection.SubSide)
        {
            SetError("The current Role does not support that pairing direction.");
            return false;
        }
        // A blank trigger phrase means incoming commands can never match once this device becomes the
        // Sub-side of a pairing (collar/chat-transport's own listener refuses to match an empty trigger) -
        // refused up front rather than letting pairing "succeed" into a relationship that can never work.
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
                // Empty must become null, not "": the Worker's own envelope reconstruction treats an
                // empty triggerPhrase as absent (protocol/schemas), so this side's signed canonical form
                // has to agree or the envelope's self-signature never verifies (collar/pairing bug: Accept
                // failing with "relay rejected this request" whenever the accepter's trigger phrase is blank).
                TriggerPhrase = string.IsNullOrWhiteSpace(config.TriggerPhrase) ? null : config.TriggerPhrase.Trim(),
                CreatedAt = now,
                ExpiresAt = now + 900,
            };
            envelope.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(envelope));

            var created = await relay.CreateInvitationAsync(envelope, ct).ConfigureAwait(false);
            outgoingInvitation = new OutgoingInvitation(created.InvitationId, direction, targetTellAddress.Trim(), created.ExpiresAt);
            config.PendingRelayOperations.RemoveAll(o => o.Kind == "pair-invite");
            config.PendingRelayOperations.Add(new PendingRelayOperationState { Kind = "pair-invite", OperationId = created.InvitationId, Target = targetTellAddress.Trim(), ExpiresAt = created.ExpiresAt, Direction = direction });
            config.Save();

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

    /// Receiver side, step 1: a `collarinvite <invitationId>` tell arrived from `senderName`@`senderWorld`
    /// (already verified by Dalamud's own chat sender field - see ChatCommandListener). Fetches and
    /// independently verifies the invitation's own signature before ever showing it as a Pending request;
    /// a copied/forged reference that doesn't verify is silently dropped, never shown.
    public async Task HandleInvitationTellAsync(string invitationId, string senderName, string senderWorld, CancellationToken ct)
    {
        // collar/multi-pairing: holding other active pairings never blocks receiving a new one. Only one
        // incoming request can be Pending at a time, though (same single-slot pattern as the outgoing side)
        // - a second invitation arriving before the first is accepted or dismissed is dropped, not queued.
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

    /// Receiver side, step 2: explicit Accept. Publishes a signed acceptance proof, then sends exactly one
    /// bounded acknowledgement tell back to the inviter - this side does not consider itself paired yet
    /// (collar/pairing "Relay acceptance lacks matching game identity" is the inviter's problem to solve,
    /// not this side's; this side's own Pending clears either way once Accept is clicked).
    public async Task<bool> AcceptPendingAsync(CancellationToken ct)
    {
        if (Pending is not { } request) return false;
        if (request.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            DismissPending();
            SetError("That invitation expired - ask for a fresh one.");
            return false;
        }

        // collar/multi-pairing: which side of the new pairing this device becomes is the opposite of what
        // the inviter declared themselves as - derived from the invitation, not from this device's own
        // Role, since a Switch's own Role doesn't say which direction any one pairing is.
        var direction = request.SenderRole == PluginRole.Owner ? PairingDirection.SubSide : PairingDirection.OwnerSide;

        // See CreateAndSendInvitationAsync's matching check - accepting into a Sub-side pairing is just as
        // pointless with a blank trigger phrase as sending into one.
        if (direction == PairingDirection.SubSide && string.IsNullOrWhiteSpace(config.TriggerPhrase))
        {
            SetError("Set a trigger phrase in Settings before accepting this - without one, incoming commands can never apply.");
            return false;
        }

        // Pre-generated here (rather than left to PairingState's own default) so a same-moment collar
        // ForceApply below can record ownership under the exact id this pairing will carry once
        // ActivateLocally actually adds it to Pairings - that only happens later, once the background
        // activation poll confirms the inviter's consume.
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
                // Empty must become null, not "": the Worker's own envelope reconstruction treats an
                // empty triggerPhrase as absent (protocol/schemas), so this side's signed canonical form
                // has to agree or the envelope's self-signature never verifies (collar/pairing bug: Accept
                // failing with "relay rejected this request" whenever the accepter's trigger phrase is blank).
                TriggerPhrase = string.IsNullOrWhiteSpace(config.TriggerPhrase) ? null : config.TriggerPhrase.Trim(),
                CreatedAt = now,
                ExpiresAt = now + 900,
            };
            envelope.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(envelope));

            await relay.AcceptInvitationAsync(request.InvitationId, envelope, ct).ConfigureAwait(false);

            var ack = composer.ComposePairingAck(request.Name, request.World, request.InvitationId, proofDigest);
            sender.Send(ack);

            // collar/pairing "Accepting a pairing request applies a configured collar": a conditional side
            // effect of acceptance itself, not a separate command - only when this device is becoming the
            // Sub-side of the new pairing (collar/collaring only ever applies to this device's own Neck).
            // Must run on the game's main thread - unlike ChatCommandListener's own synchronous call to this
            // same method, this call happens after an `await ... ConfigureAwait(false)` above, so by this
            // point execution has resumed on an arbitrary thread-pool thread. ForceApply -> ApplyAssignedMoodle
            // -> MoodlesIpc.ApplyStatus touches Dalamud's ObjectTable.LocalPlayer, which throws
            // "Not on main thread!" off the main thread - confirmed in-game: this exact exception was aborting
            // the rest of Accept silently (Pending never cleared, the activation poll below never started),
            // with no feedback to the user beyond a log line, since AcceptPendingAsync is invoked fire-and-
            // forget from the Accept button.
            if (direction == PairingDirection.SubSide && config.Permissions.Collar && config.Collar.IsConfigured)
                await Plugin.Framework.RunOnFrameworkThread(() => collar.ForceApply(pairingId)).ConfigureAwait(false);

            Pending = null;
            PendingChanged?.Invoke();
            SetError(null);

            // The accepter never calls consume, so it has no other way to learn the pair epoch the inviter
            // is about to assign - poll the deterministic pairIdHash (bounded) rather than block Accept on it.
            _ = AwaitActivationAsync(request, direction, pairingId, ct);
            return true;
        }
        catch (RelayException ex)
        {
            SetError(DescribeError(ex));
            return false;
        }
    }

    /// Bounded background poll (accepter side): the inviter typically calls consume within seconds of
    /// receiving the acknowledgement tell, so this checks every few seconds for up to two minutes before
    /// giving up and surfacing an error - the accepted invitation itself already recorded this side's
    /// consent; this is purely "learn what epoch the inviter assigned," not a second consent step.
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
                    // collar/multi-pairing: fetch-by-hash returns whichever epoch is latest for these two
                    // devices - if this pair already has another pairing (opposite direction), an early
                    // poll can land on that prior epoch before the inviter's consume() creates this one.
                    // That's a race, not a failure - keep polling instead of giving up.
                    if (ActivateLocally(pair, direction, pairingId, request.Name, request.World, inviterInvitation.InviterDeviceKeyId, inviterInvitation.InviterPublicKey, request.TriggerPhrase))
                    {
                        SetError(null);
                        return;
                    }
                }
                catch (RelayException ex) when (ex.Code is "unauthorized" or "not_found")
                {
                    // Not activated yet (the inviter hasn't called consume) - keep waiting.
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
            // Plugin shutting down or caller cancelled - not an error to surface.
        }
        finally
        {
            AwaitingActivation = false;
            AwaitingActivationChanged?.Invoke();
        }
    }

    /// Bounded retry/backoff for a transient relay hiccup while processing an acknowledgement tell (seconds
    /// to wait after each failed attempt) - mirrors the intent behind AwaitActivationAsync's poll loop on
    /// the accepter side, which the original relay design called for here too ("[Tell acknowledgement is
    /// lost] -> allow a bounded resend/recheck") but never implemented. Total worst case (~46s) stays well
    /// inside the invitation/acceptance's own 15-minute expiry.
    private static readonly int[] AcknowledgementRetryDelaysSeconds = [2, 4, 8, 16, 16];

    /// Inviter side, step 2: a `collarpairack <invitationId> <proofDigest>` tell arrived from a
    /// server-verified sender. Activates the pairing only when the fetched invitation's acceptance proof
    /// digest matches exactly what this tell carries - the tell's verified sender is what binds the relay's
    /// claimed acceptance to an actual character; relay state alone is never sufficient (collar/pairing
    /// "Relay acceptance lacks matching game identity"). A transient relay failure while fetching or
    /// consuming is retried a bounded number of times before giving up and surfacing an error - previously
    /// this failed silently into a log line with no way for the user to ever learn pairing didn't complete.
    public async Task HandleAcknowledgementTellAsync(string invitationId, string proofDigestHex, string senderName, string senderWorld, CancellationToken ct)
    {
        if (outgoingInvitation is not { } outgoing || outgoing.InvitationId != invitationId)
        {
            // Not an invitation we created (or already consumed) - ignore, never activate from claims. Logged
            // (not just silently ignored) since this is otherwise indistinguishable from "the ack never
            // arrived" from the accepter's perspective - e.g. a second `collarinvite` replaced this one
            // before the peer's ack for the first arrived.
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

                // Unlike the accepter's own AwaitActivationAsync (which fetches by pairIdHash alone and can
                // race a still-existing prior epoch between the same two devices), consume() always returns
                // the envelope for *this exact* invitation - a mismatch here is a genuine protocol violation,
                // not a race, so it's reported immediately rather than retried.
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

    /// Returns whether a pairing was actually added. `pairIdHash` alone can't distinguish directions - the
    /// same two devices pairing a second time (in the opposite direction) computes the *same* pairIdHash as
    /// their first pairing (collar/multi-pairing: ComputePairIdHash is symmetric over just the two device
    /// key ids), so a fetch-by-hash can return a *different, still-valid* epoch of the SAME hash whose
    /// owner/sub don't match the direction currently being activated - not a protocol violation, just a
    /// race against the inviter's own consume (see AwaitActivationAsync, which retries on `false` rather
    /// than treating it as terminal).
    internal bool ActivateLocally(PairEnvelope pair, PairingDirection direction, Guid pairingId, string peerName, string peerWorld, string peerDeviceKeyId, EcPublicKeyJwk peerPublicKey, string? peerTriggerPhrase)
    {
        var ownKeyId = identity.DeviceKeyId;
        var expectedOwner = direction == PairingDirection.OwnerSide ? ownKeyId : peerDeviceKeyId;
        var expectedSub = direction == PairingDirection.SubSide ? ownKeyId : peerDeviceKeyId;
        if (pair.Type != "pair" || pair.SchemaVersion != 1 || pair.OwnerDeviceKeyId != expectedOwner || pair.SubDeviceKeyId != expectedSub ||
            pair.PairIdHash != RelayCrypto.ComputePairIdHash(ownKeyId!, peerDeviceKeyId))
            return false;

        // collar/multi-pairing: re-pairing the same specific peer device in the same direction again
        // (e.g. a retried/duplicated activation from the race above, or a genuine unpair-then-re-pair)
        // updates that existing PairingState in place rather than adding a duplicate entry - this also
        // means its revocation-sequence counters and Id (so anything already pointing at it, like
        // ActivePairingId or CollarOwningPairingId, stays valid) survive the re-pair.
        var existing = config.Pairings.FirstOrDefault(p => p.PeerDeviceKeyId == peerDeviceKeyId && p.Direction == direction);
        // collar/multi-pairing: fetch-by-hash (AwaitActivationAsync's accepter-side poll) returns whichever
        // epoch is currently latest for these two devices, which can be a *stale* epoch of this exact same
        // direction if this pair was re-established before and the inviter's consume() for the new epoch
        // hasn't landed yet - owner/sub keys alone can't tell a genuinely new pairing apart from a replay of
        // an old one in that case, so a fetched epoch older than what's already on file for this peer+
        // direction is treated as "not ready yet" (false, keep polling) rather than silently regressing this
        // side's epoch out of sync with the peer's.
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
        // The very first pairing this device ever gets becomes active by default; a later one added while
        // another is already selected leaves that selection alone.
        if (existing is null)
        {
            config.Pairings.Add(pairing);
            config.ActivePairingId ??= pairing.Id;
        }
        config.PendingRelayOperations.RemoveAll(o => o.Kind is "pair-invite" or "pair-accept");
        config.Save();
        PairingActivated?.Invoke();
        return true;
    }

    public void EndFromVerifiedPeerNotice(PairingState pairing)
    {
        if (!pairing.IsPaired) return;
        pairing.Paired = false;
        config.Save();
        PairingEnded?.Invoke();
        // collar/collaring: local collar effects only unwind when the ending pairing is this device's
        // current collar-owning pairing - ending an unrelated pairing never touches the collar.
        if (config.CollarOwningPairingId == pairing.Id)
            collar.ReleaseOnUnpair();
        PairingActivated?.Invoke();
    }

    /// collar/pairing "User resets the device identity": ends every active pairing first (local teardown
    /// of each, then a best-effort revocation for each signed with the *old* identity - a revocation signed
    /// by the new key wouldn't match the deviceKeyId the peer or relay have on file for that pair) before
    /// the identity itself is replaced. Order matters: every ReleasePeer must complete before
    /// DeviceIdentityService.ResetIdentity, since a revocation for one pairing can no longer be signed once
    /// the identity underlying all of them has been swapped.
    public async Task ResetDeviceIdentityAsync(CancellationToken ct)
    {
        foreach (var pairing in config.Pairings.Where(p => p.IsPaired).ToList())
        {
            var pairIdHash = pairing.PairIdHash;
            var pairEpoch = pairing.PairEpoch;
            ReleasePeer(pairing, publishRelayRevocation: false);
            if (pairIdHash is not null)
                await revocation.PublishBestEffortAsync(pairing, pairIdHash, pairEpoch, "identity-reset", ct).ConfigureAwait(false);

            // A retry signed by the retired identity cannot be authenticated after key replacement. The
            // initial delivery was attempted above; discard any failed old-key entry rather than retaining
            // a permanently unpublishable outbox item under the new identity.
            config.RevocationOutbox.RemoveAll(o => o.PairIdHash == pairIdHash && o.PairEpoch == pairEpoch);
        }
        if (BeforeIdentityReset is { } beforeReset)
            await beforeReset(ct).ConfigureAwait(false);
        identity.ResetIdentity();
    }

    /// Deliberate manual release of one specific pairing (any direction, Owner-side or Sub-side - see
    /// PanicHandler.ReleasePairing, which wraps this with the same local-state revert panic itself does).
    /// Clears the captured peer identity entirely. Local teardown (clearing config) completes fully before
    /// the best-effort revocation publish is even attempted, same ordering guarantee as PanicHandler always
    /// used. Never touches any other pairing this device holds.
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
        config.Save();
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
