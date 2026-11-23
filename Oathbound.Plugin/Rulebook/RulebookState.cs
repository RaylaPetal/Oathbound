using System;
using System.Collections.Generic;

namespace Oathbound.Plugin.Rulebook;

/// Everything rulebook-related one pairing keeps, on whichever side it is. Reset when the pairing ends.
[Serializable]
public sealed class RulebookPairingState
{
    // ---- Owner side ----

    public RulebookDocument Draft { get; set; } = new();
    public int PublishedVersion { get; set; }
    public long PublishedAtUnixSeconds { get; set; }
    /// Digest of the draft as last published, so the editor can show unpublished changes.
    public string? PublishedDigest { get; set; }
    /// Exactly what was last sent, so a copy the relay lost can be sent again without picking up later edits.
    public RulebookDocument? PublishedDocument { get; set; }
    /// The Sub's client has collected the rulebook up to this version (the relay's pickup record).
    public int PickedUpVersion { get; set; }
    /// The last send's error, manual or automatic; cleared by the next successful send.
    public string? PublishError { get; set; }

    /// Owner: sending rulebooks (and the Sub's receipts). Sub: receiving them.
    public Oathbound.Plugin.Config.MailboxDeliveryState RulebookDelivery { get; set; } = new();
    /// Sub: sending reports (and the Owner's receipts). Owner: receiving them.
    public Oathbound.Plugin.Config.MailboxDeliveryState ReportDelivery { get; set; } = new();

    /// The Owner's report-channel receive key. The private half is DPAPI-protected.
    public string? ReportKeyId { get; set; }
    public string? ReportKeyX { get; set; }
    public string? ReportKeyY { get; set; }
    public byte[]? ReportKeyPrivate { get; set; }
    public bool? ReportKeyPrivateProtected { get; set; }

    public RulebookReport? LastReport { get; set; }
    public int LastReportSequence { get; set; }
    public long LastReportReceivedUnixSeconds { get; set; }

    /// The relay has a rulebook receive key from the Sub, so the Sub's plugin supports rulebooks.
    public bool SubSupportsRulebook { get; set; }

    /// The Sub's latest version decision notice; shown until a report generated after it arrives.
    public RulebookDecisionNotice? LastDecision { get; set; }
    /// Oath id -> the Sub's latest decision notice for that offer, under the same rule.
    public Dictionary<string, OathDecisionNotice> OathDecisions { get; set; } = new();

    // Both times are this client's own receive times: a report's GeneratedAt is the Sub's clock, which can be minutes off.
    public RulebookDecisionNotice? FreshDecision() =>
        LastDecision is { } d && (LastReport is null || LastReportReceivedUnixSeconds <= d.At) ? d : null;

    public OathStatus? FreshOathDecision(string oathId) =>
        OathDecisions.TryGetValue(oathId, out var d) && (LastReport is null || LastReportReceivedUnixSeconds <= d.At)
            ? d.Accepted ? OathStatus.Open : OathStatus.Declined
            : null;

    // ---- Sub side ----

    /// The Sub's rulebook-channel receive key. The private half is DPAPI-protected.
    public string? ReceiveKeyId { get; set; }
    public string? ReceiveKeyX { get; set; }
    public string? ReceiveKeyY { get; set; }
    public byte[]? ReceiveKeyPrivate { get; set; }
    public bool? ReceiveKeyPrivateProtected { get; set; }

    /// Highest rulebook version received, accepted or not; older ones are replays.
    public int LastReceivedVersion { get; set; }

    public RulebookDocument? Pending { get; set; }
    public long PendingReceivedUnixSeconds { get; set; }
    public RulebookDocument? Accepted { get; set; }
    public long AcceptedUnixSeconds { get; set; }
    public int? LastDeclinedVersion { get; set; }
    /// The pending version the Sub last opened the Rulebook module on, for the nav tile's attention badge.
    public int? SeenPendingVersion { get; set; }
    /// "id:offeredAt" of each oath offer the Sub has seen, so an offer made again with new terms counts as new.
    public HashSet<string> SeenOfferIds { get; set; } = new();
    /// The Owner's ResetCount as of the last wipe.
    public int AppliedResetCount { get; set; }

    /// No longer set: the Sub can't switch rules off. Kept so older configs load and reports keep their shape.
    public HashSet<string> DisabledRuleIds { get; set; } = new();
    public Dictionary<string, OathState> Oaths { get; set; } = new();
    public int LedgerScore { get; set; }
    /// Thresholds currently met; a threshold fires again only after leaving this set.
    public HashSet<string> ThresholdsMet { get; set; } = new();
    /// "Once" cards drawn since the last accepted version.
    public HashSet<string> RemovedOnceCards { get; set; } = new();

    public List<RulebookEvent> Activity { get; set; } = new();
    public int NextReportSequence { get; set; }
    public string? LastUploadedReportDigest { get; set; }
    public long LastReportUploadUnixSeconds { get; set; }
    /// Why the last report couldn't be sent; cleared by the next upload.
    public string? ReportUploadError { get; set; }

    /// Time rule id -> the Sub's local date ("yyyy-MM-dd") it last fired, so a reload doesn't fire it again.
    public Dictionary<string, string> TimeRuleLastFired { get; set; } = new();
    /// Shop item id -> when it was last bought.
    public Dictionary<string, long> ShopLastBought { get; set; } = new();
    /// The outfit this pairing's Owner last applied; what a Keep it on oath holds the Sub to.
    public OwnerOutfitSnapshot? OwnerOutfit { get; set; }
    public LadderState Ladder { get; set; } = new();
}

/// The Sub's place on the ladder. The stage survives panic and the permission going off; its restraint doesn't.
[Serializable]
public sealed class LadderState
{
    public int Stage { get; set; }
    /// The worn restraint the current stage put on; null while nothing from the ladder is worn.
    public string? RuntimeId { get; set; }
    /// The stage's restraint was taken off outside the ladder (the Owner's unlock, revert all), so it stays off until
    /// the ladder next moves.
    public bool Released { get; set; }
    public int FailedStruggles { get; set; }
    /// Struggling waits per ladder, not per stage, so a move never hands out a fresh try.
    public DateTime? StruggleNextTryUtc { get; set; }
    /// Decay: seconds counted toward the next loosening, and when they were last counted.
    public long DecaySeconds { get; set; }
    public long DecayCheckedUnixSeconds { get; set; }
}

[Serializable]
public sealed class OwnerOutfitSnapshot
{
    public Guid DesignId { get; set; }
    public string DesignName { get; set; } = "";
    /// Equip slot (as its number) -> item id, read back from Glamourer once the design settled.
    public Dictionary<int, ulong> Slots { get; set; } = new();
}

public enum OathStatus
{
    Unknown = -1,
    Offered,
    Declined,
    Expired,
    Withdrawn,
    Open,
    Kept,
    Broken,
    Voided,
}

[Serializable]
public sealed class OathState
{
    public OathStatus Status { get; set; }
    public long OfferedUnixSeconds { get; set; }
    public long ChangedUnixSeconds { get; set; }
    /// The terms as last offered, so a change by the Owner can be told apart from an unchanged resend.
    public string? OfferedTermsJson { get; set; }
    /// The terms this oath is judged by; a newer accepted version replaces them while it's open.
    public Oath? Terms { get; set; }
    public long? ScopeEndsUnixSeconds { get; set; }
    /// NextDuty scope: the duty it watches has started.
    public bool DutyEntered { get; set; }
    /// DutyTimeLimit: when the watched duty started.
    public long? DutyStartedUnixSeconds { get; set; }
    /// Curfew: seen outside the window since accepting, so accepting mid-curfew isn't an instant break.
    public bool CurfewArmed { get; set; }
    /// Rituals: the current period's start and how many times it's been done in it.
    public long PeriodStartUnixSeconds { get; set; }
    public int PeriodCount { get; set; }
    /// Rituals: periods that closed short; any at the end of the scope breaks the oath.
    public int MissedPeriods { get; set; }
    /// SayGoodnight: when the last matching tell went out.
    public long LastMatchUnixSeconds { get; set; }
    public int StrikesUsed { get; set; }
    /// Repeats done in full in a row (rituals) or runs kept in a row (other recurring oaths). Survives renewal.
    public int Streak { get; set; }
    /// The Sub chose to let this run be the last one.
    public bool StopRenewing { get; set; }
    /// KeepItOn / StayAtSide: when being out of line turns into a violation; null while in line.
    public long? GraceEndsUnixSeconds { get; set; }
    /// AskBeforeLogoff: logging off before this is allowed.
    public long LeaveGrantedUntilUnixSeconds { get; set; }

    public bool IsResolved => Status is OathStatus.Declined or OathStatus.Expired or OathStatus.Withdrawn
        or OathStatus.Kept or OathStatus.Broken or OathStatus.Voided;
}

/// A new value makes reports unreadable for Owners from before the tolerant enum reader, so prefer an existing kind
/// with descriptive text. Unknown is how a newer Sub's value reads here.
public enum RulebookEventKind
{
    Unknown = -1,
    VersionReceived,
    VersionAccepted,
    VersionDeclined,
    OathAccepted,
    OathDeclined,
    OathExpired,
    OathWithdrawn,
    OathKept,
    OathBroken,
    OathVoided,
    CardDrawn,
    DeckEmpty,
    LedgerChanged,
    ThresholdCrossed,
    ConsequenceRan,
    ConsequenceSkipped,
    ConsequenceDropped,
    RuleSwitchedOff,
    RuleSwitchedOn,
    Suspended,
    Resumed,
    RitualDone,
}

[Serializable]
public sealed class RulebookEvent
{
    public long At { get; set; }
    public RulebookEventKind Kind { get; set; }
    public string? RuleId { get; set; }
    /// Readable, written on the Sub's side; the Owner shows it as is.
    public string Text { get; set; } = "";
}

/// Sub -> Owner, encrypted. The Owner's only view of the Sub's real rulebook state.
[Serializable]
public sealed class RulebookReport
{
    public int SchemaVersion { get; set; } = 1;
    public long GeneratedAt { get; set; }
    public int AcceptedVersion { get; set; }
    public int? PendingVersion { get; set; }
    public int? DeclinedVersion { get; set; }
    public bool PermissionOn { get; set; }
    public bool Suspended { get; set; }
    public int LedgerScore { get; set; }
    /// Null without a ladder. An older Owner ignores these.
    public int? LadderStage { get; set; }
    public string? LadderStageName { get; set; }
    public List<string> DisabledRuleIds { get; set; } = new();
    public List<ReportedOath> Oaths { get; set; } = new();
    public List<RulebookEvent> Events { get; set; } = new();
}

[Serializable]
public sealed class RulebookDecisionNotice
{
    public int Version { get; set; }
    public bool Accepted { get; set; }
    public string? Reason { get; set; }
    /// When this client received it, unix seconds.
    public long At { get; set; }
}

[Serializable]
public sealed class OathDecisionNotice
{
    public bool Accepted { get; set; }
    public long At { get; set; }
}

[Serializable]
public sealed class ReportedOath
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public OathStatus Status { get; set; }
    public long ChangedAt { get; set; }
    public long? ScopeEndsAt { get; set; }
}
