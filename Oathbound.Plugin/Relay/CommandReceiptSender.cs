using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

/// Sub side: answers each Owner command frame that asked for an apply receipt. Receipts handled close together go out
/// as one notice per pairing, at most about once a second, so a burst of commands isn't answered by a burst of frames.
/// Main thread only.
public sealed class CommandReceiptSender : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);
    /// The relay's burst window; a retry sooner would be refused again.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    /// Past this the Owner has stopped waiting, and the command frame itself could no longer be delivered.
    private static readonly TimeSpan RetryMaxAge = TimeSpan.FromSeconds(100);

    private sealed record Entry(long Seq, RefusalCode? Refused, DateTime AtUtc, bool Retried);

    private sealed class PairQueue
    {
        public readonly List<Entry> Waiting = new();
        public DateTime LastFlushUtc = DateTime.MinValue;
        public DateTime NotBeforeUtc = DateTime.MinValue;
    }

    private readonly PluginConfig config;
    private readonly PeerMessenger messenger;
    private readonly CommandComposer composer;
    private readonly Dictionary<Guid, PairQueue> queues = new();
    /// Receipt frames still waiting for their result, by pairing and frame seq.
    private readonly Dictionary<(Guid, long), List<Entry>> inFlight = new();

    public CommandReceiptSender(PluginConfig config, PeerMessenger messenger, CommandComposer composer)
    {
        this.config = config;
        this.messenger = messenger;
        this.composer = composer;
        messenger.SendResult += OnSendResult;
    }

    public void Dispose() => messenger.SendResult -= OnSendResult;

    /// Null `refused` means applied.
    public void Add(PairingState pairing, long seq, RefusalCode? refused)
    {
        if (!queues.TryGetValue(pairing.Id, out var queue))
            queues[pairing.Id] = queue = new PairQueue();
        queue.Waiting.Add(new Entry(seq, refused, DateTime.UtcNow, Retried: false));
    }

    public void Tick()
    {
        if (queues.Count == 0)
            return;
        var now = DateTime.UtcNow;
        foreach (var (pairingId, queue) in queues.ToList())
        {
            if (queue.Waiting.Count == 0 || now - queue.LastFlushUtc < FlushInterval || now < queue.NotBeforeUtc)
                continue;
            if (config.FindPairingById(pairingId) is not { IsPaired: true, Direction: PairingDirection.SubSide } pairing)
            {
                queues.Remove(pairingId);
                continue;
            }
            queue.Waiting.RemoveAll(e => now - e.AtUtc > RetryMaxAge);
            if (queue.Waiting.Count == 0)
                continue;

            var batch = queue.Waiting.Take(CommandComposer.ReceiptMaxEntries).ToList();
            queue.Waiting.RemoveRange(0, batch.Count);
            queue.LastFlushUtc = now;
            var sent = messenger.SendNotice(pairing, composer.ComposeReceipt(batch.Select(e => (e.Seq, e.Refused))));
            if (sent.Failure is { } failure)
                Plugin.Log.Information($"Receipt for {batch.Count} command(s) not sent: {failure}.");
            else
                inFlight[(pairingId, sent.Seq)] = batch;
        }
    }

    private void OnSendResult(PeerSendResult result)
    {
        if (result.Kind != PeerMessagePlaintext.NoticeKind || result.Delivery == PeerDelivery.Held || !inFlight.Remove((result.PairingId, result.Seq), out var batch))
            return;
        if (result.Delivery == PeerDelivery.Delivered)
        {
            Plugin.Log.Debug($"Receipt for {batch.Count} command(s) delivered.");
            return;
        }
        if (result.Delivery != PeerDelivery.OverLimit)
        {
            Plugin.Log.Information($"Receipt for {batch.Count} command(s) not delivered: {result.Delivery}.");
            return;
        }

        var now = DateTime.UtcNow;
        var retry = batch.Where(e => !e.Retried && now + RetryDelay - e.AtUtc <= RetryMaxAge).Select(e => e with { Retried = true }).ToList();
        if (retry.Count == 0 || !queues.TryGetValue(result.PairingId, out var queue))
            return;
        Plugin.Log.Information($"Receipt for {retry.Count} command(s) over the send limit; trying once more.");
        queue.Waiting.InsertRange(0, retry);
        queue.NotBeforeUtc = now + RetryDelay;
    }
}
