using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.ImGuiNotification;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// 1:1 chat between the two people of a pairing over the relay. Only ever shows text: nothing a message says is run.
/// Cage and panic deliberately don't touch it. Main thread only.
public sealed class ChatService : IDisposable
{
    /// A message still waiting this long has expired on the relay even if its receipt never arrived.
    private static readonly TimeSpan WaitingExpiry = TimeSpan.FromDays(7) + TimeSpan.FromHours(1);

    private readonly PluginConfig config;
    private readonly PeerMessenger messenger;
    private readonly ChatGagService gag;
    private DateTime nextExpiryCheckUtc = DateTime.MinValue;
    /// Messages that waited while offline arrive together on login; one sound covers the burst.
    private DateTime nextSoundUtc = DateTime.MinValue;
    private readonly ChatImageService images;
    private readonly CancellationTokenSource shutdown = new();
    /// Received pictures being fetched now, and when each failed one may be tried again.
    private readonly HashSet<Guid> downloading = new();
    private readonly Dictionary<Guid, DateTime> retryAfter = new();
    private DateTime nextRetryScanUtc = DateTime.MinValue;

    public ChatHistoryStore History { get; } = new();

    /// Set each frame by the chat window to the conversation (its PairingId) it shows while focused, so its messages
    /// don't toast.
    public Guid? FocusedPairingId { get; set; }

    /// Asks the chat window to open on a conversation (a toast or a Chat button was clicked).
    public event Action<Guid>? OpenRequested;

    public ChatService(PluginConfig config, PeerMessenger messenger, ChatGagService gag, RelayClient relay)
    {
        this.config = config;
        this.messenger = messenger;
        this.gag = gag;
        images = new ChatImageService(relay);
        messenger.ChatReceived += OnReceived;
        messenger.ChatResult += OnResult;
        messenger.MatchChatReceived += OnMatchReceived;
        // Sent just before the last unload and never answered; a receipt that arrives later still corrects it.
        foreach (var conversation in History.All)
            foreach (var entry in conversation.Messages.Where(e => e.State == ChatEntryState.Sending))
            {
                entry.State = ChatEntryState.NotDelivered;
                entry.Reason = entry.Image == ChatImageState.Preparing
                    ? "Oathbound closed before the picture was sent"
                    : "Oathbound closed before the relay answered";
                if (entry.Image == ChatImageState.Preparing) entry.Image = ChatImageState.Unavailable;
                History.MarkDirty(conversation);
            }
    }

    public void Dispose()
    {
        shutdown.Cancel();
        messenger.ChatReceived -= OnReceived;
        messenger.ChatResult -= OnResult;
        messenger.MatchChatReceived -= OnMatchReceived;
        History.Tick(flush: true);
    }

    public int TotalUnread => History.All.Sum(c => c.Unread);

    public int UnreadFor(Guid pairingId) => History.Find(pairingId)?.Unread ?? 0;

    /// One per partner, active ones first (in config order), then ended ones that still have history. Pairings lists
    /// every active pairing with that partner (Owner and Sub of each other gives two); empty once they've all ended.
    /// The open conversations, as the window lists them.
    public IReadOnlyList<(ChatConversation Conversation, IReadOnlyList<PairingState> Pairings)> Conversations() =>
        AllConversations().Where(c => !c.Conversation.Closed).ToList();

    /// Conversations closed from the window, for reopening.
    public IReadOnlyList<(ChatConversation Conversation, IReadOnlyList<PairingState> Pairings)> ClosedConversations() =>
        AllConversations().Where(c => c.Conversation.Closed).ToList();

    public void Close(Guid conversationId)
    {
        if (History.Find(conversationId) is not { Closed: false } conversation) return;
        conversation.Closed = true;
        History.MarkDirty(conversation);
    }

    private void Reopen(ChatConversation conversation)
    {
        if (!conversation.Closed) return;
        conversation.Closed = false;
        History.MarkDirty(conversation);
    }

    private IReadOnlyList<(ChatConversation Conversation, IReadOnlyList<PairingState> Pairings)> AllConversations()
    {
        var list = new List<(ChatConversation Conversation, List<PairingState> Pairings)>();
        foreach (var pairing in config.Pairings.Where(p => p.IsPaired))
        {
            var conversation = History.For(pairing);
            var existing = list.FindIndex(l => l.Conversation == conversation);
            if (existing >= 0) list[existing].Pairings.Add(pairing);
            else list.Add((conversation, new List<PairingState> { pairing }));
        }
        var result = list.Select(l => (l.Conversation, (IReadOnlyList<PairingState>)l.Pairings)).ToList();
        foreach (var ended in History.All.Where(c => !c.IsMatch && c.Messages.Count > 0 && list.All(l => l.Conversation != c)).ToList())
            result.Add((ended, Array.Empty<PairingState>()));
        // Matches after pairings: active ones, then ended ones that still have history. They never carry a pairing.
        foreach (var match in config.LookingForPair.Matches.OrderBy(m => m.IsActive ? 0 : 1).ThenByDescending(m => m.CreatedAt))
        {
            if (match.IsActive) result.Add((History.ForMatch(match), Array.Empty<PairingState>()));
            else if (History.Find(match.LocalId) is { Messages.Count: > 0 } ended) result.Add((ended, Array.Empty<PairingState>()));
        }
        return result;
    }

    /// The match behind a match conversation, if it's still known.
    public LfpMatchState? MatchFor(ChatConversation conversation) =>
        conversation.IsMatch ? config.LookingForPair.FindMatch(conversation.PairingId) : null;

    /// Null when it went out; otherwise why it couldn't be sent. Goes through the Gagged rewrite like pairing chat.
    public string? SendMatch(LfpMatchState match, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (text.Length > PeerMessageCrypto.MaxChatChars)
            return $"That message is too long - chat messages can be at most {PeerMessageCrypto.MaxChatChars} characters.";
        var sent = gag.RewriteRelayChat(text);
        SendMatchMessage(match, new MatchPlaintext { Text = sent }, sent, null);
        return null;
    }

    /// Shares the character currently logged in, sealed for this match only. Main thread (reads the object table).
    public string? ShareCharacter(LfpMatchState match)
    {
        if (CodePairingService.CurrentCharacter(null) is not { } me) return "Log in to a character first.";
        SendMatchMessage(match, new MatchPlaintext { Kind = MatchPlaintext.CharacterKind, Name = me.Name, World = me.World },
            $"You shared your character: {me.Name} @ {me.World}", MatchPlaintext.CharacterKind);
        match.SharedMyCharacter = true;
        config.Save();
        return null;
    }

    /// Sends a pairing code made for this match. `receiverRole` is what they would become.
    public void SendMatchInvite(LfpMatchState match, string code, string receiverRole)
    {
        var entry = SendMatchMessage(match, new MatchPlaintext { Kind = MatchPlaintext.InviteKind, Code = code, Role = receiverRole },
            $"You sent a pairing invite: they'd be your {(receiverRole == "sub" ? "Sub" : "Owner")}. Code {code}", MatchPlaintext.InviteKind);
        entry.InviteCode = code;
        entry.InviteRole = receiverRole;
    }

    private ChatEntry SendMatchMessage(LfpMatchState match, MatchPlaintext message, string shownText, string? special)
    {
        var conversation = History.ForMatch(match);
        var entry = new ChatEntry
        {
            Outgoing = true,
            Text = shownText,
            At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            State = ChatEntryState.Sending,
            Special = special,
        };
        var (id, failure) = messenger.SendMatchChat(match, message);
        entry.MessageId = id;
        if (failure is { } f) Fail(conversation, entry, f);
        History.Add(conversation, entry);
        return entry;
    }

    private void OnMatchReceived(MatchChatReceived received)
    {
        var match = received.Match;
        var message = received.Message;
        var entry = new ChatEntry { At = received.SentAt, State = ChatEntryState.Received };
        switch (message.Kind)
        {
            case MatchPlaintext.CharacterKind:
                match.PeerCharacterName = message.Name;
                match.PeerCharacterWorld = message.World;
                config.Save();
                entry.Special = MatchPlaintext.CharacterKind;
                entry.Text = $"{match.Peer.DisplayName} shared their character: {message.Name} @ {message.World}";
                break;
            case MatchPlaintext.InviteKind:
                entry.Special = MatchPlaintext.InviteKind;
                entry.InviteCode = message.Code;
                entry.InviteRole = message.Role;
                entry.Text = $"{match.Peer.DisplayName} sent you a pairing invite: you'd be their {(message.Role == "sub" ? "Sub" : "Owner")}.";
                break;
            default:
                entry.Text = message.Text!;
                break;
        }
        var conversation = History.ForMatch(match);
        conversation.PeerNeedsUpdate = false;
        Reopen(conversation);
        History.Add(conversation, entry);
        if (FocusedPairingId == conversation.PairingId) return;
        conversation.Unread++;
        Notify(conversation, entry.Text);
    }

    /// The pairing a message to this partner goes out on: the active pairing when it's one of theirs.
    public PairingState? SendPairing(IReadOnlyList<PairingState> pairings) =>
        pairings.FirstOrDefault(p => p.Id == config.ActivePairing?.Id) ?? pairings.FirstOrDefault();

    /// The conversation a pairing's chat lives in (the partner's), by its conversation id.
    public Guid ConversationIdFor(Guid pairingId) =>
        config.FindPairingById(pairingId) is { IsPaired: true } pairing ? History.For(pairing).PairingId : pairingId;

    /// Opens (and reopens, if it was closed) a conversation in the chat window.
    public void RequestOpen(Guid pairingId)
    {
        var id = ConversationIdFor(pairingId);
        if (History.Find(id) is { } conversation) Reopen(conversation);
        OpenRequested?.Invoke(id);
    }

    public void MarkRead(Guid pairingId)
    {
        if (History.Find(pairingId) is { Unread: > 0 } conversation)
        {
            conversation.Unread = 0;
            History.MarkDirty(conversation);
        }
    }

    /// Null when it went out (its state follows in the history); otherwise why it couldn't be sent.
    public string? Send(PairingState pairing, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (text.Length > PeerMessageCrypto.MaxChatChars)
            return $"That message is too long - chat messages can be at most {PeerMessageCrypto.MaxChatChars} characters.";

        var sent = gag.RewriteRelayChat(text);
        var conversation = History.For(pairing);
        var entry = new ChatEntry
        {
            Outgoing = true,
            Text = sent,
            At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            State = ChatEntryState.Sending,
        };
        var (id, failure) = messenger.SendChat(pairing, new ChatPlaintext { Text = sent });
        entry.MessageId = id;
        if (failure is { } f) Fail(conversation, entry, f);
        History.Add(conversation, entry);
        return null;
    }

    /// Sends a picture from `sourcePath` with an optional caption: it shows at once as sending, then the picture is
    /// shrunk if needed, encrypted and uploaded, and only then the message carrying its key goes out.
    /// `deleteSource` removes the source file afterwards (a snapshot taken just for this).
    public void SendImage(PairingState pairing, string sourcePath, string caption, bool deleteSource)
    {
        caption = caption.Trim();
        if (caption.Length > PeerMessageCrypto.MaxChatChars) caption = caption[..PeerMessageCrypto.MaxChatChars];
        var sentCaption = gag.RewriteRelayChat(caption);
        var conversation = History.For(pairing);
        var entry = new ChatEntry
        {
            Outgoing = true,
            Text = sentCaption,
            At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            State = ChatEntryState.Sending,
            Image = ChatImageState.Preparing,
            ImageId = ChatImageCrypto.NewImageId(),
        };
        History.Add(conversation, entry);

        var pairingId = pairing.Id;
        var pairIdHash = pairing.PairIdHash;
        var pairEpoch = pairing.PairEpoch;
        var imageId = entry.ImageId;
        var ct = shutdown.Token;
        _ = Task.Run(async () =>
        {
            ChatImageService.Prepared? prepared = null;
            try
            {
                if (pairIdHash is null) throw new InvalidOperationException("this pairing isn't set up on the relay yet");
                prepared = await ChatImageService.PrepareAsync(sourcePath).ConfigureAwait(false);
                var (fileName, key) = await images.UploadAsync(pairIdHash, pairEpoch, imageId, prepared, ct).ConfigureAwait(false);
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    entry.ImageFile = fileName;
                    entry.ImageWidth = prepared.Width;
                    entry.ImageHeight = prepared.Height;
                    entry.Image = ChatImageState.Ready;
                    if (config.FindPairingById(pairingId) is not { IsPaired: true } live)
                    {
                        FailWith(conversation, entry, "the pairing ended before it was sent");
                        return;
                    }
                    var message = new ChatPlaintext
                    {
                        Kind = ChatPlaintext.ImageKind,
                        Text = sentCaption,
                        Image = new ChatImageRef { Id = imageId, Key = key, Width = prepared!.Width, Height = prepared.Height },
                    };
                    var (id, failure) = messenger.SendChat(live, message);
                    entry.MessageId = id;
                    if (failure is { } f) Fail(conversation, entry, f);
                    History.MarkDirty(conversation);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                var reason = ex is InvalidOperationException ? ex.Message : "the picture couldn't be sent";
                if (ex is not InvalidOperationException) Plugin.Log.Warning(ex, "Chat picture send failed.");
                // The prepared copy stays when the upload is what failed, so Retry can send it again.
                var kept = prepared is null ? null : ChatImageService.FileNameFor(imageId, prepared);
                var keptExists = ChatImageService.PathOf(kept) is { } keptPath && System.IO.File.Exists(keptPath);
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (keptExists && History.All.Any(c => c.Messages.Contains(entry)))
                    {
                        entry.ImageFile = kept;
                        entry.ImageWidth = prepared!.Width;
                        entry.ImageHeight = prepared.Height;
                        entry.Image = ChatImageState.Ready;
                    }
                    else
                    {
                        if (keptExists) ChatImageService.Delete(kept);
                        entry.Image = ChatImageState.Unavailable;
                    }
                    FailWith(conversation, entry, reason.TrimEnd('.'));
                }).ConfigureAwait(false);
            }
            finally
            {
                if (deleteSource)
                {
                    try { System.IO.File.Delete(sourcePath); }
                    catch (Exception ex) { Plugin.Log.Debug($"Snapshot for chat not deleted: {ex.Message}"); }
                }
            }
        }, ct);
    }

    /// Whether an outgoing message that wasn't delivered can be sent again: not while the pairing has ended, not while
    /// the partner still needs an update it failed for, and for a picture only while its local copy exists.
    public bool CanRetry(ChatConversation conversation, ChatEntry entry, IReadOnlyList<PairingState> pairings)
    {
        if (conversation.IsMatch)
            return entry is { Outgoing: true, State: ChatEntryState.NotDelivered, Special: null, Image: ChatImageState.None } && MatchFor(conversation) is { IsActive: true };
        if (!entry.Outgoing || entry.State != ChatEntryState.NotDelivered || SendPairing(pairings) is null)
            return false;
        if (entry.FailedNeedsUpdate && conversation.PeerNeedsUpdate)
            return false;
        return entry.Image == ChatImageState.None
            || entry.Image == ChatImageState.Ready && ChatImageService.PathOf(entry.ImageFile) is { } path && System.IO.File.Exists(path);
    }

    /// Sends a failed message again as a new one with its own id and delivery state; the failed one stays as it is.
    /// The text goes through the Gagged rewrite again like anything typed now. A picture is re-encrypted under a new
    /// id and key from its kept copy, which is then gone from the failed message. Null, or why it couldn't.
    public string? Retry(ChatConversation conversation, ChatEntry entry, IReadOnlyList<PairingState> pairings)
    {
        if (conversation.IsMatch)
            return CanRetry(conversation, entry, pairings) && MatchFor(conversation) is { } match ? SendMatch(match, entry.Text) : "That message can't be sent again.";
        if (!CanRetry(conversation, entry, pairings) || SendPairing(pairings) is not { } pairing)
            return "That message can't be sent again.";
        if (entry.Image == ChatImageState.None)
            return Send(pairing, entry.Text);

        var source = ChatImageService.PathOf(entry.ImageFile)!;
        entry.ImageFile = null;
        entry.Image = ChatImageState.Unavailable;
        History.MarkDirty(conversation);
        SendImage(pairing, source, entry.Text, deleteSource: true);
        return null;
    }

    /// Fetches a received picture unless that's already running or failed a moment ago.
    public void RetryImage(ChatEntry entry)
    {
        if (entry.Image != ChatImageState.Downloading || entry.ImageKey is null || entry.ImagePairIdHash is null || entry.ImageId is null) return;
        if (downloading.Contains(entry.LocalId)) return;
        if (retryAfter.TryGetValue(entry.LocalId, out var after) && DateTime.UtcNow < after) return;
        downloading.Add(entry.LocalId);

        var image = new ChatImageRef { Id = entry.ImageId, Key = entry.ImageKey, Width = entry.ImageWidth, Height = entry.ImageHeight };
        var pairIdHash = entry.ImagePairIdHash;
        var pairEpoch = entry.ImagePairEpoch;
        var ct = shutdown.Token;
        _ = Task.Run(async () =>
        {
            var (outcome, fileName) = await images.DownloadAsync(pairIdHash, pairEpoch, image, ct).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                downloading.Remove(entry.LocalId);
                var conversation = History.All.FirstOrDefault(c => c.Messages.Contains(entry));
                switch (outcome)
                {
                    case ChatImageService.FetchOutcome.Saved:
                        entry.ImageFile = fileName;
                        entry.Image = ChatImageState.Ready;
                        entry.ImageKey = null;
                        break;
                    case ChatImageService.FetchOutcome.Gone:
                        entry.Image = ChatImageState.Unavailable;
                        entry.ImageKey = null;
                        break;
                    default:
                        retryAfter[entry.LocalId] = DateTime.UtcNow.AddMinutes(1);
                        break;
                }
                if (conversation is not null) History.MarkDirty(conversation);
            }).ConfigureAwait(false);
        }, ct);
    }

    public void Tick()
    {
        var now = DateTime.UtcNow;
        if (now >= nextExpiryCheckUtc)
        {
            nextExpiryCheckUtc = now.AddMinutes(1);
            var cutoff = DateTimeOffset.UtcNow.Add(-WaitingExpiry).ToUnixTimeSeconds();
            foreach (var conversation in History.All)
                foreach (var entry in conversation.Messages.Where(e => e.State is ChatEntryState.Waiting or ChatEntryState.Sending && e.At < cutoff))
                {
                    entry.State = ChatEntryState.NotDelivered;
                    entry.Reason = "it waited 7 days without being picked up";
                    History.MarkDirty(conversation);
                }
        }
        if (now >= nextRetryScanUtc)
        {
            nextRetryScanUtc = now.AddSeconds(1);
            foreach (var conversation in History.All)
                foreach (var entry in conversation.Messages)
                    if (entry.Image == ChatImageState.Downloading) RetryImage(entry);
        }
        History.Tick();
    }

    private void OnReceived(ChatReceived received)
    {
        var conversation = History.For(received.Pairing);
        conversation.PeerNeedsUpdate = false;
        Reopen(conversation);
        var message = received.Message;
        var entry = new ChatEntry
        {
            Text = message.Text,
            At = received.SentAt,
            State = ChatEntryState.Received,
        };
        if (message.Image is { } image)
        {
            entry.Image = ChatImageState.Downloading;
            entry.ImageId = image.Id;
            entry.ImageKey = image.Key;
            entry.ImageWidth = image.Width;
            entry.ImageHeight = image.Height;
            entry.ImagePairIdHash = received.Pairing.PairIdHash;
            entry.ImagePairEpoch = received.Pairing.PairEpoch;
        }
        History.Add(conversation, entry);
        if (entry.Image == ChatImageState.Downloading) RetryImage(entry);
        if (FocusedPairingId == conversation.PairingId) return;

        conversation.Unread++;
        Notify(conversation, message.Image is null ? message.Text : message.Text.Length > 0 ? $"[Picture] {message.Text}" : "Sent you a picture");
    }

    private void Notify(ChatConversation conversation, string text)
    {
        var preview = text.Length > 120 ? text[..120] + "..." : text;
        var notification = Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = $"Chat from {conversation.PeerName}",
            Content = preview,
            Type = NotificationType.Info,
            Minimized = false,
        });
        notification.Click += _ => RequestOpen(conversation.PairingId);

        if (DateTime.UtcNow >= nextSoundUtc)
        {
            nextSoundUtc = DateTime.UtcNow.AddSeconds(2);
            UI.GameSound.Play(config.ChatSound);
        }
    }

    private void OnResult(string messageId, ChatDelivery delivery)
    {
        if (History.FindByMessageId(messageId) is not var (conversation, entry)) return;
        // A late "delivered" can follow a local timeout when the relay was only slow to answer.
        if (entry.State == ChatEntryState.Delivered) return;
        switch (delivery)
        {
            case ChatDelivery.Delivered:
                entry.State = ChatEntryState.Delivered;
                entry.Reason = null;
                conversation.PeerNeedsUpdate = false;
                break;
            case ChatDelivery.Stored:
                entry.State = ChatEntryState.Waiting;
                entry.Reason = null;
                conversation.PeerNeedsUpdate = false;
                break;
            default:
                Fail(conversation, entry, delivery);
                break;
        }
        History.MarkDirty(conversation);
    }

    private void FailWith(ChatConversation conversation, ChatEntry entry, string reason)
    {
        entry.State = ChatEntryState.NotDelivered;
        entry.Reason = reason;
        History.MarkDirty(conversation);
    }

    private static void Fail(ChatConversation conversation, ChatEntry entry, ChatDelivery delivery)
    {
        entry.State = ChatEntryState.NotDelivered;
        entry.FailedNeedsUpdate = delivery == ChatDelivery.NeedsUpdate;
        if (delivery == ChatDelivery.NeedsUpdate) conversation.PeerNeedsUpdate = true;
        entry.Reason = delivery switch
        {
            ChatDelivery.Expired => "it waited 7 days without being picked up",
            ChatDelivery.MailboxFull => $"{conversation.PeerName}'s mailbox is full - 200 of your messages are already waiting",
            ChatDelivery.NeedsUpdate => $"{conversation.PeerName} needs to update Oathbound to chat",
            ChatDelivery.OverLimit => "you're sending too fast - wait a moment",
            ChatDelivery.OverBudget => "the relay's chat budget for today is spent - try again tomorrow",
            ChatDelivery.RelayUnreachable => "the relay couldn't be reached",
            _ => "the relay refused it",
        };
    }
}
