using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// This class only ever builds text and returns it - it has no dependency on any chat-send API at all, by
/// construction, so there is no code path here that could ever transmit anything. Sending (either the
/// Owner's own paste into the game's chat box, or the explicit one-click Send button - see ChatSender) is
/// always a separate, deliberate step the UI takes with the string this class hands back, never something
/// this class does on its own.
public sealed class ChatComposer
{
    private readonly PluginConfig config;

    public ChatComposer(PluginConfig config)
    {
        this.config = config;
    }

    /// Builds `/tell <PeerName>@<PeerWorld> <trigger> <command>` - or just the trigger+command, with no
    /// `/tell` target, if a handshake hasn't captured a peer identity yet. `command` is raw text: either a
    /// plain alias, or one of ChatCommandListener's reserved-keyword override commands - this class has no
    /// idea which, since both are just text appended after the trigger phrase.
    public string Compose(string command) => Wrap(command);

    /// Every chat message one Send of `command` becomes: just the composed command when it fits, otherwise
    /// - for a `customtrigger cast` bundle only - one message per action (see
    /// CustomTriggerCommand.SplitCastCommand). Anything else that doesn't fit comes back as its single
    /// too-long message, for the caller's Fits check to reject.
    public IReadOnlyList<string> ComposeAll(string command)
    {
        var whole = Compose(command);
        if (CommandSelector.Fits(whole) || CustomTriggerCommand.SplitCastCommand(command) is not { Count: > 1 } parts)
            return [whole];
        return parts.Select(Compose).ToList();
    }

    public static bool AllFit(IReadOnlyList<string> messages) => messages.All(CommandSelector.Fits);

    /// collar/teleport: the Owner's `teleport` reserved-word command, carrying where the Owner is at send
    /// time (world, territory, instance, ward, position) - read by the caller from its own client state,
    /// since this class stays dependency-free by construction. The Sub picks its own route from this.
    public string ComposeTeleport(TeleportTarget target) => Wrap($"teleport {target.ToPayload()}");

    /// collar/pairing's relay-assisted handshake: a short lifecycle tell carrying only the invitation's
    /// capability id - everything else (role, trigger phrase, expiry) lives in the signed invitation itself,
    /// fetched from the relay once this tell's verified sender is captured (see Relay/PairingService.cs).
    /// `targetTellAddress` is typed by the user (e.g. "Name Surname@World") since there's no captured peer
    /// identity yet to address it to automatically.
    public string ComposeRelayInvitation(string targetTellAddress, string invitationId) =>
        $"/tell {targetTellAddress.Trim()} collarinvite {invitationId}";

    /// collar/pairing "Invite target is validated before sending": a typed target that doesn't match
    /// "Name Surname@World" produces a `/tell` the game itself silently rejects with no plugin-visible
    /// feedback - this catches the structural cases (no `@`, empty name/world) before that ever happens.
    /// Cannot and does not check whether the character actually exists; that information is not available
    /// to the plugin.
    public static bool TryValidateTellTarget(string target, out string error)
    {
        var trimmed = target.Trim();
        var atIndex = trimmed.IndexOf('@');
        if (atIndex < 0 || trimmed.IndexOf('@', atIndex + 1) >= 0)
        {
            error = "Target must be in the form \"Name Surname@World\" - exactly one '@' was expected.";
            return false;
        }

        var name = trimmed[..atIndex].Trim();
        var world = trimmed[(atIndex + 1)..].Trim();
        if (name.Length == 0)
        {
            error = "A character name is required before '@'.";
            return false;
        }
        if (world.Length == 0)
        {
            error = "A world name is required after '@'.";
            return false;
        }

        error = "";
        return true;
    }

    /// collar/pairing's acknowledgement tell, sent automatically as part of accepting a pending relay
    /// invitation. The target is already known (the verified sender of the invitation being accepted), so
    /// this composes a full `/tell` directly rather than going through Wrap's already-paired-peer
    /// addressing. `proofDigest` is what the inviter cross-checks against the signed acceptance it fetches
    /// from the relay before activating - a relay claim without this exact tell can never activate pairing.
    public string ComposePairingAck(string name, string world, string invitationId, string proofDigest) =>
        $"/tell {name}@{world} collarpairack {invitationId} {proofDigest}";

    /// collar/catalog-sync: the Owner's lifecycle tell telling the paired Sub a signed catalog-request now
    /// exists on the relay - carries only the request's capability id, same "short lifecycle tell, fetch
    /// the signed content separately" shape as the pairing invitation tell.
    public string ComposeCatalogRequestNotice(string name, string world, string requestId) =>
        $"/tell {name}@{world} collarcatalogreq {requestId}";

    /// collar/catalog-sync "Sub has not opted in": sent back to the Owner instead of building/uploading
    /// anything, so the Owner learns a permission status without any catalog content ever existing.
    public string ComposeCatalogPermissionDenied(string name, string world, string requestId) =>
        $"/tell {name}@{world} collarcatalogdenied {requestId}";

    /// collar/pairing "Panic notifies the peer, best-effort": the automatic notification tell sent from
    /// PanicHandler as a direct consequence of the panic action itself. Like ComposePairingAck, the target
    /// is already known (the peer identity cached at the moment panic ran), so this composes a full
    /// `/tell` directly rather than going through Wrap. Carries no code - ending a trust relationship
    /// doesn't need one, only establishing a new one does (see design.md).
    /// `direction` is this device's role in the specific pairing that just ended - never the device's
    /// overall Role, since a Switch's Role doesn't say which direction any one pairing was.
    public string ComposeUnpairNotice(string name, string world, PairingDirection direction)
    {
        var roleToken = direction == PairingDirection.OwnerSide ? "owner" : "sub";
        return $"/tell {name}@{world} collarunpair {roleToken}";
    }

    /// collar/chat-transport: uses the peer's trigger phrase captured during pairing (see
    /// PairingState.PeerTriggerPhrase) when known, so an already-paired relationship can never silently
    /// diverge again - falls back to this side's own configured TriggerPhrase only when no peer phrase has
    /// been captured (no pairing yet, or a peer whose handshake didn't declare one).
    ///
    /// collar/chat-transport "Composing and sending require active pairing, not just a remembered peer":
    /// addresses a channel command only while `IsPaired` is true, not merely whenever PeerName/PeerWorld
    /// happen to be non-empty - PairingService.EndFromVerifiedPeerNotice deliberately leaves those cached
    /// after a verified peer notice clears Paired, so checking presence alone would let this side keep
    /// composing (and, via CollarWindow's canSend, keep sending) to a peer whose own side already ended it.
    ///
    /// collar/chat-transport "Trigger-phrase command delivery over a selectable channel": which channel
    /// prefix to use comes from config.OutgoingChannel - Tell keeps its existing addressed form, the other
    /// four need no address, just their own channel command (see ChatChannelPrefix).
    private string Wrap(string body)
    {
        // collar/multi-pairing "Active pairing selection drives outgoing commands": addressed to the
        // active pairing's peer, or no target at all if none is selected.
        var pairing = config.GetActivePairing();
        var peerTriggerPhrase = pairing?.PeerTriggerPhrase;
        var trigger = (!string.IsNullOrWhiteSpace(peerTriggerPhrase) ? peerTriggerPhrase : config.TriggerPhrase).Trim();
        var full = $"{trigger} {body}".Trim();

        if (pairing is not { IsPaired: true })
            return full;

        if (config.OutgoingChannel == ChatChannel.Tell)
            return $"/tell {pairing.PeerName}@{pairing.PeerWorld} {full}";

        return $"{ChatChannelPrefix(config)} {full}";
    }

    /// collar/chat-transport: the non-tell channel commands, shared with ChatSender's allow-list so the
    /// two can never silently drift apart on what a valid composed prefix looks like.
    internal static string ChatChannelPrefix(PluginConfig config) => config.OutgoingChannel switch
    {
        ChatChannel.Party => "/p",
        ChatChannel.Alliance => "/a",
        ChatChannel.Linkshell => $"/l{Math.Clamp(config.LinkshellNumber, 1, 8)}",
        ChatChannel.CrossWorldLinkshell => $"/cwl{Math.Clamp(config.CrossWorldLinkshellNumber, 1, 8)}",
        _ => throw new ArgumentOutOfRangeException(nameof(config.OutgoingChannel), config.OutgoingChannel, "Tell is addressed separately in Wrap."),
    };
}
