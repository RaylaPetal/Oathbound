using System;
using System.Collections.Generic;
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
}

/// A chat message once decrypted. Must match protocol/schemas/peer-frame.schema.json#/$defs/chatPlaintext.
public sealed class ChatPlaintext
{
    public const string ChatKind = "chat";

    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("kind")] public string Kind { get; set; } = ChatKind;
    [JsonPropertyName("text")] public string Text { get; set; } = "";
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

    public static (string Nonce, string Ciphertext) SealChat(string text, byte[] key, PeerFrameHeader header)
    {
        if (text.Length is 0 or > MaxChatChars)
            throw new InvalidOperationException($"A chat message can be at most {MaxChatChars} characters.");
        var nonce = RelayCrypto.RandomBytes(RelayCrypto.AeadNonceLengthBytes);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new ChatPlaintext { Text = text });
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
            return message is { Version: 1, Kind: ChatPlaintext.ChatKind } && message.Text.Length is > 0 and <= MaxChatChars ? message : null;
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
