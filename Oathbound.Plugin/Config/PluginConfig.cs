using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Text.Json.Serialization;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Keys;
using Glamourer.Api.Enums;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.Config;

public enum PluginRole
{
    Owner,
    Sub,

    /// Holds pairings in both directions at once.
    Switch,
}

/// Which side of a specific pairing this device is on, independent of its Role.
public enum PairingDirection
{
    OwnerSide,

    SubSide,
}

/// Linkshell/CrossWorldLinkshell also need a slot number (LinkshellNumber/CrossWorldLinkshellNumber) to send on.
public enum ChatChannel
{
    Tell,
    Party,
    Alliance,
    Linkshell,
    CrossWorldLinkshell,
}

/// Identity is bound by matching PeerName/PeerWorld against the verified sender of the pairing tells, cross-checked
/// against the relay's signed envelopes. `Paired` is only set after a fully verified handshake, never by relay state alone.
[Serializable]
public class PairingState
{
    /// Stable per-pairing id; peer name/world can change, this can't.
    public Guid Id { get; set; } = Guid.NewGuid();

    public PairingDirection Direction { get; set; }

    /// SHA-256 of both devices' sorted key ids (matches the Worker's computePairIdHash); cached.
    public string? PairIdHash { get; set; }

    /// Server-assigned on every (re)activation; signed envelopes must carry it so a stale pairing can't affect a newer one.
    public int PairEpoch { get; set; }

    /// Needed to verify that revocation/catalog envelopes came from the real peer.
    public string? PeerDeviceKeyId { get; set; }

    /// Captured at activation so later envelopes can be verified without the relay.
    public string? PeerPublicKeyX { get; set; }
    public string? PeerPublicKeyY { get; set; }

    public string? PeerName { get; set; }
    public string? PeerWorld { get; set; }
    public bool Paired { get; set; }

    /// Null when the peer didn't declare one; ChatComposer falls back to our own TriggerPhrase.
    public string? PeerTriggerPhrase { get; set; }

    /// Monotonic across every epoch; the next outgoing revocation uses this + 1.
    public int OutgoingRevocationSequence { get; set; }

    /// Incoming revocations at or below this are replays and are ignored.
    public int IncomingRevocationSequence { get; set; }

    public long LastRevocationCheckUnixSeconds { get; set; }

    public long LastAcceptedCatalogSyncUnixSeconds { get; set; }

    /// Snapshots at or below this are stale or replayed and are ignored.
    public int LastImportedSnapshotId { get; set; }

    /// The relay requires strictly increasing snapshot ids per pair epoch.
    public int NextOutgoingSnapshotId { get; set; }

    /// Rotated on every consume, so it only ever decrypts one snapshot. The private half is DPAPI-protected and never leaves this device.
    public string? MailboxReceiveKeyId { get; set; }
    public string? MailboxReceivePublicKeyX { get; set; }
    public string? MailboxReceivePublicKeyY { get; set; }
    public byte[]? MailboxReceivePrivateKey { get; set; }
    public bool? MailboxReceivePrivateKeyProtected { get; set; }

    /// Owner-side display state for the Sync tab. SubLastPublishedUnixSeconds is null when the Sub never published.
    public long LastMailboxCheckOkUnixSeconds { get; set; }
    public string? LastMailboxCheckError { get; set; }
    public long? SubLastPublishedUnixSeconds { get; set; }

    /// Sub-side change detector, recorded only after a successful upload so a failure leaves the change pending.
    public string? LastPublishedCatalogDigest { get; set; }
    public long LastPublishedCatalogUnixSeconds { get; set; }

    /// Compared against the relay's delivery receipt so a push that never reached the Owner is published again.
    public int LastPublishedMailboxSnapshotId { get; set; }

    /// Reset when the pairing ends.
    public Rulebook.RulebookPairingState Rulebook { get; set; } = new();

    /// UI only; never controls pairing.
    public string? LastRevocationDeliveryStatus { get; set; }
    public long LastRevocationDeliveryUpdatedAt { get; set; }

    public bool IsPaired => Paired && !string.IsNullOrWhiteSpace(PeerName) && !string.IsNullOrWhiteSpace(PeerWorld);
}

/// The private key is DPAPI-protected on Windows; under Wine that gives no real confidentiality, and Settings must say so.
[Serializable]
public class DeviceIdentityState
{
    public string? PublicKeyX { get; set; }
    public string? PublicKeyY { get; set; }

    /// DPAPI-protected scalar, or the plain scalar when DPAPI was unavailable. Never exported, sent or logged.
    public byte[]? ProtectedPrivateKey { get; set; }

    /// Whether ProtectedPrivateKey was DPAPI-protected when written (null = legacy). Without it, ciphertext that DPAPI
    /// can no longer decrypt was mistaken for the plain scalar.
    public bool? IsProtected { get; set; }

    /// Recomputed from PublicKeyX/Y if absent; never trusted on its own.
    public string? DeviceKeyId { get; set; }

    public bool HasIdentity => PublicKeyX is not null && PublicKeyY is not null && ProtectedPrivateKey is not null;

    /// Set only by an explicit reset, never by first-time generation.
    public DateTime? LastResetUtc { get; set; }
}

/// A revocation whose relay call is failing, retried with backoff until it succeeds or expires. Never restores pairing.
[Serializable]
public class RevocationRetryEntry
{
    public string PairIdHash { get; set; } = "";
    public int PairEpoch { get; set; }
    public int Sequence { get; set; }
    public string Reason { get; set; } = "";
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public string Signature { get; set; } = "";
    public int Attempt { get; set; }
    public long NextAttemptAtUnixSeconds { get; set; }
}

/// Effects Custom Triggers applied since the last revert. Chat is never tracked - a sent message can't be taken back.
[Serializable]
public class CustomTriggerEffectsState
{
    public bool Title { get; set; }
    public bool Outfit { get; set; }
    public bool Gesture { get; set; }
    public List<Guid> MoodleStatusIds { get; set; } = new();
    /// Owner-written moodles, removed by id: the ledger can't re-apply them, so they never go through it.
    public List<Guid> CustomMoodleIds { get; set; } = new();
    public List<string> RestraintDeviceIds { get; set; } = new();

    public bool Any => Title || Outfit || Gesture || MoodleStatusIds.Count > 0 || CustomMoodleIds.Count > 0 || RestraintDeviceIds.Count > 0;
}

[Serializable]
public class CodeInvitationState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsInviter { get; set; }
    public string Code { get; set; } = "";
    public string InvitationId { get; set; } = "";

    public PairingDirection Direction { get; set; }
    public long ExpiresAt { get; set; }

    /// Inviter: "waiting" or "needs-confirm". Accepter: "waiting" for the inviter to confirm.
    public string Status { get; set; } = "waiting";

    public string? PeerName { get; set; }
    public string? PeerWorld { get; set; }
    public string? PeerTriggerPhrase { get; set; }
    public string? PeerDeviceKeyId { get; set; }
    public string? PeerPublicKeyX { get; set; }
    public string? PeerPublicKeyY { get; set; }

    /// Accepter: pre-generated so a collar applied at accept time already knows its owning pairing.
    public Guid PairingId { get; set; } = Guid.NewGuid();
    public long AcceptedAt { get; set; }
}

/// DPAPI-protected like the device key, with the same Wine caveat.
[Serializable]
public class RecoveryState
{
    public byte[]? ProtectedCode { get; set; }
    public bool? IsProtected { get; set; }

    public bool CodeAcknowledged { get; set; }

    /// A different fingerprint means the backup is stale.
    public string? UploadedFingerprint { get; set; }
    public long LastUploadAt { get; set; }

    /// Backups from regenerated codes whose relay copy still needs deleting.
    public List<string> PendingDeletes { get; set; } = new();

    public bool HasCode => ProtectedCode is { Length: > 0 };
}

[Serializable]
public class PendingRelayOperationState
{
    public string Kind { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string? Target { get; set; }
    public long ExpiresAt { get; set; }

    /// Kept so an invite interrupted by a restart resumes with its original direction.
    public PairingDirection Direction { get; set; }
}

/// Lets reset-imports remove only imported entries. Defaults to Manual so entries saved before this field stay put.
public enum ImportSource
{
    Manual,
    Imported,
}

/// Never authoritative: the Sub's client validates every command when it arrives.
[Serializable]
public class QuickCommand
{
    public string Label { get; set; } = "";
    public string Command { get; set; } = "";

    public ImportSource Source { get; set; } = ImportSource.Manual;

    /// Which paired Sub's snapshot this came from, so a later snapshot replaces only its own entries. Null for manual/legacy entries.
    public string? SourcePairIdHash { get; set; }

    /// The design/gesture/moodle this entry applies, used to skip duplicates on import regardless of command shape.
    public string? Target { get; set; }

    /// Kept in sync with the encoded suffix in Command. Empty means unconfigured, and it can't be sent yet.
    public List<RestraintRuleAssignment>? RestraintRules { get; set; }

    /// Null keeps a legacy captured-device command.
    public string? RestraintCatalogId { get; set; }
    public ulong? RestraintItemId { get; set; }

    /// Lets the Owner group and order gestures like the animation picker. Null/0 for older imports ("Ungrouped").
    public string? GestureModName { get; set; }
    public string? GestureGroupName { get; set; }
    public int GestureGroupOrder { get; set; }
    public int GestureOptionOrder { get; set; }

    /// UI-only mirror of the style encoded in Command, which stays the source of truth.
    public bool TitleIsPrefix { get; set; }
    public Vector3? TitleColor { get; set; }
    public Vector3? TitleGlow { get; set; }

    public bool IsFavorite { get; set; }

    /// Sent as `moodle:"..."`. Null = the Sub's own default.
    public string? MoodleOverride { get; set; }

    /// Sent as `lockfor:`. Null = Permanent.
    public int? LockSeconds { get; set; }

    /// LockSeconds when favorited, so a later change on the row doesn't change the favorite.
    public int? FavoriteLockSeconds { get; set; }

    /// Restraint locks only: the Owner's own password for the lock, kept so they can see it. Only a salted hash is sent.
    public string? LockKey { get; set; }
    public string? FavoriteLockKey { get; set; }

    /// Restraint commands only: the Owner's own picture for it, in the plugin's images folder. Never sent or synced.
    public string? ImageFile { get; set; }
    /// The Sub's shared picture for it, written on import. ImageFile wins when both are set.
    public string? SharedImageFile { get; set; }

    /// CustomMoodles entries only: what the builder edits; Command is regenerated from it on save.
    public Commands.CustomMoodle? CustomMoodle { get; set; }

    /// Restraint commands only: whether the lock it sets can be struggled against.
    public Commands.StruggleLevel Struggle { get; set; }
    public int StrugglePenaltyMinutes { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    public Commands.StruggleSetting StruggleSetting => new(Struggle, StrugglePenaltyMinutes);
}

[Serializable]
public class OwnerQuickCommands
{
    public List<QuickCommand> Titles { get; set; } = new();
    public List<QuickCommand> Outfits { get; set; } = new();
    public List<QuickCommand> Gestures { get; set; } = new();
    public List<QuickCommand> Follow { get; set; } = new();
    public List<QuickCommand> Moodles { get; set; } = new();
    /// Moodles the Owner wrote. Kept apart from Moodles, which catalog sync rebuilds from the Sub's export.
    public List<QuickCommand> CustomMoodles { get; set; } = new();
    public List<QuickCommand> Aliases { get; set; } = new();

    /// Each entry needs RestraintRules assigned before it can be sent.
    public List<QuickCommand> Restraints { get; set; } = new();

    /// Favorites for the built-in fixed actions, which have no QuickCommand to flag (see FixedActionIds).
    public HashSet<string> FavoriteFixedActions { get; set; } = new();

    /// Null = the Sub's own leash default.
    public string? LeashMoodleOverride { get; set; }

    public int LeashLengthYalms { get; set; } = 3;

    /// Sent with each leash as `duty:pause`; the Sub's client then pauses that leash while in a duty.
    public bool PauseLeashInDuties { get; set; }
}

/// Shared between CollarWindow and QuickAccessMenu so the ids never drift.
public static class FixedActionIds
{
    public const string CollarLock = "collarLock";
    public const string CollarUnlock = "collarUnlock";
    public const string ClearMoodle = "clearMoodle";
    public const string RestraintUnlock = "restraintUnlock";
    public const string ClearTitle = "clearTitle";
    public const string UnlockOutfit = "unlockOutfit";
    public const string LeashDefault = "leashDefault";
    public const string UnleashDefault = "unleashDefault";
    public const string Teleport = "teleport";
    public const string CustomTriggerRevert = "customTriggerRevert";
    public const string StopAnimation = "stopAnimation";
}

/// Whether it's locked lives in SlotLockManager, not here.
[Serializable]
public class CollarState
{
    public ulong? ItemId { get; set; }
    public byte Stain { get; set; }
    public byte Stain2 { get; set; }

    /// Optional left-ring piece, locked and released together with the Neck item.
    public ulong? RingItemId { get; set; }
    public byte RingStain { get; set; }
    public byte RingStain2 { get; set; }

    /// Optional; follows the collar's lifecycle in CollarCommand.
    public string? MoodleStatusId { get; set; }
    public string? MoodleStatusName { get; set; }

    public bool HasNeckItem => ItemId is not null;
    public bool HasRing => RingItemId is not null;
    public bool IsConfigured => HasNeckItem || HasRing;
    public bool HasMoodleAssigned => MoodleStatusId is not null;
}

public enum WornRestraintKind { Device, Catalog, AdHoc }

/// Enough to apply the restraint again: Device by its saved device, Catalog by catalog id and item, AdHoc inline.
[Serializable]
public class WornRestraint
{
    public string RuntimeId { get; set; } = "";
    public WornRestraintKind Kind { get; set; }
    /// The name the Owner's command used; what `restraint unlock <name>` and the struggle/key notices carry.
    public string Reference { get; set; } = "";
    public string? DeviceId { get; set; }
    public string? CatalogId { get; set; }
    public ApiEquipSlot? Slot { get; set; }
    public ulong? ItemId { get; set; }
    public byte Stain { get; set; }
    public byte Stain2 { get; set; }
    public List<RestraintRuleAssignment> Rules { get; set; } = new();
    public string? MoodleOverride { get; set; }
    /// Null while the restraint isn't locked.
    public WornRestraintLock? Lock { get; set; }
}

[Serializable]
public class WornRestraintLock
{
    /// The pairing whose Owner set it; escapes and key unlocks are reported to them.
    public Guid? ByPairingId { get; set; }
    /// UTC so it keeps counting through restarts. Null = Permanent.
    public DateTime? ExpiresAtUtc { get; set; }
    public Commands.StruggleLevel StruggleLevel { get; set; }
    public int StrugglePenaltyMinutes { get; set; }
    public DateTime? StruggleNextTryUtc { get; set; }
    /// `salt.hash` of the Owner's password, never the password itself.
    public string? Key { get; set; }
    public DateTime? KeyNextTryUtc { get; set; }

    public Commands.StruggleSetting Struggle => new(StruggleLevel, StrugglePenaltyMinutes);
}

/// Persisted so SlotLockManager can keep enforcing after a reload.
[Serializable]
public class SlotLockEntry
{
    public ApiEquipSlot Slot { get; set; }
    public string Owner { get; set; } = "";
    public ulong ItemId { get; set; }
    public byte Stain { get; set; }
    public byte Stain2 { get; set; }
}

[Serializable]
public class PermissionSet
{
    public bool Title { get; set; }
    public bool Outfit { get; set; }
    public bool Gesture { get; set; }

    public bool Follow { get; set; }

    public bool Collar { get; set; }
    public bool Moodles { get; set; }
    /// Moodles the Owner writes themselves: their text, shown to others through sync. Also needs Moodles.
    public bool OwnerWrittenMoodles { get; set; }

    public bool Restraints { get; set; }

    /// Lets a Custom Trigger send arbitrary chat, so it has its own opt-in (see CustomChatAcknowledged).
    public bool CustomChatMessages { get; set; }

    /// Off by default; a denied request builds and uploads nothing.
    public bool RelayCatalogSync { get; set; }

    public bool Teleport { get; set; }

    /// Also requires ToyControlAcknowledged before it can be enabled.
    public bool ToyControl { get; set; }

    /// Rulebook consequences fire with no click from either person; needs RulebookAcknowledged.
    public bool Rulebook { get; set; }
}

/// Gagged always garbles chat while active; animations on any kind are optional cosmetics.
public enum RestraintRuleKind
{
    ForcedPose,
    WalkOnly,
    ActionBlock,
    Gagged,
    ArmsCuffed,
    LegsCuffed,
    FullBodyCuffed,
}

/// Heavy is 0 so configs and commands from before levels stay Heavy.
public enum GagLevel
{
    Heavy,
    Medium,
    Light,
}

/// What a set of cuff rules draws: Arms Cuffed = Wrists, Legs Cuffed = Ankles, Fully Restrain = all three.
[Flags]
public enum CuffSet
{
    None = 0,
    Wrists = 1,
    Ankles = 2,
    /// The chain from the wrists to the ankles.
    Linked = 4,
}

public static class CuffSets
{
    public static bool IsCuff(RestraintRuleKind kind) =>
        kind is RestraintRuleKind.ArmsCuffed or RestraintRuleKind.LegsCuffed or RestraintRuleKind.FullBodyCuffed;

    public static CuffSet For(RestraintRuleKind kind) => kind switch
    {
        RestraintRuleKind.ArmsCuffed => CuffSet.Wrists,
        RestraintRuleKind.LegsCuffed => CuffSet.Ankles,
        RestraintRuleKind.FullBodyCuffed => CuffSet.Wrists | CuffSet.Ankles | CuffSet.Linked,
        _ => CuffSet.None,
    };

    public static CuffSet Drawn(IEnumerable<RestraintRuleAssignment>? rules)
    {
        var set = CuffSet.None;
        if (rules is null) return set;
        foreach (var rule in rules)
            if (rule.Drawn) set |= For(rule.Kind);
        return set;
    }
}

/// PoseModeId only matters for ForcedPose (1=GroundSit, 2=Sit, 3=Doze; 0 = mod pose via AnimationId).
/// AnimationId is the held animation for the cuffed/gagged kinds. CustomizePreset* only matter for Gagged.
[Serializable]
public class RestraintRuleAssignment
{
    public RestraintRuleKind Kind { get; set; }
    public int PoseModeId { get; set; }
    public string? AnimationId { get; set; }
    public string? AnimationLabel { get; set; }
    public string? CustomizePresetId { get; set; }
    public string? CustomizePresetLabel { get; set; }
    /// Gagged only.
    public GagLevel GagLevel { get; set; }

    /// Cuff kinds only: also draw the cuffs.
    public bool Drawn { get; set; }
}

/// A single gear piece in any lockable slot, carrying one or more restriction rules.
[Serializable]
public class RestraintDeviceDefinition
{
    public RestraintSourceKind SourceKind { get; set; } = RestraintSourceKind.Item;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ApiEquipSlot? Slot { get; set; }
    public ulong? ItemId { get; set; }
    public byte Stain { get; set; }
    public byte Stain2 { get; set; }
    public string Name { get; set; } = "";
    public List<RestraintRuleAssignment> Rules { get; set; } = new();

    public AttachedMoodleRef? AttachedMoodle { get; set; }

    /// Redraws the character when it goes on, whatever "Redraw after a mod change" is set to.
    public bool RedrawOnApply { get; set; }
}

public enum RestraintSourceKind { Item, PenumbraCatalog }

/// DurationMs 0 holds until the outer ceiling or an explicit stop.
[Serializable]
public class PatternStep
{
    public int IntensityPercent { get; set; }
    public int DurationMs { get; set; }
}

/// Names can't collide with a built-in or another custom pattern (enforced when saving).
[Serializable]
public class ToyPattern
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<PatternStep> Steps { get; set; } = new();
    public bool Loop { get; set; }
}

/// PlayerDamage (name kept for saved configs) fires on damage from any source. SpellCastOnYou fires on any action
/// used on you by another player. All three player-sourced kinds can be narrowed by SourcePlayers.
public enum ToyTriggerKind
{
    HealthPercent,
    PlayerDamage,
    RestrictionActive,
    SpellCastOnYou,
    EmoteOnYou,
}

/// Exactly one of IntensityPercent/PatternName is set. ToyTriggerEvaluator enforces a 2 s cooldown floor.
[Serializable]
public class ToyTriggerRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; }
    public ToyTriggerKind Kind { get; set; }

    public int HealthPercentThreshold { get; set; } = 50;

    public RestraintRuleKind RestrictionKind { get; set; }

    /// Empty means any job.
    public List<uint> SpellJobIds { get; set; } = new();

    /// Empty means any action.
    public List<uint> SpellActionIds { get; set; } = new();

    /// Empty means any emote targeted at you.
    public List<uint> EmoteIds { get; set; } = new();

    /// "Name Surname" (any world) or "Name Surname@World". Empty means anyone.
    public List<string> SourcePlayers { get; set; } = new();

    /// Exactly one of these two is set.
    public int? IntensityPercent { get; set; }
    public string? PatternName { get; set; }

    /// Only used with IntensityPercent. Null uses the default ceiling, like an untimed Owner vibrate.
    public int? DurationSeconds { get; set; }

    public int CooldownSeconds { get; set; } = 5;
}

/// LocalCatalog/ImportedPeerCatalog are persisted separately by CatalogStore.
[Serializable]
public class RestraintMapping
{
    public Dictionary<string, RestraintDeviceDefinition> Devices { get; set; } = new();

    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public Dictionary<string, RestraintCatalogEntry> LocalCatalog { get; set; } = new();

    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public Dictionary<string, RestraintCatalogExportEntry> ImportedPeerCatalog { get; set; } = new();

    public List<ConfiguredModRestraint> ConfiguredMods { get; set; } = new();

    // Set-only: an older config's inline catalogs are read for CatalogStore's migration, never written back.
    [Newtonsoft.Json.JsonProperty("LocalCatalog")]
    private Dictionary<string, RestraintCatalogEntry>? InlineLocalCatalog { set => LegacyLocalCatalog = value; }

    [Newtonsoft.Json.JsonProperty("ImportedPeerCatalog")]
    private Dictionary<string, RestraintCatalogExportEntry>? InlineImportedPeerCatalog { set => LegacyImportedPeerCatalog = value; }

    internal Dictionary<string, RestraintCatalogEntry>? LegacyLocalCatalog;
    internal Dictionary<string, RestraintCatalogExportEntry>? LegacyImportedPeerCatalog;
}

[Serializable]
public class ConfiguredModRestraint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CatalogId { get; set; } = "";
    public string Name { get; set; } = "";

    /// Bare it toggles the restraint; after `restraint lock` it force-applies it. Empty = no word.
    public string Alias { get; set; } = "";

    public ulong? ItemId { get; set; }
    public List<RestraintRuleAssignment> Rules { get; set; } = new();

    public AttachedMoodleRef? AttachedMoodle { get; set; }

    /// Redraws the character when it goes on, whatever "Redraw after a mod change" is set to.
    public bool RedrawOnApply { get; set; }

    /// A copied picture's file name in the plugin's images folder. Only its small copy is shared.
    public string? ImageFile { get; set; }
    /// The small JPEG copy of ImageFile that travels to the Owner in the catalog.
    public string? ThumbnailFile { get; set; }
}

/// `ExpiresAtUtc` null = until the Owner clears it.
[Serializable]
public sealed class OwnerLockState
{
    public TitleLock? Title { get; set; }
    public OutfitLock? Outfit { get; set; }
    public List<MoodleLock> Moodles { get; set; } = new();
}

[Serializable]
public sealed class TitleLock
{
    public string Text { get; set; } = "";
    public bool IsPrefix { get; set; }
    public Vector3 Color { get; set; } = new(1, 1, 1);
    public Vector3? Glow { get; set; }
    public Guid? ByPairingId { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

[Serializable]
public sealed class OutfitLock
{
    public string DesignName { get; set; } = "";
    public Guid? ByPairingId { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

/// A moodle from the Sub's library (`StatusId`), or one the Owner wrote (`Custom`, re-applied from its data).
[Serializable]
public sealed class MoodleLock
{
    public Guid StatusId { get; set; }
    public string Name { get; set; } = "";
    public Commands.CustomMoodle? Custom { get; set; }
    /// Who Moodles shows as having applied a custom one.
    public string Applier { get; set; } = "Owner";
    public Guid? ByPairingId { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

[Serializable]
public class ConfiguredModRestraintExportEntry
{
    public string Id { get; set; } = "";
    public string CatalogId { get; set; } = "";
    public string Name { get; set; } = "";
    public ulong? ItemId { get; set; }
    public List<RestraintRuleAssignment> Rules { get; set; } = new();

    /// Null on an older export.
    public string? Moodle { get; set; }

    /// Base64 JPEG thumbnail of the Sub's picture, or null. An older Owner ignores it.
    public string? Picture { get; set; }

    public static ConfiguredModRestraintExportEntry From(ConfiguredModRestraint entry) => new()
    {
        Id = entry.Id, CatalogId = entry.CatalogId, Name = entry.Name,
        ItemId = entry.ItemId, Rules = entry.Rules,
        Moodle = entry.AttachedMoodle is { } m && MoodlesTextFormat.StripMarkup(m.StatusName).Trim() is { Length: > 0 } name && !name.Contains('"') ? name : null,
    };
}

[Serializable]
public class RestraintCatalogEntry
{
    public string Id { get; set; } = "";
    public string ModDirectory { get; set; } = "";
    public string ModName { get; set; } = "";
    public Dictionary<string, List<string>> GroupSelections { get; set; } = new();
    public bool ModEnabled { get; set; }
    public List<uint> ChangedItemIds { get; set; } = new();
    public string Label => ModName;
}

[Serializable]
public class RestraintCatalogExportEntry
{
    public string Id { get; set; } = "";
    public string ModName { get; set; } = "";
    public List<uint> ChangedItemIds { get; set; } = new();
    public string Label => ModName;

    public static RestraintCatalogExportEntry From(RestraintCatalogEntry entry) => new()
    {
        Id = entry.Id, ModName = entry.ModName, ChangedItemIds = entry.ChangedItemIds,
    };
}

[Serializable]
public class PluginConfig : IPluginConfiguration
{
    public int Version { get; set; } = 7;

    public PluginRole Role { get; set; } = PluginRole.Sub;

    /// Used only while a Switch has no active pairing.
    public bool SwitchLastUsedOwnerView { get; set; }

    /// Migrated to true for installs that predate it, so they never see the Welcome window.
    public bool HasCompletedWelcome { get; set; }

    /// Tracked per role. Rerun Tutorial doesn't touch these.
    public bool HasSeenOwnerTutorial { get; set; }

    public bool HasSeenSubTutorial { get; set; }

    public List<PairingState> Pairings { get; set; } = new();

    /// Where outgoing commands go and which direction shared tabs render. Null = none selected.
    public Guid? ActivePairingId { get; set; }

    /// At most one pairing can hold the Neck-slot lock.
    public Guid? CollarOwningPairingId { get; set; }

    /// Migration-only: the pre-multi-pairing field, read once by Plugin.MigrateConfiguration then nulled.
    public PairingState? Pairing { get; set; }

    public DeviceIdentityState DeviceIdentity { get; set; } = new();

    /// Persisted so `customtrigger revert` still works after a reload.
    public CustomTriggerEffectsState CustomTriggerEffects { get; set; } = new();
    public List<RevocationRetryEntry> RevocationOutbox { get; set; } = new();
    public List<PendingRelayOperationState> PendingRelayOperations { get; set; } = new();

    /// At most 7 days each; see Relay/CodePairingService.cs.
    public List<CodeInvitationState> CodeInvitations { get; set; } = new();

    public RecoveryState Recovery { get; set; } = new();
    public PermissionSet Permissions { get; set; } = new();
    public GestureMapping GestureMapping { get; set; } = new();
    public WardrobeMapping WardrobeMapping { get; set; } = new();
    public RestraintMapping RestraintMapping { get; set; } = new();
    public MoodlesMapping MoodlesMapping { get; set; } = new();

    public CollarState Collar { get; set; } = new();

    public List<SlotLockEntry> SlotLocks { get; set; } = new();

    /// Locks a higher-priority owner took over, restored when it releases.
    public List<SlotLockEntry> SuspendedSlotLocks { get; set; } = new();

    /// While true, the Sub's own alias-triggered changes for that category are refused.
    public bool OutfitForceLocked { get; set; }
    public bool CollarForceLocked { get; set; }
    /// The single restraints lock older versions saved. Only read once, to release it: the restraints it covered
    /// weren't saved, so it can't be tied to any of them.
    public bool RestraintsForceLocked { get; set; }

    /// Every restraint the Sub wears, with its lock, so it can be put back after a reload or relog.
    public List<WornRestraint> WornRestraints { get; set; } = new();
    public bool ToyControlForceLocked { get; set; }

    /// Persisted so moodles whose source didn't survive a reload can still be removed.
    public Dictionary<string, Guid> AttachedMoodleHolds { get; set; } = new();

    /// Titles, outfits and moodles the Owner locked on the Sub, put back after a reload and ended by their timers.
    public OwnerLockState OwnerLocks { get; set; } = new();

    public OwnerQuickCommands QuickCommands { get; set; } = new();

    public bool ShowLeashLine { get; set; } = true;

    public static readonly Vector3 DefaultLeashColor = new(0.68f, 0.07f, 0.10f);
    public const float MinLeashBrightness = 0.2f;
    /// Never fully transparent; hiding the line is what ShowLeashLine is for.
    public const float MinLeashOpacity = 0.2f;

    public Vector3 LeashColor { get; set; } = DefaultLeashColor;
    public float LeashBrightness { get; set; } = 1f;
    public float LeashOpacity { get; set; } = 1f;

    public bool ShowDrawnRestraints { get; set; } = true;
    public static readonly Vector3 DefaultRestraintColor = new(0.62f, 0.64f, 0.68f);
    /// Brightness and opacity share the leash's minimums.
    public Vector3 RestraintColor { get; set; } = DefaultRestraintColor;
    public float RestraintBrightness { get; set; } = 1f;
    public float RestraintOpacity { get; set; } = 1f;

    /// Changing it only affects messages sent/parsed afterwards.
    public string TriggerPhrase { get; set; } = "command";

    public ChatChannel OutgoingChannel { get; set; } = ChatChannel.Tell;

    public int LinkshellNumber { get; set; } = 1;
    public int CrossWorldLinkshellNumber { get; set; } = 1;

    /// Never transmitted; only alias names cross chat.
    public AliasBook Aliases { get; set; } = new();

    public bool AutoRescanCatalogs { get; set; } = true;

    /// NO_KEY = unbound. The hotkey always triggers panic, safeword or not.
    public VirtualKey PanicHotkey { get; set; } = VirtualKey.NO_KEY;

    /// Case-insensitive. Unset means `/obpanic` always triggers - a missing safeword must never block panic.
    public string? PanicSafeword { get; set; }

    /// Legacy; only seeds the mod picker during migration.
    public List<string> GestureFolderAllowlist { get; set; } = new();

    /// Empty means every installed mod.
    public HashSet<string> SelectedGestureMods { get; set; } = new();

    public string GestureModFolderFilter { get; set; } = "";

    /// Restraint folders are fail-closed: empty exposes no restraint options.
    public List<string> SelectedGestureFolders { get; set; } = new();
    public List<string> SelectedRestraintFolders { get; set; } = new();

    /// Empty means every saved design.
    public List<string> WardrobeFolderAllowlist { get; set; } = new();

    /// Required before the automation-heavy permissions can be enabled.
    public bool TosAcknowledged { get; set; }

    /// Off by default: Penumbra can redraw on its own, and every extra redraw makes sync plugins re-send the character.
    public bool RedrawAfterModChange { get; set; }

    /// Separate from TosAcknowledged: arbitrary chat on any channel is a broader automation surface.
    public bool CustomChatAcknowledged { get; set; }

    /// Separate again: this one lets an Owner actuate a physical device.
    public bool ToyControlAcknowledged { get; set; }

    /// Local only, never sent.
    public string IntifaceAddress { get; set; } = "ws://127.0.0.1:12345";

    /// Hard ceiling for an explicit permanent-mode command. 4 hours.
    public int PermanentBackstopSeconds { get; set; } = 14400;

    /// Ceiling for untimed vibrates and pattern runs, clamped to [1, ToyControlCommand.MaxDurationSeconds] when read.
    public int DefaultMaxDurationSeconds { get; set; } = 120;

    /// Never synced; only a pattern's name crosses chat.
    public List<ToyPattern> ToyPatterns { get; set; } = new();

    public List<ToyTriggerRule> ToyTriggerRules { get; set; } = new();

    public List<ReactionRule> Reactions { get; set; } = new();

    /// Cleared only by the user's Resume.
    public bool ReactionsSuspended { get; set; }

    /// Gates trigger configuration; a trigger's own Enabled flag plays the permission role.
    public bool ToyTriggersAcknowledged { get; set; }

    /// Not cleared by SubRuntimeState.Reset(), so a trigger can't re-fire right after panic.
    public bool ToyTriggersSuspended { get; set; }

    /// Separate from TosAcknowledged: rulebook consequences happen without a click from either person.
    public bool RulebookAcknowledged { get; set; }

    /// Set by panic, cleared only by the Sub's Resume.
    public bool RulebookSuspended { get; set; }

    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public Action? SaveOverride { get; set; }

    /// Never returns a pairing that has since become unpaired.
    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public PairingState? ActivePairing => ActivePairingId is { } id ? FindPairingById(id) is { IsPaired: true } p ? p : null : null;

    public PairingState? GetActivePairing() => ActivePairing;

    /// Ambiguous for a mutual Owner/Sub pair - prefer the direction overload. Names are matched case-insensitively.
    public PairingState? FindPairing(string? name, string? world)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world)) return null;
        return Pairings.FirstOrDefault(p => p.IsPaired &&
            string.Equals(p.PeerName, name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.PeerWorld, world, StringComparison.OrdinalIgnoreCase));
    }

    /// Use this whenever the direction is known, or a mutual pair can resolve to the wrong pairing.
    public PairingState? FindPairing(string? name, string? world, PairingDirection direction)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world)) return null;
        return Pairings.FirstOrDefault(p => p.IsPaired && p.Direction == direction &&
            string.Equals(p.PeerName, name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.PeerWorld, world, StringComparison.OrdinalIgnoreCase));
    }

    public PairingState? FindPairingById(Guid id) => Pairings.FirstOrDefault(p => p.Id == id);

    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public IEnumerable<PairingState> ActivePairings => Pairings.Where(p => p.IsPaired);

    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public bool HasActiveSubSidePairing => Pairings.Any(p => p.IsPaired && p.Direction == PairingDirection.SubSide);

    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public bool HasActiveOwnerSidePairing => Pairings.Any(p => p.IsPaired && p.Direction == PairingDirection.OwnerSide);

    /// Active pairing's direction, else Role, else (Switch) the last shown direction.
    public PairingDirection ResolveActiveDirection() => ActivePairing switch
    {
        { } active => active.Direction,
        null when Role == PluginRole.Owner => PairingDirection.OwnerSide,
        null when Role == PluginRole.Sub => PairingDirection.SubSide,
        _ => SwitchLastUsedOwnerView ? PairingDirection.OwnerSide : PairingDirection.SubSide,
    };

    private const long SaveQuietMs = 750;
    private const long SaveMaxDelayMs = 3000;

    // Set from any thread; the write itself only happens in SaveNow or FlushPendingSave. 0 means nothing pending.
    private long saveDirtySinceTicks;
    private long saveRequestedAtTicks;

    /// Coalesced: a full config write costs a visible frame hitch, so routine changes are written once they settle.
    /// Identity, pairing and relay state must use SaveNow so a crash can't lose it.
    public void Save()
    {
        var now = Environment.TickCount64;
        Interlocked.Exchange(ref saveRequestedAtTicks, now);
        Interlocked.CompareExchange(ref saveDirtySinceTicks, now, 0);
        NotifyChanged();
    }

    /// Writes immediately, taking any pending coalesced change with it.
    public void SaveNow()
    {
        Interlocked.Exchange(ref saveDirtySinceTicks, 0);
        Write();
        NotifyChanged();
    }

    /// Writes a pending change once it has been quiet for a moment, or has waited long enough during a continuous drag.
    public void FlushPendingSave(bool force = false)
    {
        var since = Interlocked.Read(ref saveDirtySinceTicks);
        if (since == 0) return;
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref saveRequestedAtTicks) < SaveQuietMs && now - since < SaveMaxDelayMs)
            return;
        // Cleared before writing, so a change made during the write is picked up by the next flush.
        Interlocked.Exchange(ref saveDirtySinceTicks, 0);
        Write();
    }

    private void Write()
    {
        if (SaveOverride is not null) SaveOverride();
        else Plugin.PluginInterface.SavePluginConfig(this);
    }

    /// Raised on every save, including CatalogStore's, so the Sub's change detector re-checks its export.
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    public bool MigrateFolderScopes()
    {
        var beforeGesture = string.Join('\n', SelectedGestureFolders);
        var beforeRestraint = string.Join('\n', SelectedRestraintFolders);
        var legacy = GestureModFolderFilter.Trim().Replace('\\', '/').TrimEnd('/');
        if (legacy.Length > 0 && !SelectedGestureFolders.Contains(legacy, StringComparer.OrdinalIgnoreCase))
            SelectedGestureFolders.Add(legacy);
        SelectedGestureFolders = NormalizeFolders(SelectedGestureFolders);
        SelectedRestraintFolders = NormalizeFolders(SelectedRestraintFolders);
        return beforeGesture != string.Join('\n', SelectedGestureFolders) || beforeRestraint != string.Join('\n', SelectedRestraintFolders);
    }

    private static List<string> NormalizeFolders(IEnumerable<string> folders) => folders
        .Select(x => x.Trim().Replace('\\', '/').TrimEnd('/')).Where(x => x.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// GagChat and Gagged share ordinal 3, so only the legacy animation-only Gag(7) needs merging by raw ordinal.
    public void MigrateLegacyGagRules()
    {
        const int legacyGagOrdinal = 7;
        void MigrateRules(List<RestraintRuleAssignment> rules)
        {
            var legacyGag = rules.FirstOrDefault(r => (int)r.Kind == legacyGagOrdinal);
            if (legacyGag is null) return;
            var existingGagged = rules.FirstOrDefault(r => r.Kind == RestraintRuleKind.Gagged && r != legacyGag);
            if (existingGagged is not null)
            {
                existingGagged.AnimationId ??= legacyGag.AnimationId;
                existingGagged.AnimationLabel ??= legacyGag.AnimationLabel;
                rules.Remove(legacyGag);
            }
            else
            {
                legacyGag.Kind = RestraintRuleKind.Gagged;
            }
        }
        foreach (var device in RestraintMapping.Devices.Values)
            MigrateRules(device.Rules);
        foreach (var mod in RestraintMapping.ConfiguredMods)
            MigrateRules(mod.Rules);
        foreach (var cmd in QuickCommands.Restraints.Where(c => c.RestraintRules is { Count: > 0 }))
            MigrateRules(cmd.RestraintRules!);
    }

    /// A rules-only restraint's cuffs are always drawn; ones saved before that existed carry no Drawn flag.
    public void MigrateRulesOnlyCuffsToDrawn()
    {
        foreach (var rule in RestraintMapping.Devices.Values.SelectMany(d => d.Rules).Where(r => CuffSets.IsCuff(r.Kind)))
            rule.Drawn = true;
    }
}
