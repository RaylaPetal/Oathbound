using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// Builds the text of commands and notices sent to a paired peer over the relay. It has no send API; PeerMessenger
/// sends. Notice words stay the ones older builds used in tells, so their parsers are unchanged.
public sealed class CommandComposer
{
    /// The command exactly as the Sub's dispatch reads it; no trigger phrase or channel any more.
    public string Compose(string command) => command.Trim();

    /// Carries the Owner's current location; the Sub picks its own route.
    public string ComposeTeleport(TeleportTarget target) => $"teleport {target.ToPayload()}";

    public string ComposeLeashTravel(TeleportTarget target) => $"{ControlWords.Leash} {LeashTravelWord} {target.ToPayload()}";

    public const string LeashTravelWord = "travel";

    /// `gesture stop` ends a held animation on the Sub.
    public const string StopGestureWord = "stop";

    /// Sent only by Owners from before mailbox receipts; this version answers it through the mailbox.
    public const string CatalogRequestKeyword = "collarcatalogreq";

    /// Tells the Owner the Sub hasn't opted in, without any catalog content ever existing.

    /// Carries only the keyword and one reason word.
    public string ComposeLeashOffNotice(LeashEnd reason) => $"{LeashOffNoticeKeyword} {LeashOffWord} {reason.ToNoticeWord()}";

    public const string LeashOffNoticeKeyword = "collarleash";
    public const string LeashOffWord = "off";
    /// `collarleash on`: the Sub's presence rule leashed them, so the Owner's client follows up with leash travel.
    public const string LeashOnWord = "on";

    public string ComposeLeashOnNotice() => $"{LeashOffNoticeKeyword} {LeashOnWord}";

    public const string LeashPausedWord = "paused";
    public const string TeleportFailedWord = "tpfail";

    /// Carries only the keyword, the word and one code.
    public string ComposeTeleportRefusalNotice(string word, TeleportRefusal code) => $"{LeashOffNoticeKeyword} {word} {code.ToNoticeWord()}";

    /// The Sub struggled free of (`free`) or used the key on (`key`) a restraint this Owner locked, named as their command named it.
    public string ComposeStruggleNotice(string word, string restraint) => $"{StruggleNoticeKeyword} {word} {restraint}".TrimEnd();

    public const string StruggleNoticeKeyword = "collarstruggle";
    public const string StruggleFreeWord = "free";
    public const string StruggleKeyWord = "key";

    /// The Sub used their safeword: only `panic` and when.
    public string ComposeSafewordPing(long at) => $"{SafewordNoticeKeyword} {SafewordPingWord} {at}";

    public const string SafewordNoticeKeyword = "collarsafeword";
    public const string SafewordPingWord = "panic";

    /// The relay's safeword report still requires a mood; it's always this one, and it's never shown.
    public const string SafewordRelayMood = "stop";

    /// `panic`, or the `stop` / `checkin` older Sub builds sent; all of them only mean the safeword was used.
    public static bool IsSafewordWord(string? word) =>
        word?.Trim().ToLowerInvariant() is SafewordPingWord or "stop" or "checkin";

    public string ComposeGame(string body) => $"{Games.GamesService.Keyword} {body}";

    /// The Owner's "Grant leave" press for an "Ask before logging off" oath.
    public string ComposeGrantLeave() => GrantLeaveKeyword;

    public const string GrantLeaveKeyword = "collargrant";

    /// A presence rule's Owner-written message, shown on the Owner's client.
    public string ComposePresenceMessage(string text) => $"{PresenceMessageKeyword} {text.Trim()}";

    public const string PresenceMessageKeyword = "collarsay";

    /// The Sub's apply receipts: `collarreceipt <seq>:<a|r>[:<code>] ...`, one entry per answered command frame.
    public const string ReceiptNoticeKeyword = "collarreceipt";
    public const int ReceiptMaxEntries = 50;

    public string ComposeReceipt(IEnumerable<(long Seq, RefusalCode? Refused)> entries) =>
        ReceiptNoticeKeyword + " " + string.Join(' ', entries.Select(e => e.Refused is { } code ? $"{e.Seq}:r:{code.ToWire()}" : $"{e.Seq}:a"));

    /// The Sub's own Accept / Decline of a rulebook version or an oath offer.
    public const string DecisionNoticeKeyword = "collardecision";
    public const string DecisionRulebookWord = "rulebook";
    public const string DecisionOathWord = "oath";
    public const string DecisionAcceptedWord = "accepted";
    public const string DecisionDeclinedWord = "declined";
    public const int DecisionReasonMaxChars = 200;

    public string ComposeRulebookDecision(bool accepted, int version, string? reason)
    {
        var text = $"{DecisionNoticeKeyword} {DecisionRulebookWord} {(accepted ? DecisionAcceptedWord : DecisionDeclinedWord)} {version}";
        return !accepted && CleanDecisionReason(reason) is { } clean ? $"{text} {clean}" : text;
    }

    public string ComposeOathDecision(bool accepted, string oathId) =>
        $"{DecisionNoticeKeyword} {DecisionOathWord} {(accepted ? DecisionAcceptedWord : DecisionDeclinedWord)} {oathId}";

    /// Whitespace collapsed and cut to the limit; null when nothing is left.
    public static string? CleanDecisionReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return null;
        var collapsed = string.Join(' ', reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= DecisionReasonMaxChars ? collapsed : collapsed[..DecisionReasonMaxChars].TrimEnd();
    }

    /// The words an older build's tells start with, besides the trigger phrase; how an outdated partner is recognized.
    public static readonly string[] LegacyTellKeywords =
    [
        "collarinvite", "collarpairack", "collarunpair", "collarcatalogreq", "collarcatalogdenied", LeashOffNoticeKeyword,
        "collarrulebook", StruggleNoticeKeyword, SafewordNoticeKeyword, Games.GamesService.Keyword,
    ];
}
