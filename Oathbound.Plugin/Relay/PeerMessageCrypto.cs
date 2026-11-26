using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// What a frame carries once decrypted. Must match protocol/schemas/peer-frame.schema.json#/$defs/plaintext.
public sealed class PeerMessagePlaintext
{
    public const string CommandKind = "command";
    public const string NoticeKind = "notice";

    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("kind")] public string Kind { get; set; } = CommandKind;
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("character"), JsonIgnore(Condition = JsonIgnoreCondition.Never)] public CharacterRecordEntry? Character { get; set; }
    /// Owner commands only: asks the Sub's client for an apply receipt. Only ever true or absent on the wire, so an
    /// older reader that doesn't know it just ignores it.
    [JsonPropertyName("receipt"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Receipt { get; set; }

    public bool AsksReceipt => Kind == CommandKind && Receipt == true;
}

/// A chat message once decrypted. Must match protocol/schemas/peer-frame.schema.json#/$defs/chatPlaintext.
public sealed class ChatPlaintext
{
    public const string ChatKind = "chat";
    public const string ImageKind = "image";

    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("kind")] public string Kind { get; set; } = ChatKind;
    /// The message, or an image's optional caption.
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("image"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ChatImageRef? Image { get; set; }

    public bool IsWellFormed => Version == 1 && Text.Length <= PeerMessageCrypto.MaxChatChars && Kind switch
    {
        ChatKind => Text.Length > 0 && Image is null,
        ImageKind => Image is { } i && i.IsWellFormed,
        _ => false,
    };
}

/// A match chat message once decrypted. Must match protocol/schemas/peer-frame.schema.json#/$defs/matchPlaintext.
public sealed class MatchPlaintext
{
    public const string TextKind = "text";
    public const string CharacterKind = "character";
    public const string InviteKind = "invite";

    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("kind")] public string Kind { get; set; } = TextKind;
    [JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Text { get; set; }
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; set; }
    [JsonPropertyName("world"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? World { get; set; }
    [JsonPropertyName("code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Code { get; set; }
    /// What the receiver of an invite would become: "owner" or "sub".
    [JsonPropertyName("role"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Role { get; set; }

    public bool IsWellFormed => Version == 1 && Kind switch
    {
        TextKind => Text is { Length: > 0 and <= PeerMessageCrypto.MaxChatChars } && Name is null && Code is null,
        CharacterKind => Name is { Length: > 0 and <= 32 } && World is { Length: > 0 and <= 32 } && Text is null && Code is null,
        InviteKind => Code is { Length: >= 16 and <= 24 } && Role is "owner" or "sub" && Text is null && Name is null,
        _ => false,
    };
}

/// Where a chat picture is on the relay and the key that opens it; only ever sent inside a sealed chat message.
public sealed class ChatImageRef
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }

    public bool IsWellFormed =>
        ChatImageCrypto.IsImageId(Id) && Key.Length == 43 && Width is >= 1 and <= ChatImageCrypto.MaxSidePixels && Height is >= 1 and <= ChatImageCrypto.MaxSidePixels;
}

/// protocol/constants.json `peerChat.image`; protocol/vectors/crypto-vectors.json `chatImage` is the reference.
public static class ChatImageCrypto
{
    public const int MaxCiphertextBytes = 524288;
    public const int MaxPlaintextBytes = MaxCiphertextBytes - 12 - 16;
    public const int MaxSidePixels = 1600;

    public static string NewImageId() => RelayCrypto.Base64UrlEncode(RelayCrypto.RandomBytes(16));

    public static bool IsImageId(string id) =>
        id.Length == 22 && id.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    private static byte[] Aad(string imageId) => Encoding.UTF8.GetBytes("oathbound-chat-image-v1|" + imageId);

    /// A fresh key per picture: the blob alone is useless to the relay or anyone who isn't in the chat.
    public static (string Key, byte[] Blob) Encrypt(byte[] image, string imageId)
    {
        var key = RelayCrypto.RandomBytes(32);
        var nonce = RelayCrypto.RandomBytes(RelayCrypto.AeadNonceLengthBytes);
        var ciphertext = RelayCrypto.AesGcmEncrypt(key, nonce, image, Aad(imageId));
        var blob = new byte[nonce.Length + ciphertext.Length];
        nonce.CopyTo(blob, 0);
        ciphertext.CopyTo(blob, nonce.Length);
        var encodedKey = RelayCrypto.Base64UrlEncode(key);
        Array.Clear(key);
        return (encodedKey, blob);
    }

    /// Null when the blob doesn't open with this key and id.
    public static byte[]? Decrypt(byte[] blob, string key, string imageId)
    {
        try
        {
            if (blob.Length <= RelayCrypto.AeadNonceLengthBytes) return null;
            return RelayCrypto.AesGcmDecrypt(RelayCrypto.Base64UrlDecode(key), blob[..RelayCrypto.AeadNonceLengthBytes], blob[RelayCrypto.AeadNonceLengthBytes..], Aad(imageId));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}

/// The frame fields bound into the AEAD tag; they travel in the clear so the receiver can rebuild the AAD.
public readonly record struct PeerFrameHeader(string PairIdHash, int PairEpoch, string From, long Seq, long SentAt);

/// protocol/constants.json `peerMessage`; protocol/vectors/crypto-vectors.json `peerMessage` is the reference.
public static class PeerMessageCrypto
{
    public const int MaxTextBytes = 4096;
    public const int MaxAgeSeconds = 120;
    private const string InfoLabel = "oathbound-relay-message-v1";

    public static byte[] DeriveKey(RelayEcKeyPair ownKey, EcPublicKeyJwk peerPublic, string pairIdHash, int pairEpoch) =>
        CharacterRecordCrypto.DerivePairKey(ownKey, peerPublic, pairIdHash, pairEpoch, InfoLabel);

    public static byte[] Aad(PeerFrameHeader h) =>
        Encoding.UTF8.GetBytes(CanonicalJson.Serialize(new Dictionary<string, object?>
        {
            ["pairIdHash"] = h.PairIdHash,
            ["pairEpoch"] = h.PairEpoch,
            ["from"] = h.From,
            ["seq"] = h.Seq,
            ["sentAt"] = h.SentAt,
        }));

    public static (string Nonce, string Ciphertext) Seal(PeerMessagePlaintext message, byte[] key, PeerFrameHeader header)
    {
        if (Encoding.UTF8.GetByteCount(message.Text) > MaxTextBytes)
            throw new InvalidOperationException("That command is too long to send.");
        var nonce = RelayCrypto.RandomBytes(RelayCrypto.AeadNonceLengthBytes);
        var ciphertext = RelayCrypto.AesGcmEncrypt(key, nonce, JsonSerializer.SerializeToUtf8Bytes(message), Aad(header));
        return (RelayCrypto.Base64UrlEncode(nonce), RelayCrypto.Base64UrlEncode(ciphertext));
    }

    /// protocol/constants.json `peerChat`: its own key, so a chat ciphertext never opens as a command or the reverse.
    public const int MaxChatChars = 1000;
    private const string ChatInfoLabel = "oathbound-relay-chat-v1";

    public static byte[] DeriveChatKey(RelayEcKeyPair ownKey, EcPublicKeyJwk peerPublic, string pairIdHash, int pairEpoch) =>
        CharacterRecordCrypto.DerivePairKey(ownKey, peerPublic, pairIdHash, pairEpoch, ChatInfoLabel);

    public static byte[] ChatAad(PeerFrameHeader h) =>
        Encoding.UTF8.GetBytes(CanonicalJson.Serialize(new Dictionary<string, object?>
        {
            ["channel"] = "chat",
            ["pairIdHash"] = h.PairIdHash,
            ["pairEpoch"] = h.PairEpoch,
            ["from"] = h.From,
            ["seq"] = h.Seq,
            ["sentAt"] = h.SentAt,
        }));

    public static (string Nonce, string Ciphertext) SealChat(ChatPlaintext message, byte[] key, PeerFrameHeader header)
    {
        if (!message.IsWellFormed)
            throw new InvalidOperationException($"A chat message can be at most {MaxChatChars} characters.");
        var nonce = RelayCrypto.RandomBytes(RelayCrypto.AeadNonceLengthBytes);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(message);
        var ciphertext = RelayCrypto.AesGcmEncrypt(key, nonce, plaintext, ChatAad(header));
        return (RelayCrypto.Base64UrlEncode(nonce), RelayCrypto.Base64UrlEncode(ciphertext));
    }

    /// Null when it doesn't decrypt under this chat key and header, or isn't a well-formed chat message.
    public static ChatPlaintext? OpenChat(byte[] key, PeerFrameHeader header, string nonce, string ciphertext)
    {
        try
        {
            var plaintext = RelayCrypto.AesGcmDecrypt(key, RelayCrypto.Base64UrlDecode(nonce), RelayCrypto.Base64UrlDecode(ciphertext), ChatAad(header));
            var message = JsonSerializer.Deserialize<ChatPlaintext>(plaintext);
            return message is { IsWellFormed: true } ? message : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// protocol/constants.json `matchChat`: static ECDH between the two match keys, its own label, epoch always 0.
    private const string MatchInfoLabel = "oathbound-match-chat-v1";

    public static byte[] DeriveMatchKey(RelayEcKeyPair ownMatchKey, EcPublicKeyJwk peerMatchPublic, string matchId) =>
        CharacterRecordCrypto.DerivePairKey(ownMatchKey, peerMatchPublic, matchId, 0, MatchInfoLabel);

    public static byte[] MatchAad(string matchId, string from, long seq, long sentAt) =>
        Encoding.UTF8.GetBytes(CanonicalJson.Serialize(new Dictionary<string, object?>
        {
            ["channel"] = "match",
            ["matchId"] = matchId,
            ["from"] = from,
            ["seq"] = seq,
            ["sentAt"] = sentAt,
        }));

    public static (string Nonce, string Ciphertext) SealMatch(MatchPlaintext message, byte[] key, string matchId, string from, long seq, long sentAt)
    {
        if (!message.IsWellFormed)
            throw new InvalidOperationException($"A message can be at most {MaxChatChars} characters.");
        var nonce = RelayCrypto.RandomBytes(RelayCrypto.AeadNonceLengthBytes);
        var ciphertext = RelayCrypto.AesGcmEncrypt(key, nonce, JsonSerializer.SerializeToUtf8Bytes(message), MatchAad(matchId, from, seq, sentAt));
        return (RelayCrypto.Base64UrlEncode(nonce), RelayCrypto.Base64UrlEncode(ciphertext));
    }

    /// Null when it doesn't decrypt for this match and header, or isn't a kind this version knows.
    public static MatchPlaintext? OpenMatch(byte[] key, string matchId, string from, long seq, long sentAt, string nonce, string ciphertext)
    {
        try
        {
            var plaintext = RelayCrypto.AesGcmDecrypt(key, RelayCrypto.Base64UrlDecode(nonce), RelayCrypto.Base64UrlDecode(ciphertext), MatchAad(matchId, from, seq, sentAt));
            var message = JsonSerializer.Deserialize<MatchPlaintext>(plaintext);
            return message is { IsWellFormed: true } ? message : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// Null when it doesn't decrypt under this key and header, or isn't a well-formed message.
    public static PeerMessagePlaintext? Open(byte[] key, PeerFrameHeader header, string nonce, string ciphertext)
    {
        try
        {
            var plaintext = RelayCrypto.AesGcmDecrypt(key, RelayCrypto.Base64UrlDecode(nonce), RelayCrypto.Base64UrlDecode(ciphertext), Aad(header));
            var message = JsonSerializer.Deserialize<PeerMessagePlaintext>(plaintext);
            return message is { Version: 1, Kind: PeerMessagePlaintext.CommandKind or PeerMessagePlaintext.NoticeKind } && message.Text.Length > 0
                ? message
                : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
