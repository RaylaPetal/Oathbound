using System;
using System.Collections.Generic;
using System.Linq;
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

    public ChatHistoryStore History { get; } = new();

    /// Set each frame by the chat window to the conversation (its PairingId) it shows while focused, so its messages
    /// don't toast.
    public Guid? FocusedPairingId { get; set; }

    /// Asks the chat window to open on a conversation (a toast or a Chat button was clicked).
    public event Action<Guid>? OpenRequested;

    public ChatService(PluginConfig config, PeerMessenger messenger, ChatGagService gag)
    {
        this.config = config;
        this.messenger = messenger;
        this.gag = gag;
        messenger.ChatReceived += OnReceived;
        messenger.ChatResult += OnResult;
        // Sent just before the last unload and never answered; a receipt that arrives later still corrects it.
        foreach (var conversation in History.All)
            foreach (var entry in conversation.Messages.Where(e => e.State == ChatEntryState.Sending))
            {
                entry.State = ChatEntryState.NotDelivered;
                entry.Reason = "Oathbound closed before the relay answered";
                History.MarkDirty(conversation);
            }
    }

    public void Dispose()
    {
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
        var (id, failure) = messenger.SendChat(pairing, sent);
        entry.MessageId = id;
        if (failure is { } f) Fail(conversation, entry, f);
        History.Add(conversation, entry);
        return null;
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
        History.Tick();
    }

    private void OnReceived(ChatReceived received)
    {
        var conversation = History.For(received.Pairing);
        conversation.PeerNeedsUpdate = false;
        History.Add(conversation, new ChatEntry
        {
            Text = received.Text,
            At = received.SentAt,
            State = ChatEntryState.Received,
        });
        if (FocusedPairingId == conversation.PairingId) return;

        conversation.Unread++;
        var preview = received.Text.Length > 120 ? received.Text[..120] + "..." : received.Text;
        var notification = Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = $"Chat from {conversation.PeerName}",
            Content = preview,
            Type = NotificationType.Info,
            Minimized = false,
        });
        notification.Click += _ => RequestOpen(conversation.PairingId);
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
