using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// What happened to one sent frame. Held is followed later by Delivered or Offline.
public enum PeerDelivery
{
    Delivered,
    Held,
    Offline,
    NeedsUpdate,
    OverLimit,
    OverBudget,
    Refused,
    /// No live connection to send on, or the relay didn't answer.
    RelayUnreachable,
    /// The pairing can't send yet (no relay record or peer key).
    NotReady,
}

public readonly record struct PeerSendResult(Guid PairingId, string Kind, string Text, PeerDelivery Delivery);

/// The one way anything reaches a paired peer (collar/relay-messaging). Commands and notices are sealed for the pairing
/// and sent on the live connection; nothing here touches game chat. Main thread only.
public sealed class PeerMessenger
{
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(10);
    /// Accepted clock difference on top of the frame age limit.
    private const long SkewAllowanceSeconds = 30;

    private readonly PluginConfig config;
    private readonly DeviceIdentityService identity;
    private readonly RelaySocket socket;
    private readonly Dictionary<string, Pending> pending = new();
    private readonly List<SafetyRetry> safetyRetries = new();
    private readonly Dictionary<(Guid, int), byte[]> keys = new();
    private int nextId;

    private sealed record Pending(Guid PairingId, string Kind, string Text, DateTime SentUtc)
    {
        public bool Held { get; set; }
    }

    private sealed record SafetyRetry(Guid PairingId, string Text, DateTime GiveUpUtc);

    public PeerMessenger(PluginConfig config, DeviceIdentityService identity, RelaySocket socket)
    {
        this.config = config;
        this.identity = identity;
        this.socket = socket;
        socket.MessageReceived += OnMessage;
        socket.StateChanged += OnStateChanged;
    }

    /// While set, nothing is sent: a local test runs the real dispatch but must never reach the peer.
    public bool Suppressed { get; set; }

    /// A frame accepted from a pairing's peer, with the confirmed-or-not character it names.
    public event Action<PairingState, PeerMessagePlaintext>? FrameAccepted;

    /// The relay says something on this pairing changed.
    public event Action<PairingState>? HintReceived;

    /// Every final (and held) result of a frame this install sent.
    public event Action<PeerSendResult>? SendResult;

    public bool Connected => socket.State == RelaySocketState.Connected;

    public PeerDelivery? SendCommand(PairingState pairing, string text) => Send(pairing, PeerMessagePlaintext.CommandKind, text, safety: false);

    public PeerDelivery? SendNotice(PairingState pairing, string text) => Send(pairing, PeerMessagePlaintext.NoticeKind, text, safety: false);

    /// The safeword ping: retried until accepted or too old to matter, and never held back by send limits.
    public void SendSafetyNotice(PairingState pairing, string text)
    {
        if (Suppressed) return;
        if (Send(pairing, PeerMessagePlaintext.NoticeKind, text, safety: true) == PeerDelivery.RelayUnreachable)
            safetyRetries.Add(new SafetyRetry(pairing.Id, text, DateTime.UtcNow.AddSeconds(PeerMessageCrypto.MaxAgeSeconds)));
    }

    /// Null when the frame went out and its result will follow through SendResult; otherwise why it couldn't.
    private PeerDelivery? Send(PairingState pairing, string kind, string text, bool safety)
    {
        if (Suppressed) return null;
        if (!pairing.IsPaired || pairing.PairIdHash is null || pairing.PeerPublicKeyX is null || pairing.PeerPublicKeyY is null)
            return Report(pairing, kind, text, PeerDelivery.NotReady);
        if (!Connected)
            return Report(pairing, kind, text, PeerDelivery.RelayUnreachable);

        var me = CodePairingService.CurrentCharacter(null);
        var header = new PeerFrameHeader(pairing.PairIdHash, pairing.PairEpoch, SideOf(pairing.Direction), pairing.NextOutgoingFrameSeq(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + socket.ServerOffsetSeconds);
        config.Save();
        var message = new PeerMessagePlaintext
        {
            Kind = kind,
            Text = text,
            Character = me is null ? null : new CharacterRecordEntry { Name = me.Name, World = me.World },
        };

        string nonce, ciphertext;
        try
        {
            (nonce, ciphertext) = PeerMessageCrypto.Seal(message, KeyFor(pairing), header);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DeviceIdentityUnavailableException)
        {
            Plugin.Log.Warning($"Frame not sent: {ex.Message}");
            return Report(pairing, kind, text, PeerDelivery.Refused);
        }

        var id = $"f{++nextId}";
        pending[id] = new Pending(pairing.Id, kind, text, DateTime.UtcNow);
        var wire = JsonSerializer.Serialize(new
        {
            type = "send",
            id,
            safety,
            pairIdHash = header.PairIdHash,
            pairEpoch = header.PairEpoch,
            from = header.From,
            seq = header.Seq,
            sentAt = header.SentAt,
            nonce,
            ciphertext,
        });
        _ = socket.SendAsync(wire).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully && task.Result) return;
            Plugin.Framework.RunOnFrameworkThread(() => Resolve(id, PeerDelivery.RelayUnreachable));
        });
        return null;
    }

    private PeerDelivery Report(PairingState pairing, string kind, string text, PeerDelivery delivery)
    {
        SendResult?.Invoke(new PeerSendResult(pairing.Id, kind, text, delivery));
        return delivery;
    }

    /// Main thread, once a second.
    public void Tick()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, p) in pending.Where(kv => !kv.Value.Held && now - kv.Value.SentUtc > AckTimeout).ToList())
            Resolve(id, PeerDelivery.RelayUnreachable);
        // A held frame resolves when the peer reconnects or the relay drops it; a lost update shouldn't linger.
        foreach (var (id, _) in pending.Where(kv => kv.Value.Held && now - kv.Value.SentUtc > TimeSpan.FromSeconds(PeerMessageCrypto.MaxAgeSeconds + 30)).ToList())
            Resolve(id, PeerDelivery.Offline);
        safetyRetries.RemoveAll(r => r.GiveUpUtc < now);
    }

    private void OnStateChanged(RelaySocketState next)
    {
        if (next != RelaySocketState.Connected || safetyRetries.Count == 0) return;
        var retries = safetyRetries.ToList();
        safetyRetries.Clear();
        foreach (var retry in retries)
            if (config.FindPairingById(retry.PairingId) is { IsPaired: true } pairing)
                SendSafetyNotice(pairing, retry.Text);
    }

    private void OnMessage(JsonElement message)
    {
        switch (message.TryGetProperty("type", out var type) ? type.GetString() : null)
        {
            case "ack":
                OnAck(message);
                break;
            case "frame":
                OnFrame(message);
                break;
            case "hint":
                if (message.TryGetProperty("pairIdHash", out var hash) && message.TryGetProperty("pairEpoch", out var epoch)
                    && epoch.TryGetInt32(out var e) && FindPairing(hash.GetString(), e) is { } hinted)
                    HintReceived?.Invoke(hinted);
                break;
        }
    }

    private void OnAck(JsonElement message)
    {
        var id = message.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var result = message.TryGetProperty("result", out var r) ? r.GetString() : null;
        if (id is null) return;
        var delivery = result switch
        {
            "delivered" => PeerDelivery.Delivered,
            "held" => PeerDelivery.Held,
            "offline" => PeerDelivery.Offline,
            "needs_update" => PeerDelivery.NeedsUpdate,
            "over_limit" => PeerDelivery.OverLimit,
            "over_budget" => PeerDelivery.OverBudget,
            _ => PeerDelivery.Refused,
        };
        Resolve(id, delivery);
    }

    private void Resolve(string id, PeerDelivery delivery)
    {
        if (!pending.TryGetValue(id, out var p)) return;
        if (delivery == PeerDelivery.Held)
        {
            if (p.Held) return;
            p.Held = true;
        }
        else
        {
            pending.Remove(id);
        }

        if (config.FindPairingById(p.PairingId) is { } pairing)
        {
            var state = delivery switch
            {
                PeerDelivery.Delivered => PeerRelayState.Reachable,
                PeerDelivery.Held or PeerDelivery.Offline => PeerRelayState.Offline,
                PeerDelivery.NeedsUpdate => PeerRelayState.NeedsUpdate,
                _ => pairing.PeerRelayState,
            };
            SetRelayState(pairing, state);
        }
        SendResult?.Invoke(new PeerSendResult(p.PairingId, p.Kind, p.Text, delivery));
    }

    public void SetRelayState(PairingState pairing, PeerRelayState state)
    {
        if (pairing.PeerRelayState == state) return;
        pairing.PeerRelayState = state;
        pairing.PeerRelayStateAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        config.Save();
    }

    private void OnFrame(JsonElement frame)
    {
        if (!TryHeader(frame, out var header, out var nonce, out var ciphertext))
            return;
        var pairing = FindPairing(header.PairIdHash, header.PairEpoch);
        if (pairing is null)
        {
            Plugin.Log.Information("Frame ignored: no active pairing for it.");
            return;
        }
        // Only the other side's frames: one claiming our own side is a reflection of something we sent.
        if (header.From != SideOf(pairing.Direction == PairingDirection.OwnerSide ? PairingDirection.SubSide : PairingDirection.OwnerSide))
        {
            Plugin.Log.Warning("Frame ignored: it claims this install's own side.");
            return;
        }
        if (header.Seq <= pairing.LastAcceptedFrameSeq)
        {
            Plugin.Log.Warning("Frame ignored: replayed or out of order.");
            return;
        }
        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + socket.ServerOffsetSeconds - header.SentAt;
        if (Math.Abs(age) > PeerMessageCrypto.MaxAgeSeconds + SkewAllowanceSeconds)
        {
            Plugin.Log.Warning($"Frame ignored: sent {age}s ago.");
            return;
        }

        PeerMessagePlaintext? message;
        try
        {
            message = PeerMessageCrypto.Open(KeyFor(pairing), header, nonce, ciphertext);
        }
        catch (DeviceIdentityUnavailableException)
        {
            return;
        }
        if (message is null)
        {
            Plugin.Log.Warning("Frame ignored: it doesn't decrypt for this pairing.");
            return;
        }

        pairing.LastAcceptedFrameSeq = header.Seq;
        SetRelayState(pairing, PeerRelayState.Reachable);
        if (message.Character is { } c && pairing.FindPeerCharacter(c.Name, c.World) is { State: PeerCharacterState.Confirmed } known)
        {
            known.LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            pairing.ResolveTarget();
        }
        config.Save();
        FrameAccepted?.Invoke(pairing, message);
    }

    private static bool TryHeader(JsonElement frame, out PeerFrameHeader header, out string nonce, out string ciphertext)
    {
        header = default;
        nonce = ciphertext = "";
        try
        {
            header = new PeerFrameHeader(
                frame.GetProperty("pairIdHash").GetString()!,
                frame.GetProperty("pairEpoch").GetInt32(),
                frame.GetProperty("from").GetString()!,
                frame.GetProperty("seq").GetInt64(),
                frame.GetProperty("sentAt").GetInt64());
            nonce = frame.GetProperty("nonce").GetString()!;
            ciphertext = frame.GetProperty("ciphertext").GetString()!;
            return header.PairIdHash is not null && nonce is not null && ciphertext is not null;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private PairingState? FindPairing(string? pairIdHash, int pairEpoch) =>
        pairIdHash is null ? null : config.Pairings.FirstOrDefault(p => p.IsPaired && p.PairIdHash == pairIdHash && p.PairEpoch == pairEpoch);

    private static string SideOf(PairingDirection direction) =>
        direction == PairingDirection.OwnerSide ? CharacterRecordCrypto.OwnerSide : CharacterRecordCrypto.SubSide;

    private byte[] KeyFor(PairingState pairing)
    {
        if (keys.TryGetValue((pairing.Id, pairing.PairEpoch), out var key)) return key;
        var peer = new EcPublicKeyJwk { Kty = "EC", Crv = "P-256", X = pairing.PeerPublicKeyX!, Y = pairing.PeerPublicKeyY! };
        key = PeerMessageCrypto.DeriveKey(identity.GetSigningKey(), peer, pairing.PairIdHash!, pairing.PairEpoch);
        keys[(pairing.Id, pairing.PairEpoch)] = key;
        return key;
    }

    /// Ends every derived key for a pairing that ended or changed epoch.
    public void Forget(PairingState pairing)
    {
        foreach (var k in keys.Keys.Where(k => k.Item1 == pairing.Id).ToList())
        {
            Array.Clear(keys[k]);
            keys.Remove(k);
        }
    }
}
