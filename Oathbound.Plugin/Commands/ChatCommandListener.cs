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

/// The peer's role in the pairing that just ended, so the header can say "your Sub" or "your Owner".
public readonly record struct PeerUnpairedNotice(PluginRole PeerRole);

/// Watches incoming chat for pairing lifecycle tells (`collarinvite`, `collarpairack`, `collarunpair`), the leash-off notice
/// (`collarleash`), and Sub-side trigger commands from a paired peer's verified sender.
/// The rulebook's reserved Owner command words, gated by the rulebook itself.
public interface IRulebookCommandSink
{
    LocalTestResult DeckDraw(string rest, PairingState? sourcePairing);
    LocalTestResult Ledger(string rest, PairingState? sourcePairing);
}

public sealed class ChatCommandListener : IDisposable
{
    private const string PairingAckKeyword = "collarpairack";
    private const string InviteKeyword = "collarinvite";
    private const string UnpairNoticeKeyword = "collarunpair";
    private const string CatalogRequestKeyword = "collarcatalogreq";
    private const string CatalogPermissionDeniedKeyword = "collarcatalogdenied";
    private const string LeashOffNoticeKeyword = ChatComposer.LeashOffNoticeKeyword;
    private const string RulebookNudgeKeyword = ChatComposer.RulebookNudgeKeyword;

    /// Channels a trigger command may arrive on. Pairing lifecycle messages stay tell-only.
    private static readonly XivChatType[] AllowedTriggerChatTypes =
    [
        XivChatType.TellIncoming, XivChatType.Party, XivChatType.Alliance,
        XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4,
        XivChatType.Ls5, XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8,
        XivChatType.CrossLinkShell1, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3,
        XivChatType.CrossLinkShell4, XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6,
        XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8,
    ];

    /// First tokens that route to the Owner's override grammar; Sub aliases can't use them.
    public static readonly string[] ReservedCategoryWords = ["title", "outfit", "gesture", "collar", "moodle", "restraint", "toy", "customtrigger", "teleport",
        Rulebook.ConsequenceValidator.DeckWord, Rulebook.ConsequenceValidator.LedgerWord];

    /// Set once by Plugin; the rulebook service is built after this listener.
    public IRulebookCommandSink? RulebookSink { get; set; }

    /// A verified `collarrulebook` from the Owner of this Sub-side pairing.
    public event Action<PairingState>? RulebookNudgeReceived;

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

    /// A local test uses the active pairing as sender, but must never send it a notice.
    private bool isLocalTest;

    private readonly Dictionary<Guid, PeerUnpairedNotice> peerUnpairedNotices = new();
    public IReadOnlyDictionary<Guid, PeerUnpairedNotice> PeerUnpairedNotices => peerUnpairedNotices;
    public event Action? PeerUnpairedNoticeChanged;

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
            if (TryHandleRulebookNudgeMessage(text, message.Sender))
                return;
            if (TryHandleStruggleNoticeMessage(text, message.Sender))
                return;
        }

        // Only Sub-side pairings apply anything from chat, matched against every paired peer.
        if (config.Role == PluginRole.Owner)
            return;

        var (senderName, senderWorld) = ExtractNameAndWorld(message.Sender);
        if (senderName is null)
            return;

        // Some chat types lack the sender's world - fall back to a name-only match, but only when unambiguous.
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
            var outcome = ResolveFromPairing(alias, matchedPairing);
            Plugin.Log.Information($"Trigger tell dispatch: {outcome.Message}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Failed to apply alias \"{alias}\" from a trigger tell.");
        }
    }

    /// Runs the same dispatch as a real tell. Never sends chat and never requires pairing.
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

    /// Verification happens in PairingService; this only recognizes the tell and captures its verified sender.
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

    /// The verified sender is the identity PairingService binds the invitation to. Always consumed, even when invalid.
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

    /// Verified by matching the sender against our paired peers; anyone else is ignored.
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
        // The role token picks which pairing of a mutual pair ended.
        var endedDirection = peerRole == PluginRole.Owner ? PairingDirection.SubSide : PairingDirection.OwnerSide;
        var matchedPairing = world is not null
            ? config.FindPairing(name, world, endedDirection)
            : SingleOrDefaultIfUnambiguous(config.Pairings.Where(p => p.IsPaired && p.Direction == endedDirection && string.Equals(p.PeerName, name, StringComparison.OrdinalIgnoreCase)));
        if (matchedPairing is null)
            return true;

        peerUnpairedNotices[matchedPairing.Id] = new PeerUnpairedNotice(peerRole);
        // Only our own restraints, and only when the Sub-side pairing ended.
        if (matchedPairing.Direction == PairingDirection.SubSide)
            restraints.ForceUnlock();
        pairing.EndFromVerifiedPeerNotice(matchedPairing);
        PeerUnpairedNoticeChanged?.Invoke();
        return true;
    }

    /// CatalogSyncRelayService re-verifies the sender and the request signature.
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

    /// Only the Owner of an active Sub-side pairing counts; the nudge carries nothing and only triggers a mailbox check.
    private bool TryHandleRulebookNudgeMessage(string text, SeString sender)
    {
        if (!text.Trim().Equals(RulebookNudgeKeyword, StringComparison.OrdinalIgnoreCase))
            return false;
        var (name, world) = ExtractNameAndWorld(sender);
        if (name is null || world is null || config.FindPairing(name, world, PairingDirection.SubSide) is not { IsPaired: true } subPairing)
            return true;
        RulebookNudgeReceived?.Invoke(subPairing);
        return true;
    }

    /// Only a paired Sub of an Owner-side pairing counts; anyone else is ignored.
    private bool TryHandleStruggleNoticeMessage(string text, SeString sender)
    {
        if (!text.StartsWith(ChatComposer.StruggleNoticeKeyword + " ", StringComparison.OrdinalIgnoreCase))
            return false;
        var (word, _) = SplitFirstToken(text[ChatComposer.StruggleNoticeKeyword.Length..].Trim());
        if (!word.Equals(ChatComposer.StruggleFreeWord, StringComparison.OrdinalIgnoreCase))
            return true;
        var (name, world) = ExtractNameAndWorld(sender);
        if (name is null || world is null || config.FindPairing(name, world, PairingDirection.OwnerSide) is not { IsPaired: true } ownerPairing)
            return true;
        estimates.MarkUnrestrained(ownerPairing.Id);
        Plugin.NotificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
        {
            Title = "Struggled free",
            Content = $"{ownerPairing.PeerName} struggled free of your restraints.",
            Type = Dalamud.Interface.ImGuiNotification.NotificationType.Warning,
        });
        return true;
    }

    /// Runs a rulebook consequence through the same dispatch as a tell from `pairing`, so every permission check
    /// applies. Re-checks the allowlist here too, so nothing outside it can run even from a tampered config.
    public LocalTestResult RunRulebookCommand(string commandText, PairingState pairing)
    {
        if (Rulebook.ConsequenceValidator.Check(commandText) is { } refused)
            return LocalTestResult.Fail(refused);
        try
        {
            return ResolveFromPairing(commandText.Trim(), pairing);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "A rulebook consequence threw.");
            return LocalTestResult.Fail($"Threw an exception: {ex.Message}");
        }
    }

    /// An outfit applied while this runs counts as that pairing's Owner's, which a Keep it on oath holds the Sub to,
    /// and a restraint lock it sets is theirs, so a struggle escape is reported to them.
    private LocalTestResult ResolveFromPairing(string commandText, PairingState sourcePairing)
    {
        outfit.CommandSourcePairingId = sourcePairing.Id;
        restraints.PendingLockPairingId = sourcePairing.Id;
        try
        {
            return Resolve(commandText, sourcePairing);
        }
        finally
        {
            outfit.CommandSourcePairingId = null;
            restraints.PendingLockPairingId = null;
            restraints.PendingStruggle = default;
        }
    }

    /// Only a paired Sub counts. The Owner is told only if their estimate still showed the Sub leashed.
    private bool TryHandleLeashOffNoticeMessage(string text, SeString sender)
    {
        if (!text.StartsWith(LeashOffNoticeKeyword + " ", StringComparison.OrdinalIgnoreCase))
            return false;

        var (word, rest) = SplitFirstToken(text[LeashOffNoticeKeyword.Length..].Trim());
        var on = word.Equals(ChatComposer.LeashOnWord, StringComparison.OrdinalIgnoreCase);
        if (!on && !word.Equals(ChatComposer.LeashOffWord, StringComparison.OrdinalIgnoreCase))
            return true;
        var (name, world) = ExtractNameAndWorld(sender);
        if (name is null || world is null || config.FindPairing(name, world, PairingDirection.OwnerSide) is not { IsPaired: true } ownerPairing)
            return true;

        if (on)
        {
            estimates.MarkLeashed(ownerPairing.Id);
            Plugin.NotificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
            {
                Title = "Leashed",
                Content = $"Your presence rule leashed {ownerPairing.PeerName} to you.",
                Type = Dalamud.Interface.ImGuiNotification.NotificationType.Info,
            });
            return true;
        }

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

    /// Reserved category words go to the override grammar, anything else to the Sub's aliases.
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
                return permissions.Gesture && config.TosAcknowledged ? HandleForceGesture(rest, sourcePairing) : LocalTestResult.Fail("Gesture permission or the automation-risk acknowledgement is not enabled.");
            case "collar":
                return permissions.Collar ? HandleForceCollar(rest, sourcePairing) : LocalTestResult.Fail("Collar permission is not enabled.");
            case "moodle":
                return permissions.Moodles ? HandleForceMoodle(rest) : LocalTestResult.Fail("Moodles permission is not enabled.");
            case "restraint":
                // `lockfor:` comes off first, then the trailing `moodle:`.
                return permissions.Restraints && config.TosAcknowledged
                    ? HandleForceRestraint(MoodleOption.Strip(LockTimerOption.Strip(rest, out var restraintLock), out var restraintMoodle), restraintMoodle, restraintLock)
                    : LocalTestResult.Fail("Restraints permission or the automation-risk acknowledgement is not enabled.");
            case "toy":
                return permissions.ToyControl && config.ToyControlAcknowledged ? HandleForceToy(rest) : LocalTestResult.Fail("Toy control permission or its dedicated acknowledgement is not enabled.");
            case "customtrigger":
                // No outer gate: each bundled action checks its own permission.
                return HandleForceCustomTrigger(LockTimerOption.Strip(rest, out var customTriggerLock), customTriggerLock, sourcePairing);
            case ControlWords.Leash when SplitFirstToken(rest) is { First: var leashWord } && leashWord.Equals(ChatComposer.LeashTravelWord, StringComparison.OrdinalIgnoreCase):
                // Bare `leash [options]` falls through to ResolveAlias.
                if (permissions.Follow)
                    return HandleLeashTravel(SplitFirstToken(rest).Remainder, sourcePairing);
                if (follow.LeashedPairingId != sourcePairing?.Id)
                    NotifyNotLeashed(sourcePairing);
                return LocalTestResult.Fail("Follow permission is not enabled.");
            case "teleport":
                // No outer gate: TeleportCommand owns every guard, each with its own reportable reason.
                return HandleForceTeleport(rest, sourcePairing);
            case "revert":
                // No outer gate: each category below checks its own permission.
                return HandleForceRevert(rest);
            case Rulebook.ConsequenceValidator.DeckWord:
                return RulebookSink is { } deckSink ? deckSink.DeckDraw(rest, sourcePairing) : LocalTestResult.Fail("Rulebooks aren't available.");
            case Rulebook.ConsequenceValidator.LedgerWord:
                return RulebookSink is { } ledgerSink ? ledgerSink.Ledger(rest, sourcePairing) : LocalTestResult.Fail("Rulebooks aren't available.");
        }

        return ResolveAlias(commandText, sourcePairing);
    }

    /// Reverts everything the Owner can command, per the Sub's permissions. Never touches the collar or any pairing.
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

        // Restraints first: releasing their slots can hand a slot back to the outfit, which the next step clears.
        Step("restraints", permissions.Restraints, () => restraints.ForceUnlock());
        Step("outfit", permissions.Outfit, () => outfit.RevertToBase());
        Step("title", permissions.Title, title.ForceClear);
        Step("leash", permissions.Follow, () => follow.Release(LeashEnd.OwnerRevertAll));
        Step("animation", permissions.Gesture, gesture.ResetActiveTemporary);
        Step("toy", permissions.ToyControl, () => toyControl.ForceStop());
        Step("teleport", permissions.Teleport && teleport.IsInProgress, () => teleport.Stop("Owner sent Revert all"));
        // Last, so the releases above have already dropped their own holds.
        Step("moodles", permissions.Moodles, () => moodles.Ledger.ClearAllExceptCollar());
        // A later "revert custom triggers" must not undo something applied after this.
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

        // `outfit wear <name>`: like `lock`, but nothing gets locked.
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

    private LocalTestResult HandleForceGesture(string rest, PairingState? sourcePairing)
    {
        var name = LockTimerOption.StripSeconds(rest, out var holdSeconds).Trim();
        if (name.Length == 0)
            return LocalTestResult.Fail("\"gesture\" was given no name.");
        // Reserved, so no animation called "stop" can be sent by name.
        if (name.Equals(ChatComposer.StopGestureWord, StringComparison.OrdinalIgnoreCase))
            return gesture.Stop() ? LocalTestResult.Ok("Animation stopped.") : LocalTestResult.Ok("No animation was playing.");

        var result = gesture.ForceApplyDetailed(name, sourcePairing?.Id, holdSeconds);
        var held = holdSeconds is { } s ? $", held for {RestraintLock.Format(TimeSpan.FromSeconds(Math.Clamp(s, GestureCommand.MinHoldSeconds, GestureCommand.MaxHoldSeconds)))}" : "";
        return result.Status switch
        {
            GestureCommand.ApplyStatus.Success => LocalTestResult.Ok($"Gesture \"{result.DisplayName ?? name}\" queued for playback{held}."),
            GestureCommand.ApplyStatus.Missing => LocalTestResult.Fail($"Gesture \"{name}\" is missing or stale in the Sub's current animation catalog. Re-import and edit the Owner quick command."),
            GestureCommand.ApplyStatus.Ambiguous => LocalTestResult.Fail($"Gesture \"{name}\" matches more than one animation; choose a more specific selector."),
            GestureCommand.ApplyStatus.Malformed => LocalTestResult.Fail($"Gesture selector \"{name}\" is malformed."),
            GestureCommand.ApplyStatus.CollectionUnavailable => LocalTestResult.Fail("Gesture failed because the Sub's effective Penumbra collection is unavailable."),
            GestureCommand.ApplyStatus.TemporarySettingsFailed => LocalTestResult.Fail($"Gesture \"{result.DisplayName ?? name}\" failed while applying temporary Penumbra settings."),
            GestureCommand.ApplyStatus.RedrawFailed => LocalTestResult.Fail($"Gesture \"{result.DisplayName ?? name}\" failed while redrawing; temporary settings were rolled back."),
            _ => LocalTestResult.Fail($"Gesture \"{name}\" failed."),
        };
    }

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
            // Always supersedes whichever pairing owned the collar before.
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
        restraints.PendingStruggle = RestraintStruggle.FromCommand(rest);
        if (rest.Equals("unlock", StringComparison.OrdinalIgnoreCase))
        {
            return restraints.ForceUnlock()
                ? LocalTestResult.Ok("Restraints unlocked.")
                : LocalTestResult.Fail("Restraint unlock failed - nothing was force-locked.");
        }

        const string timerPrefix = "timer ";
        if (rest.StartsWith(timerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // No lock option on this form; refusing one keeps a timed command from being misread.
            if (restraintLock.IsTimed)
                return LocalTestResult.Fail("\"restraint timer\" doesn't take a lockfor: option.");
            return RestraintCommand.TryParseTimerAdjust(rest[timerPrefix.Length..], out var delta)
                ? restraints.AdjustTimedLock(delta)
                : LocalTestResult.Fail("\"restraint timer\" expects +<seconds> or -<seconds>, 60 to 604800.");
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

        return LocalTestResult.Fail($"Unrecognized \"restraint\" override \"{rest}\" - expected \"catalog <id> \\\"<label>\\\" rules:...\", \"disable <id>\", \"wear <slot> <itemId> \\\"<label>\\\" rules:...\", \"timer +|-<seconds>\", or \"unlock\" (an apply form may be preceded by \"lockfor:<seconds>\").");
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

    private LocalTestResult HandleForceCustomTrigger(string rest, RestraintLock restraintLock, PairingState? sourcePairing)
    {
        // An older Sub rejects this sub-verb, so it fails closed.
        if (rest.Trim().Equals("revert", StringComparison.OrdinalIgnoreCase))
            return customTriggers.RevertEffects();

        const string castPrefix = "cast ";
        if (rest.StartsWith(castPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var remainder = rest[castPrefix.Length..];
            if (CustomTriggerCommand.TryParseCastCommand(remainder, out var label, out var actions))
            {
                var result = customTriggers.Apply(actions, restraintLock, sourcePairing?.Id);
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

    /// Only acts while leashed to the sender; Teleport's guards apply and a refusal pauses the leash.
    private LocalTestResult HandleLeashTravel(string payload, PairingState? sourcePairing)
    {
        if (!TeleportTarget.TryParse(payload, out var destination))
            return LocalTestResult.Fail($"Unrecognized \"leash travel\" payload \"{payload}\".");

        var wasLeashedToSender = sourcePairing is not null && follow.LeashedPairingId == sourcePairing.Id;
        var (success, reason) = follow.TravelTo(destination, sourcePairing);
        // The sender's client still thinks this Sub is leashed to them. A leash that did belong to them already sent its own notice.
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

        // Fixed words every client understands, checked before the Sub's aliases so none can shadow them.
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

        // `leash [length:N] [moodle:"..."]`: moodle is always last, so it's stripped first.
        var leashRest = LengthOption.Strip(MoodleOption.Strip(alias, out var leashMoodle), out var leashLength);
        if (Matches(leashRest, ControlWords.Leash))
        {
            // A refused leash is reported back so the Owner's client doesn't show one that never engaged.
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
            // An unleash from a different Owner still releases the leash; the holding Owner is told.
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
            return gesture.Apply(gestureAlias, sourcePairing?.Id)
                ? LocalTestResult.Ok($"Alias \"{alias}\" matched a gesture.")
                : LocalTestResult.Fail($"Alias \"{alias}\" matched a gesture, but it failed to play.");
        }

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

        // No permission gate: each bundled action checks its own.
        var customTrigger = aliases.CustomTriggers.FirstOrDefault(t => Matches(alias, t.Alias));
        if (customTrigger is not null)
            return customTriggers.Apply(customTrigger.Actions, sourcePairingId: sourcePairing?.Id);

        return LocalTestResult.Fail($"No matching alias or reserved-word command for \"{alias}\".");
    }

    private static bool Matches(string received, string configured) =>
        !string.IsNullOrWhiteSpace(configured) && string.Equals(received, configured.Trim(), StringComparison.OrdinalIgnoreCase);

    /// Not every chat type embeds a PlayerPayload, so fall back to parsing "Name Surname@World".
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
