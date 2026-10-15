namespace Oathbound.Plugin.Commands;

/// Shared by the header button, Favorites and the quick-access menu. The Owner only reports its location;
/// the Sub picks the route, so this needs no Lifestream or vnavmesh.
public static class TeleportSendAction
{
    /// Callers show the error however suits their surface.
    public static (bool Success, string? Error) TryResolveAndSend(Plugin plugin)
    {
        if (!TryResolveTarget(out var target, out var error))
            return (false, error);
        plugin.ChatSender.Send(plugin.ChatComposer.ComposeTeleport(target));
        return (true, null);
    }

    /// Also used by LeashTravelWatcher.
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

        // A courtesy check only - the Sub refuses interiors itself.
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
