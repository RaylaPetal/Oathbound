using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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

    /// collar/multi-pairing: can hold pairings in both directions at once - Owner-side (commanding a Sub)
    /// and Sub-side (being commanded by an Owner). See PairingState.Direction and PluginConfig.Pairings.
    Switch,
}

/// collar/multi-pairing: which side of a specific pairing this device is on. Independent of the device's
/// own Role - a Switch holds pairings of both directions; an Owner or Sub only ever holds pairings matching
/// their own single direction.
public enum PairingDirection
{
    /// This device commands the peer (the peer is the Sub in this relationship).
    OwnerSide,

    /// This device is commanded by the peer (the peer is the Owner in this relationship).
    SubSide,
}

/// collar/ui-organization "A movable on-screen button opens the quick-access favorites menu".
public enum ScreenCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// collar/chat-transport "Trigger-phrase command delivery over a selectable channel" - which chat
/// channel an Owner's outgoing commands are composed for. Linkshell/CrossWorldLinkshell each need a
/// specific numbered slot (see PluginConfig.LinkshellNumber/CrossWorldLinkshellNumber) to actually send
/// on; the type alone isn't a sendable channel for those two.
public enum ChatChannel
{
    Tell,
    Party,
    Alliance,
    Linkshell,
    CrossWorldLinkshell,
}

/// Where the on-screen quick-access button sits - a corner preset plus a pixel margin from it, rather than
/// free drag placement (design.md's "Alternative considered": simpler to persist/validate, and matches how
/// the DTR bar itself is positioned by Dalamud, not dragged by this plugin).
[Serializable]
public class FavoritesButtonSettings
{
    public ScreenCorner Corner { get; set; } = ScreenCorner.BottomRight;
    public Vector2 Margin { get; set; } = new(16, 16);

    /// collar/ui-organization "On-screen quick-access button can be hidden" - the DTR bar entry still
    /// opens QuickAccessMenu regardless of this setting.
    public bool Visible { get; set; } = true;
}

/// collar/pairing's relay-assisted pairing state. There is no manual code handshake any more - identity is
/// bound by matching `PeerName`/`PeerWorld` (FFXIV's own server-verified sender field) against the verified
/// sender of an invitation/acknowledgement tell, cross-checked against the relay's signed invitation and
/// acceptance envelopes (see Relay/PairingService.cs). `Paired` is the explicit local activation that
/// follows a fully-verified handshake - never auto-enabled by relay state alone.
[Serializable]
public class PairingState
{
    /// collar/multi-pairing: stable identity for this specific pairing, generated once when the pairing is
    /// created (invitation sent, or invitation accepted) and never reused - what `ActivePairingId` and
    /// `CollarOwningPairingId` reference, since peer name/world can change over time but this can't.
    public Guid Id { get; set; } = Guid.NewGuid();

    /// collar/multi-pairing: which side of this specific relationship this device is on. Set once, when the
    /// pairing is created, from the local Role at that moment (Owner/Switch sending or accepting as the
    /// commanding side -> OwnerSide; Sub/Switch accepting or being invited as the commanded side -> SubSide).
    public PairingDirection Direction { get; set; }

    /// Deterministic (SHA-256 of both devices' sorted key ids - see RelayCrypto/computePairIdHash on the
    /// Worker), so this side can always recompute it locally; cached here so it doesn't need recomputing
    /// on every relay call.
    public string? PairIdHash { get; set; }

    /// Server-assigned each time this pairIdHash is (re)activated after any prior revocation; every signed
    /// envelope this side sends or accepts for this pair must declare this exact epoch, so a message from a
    /// stale pairing can never affect a newer one.
    public int PairEpoch { get; set; }

    /// The peer's persistent device signing key fingerprint - required to verify a revocation or catalog
    /// envelope's signature came from the actual paired peer, not just from someone claiming to be them.
    public string? PeerDeviceKeyId { get; set; }

    /// The peer's signing public key itself (not just its fingerprint above) - captured once from the
    /// signed invitation/acceptance envelope at activation time, so this side can verify a later revocation
    /// or catalog envelope's signature without depending on the relay being reachable at verification time.
    public string? PeerPublicKeyX { get; set; }
    public string? PeerPublicKeyY { get; set; }

    public string? PeerName { get; set; }
    public string? PeerWorld { get; set; }
    public bool Paired { get; set; }

    /// collar/chat-transport: the peer's own trigger phrase, captured from their invitation/acceptance
    /// envelope so composing a command to them never needs to manually match their independently-configured
    /// trigger phrase. Null for a peer whose envelope didn't declare one - ChatComposer falls back to this
    /// side's own TriggerPhrase in that case.
    public string? PeerTriggerPhrase { get; set; }

    /// The highest revocation sequence number this side has issued for this pairIdHash across every epoch
    /// it has ever held (monotonic; never resets on re-pair) - the next outgoing revocation uses
    /// OutgoingRevocationSequence + 1.
    public int OutgoingRevocationSequence { get; set; }

    /// The highest revocation sequence number this side has accepted for this pairIdHash - an incoming
    /// revocation at or below this value is a stale replay and is ignored (collar/pairing "Old revocation is
    /// replayed after re-pairing").
    public int IncomingRevocationSequence { get; set; }

    /// Unix seconds of the last relay revocation check for this pair (login, or the low-frequency bounded
    /// poll) - drives the "no more often than every six hours, with jitter" schedule.
    public long LastRevocationCheckUnixSeconds { get; set; }

    /// collar/catalog-sync: Owner-side, unix seconds of the last successfully *imported* catalog snapshot
    /// for this pair (manual refresh or automatic mailbox pickup) - display only ("last imported"); there is
    /// no longer any cooldown derived from it.
    public long LastAcceptedCatalogSyncUnixSeconds { get; set; }

    /// collar/catalog-sync: Owner-side, the highest snapshotId successfully imported for this pair epoch -
    /// a retrieved snapshot at or below this value is stale or replayed and is ignored without touching the
    /// existing imported set (collar/catalog-sync "Older snapshot arrives late").
    public int LastImportedSnapshotId { get; set; }

    /// collar/catalog-sync: Sub-side, this device's own monotonic counter for snapshots it has sent for
    /// this pair - the next uploaded catalog-response envelope uses NextOutgoingSnapshotId + 1, satisfying
    /// the Worker's own strictly-increasing-per-pair-epoch requirement.
    public int NextOutgoingSnapshotId { get; set; }

    /// collar/catalog-sync automatic sync, Owner-side: this pairing's current mailbox receive key - the id
    /// and public half as published (signed) to the relay, and the private scalar DPAPI-protected like the
    /// device identity (same disclosed Wine limitation). Rotated on every consume, so it only ever decrypts
    /// the one snapshot waiting for it. Never exported, logged, or sent anywhere.
    public string? MailboxReceiveKeyId { get; set; }
    public string? MailboxReceivePublicKeyX { get; set; }
    public string? MailboxReceivePublicKeyY { get; set; }
    public byte[]? MailboxReceivePrivateKey { get; set; }
    public bool? MailboxReceivePrivateKeyProtected { get; set; }

    /// Owner-side, local display state for the Sync tab's up-to-date indicator (collar/catalog-sync "Sync tab
    /// shows whether the catalog is up to date"): when the mailbox was last checked successfully, the most
    /// recent check/import failure (cleared on the next success), and when the Sub last published (null =
    /// never, i.e. an older Sub plugin or sync permission off).
    public long LastMailboxCheckOkUnixSeconds { get; set; }
    public string? LastMailboxCheckError { get; set; }
    public long? SubLastPublishedUnixSeconds { get; set; }

    /// Sub-side: SHA-256 of the exported catalog last successfully published to this pairing's mailbox - the
    /// change detector. Recorded only after a successful upload, so any failure leaves the change pending.
    /// Local only; the relay never sees it. Plus when that last successful publish happened (display only).
    public string? LastPublishedCatalogDigest { get; set; }
    public long LastPublishedCatalogUnixSeconds { get; set; }

    /// Sub-side: the snapshotId of that last successful push - compared against the relay's delivery receipt
    /// (waiting / last consumed) so a push that never reached the Owner (key reset, expired unread, new pair
    /// epoch) is published again even though the catalog itself hasn't changed since.
    public int LastPublishedMailboxSnapshotId { get; set; }

    /// UI-only delivery state for the most recent local unpair/panic notification. Never controls pairing.
    public string? LastRevocationDeliveryStatus { get; set; }
    public long LastRevocationDeliveryUpdatedAt { get; set; }

    public bool IsPaired => Paired && !string.IsNullOrWhiteSpace(PeerName) && !string.IsNullOrWhiteSpace(PeerWorld);
}

/// collar/pairing: this installation's persistent cryptographic device identity, generated once and
/// reused for every relay-assisted pairing until an explicit reset. The private key is protected with the
/// strongest locally-available OS mechanism (Windows DPAPI); under Wine this provides no real
/// confidentiality guarantee beyond ordinary file permissions - see protocol/docs/threat-model.md's "Local
/// key storage under Wine" - so Settings must disclose that limitation, never imply the key is "protected"
/// unconditionally.
[Serializable]
public class DeviceIdentityState
{
    public string? PublicKeyX { get; set; }
    public string? PublicKeyY { get; set; }

    /// DPAPI-protected private scalar, or (only when DPAPI itself was unavailable at write time, e.g. under
    /// Wine) the plain scalar. Never serialized into an export, tell, or log - see DeviceIdentityService.
    public byte[]? ProtectedPrivateKey { get; set; }

    /// Whether `ProtectedPrivateKey` was actually DPAPI-protected when written - null for an identity
    /// generated before this field existed (legacy: DeviceIdentityService falls back to its old exception-
    /// based guess for those). Lets `Unprotect` tell "this was never protected" (skip DPAPI entirely) apart
    /// from "this was protected but DPAPI now fails to decrypt it" (a genuinely unrecoverable identity -
    /// wrong Windows profile, rotated DPAPI master key, etc.) instead of conflating both into the same
    /// caught `CryptographicException` and silently treating undecryptable ciphertext as if it were the
    /// plaintext scalar - confirmed in-game as the cause of a `BouncyCastle` "Scalar is not in the interval
    /// [1, n-1]" crash on Accept, since the resulting "scalar" was actually still-encrypted ciphertext bytes.
    public bool? IsProtected { get; set; }

    /// Cached SHA-256 fingerprint of the public key JWK (see RelayCrypto.DeviceKeyId) - recomputed from
    /// PublicKeyX/Y if absent, never trusted as authoritative on its own.
    public string? DeviceKeyId { get; set; }

    public bool HasIdentity => PublicKeyX is not null && PublicKeyY is not null && ProtectedPrivateKey is not null;

    /// collar/pairing "Device identity reset has a short client-side cooldown" - set only by an explicit
    /// user-triggered reset, never by first-time identity generation. See DeviceIdentityService.
    public DateTime? LastResetUtc { get; set; }
}

/// A best-effort revocation the relay HTTP call for is currently failing (offline, relay down, quota) -
/// persisted so it survives a plugin restart and keeps retrying with bounded backoff until it either
/// succeeds or visibly expires. Never restores pairing on its own; the local teardown that created this
/// entry already happened synchronously before this was ever written (collar/pairing "Panic notifies the
/// peer, best-effort").
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

[Serializable]
public class PendingRelayOperationState
{
    public string Kind { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string? Target { get; set; }
    public long ExpiresAt { get; set; }

    /// collar/multi-pairing: which direction a "pair-invite" operation declared itself as, so an invite
    /// interrupted by a restart resumes with its original direction rather than re-deriving it from the
    /// device's current Role (ambiguous for Switch, and wrong if Role changed since the invite was sent).
    public PairingDirection Direction { get; set; }
}

/// collar/catalog-sync: where a `QuickCommand` came from - lets reset-imports (see CollarWindow's
/// "Reset imports" button) remove only entries an import added, once a category's list can mix
/// manually-added and imported entries together. Defaults to `Manual` so every pre-existing entry
/// (serialized before this field existed) deserializes as not-imported, matching design.md's Migration
/// Plan - nothing already saved is wrongly swept by the new per-category filtering; it starts being
/// tracked correctly from the next import onward. No separate `Scanned` state: only the Sub scans, never
/// the Owner - every populated OwnerQuickCommands entry is either typed by the Owner (`Manual`) or came
/// from a Sub-exported file via "Import commands" (`Imported`).
public enum ImportSource
{
    Manual,
    Imported,
}

/// Owner-side: a saved, one-click command - `Command` is the same raw text ChatComposer appends after the
/// trigger phrase (a plain alias, or a "title"/"outfit"/"gesture" override). Never authoritative -
/// ChatCommandListener still matches/validates everything against the Sub's own live state when a command
/// actually arrives, so a stale entry here just fails silently, it can't apply anything wrong.
[Serializable]
public class QuickCommand
{
    public string Label { get; set; } = "";
    public string Command { get; set; } = "";

    /// collar/catalog-sync "Owner can reset every import to a blank slate": see ImportSource.
    public ImportSource Source { get; set; } = ImportSource.Manual;

    /// collar/catalog-sync: which paired Sub's relay snapshot this entry came from (see
    /// CatalogSyncService.ApplyRelaySnapshot) - null for a manually-imported-file entry, a
    /// pre-existing legacy import (from before this field existed), or any Manual entry. Lets a
    /// later relay snapshot from the *same* pair atomically replace only its own prior entries
    /// without touching manual entries or another pair's imports, and lets a stale/legacy imported
    /// entry be explicitly associated or reset (task 6.6) rather than silently swept.
    public string? SourcePairIdHash { get; set; }

    /// collar/catalog-sync "Import skips commands that duplicate an existing quick command": the design
    /// name, gesture id, or (markup-stripped) Moodles status name this entry applies, set only for an
    /// imported Outfit/Gesture/Moodle entry (plain scanned name or single-action alias alike) so a later
    /// import can recognize a same-target duplicate even when its command text takes a different shape
    /// (an alias word vs. a "outfit lock <name>"-style override). Null for every other category, and for
    /// any entry saved before this field existed.
    public string? Target { get; set; }

    /// collar/restraints only: the Owner-assigned restriction rules for this restraint quick command, kept
    /// in sync with the encoded suffix in `Command` (see CollarWindow's restraint rule editor). Null or
    /// empty means the Owner hasn't configured this entry yet - it can't be sent until they do.
    public List<RestraintRuleAssignment>? RestraintRules { get; set; }

    /// Stable identity of a scanned Penumbra restraint option. Null keeps this as a legacy captured-device
    /// command, so old imports and hand-authored commands retain their original behavior.
    public string? RestraintCatalogId { get; set; }
    public ulong? RestraintItemId { get; set; }

    /// collar/gesture only: the source mod/group names and their manifest order, carried through from the
    /// Sub's export, so the Owner's Gesture quick-command list can group and order entries the same way the
    /// animation picker does (numerically, e.g. option 1..400, rather than alphabetically as text - "10"
    /// sorts before "2" as a string). Null/0 for entries imported before these fields existed - those fall
    /// back to an "Ungrouped" bucket, alphabetically ordered, until re-imported.
    public string? GestureModName { get; set; }
    public string? GestureGroupName { get; set; }
    public int GestureGroupOrder { get; set; }
    public int GestureOptionOrder { get; set; }

    /// collar/title only: the Owner-chosen prefix/color for this title quick command, kept in sync with the
    /// encoded `title style ...` command (see CollarWindow's title quick-command editor) - `Command` remains
    /// the actual source of truth sent over the wire; these exist purely so the UI can display/reconstruct
    /// the chosen style without re-parsing it. Null color means a plain `title create <text>` command with
    /// no style at all.
    public bool TitleIsPrefix { get; set; }
    public Vector3? TitleColor { get; set; }
    public Vector3? TitleGlow { get; set; }

    /// collar/ui-organization "Owner can favorite quick commands for quick access": a plain flag, not a
    /// separate list - removing/renaming this entry in its own category list already removes/renames it
    /// everywhere it's referenced, so the favorites window just filters all seven lists by this field.
    public bool IsFavorite { get; set; }

    /// collar/attached-moodles: the Owner's moodle pick for this one command (a selector from the Sub's
    /// imported moodle list), sent as its `moodle:"..."` option. Null = the Sub's own default. Only used on
    /// commands that accept the option (outfit lock / restraint lock|catalog|wear) - see OwnerMoodleOverride.
    public string? MoodleOverride { get; set; }
}

/// Owner-side only in practice. Outfits/Gestures are normally auto-populated by "Add from clipboard" (one
/// QuickCommand per imported name) - see CollarWindow's Owner tab - so there's a ready, one-click button
/// per name without an extra manual "save" step. Titles/Follow/Aliases are built one at a time since
/// there's nothing to bulk-import for freeform title text or arbitrary alias words - Follow in particular
/// has no direct-override syntax the way Title/Outfit/Gesture do (see ChatCommandListener's reserved
/// words), so a Follow entry is always a plain alias like any other.
[Serializable]
public class OwnerQuickCommands
{
    public List<QuickCommand> Titles { get; set; } = new();
    public List<QuickCommand> Outfits { get; set; } = new();
    public List<QuickCommand> Gestures { get; set; } = new();
    public List<QuickCommand> Follow { get; set; } = new();
    public List<QuickCommand> Moodles { get; set; } = new();
    public List<QuickCommand> Aliases { get; set; } = new();

    /// collar/restraints: Owner-side saved `restraint lock <name>` quick commands, one per scanned design
    /// name (tagged by the Sub or not) - same auto-populated-via-import pattern as Outfits/Moodles. Each
    /// entry needs its own QuickCommand.RestraintRules assigned by the Owner before it can be sent.
    public List<QuickCommand> Restraints { get; set; } = new();

    /// collar/ui-organization "Owner can favorite ... built-in fixed-action row[s]": a parallel favorite
    /// mechanism to QuickCommand.IsFavorite for the built-in one-off Send actions (Collar lock/unlock,
    /// Clear moodle, Restraint unlock, Clear title, Unlock outfit, the Leash/Unleash defaults, Teleport) -
    /// these have no backing QuickCommand object to attach a bool to, so favorite state is tracked by the
    /// stable id string instead (see FixedActionIds).
    public HashSet<string> FavoriteFixedActions { get; set; } = new();

    /// collar/attached-moodles: the Owner's moodle pick for the fixed `leash` command (which has no
    /// QuickCommand of its own to hold it). Null = the Sub's own leash default.
    public string? LeashMoodleOverride { get; set; }
}

/// Stable ids for OwnerQuickCommands.FavoriteFixedActions, shared between CollarWindow (which draws the
/// toggle on each row) and QuickAccessMenu (which reads favorited ids to populate the popup) so the two
/// never drift by using different string literals for the same action.
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
}

/// The Sub's configured collar item (collar/collaring) - a single Neck-slot item, picked from a
/// Neck-locked ItemPickerWindow (see CollarCommand.ConfigureFromItem), never typed in as a raw item id.
/// Whether it's currently locked lives in SlotLockManager (collar/slot-locking), not here.
[Serializable]
public class CollarState
{
    public ulong? ItemId { get; set; }
    public byte Stain { get; set; }
    public byte Stain2 { get; set; }

    /// collar/collaring "Sub can optionally assign a Moodle to the collar" - independent of the Neck-slot
    /// item above, optional (both null when unassigned), applied/re-asserted/cleared alongside the collar's
    /// own lock lifecycle (CollarCommand), never through the ordinary Moodles alias/override path.
    public string? MoodleStatusId { get; set; }
    public string? MoodleStatusName { get; set; }

    public bool IsConfigured => ItemId is not null;
    public bool HasMoodleAssigned => MoodleStatusId is not null;
}

/// One category's (Collar/Outfit/future Restraints) claim on a single equipment slot (collar/slot-locking)
/// - persisted so SlotLockManager can resume enforcing it after a plugin reload without needing any
/// Glamourer-side key, unlike the whole-actor `Combination` lock this replaces.
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

    // Separate, higher-risk opt-in per collar/follow's spec - never implied by the other three.
    public bool Follow { get; set; }

    // collar/collaring and collar/moodles: same independent opt-in-per-category pattern as the four above.
    public bool Collar { get; set; }
    public bool Moodles { get; set; }

    // collar/restraints: same independent opt-in pattern - gates both Sub self-apply and the Owner's
    // force-apply override, same as every other category's permission flag.
    public bool Restraints { get; set; }

    /// collar/custom-triggers "Sending a chat message requires its own dedicated permission and
    /// acknowledgement": deliberately independent of every category above (including Gesture, whose own
    /// chat use is a closed set of self-targeting commands) - this permission alone lets a Custom Trigger's
    /// chat action send arbitrary text to any channel, so it needs its own opt-in, never implied by any
    /// other permission being on. See PluginConfig.CustomChatAcknowledged for the matching acknowledgement.
    public bool CustomChatMessages { get; set; }

    /// collar/catalog-sync "Sub has not opted in": Sub-side, gates whether a valid catalog-request tell
    /// from the paired Owner is honored at all. Defaults off for both existing and new installs (task 6.1)
    /// - a denied request never builds or uploads anything, and the Owner learns only a permission status,
    /// never catalog contents.
    public bool RelayCatalogSync { get; set; }

    /// collar/teleport "Separate opt-in permission for teleport": distinct from every other category,
    /// same independent-opt-in pattern as Follow - never implied by any other permission being on.
    public bool Teleport { get; set; }

    /// collar/toy-control: same independent opt-in pattern as every other category, but this one alone
    /// also requires `PluginConfig.ToyControlAcknowledged` before it can be enabled at all (see
    /// CollarWindow.DrawPermissionsCard) - direct physical-device actuation is a materially greater risk
    /// than anything else this permission set gates.
    public bool ToyControl { get; set; }
}

/// collar/restraints: the fixed set of restriction rule kinds a restraint device may carry. `Gagged` always
/// applies its chat-mangling restriction while active; the bound animation on it (like every other
/// animation-bearing kind) is purely an optional cosmetic layer, not a separate rule.
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

/// One restriction rule assigned to a device. `PoseModeId` only matters for ForcedPose: 1=GroundSit,
/// 2=Sit, 3=Doze (the same EmoteModeId values GestureTrigger already uses) select a vanilla pose; 0 is the
/// sentinel for a mod-sourced pose, in which case `AnimationId` carries the chosen animation instead.
/// `AnimationId` also matters for ArmsCuffed/LegsCuffed/FullBodyCuffed/Gagged - a `GestureCatalogEntry.Id`
/// (collar/gesture) identifying the chosen animation to temporarily activate and hold for as long as the
/// rule stays active. `CustomizePresetId`/`CustomizePresetLabel` only matter for Gagged - an optional
/// Customize+ profile applied to the Sub's character for as long as the rule stays active; unset means no
/// Customize+ change. All four fields are ignored by every rule kind that doesn't use them.
[Serializable]
public class RestraintRuleAssignment
{
    public RestraintRuleKind Kind { get; set; }
    public int PoseModeId { get; set; }
    public string? AnimationId { get; set; }
    public string? AnimationLabel { get; set; }
    public string? CustomizePresetId { get; set; }
    public string? CustomizePresetLabel { get; set; }
}

/// A single gear piece (collar/restraints) picked from a slot-and-item picker, generalized to any of the
/// 10 lockable slots - carrying one or more restriction rules. There is no separate scan/tag step: a
/// device is captured and named in one action (see RestraintCommand.CaptureDeviceFromItem).
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

    /// collar/attached-moodles: the Sub's default moodle while this device is engaged, if any.
    public AttachedMoodleRef? AttachedMoodle { get; set; }
}

public enum RestraintSourceKind { Item, PenumbraCatalog }

/// collar/toy-control: one step of a vibration sequence - a fixed intensity held for `DurationMs`
/// milliseconds, or (when `DurationMs` is 0) held indefinitely until the sequence's own outer ceiling or
/// an explicit stop/panic. Shared by the fixed built-in patterns (weak/medium/strong/pulse, defined in code)
/// and Sub-authored custom patterns (`ToyPattern.Steps`, defined here) - `ToyControlCommand` plays both the
/// same way.
[Serializable]
public class PatternStep
{
    public int IntensityPercent { get; set; }
    public int DurationMs { get; set; }
}

/// collar/toy-control "Sub-authored custom vibration patterns": a named, locally-stored step sequence,
/// usable anywhere a built-in named pattern (weak/medium/strong/pulse) is usable - an Owner-sent `toy
/// pattern:<name>` command, or a local automatic trigger's configured action. `Name` cannot collide with a
/// built-in preset name or another custom pattern's name - enforced at the point a pattern is saved (see
/// CollarWindow's pattern editor), not here.
[Serializable]
public class ToyPattern
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<PatternStep> Steps { get; set; } = new();
    public bool Loop { get; set; }
}

/// collar/toy-control "Local automatic toy triggers": which local game-state signal a `ToyTriggerRule`
/// reacts to. `HealthPercentThreshold` only matters for `HealthPercent`; `RestrictionKind` only matters for
/// `RestrictionActive`. `PlayerDamage` (name kept for saved-config compatibility) fires on damage from any
/// source - another player or an NPC/enemy alike. `SpellCastOnYou` fires on any action (damaging or not)
/// used on you by another player character, optionally narrowed by `SpellJobIds`/`SpellActionIds` - unlike
/// `PlayerDamage`, it is not restricted to damage-classified effects (a heal, buff, or debuff cast on you
/// counts too). `EmoteOnYou` fires when another player character uses an emote targeted at you, optionally
/// narrowed by `EmoteIds`. `PlayerDamage`/`SpellCastOnYou`/`EmoteOnYou` can all be narrowed further to
/// specific characters via `SourcePlayers`.
public enum ToyTriggerKind
{
    HealthPercent,
    PlayerDamage,
    RestrictionActive,
    SpellCastOnYou,
    EmoteOnYou,
}

/// collar/toy-control "Local automatic toy triggers"/"Automatic triggers are rate-limited per rule": a
/// single Sub-configured rule that fires a toy action entirely locally when its condition is met, subject
/// to its own `CooldownSeconds` (see `ToyTriggerEvaluator`, which also enforces a 2-second floor beneath
/// whatever value is stored here, defensively, the same clamp-not-reject posture every other numeric input
/// in this plugin already uses). Exactly one of `IntensityPercent`/`PatternName` is set, matching how a
/// wire vibrate/pattern command is one or the other, never both.
[Serializable]
public class ToyTriggerRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; }
    public ToyTriggerKind Kind { get; set; }

    /// Only for `HealthPercent`: fires when current health drops to or below this percentage.
    public int HealthPercentThreshold { get; set; } = 50;

    /// Only for `RestrictionActive`: which restriction category's "currently active" transition fires this
    /// rule (see `Safety.RestrictionRuleManager.IsActive`).
    public RestraintRuleKind RestrictionKind { get; set; }

    /// Only for `SpellCastOnYou`: which caster job(s) (`Lumina.Excel.Sheets.ClassJob.RowId`) this rule
    /// reacts to. Empty means any job.
    public List<uint> SpellJobIds { get; set; } = new();

    /// Only for `SpellCastOnYou`: which specific action(s) (`Lumina.Excel.Sheets.Action.RowId`) this rule
    /// reacts to. Empty means any action. `SpellJobIds` and `SpellActionIds` are independent AND filters -
    /// both empty means "any action from any player", matching `PlayerDamage` but without the
    /// damage-classification restriction.
    public List<uint> SpellActionIds { get; set; } = new();

    /// Only for `EmoteOnYou`: which emote(s) (`Lumina.Excel.Sheets.Emote.RowId`) this rule reacts to. Empty
    /// means any emote targeted at you.
    public List<uint> EmoteIds { get; set; } = new();

    /// For `PlayerDamage`/`SpellCastOnYou`/`EmoteOnYou`: only react when the source is one of these
    /// characters - each entry "Name Surname" (any world) or "Name Surname@World". Empty means anyone.
    public List<string> SourcePlayers { get; set; } = new();

    /// The toy action this rule fires. Exactly one of these two is set - `IntensityPercent` for a plain
    /// vibrate at that intensity, `PatternName` for a built-in or custom named pattern.
    public int? IntensityPercent { get; set; }
    public string? PatternName { get; set; }

    /// Only meaningful alongside `IntensityPercent` (a pattern already carries its own timing via its
    /// steps and loop flag). Null means the fired vibrate uses `ToyDuration.Unspecified` - the same
    /// default-ceiling behavior as an Owner's untimed vibrate command; a value clamps to
    /// `ToyControlCommand.MaxDurationSeconds` exactly like an Owner-requested bounded duration does.
    public int? DurationSeconds { get; set; }

    public int CooldownSeconds { get; set; } = 5;
}

/// Sub-side: the restraint device catalog, keyed by RestraintDeviceDefinition.Id. No scan step or
/// allowlist - each device is captured individually from whatever gear piece the Sub currently has
/// equipped (collar/restraints), the same way CollarState captures the collar item.
///
/// collar/config-performance "Catalogs live outside the hot-saved config file": LocalCatalog/
/// ImportedPeerCatalog are [JsonIgnore]d and persisted separately by CatalogStore - see GestureMapping's
/// equivalent comment.
[Serializable]
public class RestraintMapping
{
    public Dictionary<string, RestraintDeviceDefinition> Devices { get; set; } = new();

    [JsonIgnore]
    public Dictionary<string, RestraintCatalogEntry> LocalCatalog { get; set; } = new();

    [JsonIgnore]
    public Dictionary<string, RestraintCatalogExportEntry> ImportedPeerCatalog { get; set; } = new();

    public List<ConfiguredModRestraint> ConfiguredMods { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? LegacyExtensionData { get; set; }
}

[Serializable]
public class ConfiguredModRestraint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CatalogId { get; set; } = "";
    public string Name { get; set; } = "";

    /// Optional short word the Owner sends for this restraint - bare it toggles it, after `restraint lock`
    /// it force-applies it (RestraintCommand.ToggleByWord/ForceApply). Empty = no word of its own.
    public string Alias { get; set; } = "";

    public ulong? ItemId { get; set; }
    public List<RestraintRuleAssignment> Rules { get; set; } = new();

    /// collar/attached-moodles: the Sub's default moodle while this configured mod restraint is engaged.
    public AttachedMoodleRef? AttachedMoodle { get; set; }
}

[Serializable]
public class ConfiguredModRestraintExportEntry
{
    public string Id { get; set; } = "";
    public string CatalogId { get; set; } = "";
    public string Name { get; set; } = "";
    public ulong? ItemId { get; set; }
    public List<RestraintRuleAssignment> Rules { get; set; } = new();

    /// collar/attached-moodles: the restraint's attached moodle name, imported as the Owner's per-command
    /// moodle pick for the shared copy. Null on an older export (and ignored by an older Owner).
    public string? Moodle { get; set; }

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
    public int Version { get; set; } = 6;

    public PluginRole Role { get; set; } = PluginRole.Sub;

    /// collar/ui-organization "Switch with no active pairing falls back to a default view": which direction
    /// a Switch's shared category tabs last rendered, used only while Switch has no active pairing selected
    /// (an active pairing's own direction always wins otherwise). Defaults to Sub-side (false).
    public bool SwitchLastUsedOwnerView { get; set; }

    /// collar/onboarding: whether the first-run Welcome window (Role + trigger phrase setup) has been
    /// completed or dismissed. An install that predates this field is migrated to `true` on load (see
    /// Plugin.MigrateConfiguration) - it must never pop the Welcome window in front of an already-configured
    /// install.
    public bool HasCompletedWelcome { get; set; }

    /// collar/onboarding: tracked independently per Role - the Owner guided tutorial runs automatically the
    /// first time Role is ever set to Owner (including via the Welcome window itself), and never again
    /// automatically once true. Settings' "Rerun Tutorial" replays it without touching this flag.
    public bool HasSeenOwnerTutorial { get; set; }

    /// collar/onboarding: the Sub-side equivalent of HasSeenOwnerTutorial - independent of it, since a user
    /// who has seen one Role's tutorial has not necessarily seen the other's.
    public bool HasSeenSubTutorial { get; set; }

    /// collar/multi-pairing: every concurrent pairing this device holds, in either direction. Replaces the
    /// old single scalar `Pairing` field (see `LegacyPairing` for the one-time migration path).
    public List<PairingState> Pairings { get; set; } = new();

    /// collar/multi-pairing: which pairing outgoing commands are currently addressed to, and which
    /// direction's view shared category tabs render. Null means no pairing is selected (e.g. nothing is
    /// paired yet, or the sole prior pairing just ended).
    public Guid? ActivePairingId { get; set; }

    /// collar/collaring: which pairing currently holds the Neck-slot collar lock - at most one, since the
    /// Neck slot itself can only ever be locked by one relationship at a time. Null means the collar is not
    /// currently locked by any pairing.
    public Guid? CollarOwningPairingId { get; set; }

    /// Migration-only: the pre-multi-pairing single pairing. Kept under its original property name so an
    /// old config file (serialized before this change) still deserializes it correctly; `Plugin.
    /// MigrateConfiguration` reads it once to seed `Pairings[0]` and then sets it back to null. Never read
    /// or written anywhere else - every other call site uses `Pairings`/`GetActivePairing()`/`FindPairing()`.
    public PairingState? Pairing { get; set; }

    public DeviceIdentityState DeviceIdentity { get; set; } = new();
    public List<RevocationRetryEntry> RevocationOutbox { get; set; } = new();
    public List<PendingRelayOperationState> PendingRelayOperations { get; set; } = new();
    public PermissionSet Permissions { get; set; } = new();
    public GestureMapping GestureMapping { get; set; } = new();
    public WardrobeMapping WardrobeMapping { get; set; } = new();
    public RestraintMapping RestraintMapping { get; set; } = new();
    public MoodlesMapping MoodlesMapping { get; set; } = new();

    /// Sub-side: the Sub's configured collar item (collar/collaring). See CollarState.
    public CollarState Collar { get; set; } = new();

    /// Every active per-slot lock (collar/slot-locking) - see SlotLockEntry and SlotLockManager.
    public List<SlotLockEntry> SlotLocks { get; set; } = new();

    /// collar/slot-locking: locks a higher-priority owner took over (an outfit slot a restraint now
    /// covers), kept so they're restored when that owner releases. See SlotLockManager.TryLock.
    public List<SlotLockEntry> SuspendedSlotLocks { get; set; } = new();

    /// Set by CollarCommand.ForceApply/OutfitCommand.ForceApply (the Owner's "joker" override). While
    /// true, the Sub's own alias-triggered Apply/Clear/Unlock for that category is refused - only the
    /// matching Force* release (or panic) can undo it. Plain bookkeeping, independent of the Glamourer
    /// lock model - unaffected by the move to per-slot locking.
    public bool OutfitForceLocked { get; set; }
    public bool CollarForceLocked { get; set; }
    public bool RestraintsForceLocked { get; set; }
    public bool ToyControlForceLocked { get; set; }

    /// collar/attached-moodles: which Oathbound source currently holds which Moodles status on this Sub
    /// (see AttachedMoodleLedger for the source keys). Persisted so moodles whose source didn't survive a
    /// reload can still be found and removed afterwards.
    public Dictionary<string, Guid> AttachedMoodleHolds { get; set; } = new();

    /// Owner-side only in practice (a Sub has no use for their own names here) - see OwnerQuickCommands.
    public OwnerQuickCommands QuickCommands { get; set; } = new();

    /// collar/ui-organization: persisted position of the on-screen quick-access favorites button.
    public FavoritesButtonSettings FavoritesButton { get; set; } = new();

    /// The phrase that precedes an alias in a trigger tell (collar/chat-transport). Both sides must agree
    /// on this - the Owner's composer and the Sub's parser both read it from their own local config, so
    /// changing it only takes effect for messages sent/parsed after the change.
    public string TriggerPhrase { get; set; } = "command";

    /// collar/chat-transport "Trigger-phrase command delivery over a selectable channel": which channel
    /// this install's outgoing commands are composed for - persisted per install, not per pairing, editable
    /// from the main character header. Defaults to Tell, preserving prior behavior for existing configs.
    public ChatChannel OutgoingChannel { get; set; } = ChatChannel.Tell;

    /// collar/chat-transport "Linkshell and cross-world linkshell slot is configured once": which of the
    /// eight linkshells/cross-world linkshells to compose on when OutgoingChannel selects that channel type.
    public int LinkshellNumber { get; set; } = 1;
    public int CrossWorldLinkshellNumber { get; set; } = 1;

    /// Sub-side: what each alias actually does. Never transmitted - only the alias name crosses chat.
    public AliasBook Aliases { get; set; } = new();

    /// collar/catalog-sync "Sub's catalog is rescanned periodically": when true, Glamourer designs, Penumbra
    /// animation/restraint mods, and Moodles statuses are rescanned at login and about hourly, using the same
    /// selections as a manual rescan. Default on; the Sub can turn it off from the Sync tab.
    public bool AutoRescanCatalogs { get; set; } = true;

    /// The always-available local panic hotkey (collar/pairing). NO_KEY means "not bound" - the hotkey
    /// always triggers panic unconditionally (it's already a deliberate physical action, nothing to type),
    /// regardless of whether a PanicSafeword is set below.
    public VirtualKey PanicHotkey { get; set; } = VirtualKey.NO_KEY;

    /// `/oathboundpanic <word>` only triggers if `<word>` matches this (case-insensitive) - the actual
    /// safeword mechanic: no visible button to hit by accident or under someone else's eye, just a typed
    /// word like any other safeword convention. Empty/unset means no safeword configured, in which case
    /// `/oathboundpanic` (with or without any argument) still triggers unconditionally - an unconfigured
    /// safeword must never become the reason panic stops working.
    public string? PanicSafeword { get; set; }

    /// Legacy folder allowlist, retained only to seed the explicit mod picker during migration.
    public List<string> GestureFolderAllowlist { get; set; } = new();

    /// PoseKit-style Penumbra mod scope. Empty means every installed mod; entries restrict the scan.
    public HashSet<string> SelectedGestureMods { get; set; } = new();

    /// Non-mutating convenience filter for the explicit mod picker.
    public string GestureModFolderFilter { get; set; } = "";

    /// Penumbra sort-folder unions. Gesture folders combine with explicit mod selections; restraint
    /// folders are intentionally fail-closed (empty means no restraint options are exposed).
    public List<string> SelectedGestureFolders { get; set; } = new();
    public List<string> SelectedRestraintFolders { get; set; } = new();

    /// Sub-side: optional Glamourer design-browser folders applied to `Glamourer.GetDesignListExtended`'s
    /// FullPath. Empty means every saved design; entries restrict the scan.
    public List<string> WardrobeFolderAllowlist { get; set; } = new();

    /// Gate per collar/gesture and collar/follow's ToS-disclosure requirement: the Sub must acknowledge
    /// the automation-risk caveat before either permission can be enabled.
    public bool TosAcknowledged { get; set; }

    /// collar/custom-triggers "Sending a chat message requires its own dedicated permission and
    /// acknowledgement": deliberately separate from `TosAcknowledged` above - a Custom Trigger's chat
    /// action is a materially broader automation surface (arbitrary text, any channel) than anything the
    /// general acknowledgement was written to cover, so it gets its own explicit, dedicated checkbox rather
    /// than silently riding on the existing one.
    public bool CustomChatAcknowledged { get; set; }

    /// collar/toy-control "Toy control requires its own dedicated consent acknowledgment": same rationale
    /// as `CustomChatAcknowledged` above, but for an even greater risk category - this is the one
    /// permission in the whole plugin that lets an Owner directly actuate a connected physical device,
    /// rather than only change in-game state.
    public bool ToyControlAcknowledged { get; set; }

    /// collar/toy-control: the Sub's local Intiface Central WebSocket address. Defaults to Intiface
    /// Central's own default listen address - never sent over the wire, purely a local connection setting.
    public string IntifaceAddress { get; set; } = "ws://127.0.0.1:12345";

    /// collar/toy-control "Locally enforced maximum duration": the ceiling an explicit permanent-mode
    /// vibrate/pattern command is held to instead of the default `ToyControlCommand.MaxDurationSeconds` -
    /// still a hard local stop, just a much longer one. Defaults to 4 hours (14400 seconds).
    public int PermanentBackstopSeconds { get; set; } = 14400;

    /// collar/toy-control "Locally enforced maximum duration": the Sub's own configured ceiling used
    /// whenever no explicit bounded duration applies - an untimed vibrate command, or any named/custom
    /// pattern's own outer ceiling (a pattern's steps only govern its rhythm, not how long the whole thing
    /// runs before the safety stop). Always clamped to `[1, ToyControlCommand.MaxDurationSeconds]` at the
    /// point it's read (see `ToyControlCommand`'s effective-ceiling resolution) - a Sub can shorten this
    /// default below the fixed 120s ceiling, but never raise it past it; an explicitly *bounded* Owner
    /// request (`toy vibrate ... duration:<n>`) is still clamped against the fixed ceiling directly,
    /// independent of this setting. Defaults to 120, matching the ceiling's own compiled default.
    public int DefaultMaxDurationSeconds { get; set; } = 120;

    /// collar/toy-control "Sub-authored custom vibration patterns": Sub-local, never synced or sent over
    /// the wire - only a pattern's name crosses chat, the same way only an alias name does.
    public List<ToyPattern> ToyPatterns { get; set; } = new();

    /// collar/toy-control "Local automatic toy triggers": Sub-local trigger configuration.
    public List<ToyTriggerRule> ToyTriggerRules { get; set; } = new();

    /// collar/toy-control "Automatic triggers require their own dedicated consent, separate from
    /// Owner-command permission": a fourth rung on the acknowledgement ladder (TosAcknowledged ->
    /// CustomChatAcknowledged/ToyControlAcknowledged -> this one). Gates trigger *configuration* directly -
    /// unlike ToyControlAcknowledged, there is no separate `PermissionSet` flag alongside it, since a
    /// trigger's own `Enabled` field already plays that role and this is Sub-local automation with no
    /// Owner-command permission involved at all.
    public bool ToyTriggersAcknowledged { get; set; }

    /// collar/toy-control "Panic suspends automatic triggers, not just active device output": deliberately
    /// NOT cleared by SubRuntimeState.Reset() (see that method's comment) - panic sets this true so a
    /// trigger cannot immediately re-fire the instant panic's own revert sequence finishes; only an explicit
    /// Sub UI action clears it.
    public bool ToyTriggersSuspended { get; set; }

    [JsonIgnore]
    public Action? SaveOverride { get; set; }

    /// collar/multi-pairing: the pairing outgoing commands are currently addressed to, or null if none is
    /// selected. Never returns a pairing that has since become unpaired.
    [JsonIgnore]
    public PairingState? ActivePairing => ActivePairingId is { } id ? FindPairingById(id) is { IsPaired: true } p ? p : null : null;

    public PairingState? GetActivePairing() => ActivePairing;

    /// collar/multi-pairing "Incoming commands resolve against the full peer list": matches an incoming
    /// message's verified sender against every currently-paired peer, independent of which pairing (if any)
    /// is active. Case-insensitive, since FFXIV character/world names are not case-sensitive identity.
    /// Ambiguous whenever the same peer holds pairings in both directions (a mutual Owner/Sub pair) -
    /// prefer `FindPairing(name, world, direction)` wherever the caller already knows which direction it
    /// needs; this overload exists only for call sites that genuinely mean "any direction" (there's exactly
    /// one today: an unpair notice's sender match, before the notice's own role token narrows it further).
    public PairingState? FindPairing(string? name, string? world)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world)) return null;
        return Pairings.FirstOrDefault(p => p.IsPaired &&
            string.Equals(p.PeerName, name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.PeerWorld, world, StringComparison.OrdinalIgnoreCase));
    }

    /// collar/multi-pairing: disambiguates a mutual Owner/Sub pair, where the same peer name+world matches
    /// two different pairings (one per direction) - callers that already know which direction they need
    /// (an incoming trigger tell only ever means the Sub-side pairing with that sender, a catalog request
    /// only the Sub-side pairing, etc.) must use this, not the direction-less overload, or they can
    /// silently resolve to the wrong one of the two.
    public PairingState? FindPairing(string? name, string? world, PairingDirection direction)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world)) return null;
        return Pairings.FirstOrDefault(p => p.IsPaired && p.Direction == direction &&
            string.Equals(p.PeerName, name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.PeerWorld, world, StringComparison.OrdinalIgnoreCase));
    }

    public PairingState? FindPairingById(Guid id) => Pairings.FirstOrDefault(p => p.Id == id);

    [JsonIgnore]
    public IEnumerable<PairingState> ActivePairings => Pairings.Where(p => p.IsPaired);

    [JsonIgnore]
    public bool HasActiveSubSidePairing => Pairings.Any(p => p.IsPaired && p.Direction == PairingDirection.SubSide);

    [JsonIgnore]
    public bool HasActiveOwnerSidePairing => Pairings.Any(p => p.IsPaired && p.Direction == PairingDirection.OwnerSide);

    /// collar/multi-pairing "Active pairing selection drives outgoing commands and role-aware views": the
    /// active pairing's own direction wins when one is selected; otherwise falls back to Role, and for a
    /// Switch with nothing active, to whichever direction its shared category tabs last showed.
    public PairingDirection ResolveActiveDirection() => ActivePairing switch
    {
        { } active => active.Direction,
        null when Role == PluginRole.Owner => PairingDirection.OwnerSide,
        null when Role == PluginRole.Sub => PairingDirection.SubSide,
        _ => SwitchLastUsedOwnerView ? PairingDirection.OwnerSide : PairingDirection.SubSide,
    };

    public void Save()
    {
        if (SaveOverride is not null) SaveOverride();
        else Plugin.PluginInterface.SavePluginConfig(this);
        NotifyChanged();
    }

    /// collar/catalog-sync automatic sync: raised whenever persisted state is saved - this config, or the
    /// scanned catalogs CatalogStore keeps in their own file - so the Sub's change detector knows to re-check
    /// its export (after a quiet period; it compares digests, so a save that changed nothing costs one
    /// export build and never an upload). A plain event, never serialized.
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

    /// collar/restraints "Migration of legacy animation-only Gag devices": the old GagChat(3) and
    /// Gagged(3) share the same ordinal, so a legacy GagChat-only entry already deserializes correctly as
    /// Gagged with no further action. Only the legacy animation-only Gag(7) - now outside the enum's
    /// defined range entirely, so it must be matched by its raw ordinal rather than by name - needs an
    /// explicit merge: fold onto an existing Gagged entry in the same rule list if one is already present
    /// (from a legacy GagChat), or convert in place otherwise. Chat-garbling becomes active for every
    /// migrated entry per the confirmed decision, even for a Sub who previously used Gag alone with no
    /// chat effect.
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
}
