using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

public enum ChatEntryState
{
    Sending,
    /// Stored on the relay for a partner who's away.
    Waiting,
    Delivered,
    NotDelivered,
    Received,
}

public sealed class ChatEntry
{
    public Guid LocalId { get; set; } = Guid.NewGuid();
    /// The relay message id of an outgoing message, which later acks and receipts name.
    public string? MessageId { get; set; }
    public bool Outgoing { get; set; }
    public string Text { get; set; } = "";
    /// Unix seconds: when this install sent it, or when the partner sent it.
    public long At { get; set; }
    public ChatEntryState State { get; set; }
    /// Why an outgoing message wasn't delivered.
    public string? Reason { get; set; }

    /// A picture message: Text is then its caption, possibly empty.
    public ChatImageState Image { get; set; }
    public string? ImageId { get; set; }
    /// A received picture's key, kept only until it's downloaded.
    public string? ImageKey { get; set; }
    /// The pairing a received picture is fetched through.
    public string? ImagePairIdHash { get; set; }
    public int ImagePairEpoch { get; set; }
    /// Under chat/images once it's on this computer.
    public string? ImageFile { get; set; }
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
}

public enum ChatImageState
{
    None,
    /// Being shrunk, encrypted and uploaded before the message goes out.
    Preparing,
    Downloading,
    Ready,
    /// Expired on the relay, or couldn't be opened.
    Unavailable,
}

/// One per partner: someone who is both your Owner and your Sub is one conversation, keyed by their device key.
public sealed class ChatConversation
{
    /// The pairing the conversation was first created for; also its file name.
    public Guid PairingId { get; set; }
    /// The partner's install; pairings with the same one share this conversation.
    public string? PeerDeviceKeyId { get; set; }
    /// Kept so an ended pairing's conversation still has a name.
    public string PeerName { get; set; } = "";
    public string PeerWorld { get; set; } = "";
    public PairingDirection Direction { get; set; }
    public int Unread { get; set; }
    /// The relay last said the partner can't receive chat yet.
    public bool PeerNeedsUpdate { get; set; }
    public List<ChatEntry> Messages { get; set; } = new();
}

/// Chat history, one file per pairing under the config folder, so a busy chat never rewrites the main config.
/// Local only: never uploaded or backed up. Main thread only; file writes happen off it.
public sealed class ChatHistoryStore
{
    public const int MaxMessages = 1000;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private readonly Dictionary<Guid, ChatConversation> conversations = new();
    private readonly Dictionary<Guid, DateTime> dirty = new();
    private Task writes = Task.CompletedTask;

    private static string Folder => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "chat");
    private static string FileFor(Guid pairingId) => Path.Combine(Folder, $"{pairingId:N}.json");

    public ChatHistoryStore()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
            {
                try
                {
                    var conversation = JsonSerializer.Deserialize<ChatConversation>(File.ReadAllText(file));
                    if (conversation is not null && conversation.PairingId != Guid.Empty)
                        conversations[conversation.PairingId] = conversation;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, $"Chat history {Path.GetFileName(file)} couldn't be read; it's skipped.");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Chat history folder couldn't be read.");
        }
    }

    public IReadOnlyCollection<ChatConversation> All => conversations.Values;

    public ChatConversation? Find(Guid pairingId) => conversations.GetValueOrDefault(pairingId);

    /// The partner's conversation, created on first use. Older per-pairing conversations with the same partner are
    /// merged into one here.
    public ChatConversation For(PairingState pairing)
    {
        var matches = conversations.Values
            .Where(c => c.PairingId == pairing.Id || (pairing.PeerDeviceKeyId is { } key && c.PeerDeviceKeyId == key))
            .OrderBy(c => c.PairingId == pairing.Id ? 1 : 0)
            .ThenBy(c => c.PairingId)
            .ToList();
        ChatConversation conversation;
        if (matches.Count == 0)
        {
            conversation = new ChatConversation { PairingId = pairing.Id, Direction = pairing.Direction };
            conversations[pairing.Id] = conversation;
        }
        else
        {
            conversation = matches[0];
            foreach (var other in matches.Skip(1))
            {
                conversation.Messages.AddRange(other.Messages);
                conversation.Unread += other.Unread;
                Clear(other.PairingId, deleteImages: false);
            }
            if (matches.Count > 1)
            {
                conversation.Messages.Sort((a, b) => a.At.CompareTo(b.At));
                if (conversation.Messages.Count > MaxMessages)
                    conversation.Messages.RemoveRange(0, conversation.Messages.Count - MaxMessages);
                MarkDirty(conversation);
            }
        }
        if (conversation.PeerDeviceKeyId is null && pairing.PeerDeviceKeyId is not null)
        {
            conversation.PeerDeviceKeyId = pairing.PeerDeviceKeyId;
            MarkDirty(conversation);
        }
        if (!string.IsNullOrWhiteSpace(pairing.PeerName) && (conversation.PeerName != pairing.PeerName || conversation.PeerWorld != pairing.PeerWorld))
        {
            conversation.PeerName = pairing.PeerName!;
            conversation.PeerWorld = pairing.PeerWorld ?? "";
            MarkDirty(conversation);
        }
        return conversation;
    }

    public void Add(ChatConversation conversation, ChatEntry entry)
    {
        conversation.Messages.Add(entry);
        if (conversation.Messages.Count > MaxMessages)
        {
            var dropped = conversation.Messages.Count - MaxMessages;
            foreach (var old in conversation.Messages.Take(dropped)) ChatImageService.Delete(old.ImageFile);
            conversation.Messages.RemoveRange(0, dropped);
        }
        MarkDirty(conversation);
    }

    public (ChatConversation Conversation, ChatEntry Entry)? FindByMessageId(string messageId)
    {
        foreach (var conversation in conversations.Values)
            for (var i = conversation.Messages.Count - 1; i >= 0; i--)
                if (conversation.Messages[i].MessageId == messageId)
                    return (conversation, conversation.Messages[i]);
        return null;
    }

    public void MarkDirty(ChatConversation conversation) => dirty[conversation.PairingId] = DateTime.UtcNow;

    public void Clear(Guid pairingId, bool deleteImages = true)
    {
        if (deleteImages && conversations.TryGetValue(pairingId, out var cleared))
            foreach (var file in cleared.Messages.Select(m => m.ImageFile).Where(f => f is not null).ToList())
                ChatImageService.Delete(file);
        conversations.Remove(pairingId);
        dirty.Remove(pairingId);
        var path = FileFor(pairingId);
        writes = writes.ContinueWith(_ =>
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Plugin.Log.Warning(ex, "Chat history file couldn't be deleted."); }
        }, TaskScheduler.Default);
    }

    /// Main thread, about once a second; `flush` writes everything pending now (unload).
    public void Tick(bool flush = false)
    {
        var now = DateTime.UtcNow;
        foreach (var (id, changed) in dirty.ToList())
        {
            if (!flush && now - changed < SaveDelay) continue;
            dirty.Remove(id);
            if (!conversations.TryGetValue(id, out var conversation)) continue;
            // Serialized here, on the main thread, so the background write never sees a list being changed.
            var json = JsonSerializer.Serialize(conversation);
            var path = FileFor(id);
            writes = writes.ContinueWith(_ => Write(path, json), TaskScheduler.Default);
        }
        if (flush) writes.Wait(TimeSpan.FromSeconds(5));
    }

    private static void Write(string path, string json)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Chat history couldn't be saved.");
        }
    }
}
