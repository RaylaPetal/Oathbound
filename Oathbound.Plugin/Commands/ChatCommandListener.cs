using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace Oathbound.Plugin.Commands;

/// collar/pairing "Receiving an unpair notification updates the header": the peer's declared role in the
/// pairing that just ended (their own deliberate unpair, not panic - panic no longer ends any pairing), so
/// the header can say "your Sub" or "your Owner" - transient, in-memory. Keyed by which specific pairing
/// ended (collar/multi-pairing: ending one pairing never affects another).
public readonly record struct PeerUnpairedNotice(PluginRole PeerRole);

/// collar/chat-transport's listener, watching every incoming tell for three things:
///  - a relay invitation reference (`collarinvite`) and its acknowledgement (`collarpairack`) - both roles
///    listen, both only ever hand off to PairingService, which owns the actual relay verification and
///    Pending state (collar/pairing);
///  - a panic/unpair notification (`collarunpair`);
///  - a Sub's leash-off notice (`collarleash off <reason>`, Owner side - collar/leash); and
///  - ongoing alias-trigger tells (Sub role only), matched by the sender name+world an accepted relay
///    pairing captured.
public sealed class ChatCommandListener : IDisposable
{
    private const string PairingAckKeyword = "collarpairack";
    private const string InviteKeyword = "collarinvite";
    private const string UnpairNoticeKeyword = "collarunpair";
    private const string CatalogRequestKeyword = "collarcatalogreq";
    private const string CatalogPermissionDeniedKeyword = "collarcatalogdenied";
    private const string LeashOffNoticeKeyword = ChatComposer.LeashOffNoticeKeyword;

    /// collar/chat-transport "Trigger-phrase command delivery over a selectable channel": every channel
    /// type an ongoing alias-trigger message can arrive on. Pairing lifecycle messages (invite/ack/unpair/
    /// catalog) deliberately stay tell-only below - this broadening is scoped to ongoing command delivery
    /// only, per design.md.
    private static readonly XivChatType[] AllowedTriggerChatTypes =
    [
        XivChatType.TellIncoming, XivChatType.Party, XivChatType.Alliance,
        XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4,
        XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
        XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3,
        XivChatType.CrossLinkShell4, XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6,
        XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
    ];

    /// Reserved first-tokens that route to the Owner's direct "joker" override grammar instead of alias
    /// lookup (see Resolve/HandleForce*). A Sub alias can never be named one of these - CollarWindow's
    /// alias-creation forms validate against this list so the two paths can never collide.
    public static readonly string[] ReservedCategoryWords = ["title", "outfit", "gesture", "collar", "moodle", "restraint", "toy", "customtrigger", "teleport"];

    private readonly PluginConfig config;
    private readonly PairingService pairing;
    private readonly CatalogSyncRelayService catalogSyncRelay;
    private readonly TitleCommand title;
    private readonly OutfitCommand outfit;
    private readonly GestureCommand gesture;
    private readonly FollowCommand follow;
    private readonly CollarCommand collar;
    private readonly MoodlesCommand moodles;
    private readonly RestraintCommand restraints;
    private readonly ToyControlCommand toyControl;
    private readonly CustomTriggerCommand customTriggers;
    private readonly TeleportCommand teleport;
    private readonly LeashOffNotifier leashOffNotifier;
    private readonly OwnerStatusEstimateTracker estimates;

    /// True while TestIncomingCommand runs: a local test stands the active pairing in as sender, but must never
    /// send a leash-off notice to it.
    private bool isLocalTest;

    private readonly Dictionary<Guid, PeerUnpairedNotice> peerUnpairedNotices = new();
    public IReadOnlyDictionary<Guid, PeerUnpairedNotice> PeerUnpairedNotices => peerUnpairedNotices;
    public event Action? PeerUnpairedNoticeChanged;

    /// Called from the header once one pairing's notice has been shown and acted on (Owner clicks Release;
    /// Sub dismisses the informational note) - clears only that pairing's notice.
    public void DismissPeerUnpairedNotice(Guid pairingId)
    {
        if (peerUnpairedNotices.Remove(pairingId))
            PeerUnpairedNoticeChanged?.Invoke();
    }

    public ChatCommandListener(PluginConfig config, PairingService pairing, CatalogSyncRelayService catalogSyncRelay, TitleCommand title, OutfitCommand outfit, GestureCommand gesture, FollowCommand follow, CollarCommand collar, MoodlesCommand moodles, RestraintCommand restraints, ToyControlCommand toyControl, CustomTriggerCommand customTriggers, TeleportCommand teleport, LeashOffNotifier leashOffNotifier, OwnerStatusEstimateTracker estimates)
    {
        this.config = config;
        this.pairing = pairing;
        this.catalogSyncRelay = catalogSyncRelay;
        this.title = title;
        this.outfit = outfit;
        this.gesture = gesture;
        this.follow = follow;
        this.collar = collar;
        this.moodles = moodles;
        this.restraints = restraints;
        this.toyControl = toyControl;
        this.customTriggers = customTriggers;
        this.teleport = teleport;
        this.leashOffNotifier = leashOffNotifier;
        this.estimates = estimates;

        Plugin.ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose() => Plugin.ChatGui.ChatMessage -= OnChatMessage;

    private void OnChatMessage(Dalamud.Game.Chat.IChatMessage message)
    {
        if (Array.IndexOf(AllowedTriggerChatTypes, message.LogKind) < 0)
            return;

        var text = message.Message.TextValue.Trim();

        // Pairing lifecycle messages stay tell-only - see AllowedTriggerChatTypes' doc comment.
        if (message.LogKind == XivChatType.TellIncoming)
        {
            if (TryHandleRelayAckMessage(text, message.Sender))
                return;
            if (TryHandleRelayInviteMessage(text, message.Sender))
                return;
            if (TryHandleUnpairNoticeMessage(text, message.Sender))
                return;
            if (TryHandleCatalogRequestMessage(text, message.Sender))
                return;
            if (TryHandleCatalogPermissionDeniedMessage(text, message.Sender))
                return;
            if (TryHandleLeashOffNoticeMessage(text, message.Sender))
                return;
        }

        // Only a role that can hold a Sub-side pairing (Sub or Switch) ever reacts to ongoing alias
        // triggers - the Owner-side of any pairing only composes (see ChatComposer), it never applies
        // anything from a tell. collar/multi-pairing "Incoming commands resolve against the full peer
        // list": resolved against every currently-paired peer, independent of which pairing (if any) is
        // selected as active in the UI.
        if (config.Role == PluginRole.Owner)
            return;

        var (senderName, senderWorld) = ExtractNameAndWorld(message.Sender);
        if (senderName is null)
            return;

        // World is unavailable on some chat types' sender payload - fall back to a name-only match, but
        // only when it's unambiguous (some chat types embed PlayerPayload reliably; this mirrors the
        // pre-multi-pairing leniency for those that don't, without silently guessing between two Owners
        // sharing a first+last name on different worlds).
        var matchedPairing = senderWorld is not null
            ? config.FindPairing(senderName, senderWorld, PairingDirection.SubSide)
            : SingleOrDefaultIfUnambiguous(config.Pairings.Where(p => p.IsPaired && p.Direction == PairingDirection.SubSide &&
                string.Equals(p.PeerName, senderName, StringComparison.OrdinalIgnoreCase)));
        if (matchedPairing is null)
        {
            Plugin.Log.Information($"Trigger tell ignored: sender \"{senderName}\" does not match any currently-paired Owner.");
            return;
        }

        var trigger = config.TriggerPhrase.Trim();
        if (trigger.Length == 0)
            return;

        if (!text.StartsWith(trigger, StringComparison.OrdinalIgnoreCase))
        {
            Plugin.Log.Information($"Trigger tell ignored: message did not start with the configured trigger phrase \"{trigger}\".");
            return;
        }

        var alias = text[trigger.Length..].Trim();
        if (alias.Length == 0)
            return;

        try
        {
            var outcome = Resolve(alias, matchedPairing);
            Plugin.Log.Information($"Trigger tell dispatch: {outcome.Message}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Failed to apply alias \"{alias}\" from a trigger tell.");
        }
    }

    /// collar/chat-transport "An Owner-style command can be tested entirely locally": exercises the exact
    /// same trigger-phrase check and `Resolve` dispatch a real incoming tell goes through, so this can never
    /// drift from what a real tell would actually do - the only differences are the checks a local test
    /// can't meaningfully perform (there is no real sender to verify, and no tell channel to require).
    /// Never sends or receives any chat message, and never requires pairing.
    public LocalTestResult TestIncomingCommand(string rawText)
    {
        var trigger = config.TriggerPhrase.Trim();
        var trimmed = rawText.Trim();
        if (trigger.Length == 0 || !trimmed.StartsWith(trigger, StringComparison.OrdinalIgnoreCase))
            return LocalTestResult.Fail($"Doesn't start with your configured trigger phrase \"{trigger}\".");

        var alias = trimmed[trigger.Length..].Trim();
        if (alias.Length == 0)
            return LocalTestResult.Fail("No command text after the trigger phrase.");

        try
        {
            // No real sender to resolve in a local test - the active pairing (if any) stands in, matching
            // what a real tell from that peer would use.
            isLocalTest = true;
            return Resolve(alias, config.GetActivePairing());
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Local command test for \"{alias}\" threw an exception.");
            return LocalTestResult.Fail($"Threw an exception: {ex.Message}");
        }
        finally
        {
            isLocalTest = false;
        }
    }

    /// collar/pairing's relay handshake, inviter side: a "collarpairack <invitationId> <proofDigest>" tell
    /// sent automatically by PairingService.AcceptPendingAsync. Consumes the message (returns true)
    /// whenever it starts with the keyword and parses, matching every other handshake message's "fail
    /// closed, never fall through" shape - the actual verification (does the fetched invitation's signed
    /// acceptance carry this exact proof digest, from an invitation this side created) happens in
    /// PairingService, not here; this method's only job is recognizing the tell and capturing its
    /// server-verified sender.
    private bool TryHandleRelayAckMessage(string text, SeString sender)
    {
        if (!text.StartsWith(PairingAckKeyword, StringComparison.OrdinalIgnoreCase))
            return false;

        var (invitationId, rest) = SplitFirstToken(text[PairingAckKeyword.Length..].Trim());
        var (proofDigest, _) = SplitFirstToken(rest);
        if (invitationId.Length == 0 || proofDigest.Length == 0)
            return true;

        var (name, world) = ExtractNameAndWorld(sender);
        if (name is null || world is null)
            return true;

        Plugin.FireAndForget(pairing.HandleAcknowledgementTellAsync(invitationId, proofDigest, name, world, CancellationToken.None));
        return true;
    }

    /// collar/pairing's relay handshake, receiver side: a "collarinvite <invitationId>" tell from either
    /// role. `senderName`/`senderWorld` (FFXIV's own server-verified sender) is the character identity
    /// PairingService binds the fetched invitation to - never a free-text field. Consumes the message
    /// (returns true) whenever it starts with the keyword and parses, whether or not the invitation turns
    /// out to be valid once fetched - a wrong/expired/forged reference is silently ignored by
    /// PairingService, never falls through to alias parsing.
    private bool TryHandleRelayInviteMessage(string text, SeString sender)
    {
        if (!text.StartsWith(InviteKeyword, StringComparison.OrdinalIgnoreCase))
            return false;

        var (invitationId, _) = SplitFirstToken(text[InviteKeyword.Length..].Trim());
        if (invitationId.Length == 0)
            return true;

        var (name, world) = ExtractNameAndWorld(sender);
        if (name is null || world is null)
            return true;

        Plugin.FireAndForget(pairing.HandleInvitationTellAsync(invitationId, name, world, CancellationToken.None));
        return true;
    }

    /// collar/pairing "Receiving a panic notification updates the header": a "collarunpair <role>" tell
    /// sent automatically by PanicHandler. Verified by comparing the sender against this side's own
    /// currently-configured peer name/world - no code involved, since ending an already-trusted
    /// relationship doesn't need the same shared-secret gate establishing one does (see design.md). A
    /// notice from anyone else is silently ignored, the same "fail closed" shape every other handshake
    /// message here already uses.
    private bool TryHandleUnpairNoticeMessage(string text, SeString sender)
    {
        if (!text.StartsWith(UnpairNoticeKeyword, StringComparison.OrdinalIgnoreCase))
            return false;

        var (roleToken, _) = SplitFirstToken(text[UnpairNoticeKeyword.Length..].Trim());
        if (!TryParseRole(roleToken, out var peerRole))
            return true;

        var (name, world) = ExtractNameAndWorld(sender);
        if (name is null)
            return true;
        // collar/multi-pairing: a mutual pair has two pairings with the same peer name+world - the
        // notice's own role token (the sender's role in the pairing that ended) says which one: if the
        // sender was the Owner there, it's this device's Sub-side pairing with them, and vice versa.
        var endedDirection = peerRole == PluginRole.Owner ? PairingDirection.SubSide : PairingDirection.OwnerSide;
        var matchedPairing = world is not null
            ? config.FindPairing(name, world, endedDirection)
            : SingleOrDefaultIfUnambiguous(config.Pairings.Where(p => p.IsPaired && p.Direction == endedDirection && string.Equals(p.PeerName, name, StringComparison.OrdinalIgnoreCase)));
        if (matchedPairing is null)
            return true;

        peerUnpairedNotices[matchedPairing.Id] = new PeerUnpairedNotice(peerRole);
        // collar/restraints: only force-unlock this device's own restraints when the ending pairing was
        // this side's Sub-side relationship - an Owner-side pairing ending (one of this device's Subs
        // panicked) has no bearing on this device's own restraints.
        if (matchedPairing.Direction == PairingDirection.SubSide)
            restraints.ForceUnlock();
        pairing.EndFromVerifiedPeerNotice(matchedPairing);
        PeerUnpairedNoticeChanged?.Invoke();
        return true;
    }

    /// collar/catalog-sync: a "collarcatalogreq <requestId>" tell from either role - CatalogSyncRelayService
    /// itself re-verifies sender identity and the request's own signature before doing anything, so this
    /// method's only job is recognizing the keyword and capturing the verified sender.
    private bool TryHandleCatalogRequestMessage(string text, SeString sender)
    {
        if (!text.StartsWith(CatalogRequestKeyword, StringComparison.OrdinalIgnoreCase))
            return false;

        var (requestId, _) = SplitFirstToken(text[CatalogRequestKeyword.Length..].Trim());
        if (requestId.Length == 0)
            return true;

        var (name, world) = ExtractNameAndWorld(sender);
        if (name is null || world is null)
            return true;

        Plugin.FireAndForget(catalogSyncRelay.HandleCatalogRequestTellAsync(requestId, name, world, CancellationToken.None));
        return true;
    }

    /// collar/catalog-sync "Sub has not opted in": a "collarcatalogdenied <requestId>" tell telling the
    /// Owner their paired Sub hasn't enabled catalog sync.
    private bool TryHandleCatalogPermissionDeniedMessage(string text, SeString sender)
    {
        if (!text.StartsWith(CatalogPermissionDeniedKeyword, StringComparison.OrdinalIgnoreCase))
            return false;

        var (requestId, _) = SplitFirstToken(text[CatalogPermissionDeniedKeyword.Length..].Trim());
        if (requestId.Length == 0)
            return true;
        var (name, world) = ExtractNameAndWorld(sender);
        if (name is null || world is null || config.FindPairing(name, world, PairingDirection.OwnerSide) is null)
            return true;

        catalogSyncRelay.HandlePermissionDeniedTell(requestId);
        return true;
    }

    /// collar/leash "Owner's client stops treating a released Sub as leashed": a "collarleash off <reason>"
    /// tell from a Sub whose leash ended on their side. Only a verified sender this device holds an active
    /// Owner-side pairing with counts; anyone else is silently ignored, the same fail-closed shape as the
    /// catalog-denied notice. The Owner is only told when their estimate actually showed the Sub leashed.
    private bool TryHandleLeashOffNoticeMessage(string text, SeString sender)
    {
        if (!text.StartsWith(LeashOffNoticeKeyword + " ", StringComparison.OrdinalIgnoreCase))
            return false;

        var (word, rest) = SplitFirstToken(text[LeashOffNoticeKeyword.Length..].Trim());
        if (!word.Equals(ChatComposer.LeashOffWord, StringComparison.OrdinalIgnoreCase))
            return true;
        var (name, world) = ExtractNameAndWorld(sender);
        if (name is null || world is null || config.FindPairing(name, world, PairingDirection.OwnerSide) is not { IsPaired: true } ownerPairing)
            return true;

        var reason = LeashEndExtensions.FromNoticeWord(SplitFirstToken(rest).First);
        if (!estimates.MarkUnleashed(ownerPairing.Id))
            return true;
        Plugin.NotificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
        {
            Title = "Leash came off",
            Content = $"{ownerPairing.PeerName}'s leash came off: {reason.ToOwnerText()}",
            Type = Dalamud.Interface.ImGuiNotification.NotificationType.Info,
        });
        return true;
    }

    private static bool TryParseRole(string token, out PluginRole role)
    {
        switch (token.ToLowerInvariant())
        {
            case "sub":
                role = PluginRole.Sub;
                return true;
            case "owner":
                role = PluginRole.Owner;
                return true;
            default:
                role = default;
                return false;
        }
    }

    /// Dispatches to the Owner's direct "joker" override grammar when the command starts with a reserved
    /// category word, otherwise falls back to collar/chat-transport's normal "alias resolution against a
    /// locally-defined dictionary." Returns a `LocalTestResult` describing what happened - reused both for
    /// this dispatch's own diagnostic log line (OnChatMessage) and for the local test tool
    /// (TestIncomingCommand), so neither can drift from what a real tell actually does. An alias that
    /// doesn't match anything the Sub has defined is still never an error visible to the Owner - the result
    /// here is purely local (logged or shown to the Sub only).
    private LocalTestResult Resolve(string commandText, PairingState? sourcePairing)
    {
        var (firstToken, rest) = SplitFirstToken(commandText);
        var permissions = config.Permissions;

        switch (firstToken.ToLowerInvariant())
        {
            case "title":
                return permissions.Title ? HandleForceTitle(rest) : LocalTestResult.Fail("Title permission is not enabled.");
            case "outfit":
                return permissions.Outfit ? HandleForceOutfit(MoodleOption.Strip(rest, out var outfitMoodle), outfitMoodle) : LocalTestResult.Fail("Outfit permission is not enabled.");
            case "gesture":
                return permissions.Gesture && config.TosAcknowledged ? HandleForceGesture(rest) : LocalTestResult.Fail("Gesture permission or the automation-risk acknowledgement is not enabled.");
            case "collar":
                return permissions.Collar ? HandleForceCollar(rest, sourcePairing) : LocalTestResult.Fail("Collar permission is not enabled.");
            case "moodle":
                return permissions.Moodles ? HandleForceMoodle(rest) : LocalTestResult.Fail("Moodles permission is not enabled.");
            case "restraint":
                // collar/restraint-lock-timer: the leading `lockfor:` option comes off first, then the trailing
                // `moodle:` one, leaving the unchanged sub-verb grammar for HandleForceRestraint.
                return permissions.Restraints && config.TosAcknowledged
                    ? HandleForceRestraint(MoodleOption.Strip(LockTimerOption.Strip(rest, out var restraintLock), out var restraintMoodle), restraintMoodle, restraintLock)
                    : LocalTestResult.Fail("Restraints permission or the automation-risk acknowledgement is not enabled.");
            case "toy":
                return permissions.ToyControl && config.ToyControlAcknowledged ? HandleForceToy(rest) : LocalTestResult.Fail("Toy control permission or its dedicated acknowledgement is not enabled.");
            case "customtrigger":
                // Deliberately no outer permission gate here, unlike every other case above - a
                // Custom Trigger bundle mixes categories with independent permissions, so
                // CustomTriggerCommand.Apply checks each bundled action's own permission (and Chat's
                // dedicated acknowledgement) individually as it dispatches (design.md's "orchestrator,
                // not a reimplementation" decision - see also the ResolveAlias CustomTriggers branch).
                return HandleForceCustomTrigger(LockTimerOption.Strip(rest, out var customTriggerLock), customTriggerLock);
            case ControlWords.Leash when SplitFirstToken(rest) is { First: var leashWord } && leashWord.Equals(ChatComposer.LeashTravelWord, StringComparison.OrdinalIgnoreCase):
                // collar/leash-travel (design D4): `leash travel <teleport payload>`. Bare `leash [options]` falls
                // through to ResolveAlias's fixed-word handling as before.
                if (permissions.Follow)
                    return HandleLeashTravel(SplitFirstToken(rest).Remainder, sourcePairing);
                if (follow.LeashedPairingId != sourcePairing?.Id)
                    NotifyNotLeashed(sourcePairing);
                return LocalTestResult.Fail("Follow permission is not enabled.");
            case "teleport":
                // Deliberately no outer permission gate here either, unlike the categories above - unlike
                // them, every one of Teleport's guards (permission, ToS acknowledgement, duty, combat,
                // Lifestream/vnavmesh availability, housing, aetheryte) needs a distinct, reportable reason, so TeleportCommand.Apply
                // owns all of it (collar/teleport's "Refuses when travel cannot safely happen").
                return HandleForceTeleport(rest, sourcePairing);
            case "revert":
                // No outer gate: like customtrigger, each category below checks its own permission.
                return HandleForceRevert(rest);
        }

        return ResolveAlias(commandText, sourcePairing);
    }

    /// The Owner's `revert all` (header "Revert all"): puts everything the Owner can command back to nothing
    /// in one tell - restraints (gear, rules, bound animations, their moodles), outfit (unlocked, then the
    /// whole character reverted to Glamourer automation), title, leash, playing animation, toy, an in-progress
    /// teleport journey, and every moodle. Each category only if the Sub's own permission for it is on, same as each individual command.
    /// Never touches the collar (its slot lock, piece and moodle all stay) or any pairing - the same as
    /// panic, the Sub's own safeword (collar/collaring). Each step is isolated, so one failing (an
    /// IPC plugin not loaded) never stops the rest.
    private LocalTestResult HandleForceRevert(string rest)
    {
        if (!rest.Equals("all", StringComparison.OrdinalIgnoreCase))
            return LocalTestResult.Fail($"Unrecognized \"revert\" override \"{rest}\" - expected \"all\".");

        var permissions = config.Permissions;
        var done = new List<string>();
        var failed = new List<string>();
        void Step(string name, bool allowed, Action action)
        {
            if (!allowed) return;
            try
            {
                action();
                done.Add(name);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"revert all: {name} step failed - continuing.");
                failed.Add(name);
            }
        }

        // Restraints first: releasing their slot locks can hand a slot back to the outfit underneath, which
        // the outfit revert right after then clears too.
        Step("restraints", permissions.Restraints, () => restraints.ForceUnlock());
        Step("outfit", permissions.Outfit, () => outfit.RevertToBase());
        Step("title", permissions.Title, title.ForceClear);
        Step("leash", permissions.Follow, () => follow.Release(LeashEnd.OwnerRevertAll));
        Step("animation", permissions.Gesture, gesture.ResetActiveTemporary);
        Step("toy", permissions.ToyControl, () => toyControl.ForceStop());
        Step("teleport", permissions.Teleport && teleport.IsInProgress, () => teleport.Stop("Owner sent Revert all"));
        // Last, so the releases above have already dropped their own holds; the collar's is re-applied.
        Step("moodles", permissions.Moodles, () => moodles.Ledger.ClearAllExceptCollar());
        // Everything a Custom Trigger could have applied is gone now, so a later "revert custom triggers"
        // must not undo something applied after this.
        customTriggers.ForgetEffects();

        if (done.Count == 0 && failed.Count == 0)
            return LocalTestResult.Fail("Revert all did nothing - every category it covers has its permission turned off.");
        var summary = $"Reverted {string.Join(", ", done)} (collar and pairing untouched).";
        return failed.Count == 0 ? LocalTestResult.Ok(summary) : LocalTestResult.Fail($"{summary} Failed: {string.Join(", ", failed)}.");
    }

    private LocalTestResult HandleForceTitle(string rest)
    {
        if (rest.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            title.ForceClear();
            return LocalTestResult.Ok("Title cleared.");
        }

        const string createPrefix = "create ";
        if (rest.StartsWith(createPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var text = StripQuotes(rest[createPrefix.Length..].Trim());
            if (text.Length > 0)
            {
                title.ForceApply(text);
                return LocalTestResult.Ok($"Title \"{text}\" applied.");
            }
            return LocalTestResult.Fail("\"title create\" was given no text.");
        }

        const string stylePrefix = "style ";
        if (rest.StartsWith(stylePrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (TitleCommand.TryParseStyleCommand(rest[stylePrefix.Length..], out var text, out var isPrefix, out var color, out var glow))
            {
                title.ForceApply(text, isPrefix, color, glow);
                return LocalTestResult.Ok($"Title \"{text}\" applied with style.");
            }
            return LocalTestResult.Fail("\"title style\" was malformed - expected \"style \\\"<text>\\\" prefix:<0|1> color:<r>,<g>,<b> [glow:<r>,<g>,<b>]\".");
        }

        return LocalTestResult.Fail($"Unrecognized \"title\" override \"{rest}\" - expected \"create <text>\", \"style \\\"<text>\\\" prefix:<0|1> color:<r>,<g>,<b>\", or \"clear\".");
    }

    private LocalTestResult HandleForceOutfit(string rest, string? moodleOverride)
    {
        if (rest.Equals("unlock", StringComparison.OrdinalIgnoreCase))
        {
            return outfit.ForceUnlock()
                ? LocalTestResult.Ok("Outfit released (locks and attached moodle).")
                : LocalTestResult.Fail("Outfit unlock did nothing - no lock or attached moodle was held.");
        }

        // `outfit wear <name>`: the Owner's unlocked apply - same as `lock`, but nothing gets locked.
        const string wearPrefix = "wear ";
        if (rest.StartsWith(wearPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = StripQuotes(rest[wearPrefix.Length..].Trim());
            if (name.Length == 0)
                return LocalTestResult.Fail("\"outfit wear\" was given no design name.");
            var (worn, wearReason) = outfit.ForceApply(name, moodleOverride, lockOutfit: false);
            return worn
                ? LocalTestResult.Ok($"Outfit \"{name}\" applied (not locked)." + (wearReason is null ? "" : $" {wearReason}"))
                : LocalTestResult.Fail($"Outfit \"{name}\" not applied: {wearReason}");
        }

        const string lockPrefix = "lock ";
        if (rest.StartsWith(lockPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = StripQuotes(rest[lockPrefix.Length..].Trim());
            if (name.Length > 0)
            {
                var (success, reason) = outfit.ForceApply(name, moodleOverride);
                return success
                    ? LocalTestResult.Ok($"Outfit \"{name}\" applied and locked." + (reason is null ? "" : $" {reason}"))
                    : LocalTestResult.Fail($"Outfit \"{name}\" not applied: {reason}");
            }
            return LocalTestResult.Fail("\"outfit lock\" was given no design name.");
        }

        return LocalTestResult.Fail($"Unrecognized \"outfit\" override \"{rest}\" - expected \"lock <design name>\", \"wear <design name>\" or \"unlock\".");
    }

    private LocalTestResult HandleForceGesture(string rest)
    {
        var name = rest.Trim();
        if (name.Length == 0)
            return LocalTestResult.Fail("\"gesture\" was given no name.");

        var result = gesture.ForceApplyDetailed(name);
        return result.Status switch
        {
            GestureCommand.ApplyStatus.Success => LocalTestResult.Ok($"Gesture \"{result.DisplayName ?? name}\" queued for playback."),
            GestureCommand.ApplyStatus.Missing => LocalTestResult.Fail($"Gesture \"{name}\" is missing or stale in the Sub's current animation catalog. Re-import and edit the Owner quick command."),
            GestureCommand.ApplyStatus.Ambiguous => LocalTestResult.Fail($"Gesture \"{name}\" matches more than one animation; choose a more specific selector."),
            GestureCommand.ApplyStatus.Malformed => LocalTestResult.Fail($"Gesture selector \"{name}\" is malformed."),
            GestureCommand.ApplyStatus.CollectionUnavailable => LocalTestResult.Fail("Gesture failed because the Sub's effective Penumbra collection is unavailable."),
            GestureCommand.ApplyStatus.TemporarySettingsFailed => LocalTestResult.Fail($"Gesture \"{result.DisplayName ?? name}\" failed while applying temporary Penumbra settings."),
            GestureCommand.ApplyStatus.RedrawFailed => LocalTestResult.Fail($"Gesture \"{result.DisplayName ?? name}\" failed while redrawing; temporary settings were rolled back."),
            _ => LocalTestResult.Fail($"Gesture \"{name}\" failed."),
        };
    }

    /// Only `collar unlock` exists - the collar only ever applies as a side effect of pairing acceptance
    /// (see AcceptPending), never through a chat command, so there is no `collar lock` counterpart to
    /// title/outfit's own force-apply grammar.
    private LocalTestResult HandleForceCollar(string rest, PairingState? sourcePairing)
    {
        if (sourcePairing is null)
            return LocalTestResult.Fail("No pairing to attribute this command to.");

        if (rest.Equals("unlock", StringComparison.OrdinalIgnoreCase))
        {
            return collar.ForceUnlock(sourcePairing.Id)
                ? LocalTestResult.Ok("Collar unlocked.")
                : LocalTestResult.Fail("Collar unlock failed - nothing was locked by this pairing.");
        }

        if (rest.Equals("lock", StringComparison.OrdinalIgnoreCase))
        {
            // collar/collaring "A different Owner's lock command takes over the collar": this always
            // supersedes whichever pairing owned the collar before.
            return collar.ForceApply(sourcePairing.Id)
                ? LocalTestResult.Ok("Collar applied and locked.")
                : LocalTestResult.Fail("Collar apply failed - no collar item configured.");
        }

        return LocalTestResult.Fail($"Unrecognized \"collar\" override \"{rest}\" - expected \"lock\" or \"unlock\".");
    }

    private LocalTestResult HandleForceMoodle(string rest)
    {
        if (rest.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            return moodles.ForceClear()
                ? LocalTestResult.Ok("Moodle cleared.")
                : LocalTestResult.Fail("Moodle clear failed - Moodles may be unavailable.");
        }

        const string applyPrefix = "apply ";
        if (rest.StartsWith(applyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = rest[applyPrefix.Length..].Trim();
            if (name.Length > 0)
            {
                return moodles.ForceApply(name)
                    ? LocalTestResult.Ok($"Moodle \"{name}\" applied.")
                    : LocalTestResult.Fail($"No Moodles status named \"{name}\" (or the apply failed).");
            }
            return LocalTestResult.Fail("\"moodle apply\" was given no status name.");
        }

        return LocalTestResult.Fail($"Unrecognized \"moodle\" override \"{rest}\" - expected \"apply <status name>\" or \"clear\".");
    }

    private LocalTestResult HandleForceRestraint(string rest, string? moodleOverride, RestraintLock restraintLock)
    {
        if (rest.Equals("unlock", StringComparison.OrdinalIgnoreCase))
        {
            return restraints.ForceUnlock()
                ? LocalTestResult.Ok("Restraints unlocked.")
                : LocalTestResult.Fail("Restraint unlock failed - nothing was force-locked.");
        }

        const string lockPrefix = "lock ";
        if (rest.StartsWith(lockPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var remainder = rest[lockPrefix.Length..];
            if (RestraintCommand.TryParseLockCommand(remainder, out var name, out var rules) && name.Length > 0)
            {
                var applied = rules is { Count: > 0 } ? restraints.ForceApply(name, rules, moodleOverride, restraintLock) : restraints.ForceApply(name, moodleOverride, restraintLock);
                return applied
                    ? LocalTestResult.Ok($"Restraint device \"{name}\" applied.")
                    : LocalTestResult.Fail($"No restraint device named \"{name}\" (or the apply failed).");
            }
            return LocalTestResult.Fail("\"restraint lock\" was given no device name.");
        }

        const string catalogPrefix = "catalog ";
        if (rest.StartsWith(catalogPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!RestraintCommand.TryParseCatalogCommand(rest[catalogPrefix.Length..], out var id, out var itemId, out var rules))
                return LocalTestResult.Fail("The catalog restraint command was malformed.");
            return restraints.ForceApplyCatalog(id, itemId, rules, moodleOverride, restraintLock)
                ? LocalTestResult.Ok("Shared restraint applied.")
                : LocalTestResult.Fail(restraints.LastFailureReason ?? "The shared restraint could not be applied.");
        }

        const string wearPrefix = "wear ";
        if (rest.StartsWith(wearPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var remainder = rest[wearPrefix.Length..];
            if (RestraintCommand.TryParseWearCommand(remainder, out var slot, out var itemId, out var label, out var rules))
            {
                return restraints.ForceApplyAdHoc(slot, itemId, label, rules, moodleOverride, restraintLock)
                    ? LocalTestResult.Ok($"Ad-hoc restraint device \"{label}\" applied.")
                    : LocalTestResult.Fail($"Ad-hoc restraint device \"{label}\" failed to apply.");
            }
            return LocalTestResult.Fail("\"restraint wear\" was malformed - expected \"wear <slot> <itemId> \\\"<label>\\\" rules:...\".");
        }

        return LocalTestResult.Fail($"Unrecognized \"restraint\" override \"{rest}\" - expected \"catalog <id> \\\"<label>\\\" rules:...\", \"disable <id>\", \"wear <slot> <itemId> \\\"<label>\\\" rules:...\", or \"unlock\" (an apply form may be preceded by \"lockfor:<seconds>\").");
    }

    private LocalTestResult HandleForceToy(string rest)
    {
        if (rest.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            return toyControl.ForceStop()
                ? LocalTestResult.Ok("Toy stopped.")
                : LocalTestResult.Fail("Toy stop failed.");
        }

        const string patternPrefix = "pattern:";
        if (rest.StartsWith(patternPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = rest[patternPrefix.Length..].Trim();
            return toyControl.ForceApplyPattern(name)
                ? LocalTestResult.Ok($"Toy pattern \"{name}\" started.")
                : LocalTestResult.Fail($"Unrecognized toy pattern \"{name}\", or no toy is connected.");
        }

        const string vibratePrefix = "vibrate ";
        if (rest.StartsWith(vibratePrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (ToyControlCommand.TryParseVibrateCommand(rest[vibratePrefix.Length..], out var intensity, out var duration))
            {
                return toyControl.ForceApplyVibrate(intensity, duration)
                    ? LocalTestResult.Ok($"Toy vibrating at {intensity}%.")
                    : LocalTestResult.Fail("Toy vibrate failed - no toy is connected.");
            }
            return LocalTestResult.Fail("\"toy vibrate\" was malformed - expected \"intensity:<0-100> [duration:<seconds>|duration:permanent]\".");
        }

        const string sequencePrefix = "sequence ";
        if (rest.StartsWith(sequencePrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (ToyControlCommand.TryParseCustomSequenceCommand(rest[sequencePrefix.Length..], out var steps, out var loop))
            {
                return toyControl.ForceApplyCustomSequence(steps, loop)
                    ? LocalTestResult.Ok($"Toy playing a {steps.Count}-step custom sequence.")
                    : LocalTestResult.Fail("Toy sequence failed - no toy is connected.");
            }
            return LocalTestResult.Fail("\"toy sequence\" was malformed - expected \"steps:<intensity>=<ms>,... [loop:true]\".");
        }

        return LocalTestResult.Fail($"Unrecognized \"toy\" override \"{rest}\" - expected \"vibrate intensity:<0-100> [duration:<seconds>|duration:permanent]\", \"pattern:<weak|medium|strong|pulse|a custom pattern name>\", \"sequence steps:<intensity>=<ms>,... [loop:true]\", or \"stop\".");
    }

    private LocalTestResult HandleForceCustomTrigger(string rest, RestraintLock restraintLock)
    {
        // collar/custom-triggers "Revert custom triggers". An older Sub doesn't know this sub-verb and rejects
        // it, so it fails closed (nothing is reverted) rather than doing anything unexpected.
        if (rest.Trim().Equals("revert", StringComparison.OrdinalIgnoreCase))
            return customTriggers.RevertEffects();

        const string castPrefix = "cast ";
        if (rest.StartsWith(castPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var remainder = rest[castPrefix.Length..];
            if (CustomTriggerCommand.TryParseCastCommand(remainder, out var label, out var actions))
            {
                var result = customTriggers.Apply(actions, restraintLock);
                return result.Success
                    ? LocalTestResult.Ok($"Custom trigger \"{label}\": {result.Message}")
                    : LocalTestResult.Fail($"Custom trigger \"{label}\": {result.Message}");
            }
            return LocalTestResult.Fail("\"customtrigger cast\" was malformed - expected \"cast \\\"<label>\\\" title=...;outfit=...;gesture=...;moodle=...;restraint=...;chat=<rest of line>\" (chat, if present, must be last).");
        }

        return LocalTestResult.Fail($"Unrecognized \"customtrigger\" override \"{rest}\" - expected \"[lockfor:<seconds>] cast \\\"<label>\\\" ...\".");
    }

    private LocalTestResult HandleForceTeleport(string rest, PairingState? sourcePairing)
    {
        if (!TeleportTarget.TryParse(rest, out var destination))
            return LocalTestResult.Fail($"Unrecognized \"teleport\" payload \"{rest}\" - expected \"world:\\\"<name>\\\" terr:<id> inst:<n> ward:<n> sub:<0|1> pos:<x>,<y>,<z>\" (your Owner may be on an older Oathbound build).");

        var (success, reason) = teleport.Apply(destination, sourcePairing);
        return success
            ? LocalTestResult.Ok($"On the way to your Owner on \"{destination.World}\".")
            : LocalTestResult.Fail(reason ?? "Teleport failed.");
    }

    /// collar/leash-travel: the leashed Owner's automatic "I moved, come along". Only acts while this Sub is
    /// leashed to the sender (FollowCommand checks); Teleport's own guards (permission, acknowledgement,
    /// duty, plugins, aetheryte) apply through TeleportCommand, and a refusal ends the leash.
    private LocalTestResult HandleLeashTravel(string payload, PairingState? sourcePairing)
    {
        if (!TeleportTarget.TryParse(payload, out var destination))
            return LocalTestResult.Fail($"Unrecognized \"leash travel\" payload \"{payload}\".");

        var wasLeashedToSender = sourcePairing is not null && follow.LeashedPairingId == sourcePairing.Id;
        var (success, reason) = follow.TravelTo(destination, sourcePairing);
        // collar/leash-travel: the sender's client still thinks this Sub is leashed to them - say it isn't,
        // once per `leash travel`. (A leash that did belong to them and failed to travel already sent its
        // own `travel` notice from the release.)
        if (!success && !wasLeashedToSender)
            NotifyNotLeashed(sourcePairing);
        return success
            ? LocalTestResult.Ok($"Leash travel: following your Owner to \"{destination.World}\".")
            : LocalTestResult.Fail(reason ?? "Leash travel failed.");
    }

    private void NotifyLeashRefused(PairingState? sourcePairing)
    {
        if (!isLocalTest && sourcePairing is { IsPaired: true, Direction: PairingDirection.SubSide })
            leashOffNotifier.NotifyRefused(sourcePairing);
    }

    private void NotifyNotLeashed(PairingState? sourcePairing)
    {
        if (!isLocalTest && sourcePairing is { IsPaired: true, Direction: PairingDirection.SubSide })
            leashOffNotifier.NotifyNotLeashed(sourcePairing);
    }

    private static PairingState? SingleOrDefaultIfUnambiguous(IEnumerable<PairingState> candidates)
    {
        using var e = candidates.GetEnumerator();
        if (!e.MoveNext()) return null;
        var only = e.Current;
        return e.MoveNext() ? null : only;
    }

    private static (string First, string Remainder) SplitFirstToken(string text)
    {
        var trimmed = text.Trim();
        var spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex < 0 ? (trimmed, "") : (trimmed[..spaceIndex], trimmed[(spaceIndex + 1)..].Trim());
    }

    private static string StripQuotes(string text) => text.Trim('"', '\'');

    private LocalTestResult ResolveAlias(string alias, PairingState? sourcePairing)
    {
        var aliases = config.Aliases;
        var permissions = config.Permissions;

        // collar/control-vocabulary "Control words are fixed": clear-title, unlock, leash, unleash and
        // clear-moodle are literals every client understands, never Sub-renamable, so an Owner never has to
        // discover a mutable command. Checked before the Sub's own aliases so no alias can shadow them.
        if (Matches(alias, ControlWords.ClearTitle))
        {
            if (!permissions.Title)
                return LocalTestResult.Fail("Title permission is not enabled.");
            title.Clear();
            return LocalTestResult.Ok($"\"{alias}\" matched clear-title.");
        }

        if (Matches(alias, ControlWords.Unlock))
        {
            if (!permissions.Outfit)
                return LocalTestResult.Fail("Outfit permission is not enabled.");
            outfit.Unlock();
            return LocalTestResult.Ok($"\"{alias}\" matched unlock-outfit.");
        }

        // collar/control-vocabulary "Leash accepts options": `leash [length:N] [moodle:"..."]`, moodle always
        // last, so it's stripped first. Anything else after it (an invalid length included) isn't a leash command.
        var leashRest = LengthOption.Strip(MoodleOption.Strip(alias, out var leashMoodle), out var leashLength);
        if (Matches(leashRest, ControlWords.Leash))
        {
            // collar/leash "Sub tells the Owner when the leash comes off": a refused leash is reported back too,
            // so the Owner's client doesn't show (or travel with) a leash that never engaged.
            if (!permissions.Follow)
            {
                NotifyLeashRefused(sourcePairing);
                return LocalTestResult.Fail("Follow permission is not enabled.");
            }
            if (follow.Engage(sourcePairing, leashLength ?? LengthOption.DefaultYalms, leashMoodle))
                return LocalTestResult.Ok($"\"{alias}\" matched leash-engage ({follow.EffectiveLength:0} yalms).");
            NotifyLeashRefused(sourcePairing);
            return LocalTestResult.Fail("Leash engage failed - movement lock is unavailable, or no Owner to follow.");
        }

        if (Matches(alias, ControlWords.Unleash))
        {
            if (!permissions.Follow)
                return LocalTestResult.Fail("Follow permission is not enabled.");
            // An unleash from a different Owner than the one holding the leash still releases it, but the
            // holding Owner's client didn't send it - they're told.
            follow.Release(sourcePairing is not null && follow.LeashedPairingId == sourcePairing.Id ? LeashEnd.OwnerUnleash : LeashEnd.Other);
            return LocalTestResult.Ok($"\"{alias}\" matched leash-release.");
        }

        if (Matches(alias, ControlWords.ClearMoodle))
        {
            if (!permissions.Moodles)
                return LocalTestResult.Fail("Moodles permission is not enabled.");
            return moodles.Clear()
                ? LocalTestResult.Ok($"\"{alias}\" matched clear-moodle.")
                : LocalTestResult.Fail($"\"{alias}\" matched clear-moodle, but Moodles may be unavailable.");
        }

        var titleAlias = aliases.Titles.FirstOrDefault(a => Matches(alias, a.Alias));
        if (titleAlias is not null)
        {
            if (!permissions.Title)
                return LocalTestResult.Fail("Title permission is not enabled.");
            title.Apply(titleAlias);
            return LocalTestResult.Ok($"Alias \"{alias}\" matched a title.");
        }

        var outfitAlias = aliases.Outfits.FirstOrDefault(a => Matches(alias, a.Alias));
        if (outfitAlias is not null)
        {
            if (!permissions.Outfit)
                return LocalTestResult.Fail("Outfit permission is not enabled.");
            var (outfitApplied, outfitReason) = outfit.Apply(outfitAlias);
            return outfitApplied
                ? LocalTestResult.Ok($"Alias \"{alias}\" matched an outfit." + (outfitReason is null ? "" : $" {outfitReason}"))
                : LocalTestResult.Fail($"Alias \"{alias}\" matched an outfit, but it wasn't applied: {outfitReason}");
        }

        var gestureAlias = aliases.Gestures.FirstOrDefault(a => Matches(alias, a.Alias));
        if (gestureAlias is not null)
        {
            if (!(permissions.Gesture && config.TosAcknowledged))
                return LocalTestResult.Fail("Gesture permission or the automation-risk acknowledgement is not enabled.");
            return gesture.Apply(gestureAlias)
                ? LocalTestResult.Ok($"Alias \"{alias}\" matched a gesture.")
                : LocalTestResult.Fail($"Alias \"{alias}\" matched a gesture, but it failed to play.");
        }

        // A restraint's own word - a captured device's name or a configured mod restraint's alias - toggles it.
        if (restraints.MatchesWord(alias))
        {
            if (!(permissions.Restraints && config.TosAcknowledged))
                return LocalTestResult.Fail("Restraints permission or the automation-risk acknowledgement is not enabled.");
            return restraints.ToggleByWord(alias)
                ? LocalTestResult.Ok($"\"{alias}\" matched a restraint (toggled).")
                : LocalTestResult.Fail($"\"{alias}\" matched a restraint, but it wasn't toggled: {restraints.LastFailureReason ?? "the apply failed"}.");
        }

        var moodleAlias = aliases.Moodles.FirstOrDefault(a => Matches(alias, a.Alias));
        if (moodleAlias is not null)
        {
            if (!permissions.Moodles)
                return LocalTestResult.Fail("Moodles permission is not enabled.");
            return moodles.Apply(moodleAlias)
                ? LocalTestResult.Ok($"Alias \"{alias}\" matched a Moodle.")
                : LocalTestResult.Fail($"Alias \"{alias}\" matched a Moodle, but it failed to apply.");
        }

        // collar/custom-triggers "Sub defines a named Custom Trigger bundling multiple actions": no
        // category permission gate here - each bundled action checks its own category's permission
        // independently inside CustomTriggerCommand.Apply, so a disabled category is skipped rather than
        // blocking the whole trigger the way every other branch above does.
        var customTrigger = aliases.CustomTriggers.FirstOrDefault(t => Matches(alias, t.Alias));
        if (customTrigger is not null)
            return customTriggers.Apply(customTrigger.Actions);

        return LocalTestResult.Fail($"No matching alias or reserved-word command for \"{alias}\".");
    }

    private static bool Matches(string received, string configured) =>
        !string.IsNullOrWhiteSpace(configured) && string.Equals(received, configured.Trim(), StringComparison.OrdinalIgnoreCase);

    /// Prefers a PlayerPayload when present (structured, unambiguous); falls back to parsing the plain
    /// "Name Surname@World" text form, since not every chat type embeds a PlayerPayload for the sender.
    private static (string? Name, string? World) ExtractNameAndWorld(SeString sender)
    {
        var playerPayload = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
        if (playerPayload is not null)
            return (playerPayload.PlayerName, playerPayload.World.Value.Name.ExtractText());

        var text = sender.TextValue.Trim();
        var atIndex = text.IndexOf('@');
        return atIndex >= 0 ? (text[..atIndex].Trim(), text[(atIndex + 1)..].Trim()) : (text.Length > 0 ? text : null, null);
    }
}
