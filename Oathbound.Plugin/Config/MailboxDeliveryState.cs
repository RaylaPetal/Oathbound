using System;

namespace Oathbound.Plugin.Config;

/// One relay mailbox's delivery bookkeeping on one pairing (catalog, rulebook or report). A pairing is on one side
/// of each mailbox, so only that side's fields are ever filled.
[Serializable]
public sealed class MailboxDeliveryState
{
    // ---- Receiving side ----

    /// The key a pickup rotates the relay to. Stored before the consume request, so a lost response can be retried
    /// with exactly this key; it replaces the current key only once the item's outcome is settled.
    public string? NextKeyId { get; set; }
    public string? NextKeyX { get; set; }
    public string? NextKeyY { get; set; }
    public byte[]? NextKeyPrivate { get; set; }
    public bool? NextKeyPrivateProtected { get; set; }
    /// The signed key envelope as JSON, sent again unchanged on a retry.
    public string? NextKeyEnvelopeJson { get; set; }
    /// The item the last consume with NextKey asked for; 0 until a consume was attempted.
    public int NextKeyItem { get; set; }

    /// A receipt the relay doesn't hold yet; posted again at the next pair status check or hint.
    public int? PendingReceiptSequence { get; set; }
    public string? PendingReceiptCode { get; set; }

    /// The last item this side couldn't take, shown until a later one is taken.
    public string? LastFailureCode { get; set; }
    public int LastFailureSequence { get; set; }
    public long LastFailureAt { get; set; }

    /// The sending side's latest status, verified against the paired device key; null or "ok" when it can send.
    public string? PeerSenderCode { get; set; }
    public long PeerSenderAt { get; set; }

    // ---- Sending side ----

    /// What the relay holds from us as sender, so an unchanged problem isn't posted again.
    public string? PostedSenderCode { get; set; }
    public int PostedSenderSequence { get; set; }

    /// The receiver posts receipts (a status from the pair's current receiver device was seen this epoch).
    public bool PeerSendsReceipts { get; set; }
    public int PeerSendsReceiptsEpoch { get; set; } = -1;
    /// The receiver's latest verified receipt.
    public string? PeerReceiptCode { get; set; }
    public int PeerReceiptSequence { get; set; }
    public long PeerReceiptAt { get; set; }

    /// The relay's view of our last item at the last pair status check.
    public int RelayWaiting { get; set; }
    public int RelayConsumed { get; set; }

    /// Automatic resends of one content after resend-able rejects; reset when the content changes or the user sends again.
    public string? ResendDigest { get; set; }
    public int ConsecutiveResends { get; set; }
    /// The next send is a resend after a reject, so it counts toward the limit.
    public bool ResendAfterReject { get; set; }

    /// Catalog only: the Owner's last Get latest this side already answered.
    public long LastSeenRefreshAt { get; set; }

    public void ClearNextKey()
    {
        if (NextKeyPrivate is { } d)
            Array.Clear(d);
        NextKeyId = null;
        NextKeyX = null;
        NextKeyY = null;
        NextKeyPrivate = null;
        NextKeyPrivateProtected = null;
        NextKeyEnvelopeJson = null;
        NextKeyItem = 0;
    }
}
