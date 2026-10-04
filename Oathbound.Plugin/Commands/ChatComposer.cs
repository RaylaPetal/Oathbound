using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// Only builds text; it has no send API, so nothing here can ever transmit. Sending is a separate deliberate step.
public sealed class ChatComposer
{
    private readonly PluginConfig config;

    public ChatComposer(PluginConfig config)
    {
        this.config = config;
    }

    /// `/tell <Peer>@<World> <trigger> <command>`, or just trigger + command when no peer is known.
    public string Compose(string command) => Wrap(command);

    /// Oversized `customtrigger cast` bundles split into one message per action; anything else oversized comes back as-is for the caller's Fits check to reject.
    public IReadOnlyList<string> ComposeAll(string command)
    {
        var whole = Compose(command);
        if (CommandSelector.Fits(whole) || CustomTriggerCommand.SplitCastCommand(command) is not { Count: > 1 } parts)
            return [whole];
        return parts.Select(Compose).ToList();
    }

    public static bool AllFit(IReadOnlyList<string> messages) => messages.All(CommandSelector.Fits);

    /// Carries the Owner's current location; the Sub picks its own route.
    public string ComposeTeleport(TeleportTarget target) => Wrap($"teleport {target.ToPayload()}");

    /// Always a /tell to that pairing: a channel can't address one Sub and would broadcast the Owner's location.
    public string ComposeLeashTravel(PairingState pairing, TeleportTarget target)
    {
        var trigger = (!string.IsNullOrWhiteSpace(pairing.PeerTriggerPhrase) ? pairing.PeerTriggerPhrase : config.TriggerPhrase).Trim();
        return $"/tell {pairing.PeerName}@{pairing.PeerWorld} {trigger} {ControlWords.Leash} {LeashTravelWord} {target.ToPayload()}";
    }

    public const string LeashTravelWord = "travel";

    /// `gesture stop` ends a held animation on the Sub.
    public const string StopGestureWord = "stop";

    /// Carries only the invitation's id; everything else lives in the signed invitation on the relay.
    public string ComposeRelayInvitation(string targetTellAddress, string invitationId) =>
        $"/tell {targetTellAddress.Trim()} collarinvite {invitationId}";

    /// Catches structural errors the game would otherwise reject silently. Can't check that the character exists.
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

    /// `proofDigest` is what the inviter checks against the relay's signed acceptance before activating.
    public string ComposePairingAck(string name, string world, string invitationId, string proofDigest) =>
        $"/tell {name}@{world} collarpairack {invitationId} {proofDigest}";

    /// Carries only the request's id; the signed content is fetched from the relay.
    public string ComposeCatalogRequestNotice(string name, string world, string requestId) =>
        $"/tell {name}@{world} collarcatalogreq {requestId}";

    /// Tells the Owner the Sub hasn't opted in, without any catalog content ever existing.
    public string ComposeCatalogPermissionDenied(string name, string world, string requestId) =>
        $"/tell {name}@{world} collarcatalogdenied {requestId}";

    /// Carries only the keyword and one reason word.
    public string ComposeLeashOffNotice(string name, string world, LeashEnd reason) =>
        $"/tell {name}@{world} {LeashOffNoticeKeyword} {LeashOffWord} {reason.ToNoticeWord()}";

    public const string LeashOffNoticeKeyword = "collarleash";

    /// Carries nothing; asks the Sub's plugin to check the rulebook mailbox now.
    public string ComposeRulebookNudge(string name, string world) => $"/tell {name}@{world} {RulebookNudgeKeyword}";

    public const string RulebookNudgeKeyword = "collarrulebook";
    public const string LeashOffWord = "off";
    /// `collarleash on`: the Sub's presence rule leashed them, so the Owner's client follows up with leash travel.
    public const string LeashOnWord = "on";

    public string ComposeLeashOnNotice(string name, string world) =>
        $"/tell {name}@{world} {LeashOffNoticeKeyword} {LeashOnWord}";

    /// A presence rule's message, only ever as a /tell to that Owner.
    public string ComposePresenceTell(string name, string world, string text) =>
        $"/tell {name}@{world} {text.Trim()}";

    /// `direction` is this device's side in the pairing that ended, not its Role.
    public string ComposeUnpairNotice(string name, string world, PairingDirection direction)
    {
        var roleToken = direction == PairingDirection.OwnerSide ? "owner" : "sub";
        return $"/tell {name}@{world} collarunpair {roleToken}";
    }

    /// Uses the peer's captured trigger phrase when known, else our own. Addresses a command only while `IsPaired`:
    /// peer name/world stay cached after a verified unpair notice.
    private string Wrap(string body)
    {
        // Addressed to the active pairing's peer, or no target if none is selected.
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

    /// Shared with ChatSender's allow-list so the two never drift.
    internal static string ChatChannelPrefix(PluginConfig config) => config.OutgoingChannel switch
    {
        ChatChannel.Party => "/p",
        ChatChannel.Alliance => "/a",
        ChatChannel.Linkshell => $"/l{Math.Clamp(config.LinkshellNumber, 1, 8)}",
        ChatChannel.CrossWorldLinkshell => $"/cwl{Math.Clamp(config.CrossWorldLinkshellNumber, 1, 8)}",
        _ => throw new ArgumentOutOfRangeException(nameof(config.OutgoingChannel), config.OutgoingChannel, "Tell is addressed separately in Wrap."),
    };
}
