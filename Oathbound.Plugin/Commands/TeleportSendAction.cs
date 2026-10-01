namespace Oathbound.Plugin.Commands;

/// collar/teleport: the Owner-side "come here" resolve-compose-send action, shared by the always-visible
/// header button, the Favorites window and the quick-access menu's favorited Teleport entry - two of those
/// have no CollarWindow reference to call back into.
///
/// The Owner only reports where it is (design.md D2); choosing an aetheryte or housing route is the Sub's
/// job, so this needs no Lifestream, no vnavmesh, and no nearby aetheryte.
public static class TeleportSendAction
{
    /// Callers render the failure message however fits their own surface - the header persists it inline
    /// (see CollarWindow.teleportResolveError), the quick-access menu shows a transient notification since
    /// its popup closes on click and has no persistent surface of its own.
    public static (bool Success, string? Error) TryResolveAndSend(Plugin plugin)
    {
        if (!TryResolveTarget(out var target, out var error))
            return (false, error);
        plugin.ChatSender.Send(plugin.ChatComposer.ComposeTeleport(target));
        return (true, null);
    }

    /// Where the Owner is right now, as a Teleport destination - shared with LeashTravelWatcher
    /// (collar/leash-travel), which sends the same location on its own after the Owner changes area.
    public static bool TryResolveTarget(out TeleportTarget target, out string? error)
    {
        target = null!;
        var player = Plugin.ObjectTable.LocalPlayer;
        var world = player?.CurrentWorld.Value.Name.ExtractText();
        var territory = Plugin.ClientState.TerritoryType;
        if (player is null || string.IsNullOrWhiteSpace(world) || territory == 0)
        {
            error = "Could not read your current location - try again once you've finished loading in.";
            return false;
        }

        // collar/teleport: a courtesy check only - the Sub refuses interiors itself regardless.
        if (TeleportDestinations.IsInsideHousing())
        {
            error = "Teleport doesn't work from inside a house or apartment - step outside first.";
            return false;
        }

        var ward = TeleportDestinations.CurrentWard();
        target = new TeleportTarget(
            world,
            territory,
            TeleportDestinations.CurrentPublicInstance(),
            ward?.Ward ?? 0,
            ward?.Subdivision ?? false,
            player.Position);
        error = null;
        return true;
    }
}
