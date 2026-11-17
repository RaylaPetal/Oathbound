using System;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// Shared by the Cage module and Sub Control. The cage's place and position travel in the command itself.
public static class CageSendAction
{
    /// Callers show the error however suits their surface.
    /// `bring` also travels the Sub there when they're elsewhere.
    public static (bool Success, string? Error) TrySend(Plugin plugin, SavedCage cage, bool bring = false) =>
        Send(plugin, CageService.ComposeClose(cage, bring), cage, open: false);

    /// Ties the leashed Sub to a hitching post where the Owner stands, with the leash's length as its radius.
    public static (bool Success, string? Error) TryTieOff(Plugin plugin, int radius, int? lockSeconds)
    {
        if (CagePlaces.Capture(out var label, out var error) is not { } here || Plugin.ObjectTable.LocalPlayer is not { } me)
            return (false, error ?? "Your location can't be read right now.");
        var post = new SavedCage
        {
            Name = CageLimits.PostName,
            PlaceLabel = label,
            Place = here,
            Position = me.Position,
            Radius = Math.Clamp(radius, CageLimits.MinPostRadius, CageLimits.MaxPostRadius),
            LockSeconds = lockSeconds,
            Post = true,
        };
        return Send(plugin, CageService.ComposeClose(post), post, open: false);
    }

    public static (bool Success, string? Error) TryOpen(Plugin plugin) =>
        Send(plugin, CageService.ComposeOpen(), null, open: true);

    private static (bool Success, string? Error) Send(Plugin plugin, string body, SavedCage? cage, bool open)
    {
        if (plugin.Configuration.ActivePairing is not { Direction: PairingDirection.OwnerSide } pairing)
            return (false, "Select an Owner-side pairing first.");
        if (!CommandSelector.Fits(body))
            return (false, "That cage command is too long to send - try a shorter name.");
        if (plugin.SendCommand(pairing, body) is { } failed)
            return (false, OwnerSendFeedback.Explain(failed));
        plugin.OwnerCageStatus.MarkSent(pairing, cage, open);
        return (true, null);
    }
}
