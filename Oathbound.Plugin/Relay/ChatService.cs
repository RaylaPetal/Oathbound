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
        History.Tick(flush: true);
    }

    public int TotalUnread => History.All.Sum(c => c.Unread);

    public int UnreadFor(Guid pairingId) => History.Find(pairingId)?.Unread ?? 0;

    /// One per partner, active ones first (in config order), then ended ones that still have history. Pairings lists
    /// every active pairing with that partner (Owner and Sub of each other gives two); empty once they've all ended.
    public IReadOnlyList<(ChatConversation Conversation, IReadOnlyList<PairingState> Pairings)> Conversations()
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
        foreach (var ended in History.All.Where(c => c.Messages.Count > 0 && list.All(l => l.Conversation != c)).ToList())
            result.Add((ended, Array.Empty<PairingState>()));
        return result;
    }

    /// The pairing a message to this partner goes out on: the active pairing when it's one of theirs.
    public PairingState? SendPairing(IReadOnlyList<PairingState> pairings) =>
        pairings.FirstOrDefault(p => p.Id == config.ActivePairing?.Id) ?? pairings.FirstOrDefault();

    /// The conversation a pairing's chat lives in (the partner's), by its conversation id.
    public Guid ConversationIdFor(Guid pairingId) =>
        config.FindPairingById(pairingId) is { IsPaired: true } pairing ? History.For(pairing).PairingId : pairingId;

    public void RequestOpen(Guid pairingId) => OpenRequested?.Invoke(ConversationIdFor(pairingId));

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
            try
            {
                if (pairIdHash is null) throw new InvalidOperationException("this pairing isn't set up on the relay yet");
                var prepared = await ChatImageService.PrepareAsync(sourcePath).ConfigureAwait(false);
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
                        Image = new ChatImageRef { Id = imageId, Key = key, Width = prepared.Width, Height = prepared.Height },
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
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    entry.Image = ChatImageState.Unavailable;
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
        var text = message.Image is null ? message.Text : message.Text.Length > 0 ? $"[Picture] {message.Text}" : "Sent you a picture";
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
