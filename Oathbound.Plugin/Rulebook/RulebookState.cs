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
    /// The Sub's client has collected the rulebook up to this version (the relay's delivery receipt).
    public int PickedUpVersion { get; set; }
    public string? PublishError { get; set; }

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

    /// Time rule id -> the Sub's local date ("yyyy-MM-dd") it last fired, so a reload doesn't fire it again.
    public Dictionary<string, string> TimeRuleLastFired { get; set; } = new();
    /// Shop item id -> when it was last bought.
    public Dictionary<string, long> ShopLastBought { get; set; } = new();
    /// The outfit this pairing's Owner last applied; what a Keep it on oath holds the Sub to.
    public OwnerOutfitSnapshot? OwnerOutfit { get; set; }
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
    public List<string> DisabledRuleIds { get; set; } = new();
    public List<ReportedOath> Oaths { get; set; } = new();
    public List<RulebookEvent> Events { get; set; } = new();
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
