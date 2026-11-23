using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// How the Owner's views word where a module's latest command stands with the Sub's apply receipt.
public static class CommandMarkText
{
    /// Null for a Sub whose version doesn't confirm, where the "last sent by you" caption already says it all.
    public static StatusMessage? Describe(CommandMark? mark, string who, string what) => mark?.State switch
    {
        CommandConfirmation.Confirmed => new StatusMessage(StatusKind.Ok, $"Confirmed: {who}'s client applied your last {what} command."),
        CommandConfirmation.Waiting => new StatusMessage(StatusKind.Info, $"Waiting for {who}'s client to confirm your last {what} command..."),
        CommandConfirmation.NotConfirmed => new StatusMessage(StatusKind.Caution, $"Sent, not confirmed: {who}'s client didn't answer your last {what} command in time."),
        CommandConfirmation.Refused => new StatusMessage(StatusKind.Error, $"Your last {what} command was refused: {mark.Code.ToOwnerText()}. This shows what was there before it."),
        _ => null,
    };

    /// The estimate's caveat, wherever the Owner sees it.
    public static string EstimateCaveat(PairingState pairing) => pairing.PeerSendsReceipts
        ? "Built from the commands you sent that your Sub's client didn't refuse. Apart from the leash, which your Sub's client reports when it comes off, it can't see your Sub's panic or their own releases. Clear it if it's wrong."
        : "Built from the commands you sent - your client can't see your Sub's side. Apart from the leash, which your Sub's client reports when it comes off, it can't see your Sub's panic, their own releases, or commands their client refused (their version doesn't confirm commands). Clear it if it's wrong.";
}
