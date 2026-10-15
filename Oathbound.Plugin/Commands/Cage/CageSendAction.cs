using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// Shared by the Cage module and Sub Control. The cage's place and position travel in the command itself.
public static class CageSendAction
{
    /// Callers show the error however suits their surface.
    public static (bool Success, string? Error) TrySend(Plugin plugin, SavedCage cage) =>
        Send(plugin, CageService.ComposeClose(cage), cage, open: false);

    public static (bool Success, string? Error) TryOpen(Plugin plugin) =>
        Send(plugin, CageService.ComposeOpen(), null, open: true);

    private static (bool Success, string? Error) Send(Plugin plugin, string body, SavedCage? cage, bool open)
    {
        if (plugin.Configuration.ActivePairing is not { Direction: PairingDirection.OwnerSide } pairing)
            return (false, "Select an Owner-side pairing first.");
        var message = plugin.ChatComposer.ComposeCage(pairing, body);
        if (!CommandSelector.Fits(message))
            return (false, "That cage command is too long to send - try a shorter name.");
        if (!plugin.ChatSender.Send(message))
            return (false, "The command couldn't be sent.");
        plugin.OwnerCageStatus.MarkSent(pairing, cage, open);
        return (true, null);
    }
}
