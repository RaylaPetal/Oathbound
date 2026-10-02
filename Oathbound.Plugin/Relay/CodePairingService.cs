using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

public sealed record CodePreview(string Code, string InvitationId, PairingCharacter Inviter, PairingDirection OwnDirection, long ExpiresAt,
    string InviterDeviceKeyId, EcPublicKeyJwk InviterPublicKey);

/// Pairing by code, with no tells and no need to be online together. Each side's character travels encrypted under
/// a key derived from the code, so the relay never learns who pairs. Commands are still only accepted from the
/// stored peer's verified tell sender.
public sealed class CodePairingService
{
    /// Fast for a while after this side acts, then slow for the rest of the code's 7-day life.
    private static readonly TimeSpan FastPollInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FastPollWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SlowPollInterval = TimeSpan.FromMinutes(5);

    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly DeviceIdentityService identity;
    private readonly PairingService pairing;
    private readonly CollarCommand collar;

    private DateTime nextPollUtc = DateTime.MinValue;
    private DateTime fastUntilUtc = DateTime.MinValue;
    private int polling;

    public CodePairingService(PluginConfig config, RelayClient relay, DeviceIdentityService identity, PairingService pairing, CollarCommand collar)
    {
        this.config = config;
        this.relay = relay;
        this.identity = identity;
        this.pairing = pairing;
        this.collar = collar;
    }

    public CodePreview? Preview { get; private set; }

    /// Cleared by the next successful action.
    public string? LastError { get; private set; }
    public string? Notice { get; private set; }

    public CodeInvitationState? Outgoing => config.CodeInvitations.FirstOrDefault(c => c.IsInviter);
    public CodeInvitationState[] Accepted => config.CodeInvitations.Where(c => !c.IsInviter).ToArray();

    public void DismissNotice() => Notice = null;

    /// Reads the object table, so call it on the main thread.
    public static PairingCharacter? CurrentCharacter(string? triggerPhrase)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null) return null;
        var world = player.HomeWorld.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(world)) return null;
        return new PairingCharacter(player.Name.TextValue, world, string.IsNullOrWhiteSpace(triggerPhrase) ? null : triggerPhrase.Trim());
    }

    private void Fail(string message)
    {
        LastError = message;
        Plugin.Log.Information($"Code pairing: {message}");
    }

    private void Succeed()
    {
        LastError = null;
        fastUntilUtc = DateTime.UtcNow + FastPollWindow;
        nextPollUtc = DateTime.UtcNow + FastPollInterval;
    }

    private bool DirectionAllowed(PairingDirection direction) =>
        !(config.Role == PluginRole.Owner && direction != PairingDirection.OwnerSide ||
          config.Role == PluginRole.Sub && direction != PairingDirection.SubSide);

    // ---- Inviter ----

    /// Replaces (and withdraws on the relay) any earlier unused code. Null on failure (see LastError).
    public async Task<string?> CreateCodeAsync(PairingDirection direction, PairingCharacter self, CancellationToken ct)
    {
        if (!DirectionAllowed(direction)) { Fail("The current Role does not support that pairing direction."); return null; }
        if (direction == PairingDirection.SubSide && string.IsNullOrWhiteSpace(config.TriggerPhrase))
        {
            Fail("Set a trigger phrase in Settings before pairing as Sub - without one, incoming commands can never apply.");
            return null;
        }

        try
        {
            identity.EnsureIdentity();
            if (Outgoing is { } previous)
                await WithdrawAsync(previous, ct).ConfigureAwait(false);

            var code = PairingCodes.NewPairingCode();
            var invitationId = PairingCodes.LookupId(code);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var envelope = new InvitationEnvelope
            {
                InvitationId = invitationId,
                InviterDeviceKeyId = identity.DeviceKeyId!,
                InviterPublicKey = identity.GetPublicKeyJwk(),
                Role = direction == PairingDirection.OwnerSide ? "owner" : "sub",
                Kind = "code",
                EncryptedCharacter = PairingCodes.EncryptCharacter(code, invitationId, inviter: true, self with { TriggerPhrase = config.TriggerPhrase }),
                CreatedAt = now,
                ExpiresAt = now + RelayProtocolConstants.CodeInvitationExpirySeconds,
            };
            envelope.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(envelope));
            await relay.CreateInvitationAsync(envelope, ct).ConfigureAwait(false);

            config.CodeInvitations.Add(new CodeInvitationState
            {
                IsInviter = true,
                Code = code,
                InvitationId = invitationId,
                Direction = direction,
                ExpiresAt = envelope.ExpiresAt,
            });
            config.SaveNow();
            Notice = null;
            Succeed();
            return PairingCodes.Format(code);
        }
        catch (RelayException ex)
        {
            Fail(Describe(ex));
            return null;
        }
    }

    /// "cancel" while nobody has accepted, "reject" once someone has.
    public async Task CancelOutgoingAsync(CancellationToken ct)
    {
        if (Outgoing is not { } outgoing) return;
        await WithdrawAsync(outgoing, ct).ConfigureAwait(false);
    }

    private async Task WithdrawAsync(CodeInvitationState state, CancellationToken ct)
    {
        try
        {
            await relay.CancelInvitationAsync(state.InvitationId, ct).ConfigureAwait(false);
        }
        catch (RelayException ex) when (ex.Code is "expired" or "not_found")
        {
            // Already over on the relay.
        }
        catch (RelayException ex)
        {
            Fail(Describe(ex));
            return;
        }
        Remove(state);
        LastError = null;
    }

    /// The relay creates the pair on consume.
    public async Task<bool> ConfirmAsync(CodeInvitationState state, CancellationToken ct)
    {
        if (!state.IsInviter || state.Status != "needs-confirm" || state.PeerDeviceKeyId is null || state.PeerPublicKeyX is null || state.PeerPublicKeyY is null)
            return false;
        try
        {
            var pair = await relay.ConsumeInvitationAsync(state.InvitationId, ct).ConfigureAwait(false);
            var peerKey = new EcPublicKeyJwk { X = state.PeerPublicKeyX, Y = state.PeerPublicKeyY };
            if (!pairing.ActivateLocally(pair, state.Direction, Guid.NewGuid(), state.PeerName!, state.PeerWorld!, state.PeerDeviceKeyId, peerKey, state.PeerTriggerPhrase))
            {
                Fail("The relay returned pairing data that did not match the verified devices and roles.");
                return false;
            }
            Remove(state);
            Succeed();
            return true;
        }
        catch (RelayException ex)
        {
            if (ex.Code is "expired" or "not_found")
            {
                Remove(state);
                Notice = "That invitation expired before it was confirmed - create a new code.";
            }
            Fail(Describe(ex));
            return false;
        }
    }

    public Task RejectAsync(CodeInvitationState state, CancellationToken ct) => WithdrawAsync(state, ct);

    // ---- Invitee ----

    /// Nothing is sent or changed until AcceptPreviewAsync.
    public async Task<bool> LookupAsync(string typedCode, CancellationToken ct)
    {
        Preview = null;
        var code = PairingCodes.Normalize(typedCode, PairingCodes.PairingCodeChars);
        if (code is null) { Fail("That doesn't look like a pairing code - it's 16 letters and numbers, like K7QM-3XRP-9DTA-WV2E."); return false; }

        try
        {
            var invitationId = PairingCodes.LookupId(code);
            var invitation = await relay.FetchInvitationAsync(invitationId, ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (invitation.Kind != "code" || invitation.Type != "invitation" || invitation.SchemaVersion != 1 || invitation.ExpiresAt <= now)
            {
                Fail("That pairing code has expired - ask for a new one.");
                return false;
            }
            if (invitation.Status is "cancelled" or "rejected" or "consumed" or "accepted")
            {
                Fail("That pairing code has already been used or withdrawn - ask for a new one.");
                return false;
            }
            if (invitation.InviterDeviceKeyId != RelayCrypto.DeviceKeyId(invitation.InviterPublicKey) ||
                !RelayCrypto.VerifyRaw(invitation.InviterPublicKey, invitation.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(invitation)))
            {
                Fail("That invitation couldn't be verified.");
                return false;
            }
            if (invitation.InviterDeviceKeyId == identity.DeviceKeyId)
            {
                Fail("That's your own code - give it to the other person to enter.");
                return false;
            }
            var inviter = PairingCodes.DecryptCharacter(code, invitationId, inviter: true, invitation.EncryptedCharacter);
            if (inviter is null) { Fail("That invitation couldn't be read."); return false; }
            if (invitation.Role is not ("owner" or "sub")) { Fail("That invitation couldn't be read."); return false; }

            var ownDirection = invitation.Role == "owner" ? PairingDirection.SubSide : PairingDirection.OwnerSide;
            if (!DirectionAllowed(ownDirection))
            {
                Fail(invitation.Role == "owner"
                    ? "This invites you as a Sub, but your Role is Owner. Change Role in Settings to accept."
                    : "This invites you as an Owner, but your Role is Sub. Change Role in Settings to accept.");
                return false;
            }

            Preview = new CodePreview(code, invitationId, inviter, ownDirection, invitation.ExpiresAt, invitation.InviterDeviceKeyId, invitation.InviterPublicKey);
            LastError = null;
            return true;
        }
        catch (RelayException ex)
        {
            Fail(ex.Code is "not_found" or "expired" ? "No invitation matches that code - check it, or ask for a new one." : Describe(ex));
            return false;
        }
    }

    /// Only forgets the preview locally; the code stays usable until it expires or is withdrawn.
    public void DeclinePreview() => Preview = null;

    public async Task<bool> AcceptPreviewAsync(PairingCharacter self, CancellationToken ct)
    {
        if (Preview is not { } preview) return false;
        if (preview.OwnDirection == PairingDirection.SubSide && string.IsNullOrWhiteSpace(config.TriggerPhrase))
        {
            Fail("Set a trigger phrase in Settings before accepting this - without one, incoming commands can never apply.");
            return false;
        }

        var state = new CodeInvitationState
        {
            IsInviter = false,
            Code = preview.Code,
            InvitationId = preview.InvitationId,
            Direction = preview.OwnDirection,
            ExpiresAt = preview.ExpiresAt,
            PeerName = preview.Inviter.Name,
            PeerWorld = preview.Inviter.World,
            PeerTriggerPhrase = preview.Inviter.TriggerPhrase,
            PeerDeviceKeyId = preview.InviterDeviceKeyId,
            PeerPublicKeyX = preview.InviterPublicKey.X,
            PeerPublicKeyY = preview.InviterPublicKey.Y,
            AcceptedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        try
        {
            identity.EnsureIdentity();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var envelope = new AcceptanceEnvelope
            {
                InvitationId = preview.InvitationId,
                AccepterDeviceKeyId = identity.DeviceKeyId!,
                AccepterPublicKey = identity.GetPublicKeyJwk(),
                // Unused by code invitations, but part of the acceptance shape.
                ProofDigest = RelayCrypto.RandomProofDigestHex(),
                Role = preview.OwnDirection == PairingDirection.OwnerSide ? "owner" : "sub",
                EncryptedCharacter = PairingCodes.EncryptCharacter(preview.Code, preview.InvitationId, inviter: false, self with { TriggerPhrase = config.TriggerPhrase }),
                CreatedAt = now,
                ExpiresAt = Math.Min(now + RelayProtocolConstants.CodeInvitationExpirySeconds, preview.ExpiresAt),
            };
            envelope.Signature = RelayCrypto.SignRaw(identity.GetSigningKey(), EnvelopeCanonical.SerializeExcludingSignature(envelope));
            await relay.AcceptInvitationAsync(preview.InvitationId, envelope, ct).ConfigureAwait(false);

            config.CodeInvitations.Add(state);
            config.SaveNow();
            Preview = null;

            // Must run on the main thread (MoodlesIpc touches the object table).
            if (state.Direction == PairingDirection.SubSide && config.Permissions.Collar && config.Collar.IsConfigured)
                await Plugin.Framework.RunOnFrameworkThread(() => collar.ForceApply(state.PairingId)).ConfigureAwait(false);

            Notice = null;
            Succeed();
            return true;
        }
        catch (RelayException ex)
        {
            Fail(ex.Code == "expired" ? "That invitation is no longer available - ask for a new code." : Describe(ex));
            return false;
        }
    }

    // ---- Polling ----

    public void OnFrameworkUpdate(CancellationToken ct)
    {
        if (config.CodeInvitations.Count == 0) return;
        var now = DateTime.UtcNow;
        if (now < nextPollUtc) return;
        nextPollUtc = now + (now < fastUntilUtc ? FastPollInterval : SlowPollInterval);
        if (Interlocked.Exchange(ref polling, 1) == 1) return;
        Plugin.FireAndForget(PollAllAsync(ct));
    }

    public void PollSoon() => nextPollUtc = DateTime.MinValue;

    private async Task PollAllAsync(CancellationToken ct)
    {
        try
        {
            foreach (var state in config.CodeInvitations.ToList())
            {
                if (ct.IsCancellationRequested) return;
                if (state.IsInviter) await PollOutgoingAsync(state, ct).ConfigureAwait(false);
                else await PollAcceptedAsync(state, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref polling, 0);
        }
    }

    private async Task PollOutgoingAsync(CodeInvitationState state, CancellationToken ct)
    {
        InvitationEnvelope invitation;
        try
        {
            invitation = await relay.FetchInvitationAsync(state.InvitationId, ct).ConfigureAwait(false);
        }
        catch (RelayException ex) when (ex.Code is "expired" or "not_found")
        {
            Remove(state);
            Notice = "Your pairing code expired without being used.";
            return;
        }
        catch (RelayException)
        {
            return; // Transient - try again next poll.
        }

        switch (invitation.Status)
        {
            case "accepted" when state.Status == "waiting":
                if (invitation.Acceptance is not { } acceptance) return;
                var expectedRole = state.Direction == PairingDirection.OwnerSide ? "sub" : "owner";
                var character = PairingCodes.DecryptCharacter(state.Code, state.InvitationId, inviter: false, acceptance.EncryptedCharacter);
                if (acceptance.Role != expectedRole || character is null ||
                    acceptance.AccepterDeviceKeyId != RelayCrypto.DeviceKeyId(acceptance.AccepterPublicKey) ||
                    !RelayCrypto.VerifyRaw(acceptance.AccepterPublicKey, acceptance.Signature ?? "", EnvelopeCanonical.SerializeExcludingSignature(acceptance)))
                {
                    Plugin.Log.Warning("Code pairing: an acceptance didn't verify; ignoring it.");
                    return;
                }
                state.Status = "needs-confirm";
                state.PeerName = character.Name;
                state.PeerWorld = character.World;
                state.PeerTriggerPhrase = character.TriggerPhrase;
                state.PeerDeviceKeyId = acceptance.AccepterDeviceKeyId;
                state.PeerPublicKeyX = acceptance.AccepterPublicKey.X;
                state.PeerPublicKeyY = acceptance.AccepterPublicKey.Y;
                config.SaveNow();
                Plugin.ChatGui.Print($"[Oathbound] {character.Name}@{character.World} accepted your pairing code - open Settings to confirm.");
                break;
            case "cancelled" or "rejected" or "consumed":
                Remove(state);
                break;
        }
    }

    private async Task PollAcceptedAsync(CodeInvitationState state, CancellationToken ct)
    {
        if (state.PeerDeviceKeyId is null || state.PeerPublicKeyX is null || state.PeerPublicKeyY is null || identity.DeviceKeyId is null)
        {
            Remove(state);
            return;
        }

        // Activation first: the pair row is what "confirmed" means, and it outlives the invitation.
        var pairIdHash = RelayCrypto.ComputePairIdHash(identity.DeviceKeyId, state.PeerDeviceKeyId);
        try
        {
            var pair = await relay.FetchPairAsync(pairIdHash, ct).ConfigureAwait(false);
            // Only a live pair created after this acceptance counts, never an older epoch between the same devices.
            if (pair.RevokedAt is null && pair.CreatedAt >= state.AcceptedAt - 300)
            {
                var peerKey = new EcPublicKeyJwk { X = state.PeerPublicKeyX, Y = state.PeerPublicKeyY };
                if (pairing.ActivateLocally(pair, state.Direction, state.PairingId, state.PeerName!, state.PeerWorld!, state.PeerDeviceKeyId, peerKey, state.PeerTriggerPhrase))
                {
                    Remove(state);
                    Plugin.ChatGui.Print($"[Oathbound] {state.PeerName}@{state.PeerWorld} confirmed - you're paired.");
                    return;
                }
            }
        }
        catch (RelayException ex) when (ex.Code is "unauthorized" or "not_found")
        {
        }
        catch (RelayException)
        {
            return; // Transient.
        }

        try
        {
            var invitation = await relay.FetchInvitationAsync(state.InvitationId, ct).ConfigureAwait(false);
            if (invitation.Status is "rejected" or "cancelled")
                EndAccepted(state, $"{state.PeerName}@{state.PeerWorld} declined the pairing.");
        }
        catch (RelayException ex) when (ex.Code is "expired" or "not_found")
        {
            EndAccepted(state, $"The pairing with {state.PeerName}@{state.PeerWorld} was never confirmed and has expired.");
        }
        catch (RelayException)
        {
        }
    }

    /// Undo the collar applied at accept time, if it was for this pairing.
    private void EndAccepted(CodeInvitationState state, string notice)
    {
        Remove(state);
        Notice = notice;
        if (config.CollarOwningPairingId == state.PairingId)
            Plugin.Framework.RunOnFrameworkThread(collar.ReleaseOnUnpair);
    }

    private void Remove(CodeInvitationState state)
    {
        config.CodeInvitations.RemoveAll(c => c.Id == state.Id);
        config.SaveNow();
    }

    private static string Describe(RelayException ex) => ex.Code switch
    {
        "network" => "Could not reach the relay - check your connection and try again.",
        "rate_limited" => "Too many attempts - try again shortly.",
        "expired" => "That invitation is no longer valid.",
        "unauthorized" => "The relay rejected this request.",
        "service_unavailable" => "The relay is temporarily unavailable - try again shortly.",
        "invalid_request" => "The relay didn't accept this request - both of you may need the latest plugin version.",
        _ => "The relay request failed.",
    };
}
