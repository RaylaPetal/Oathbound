using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.Commands;

public enum CommandOutcomeKind
{
    /// Delivered to a Sub that confirms commands; its receipt hasn't arrived yet.
    Waiting,
    Applied,
    Refused,
    /// Delivered, but no receipt came in time; treated as applied until a late receipt says otherwise.
    NotConfirmed,
    /// Delivered to a Sub whose version doesn't confirm commands: treated as applied, as before receipts.
    Legacy,
}

/// One step in a delivered command's life. `Previous` is what was raised for it before (Waiting for a first answer);
/// an Applied or Refused after NotConfirmed or Legacy is a late receipt changing what was already shown.
public readonly record struct CommandOutcome(Guid PairingId, long Seq, string Text, CommandOutcomeKind Kind, CommandOutcomeKind Previous, RefusalCode Code = RefusalCode.Other)
{
    public bool Late => Previous is CommandOutcomeKind.NotConfirmed or CommandOutcomeKind.Legacy;
}

/// Owner side: turns delivered commands and the Sub's apply receipts into one outcome per command, which every Owner
/// surface (feedback line, estimate, banners, toy status, cage) follows. In memory only. Main thread only.
public sealed class OwnerReceiptTracker : IDisposable
{
    public static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(15);
    /// A receipt can't be later than the command frame's age limit plus its own; anything older is forgotten.
    private static readonly TimeSpan LateWindow = TimeSpan.FromSeconds(150);
    /// A receipt can beat the relay's "delivered" ack: a held frame is flushed to the Sub before the hub reports it
    /// delivered. Such a receipt waits this long for its command to be tracked.
    private static readonly TimeSpan EarlyWindow = TimeSpan.FromSeconds(30);
    private const int EarlyMax = 200;

    private sealed class Tracked
    {
        public required Guid PairingId { get; init; }
        public required int Epoch { get; init; }
        public required long Seq { get; init; }
        public required string Text { get; init; }
        public required DateTime DeliveredUtc { get; init; }
        public CommandOutcomeKind State { get; set; }
    }

    private readonly PluginConfig config;
    private readonly PeerMessenger messenger;
    private readonly Dictionary<(Guid, long), Tracked> tracked = new();
    private readonly Dictionary<(Guid, long), (int Epoch, RefusalCode? Refused, DateTime ReceivedUtc)> early = new();

    public OwnerReceiptTracker(PluginConfig config, PeerMessenger messenger)
    {
        this.config = config;
        this.messenger = messenger;
        messenger.SendResult += OnSendResult;
    }

    public void Dispose() => messenger.SendResult -= OnSendResult;

    public event Action<CommandOutcome>? CommandOutcome;

    private void OnSendResult(PeerSendResult result)
    {
        if (result.Kind != PeerMessagePlaintext.CommandKind || result.Delivery != PeerDelivery.Delivered || result.Seq <= 0)
            return;
        if (config.FindPairingById(result.PairingId) is not { IsPaired: true, Direction: PairingDirection.OwnerSide } pairing || result.Epoch != pairing.PairEpoch)
            return;
        var state = pairing.PeerSendsReceipts ? CommandOutcomeKind.Waiting : CommandOutcomeKind.Legacy;
        var t = new Tracked
        {
            PairingId = pairing.Id,
            Epoch = result.Epoch,
            Seq = result.Seq,
            Text = result.Text.Trim(),
            DeliveredUtc = DateTime.UtcNow,
            State = state,
        };
        tracked[(pairing.Id, result.Seq)] = t;
        Raise(t, state, CommandOutcomeKind.Waiting);

        if (early.Remove((pairing.Id, result.Seq), out var waiting) && waiting.Epoch == pairing.PairEpoch)
            Answer(pairing, t, waiting.Refused);
    }

    public void Tick()
    {
        var now = DateTime.UtcNow;
        if (early.Count > 0)
            foreach (var key in early.Where(e => now - e.Value.ReceivedUtc > EarlyWindow).Select(e => e.Key).ToList())
                early.Remove(key);
        if (tracked.Count == 0)
            return;
        foreach (var (key, t) in tracked.ToList())
        {
            if (now - t.DeliveredUtc > LateWindow)
            {
                tracked.Remove(key);
                continue;
            }
            if (t.State == CommandOutcomeKind.Waiting && now - t.DeliveredUtc >= ConfirmTimeout)
            {
                t.State = CommandOutcomeKind.NotConfirmed;
                Raise(t, CommandOutcomeKind.NotConfirmed, CommandOutcomeKind.Waiting);
            }
        }
    }

    /// `body` is the notice after its keyword. The messenger already checked it came from this pairing's Sub device,
    /// unreplayed and fresh; each entry must also name a command this client sent in the current epoch and hasn't had
    /// answered.
    public void OnReceipt(PairingState pairing, string body)
    {
        foreach (var token in body.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(CommandComposer.ReceiptMaxEntries))
        {
            if (!TryParse(token, out var seq, out var refused))
            {
                Plugin.Log.Warning("Receipt entry ignored: malformed.");
                continue;
            }
            if (!tracked.TryGetValue((pairing.Id, seq), out var t) || t.Epoch != pairing.PairEpoch)
            {
                // Only for a command this client could still be waiting to hear "delivered" about: one it sent.
                if (seq <= pairing.OutgoingFrameSeq && early.Count < EarlyMax)
                {
                    early[(pairing.Id, seq)] = (pairing.PairEpoch, refused, DateTime.UtcNow);
                    Plugin.Log.Debug($"Receipt for command {seq} arrived before its delivered ack; holding it.");
                }
                else
                {
                    Plugin.Log.Warning($"Receipt for command {seq} ignored: not a command this client sent and is waiting on (already answered, too old, or never sent).");
                }
                continue;
            }
            Answer(pairing, t, refused);
        }
    }

    private void Answer(PairingState pairing, Tracked t, RefusalCode? refused)
    {
        tracked.Remove((t.PairingId, t.Seq));
        var previous = t.State;
        t.State = refused is null ? CommandOutcomeKind.Applied : CommandOutcomeKind.Refused;
        Raise(t, t.State, previous, refused ?? RefusalCode.Other);

        if (!pairing.PeerSendsReceipts)
        {
            pairing.PeerSendsReceipts = true;
            config.Save();
            Plugin.Log.Information($"{pairing.PeerName} confirms commands now (first receipt accepted).");
        }
    }

    private static bool TryParse(string token, out long seq, out RefusalCode? refused)
    {
        refused = null;
        var parts = token.Split(':');
        if (parts.Length < 2 || !long.TryParse(parts[0], out seq) || seq <= 0)
        {
            seq = 0;
            return false;
        }
        switch (parts[1])
        {
            case "a" when parts.Length == 2:
                return true;
            case "r" when parts.Length == 3:
                refused = RefusalCodes.FromWire(parts[2]);
                return true;
            default:
                return false;
        }
    }

    private void Raise(Tracked t, CommandOutcomeKind kind, CommandOutcomeKind previous, RefusalCode code = RefusalCode.Other)
    {
        try
        {
            CommandOutcome?.Invoke(new CommandOutcome(t.PairingId, t.Seq, t.Text, kind, previous, code));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "A command outcome handler threw.");
        }
    }
}
