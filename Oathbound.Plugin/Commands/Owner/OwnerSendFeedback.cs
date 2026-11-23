using System;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.Commands;

/// The Owner's one local line per command: what was sent, and whether it reached the Sub. It says a command took
/// effect only when the Sub's apply receipt says so; for a Sub whose version doesn't confirm, it says "Sent".
public sealed class OwnerSendFeedback : IDisposable
{
    private readonly PluginConfig config;
    private readonly PeerMessenger messenger;
    private readonly OwnerReceiptTracker receipts;

    public OwnerSendFeedback(PluginConfig config, PeerMessenger messenger, OwnerReceiptTracker receipts)
    {
        this.config = config;
        this.messenger = messenger;
        this.receipts = receipts;
        messenger.SendResult += OnResult;
        receipts.CommandOutcome += OnOutcome;
    }

    public void Dispose()
    {
        messenger.SendResult -= OnResult;
        receipts.CommandOutcome -= OnOutcome;
    }

    /// Why a frame didn't reach the other person, in words for the user.
    public static string Explain(PeerDelivery delivery) => delivery switch
    {
        PeerDelivery.Offline or PeerDelivery.Held => "they're offline",
        PeerDelivery.NeedsUpdate => "they need to update Oathbound first",
        PeerDelivery.OverLimit => "too many commands at once - wait a few seconds",
        PeerDelivery.OverBudget => "the Oathbound relay has reached its daily limit - try again later",
        PeerDelivery.RelayUnreachable => "the Oathbound relay can't be reached right now",
        PeerDelivery.NotReady => "this pairing isn't ready to send yet",
        _ => "it couldn't be sent",
    };

    private void OnResult(PeerSendResult result)
    {
        if (config.FindPairingById(result.PairingId) is not { } pairing)
            return;
        var who = pairing.PeerName ?? "your partner";

        if (result.Kind == PeerMessagePlaintext.NoticeKind)
        {
            // Only the grant answers a button press; the rest are automatic and stay quiet.
            if (result.Text == CommandComposer.GrantLeaveKeyword && result.Delivery is not (PeerDelivery.Delivered or PeerDelivery.Held))
                OathboundChat.Print($"Leave wasn't granted - {who} didn't get it: {Explain(result.Delivery)}.");
            return;
        }
        if (pairing.Direction != PairingDirection.OwnerSide)
            return;

        var what = CommandDescriber.Describe(result.Text);
        switch (result.Delivery)
        {
            case PeerDelivery.Delivered:
                // OnOutcome reports it: applied, refused, not confirmed, or "Sent" for a Sub that doesn't confirm.
                break;
            case PeerDelivery.Held:
                OathboundChat.Print($"Sent {who}: {what} - waiting for them to reconnect.");
                break;
            default:
                OathboundChat.Print($"Didn't reach {who}: {what} - {Explain(result.Delivery)}.");
                break;
        }
    }

    private void OnOutcome(CommandOutcome outcome)
    {
        if (config.FindPairingById(outcome.PairingId) is not { Direction: PairingDirection.OwnerSide } pairing)
            return;
        var who = pairing.PeerName ?? "your Sub";
        var what = CommandDescriber.Describe(outcome.Text);
        switch (outcome.Kind)
        {
            case CommandOutcomeKind.Legacy:
                OathboundChat.Print($"Sent {who}: {what}.");
                break;
            case CommandOutcomeKind.NotConfirmed:
                OathboundChat.Print($"Sent {who}: {what} - not confirmed yet; their client hasn't answered.");
                break;
            case CommandOutcomeKind.Applied when outcome.Previous != CommandOutcomeKind.Legacy:
                OathboundChat.Print(outcome.Late ? $"{who}: {what} - applied after all." : $"{who}: {what} - applied.");
                break;
            case CommandOutcomeKind.Refused:
                OathboundChat.Print($"{who}: {what} - refused, {outcome.Code.ToOwnerText()}.");
                break;
        }
    }
}
