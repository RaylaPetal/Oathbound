namespace Oathbound.Plugin.Commands;

/// Why a Teleport or leash journey didn't bring the Sub to the Owner. Only this code reaches the Owner; the Sub's
/// own message names the exact cause.
public enum TeleportRefusal
{
    Setup,
    Permission,
    Busy,
    Caged,
    NoAetheryte,
    Interior,
    Travel,
    NoPath,
    Stopped,
    Other,
}

/// Which journeys report their end to the Owner: only Owner and Leash ones do.
public enum JourneyOrigin { Owner, Leash, Cage, Local }

public static class TeleportRefusalExtensions
{
    public static string ToNoticeWord(this TeleportRefusal code) => code switch
    {
        TeleportRefusal.Setup => "setup",
        TeleportRefusal.Permission => "permission",
        TeleportRefusal.Busy => "busy",
        TeleportRefusal.Caged => "caged",
        TeleportRefusal.NoAetheryte => "noaetheryte",
        TeleportRefusal.Interior => "interior",
        TeleportRefusal.Travel => "travel",
        TeleportRefusal.NoPath => "nopath",
        TeleportRefusal.Stopped => "stopped",
        _ => "other",
    };

    public static TeleportRefusal FromNoticeWord(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "setup" => TeleportRefusal.Setup,
        "permission" => TeleportRefusal.Permission,
        "busy" => TeleportRefusal.Busy,
        "caged" => TeleportRefusal.Caged,
        "noaetheryte" => TeleportRefusal.NoAetheryte,
        "interior" => TeleportRefusal.Interior,
        "travel" => TeleportRefusal.Travel,
        "nopath" => TeleportRefusal.NoPath,
        "stopped" => TeleportRefusal.Stopped,
        _ => TeleportRefusal.Other,
    };

    /// Completes both "Couldn't bring {Sub} to you: ..." and "{Sub}'s leash is paused: ...".
    public static string ToOwnerText(this TeleportRefusal code) => code switch
    {
        TeleportRefusal.Setup => "their setup is missing something Teleport needs (Lifestream or vnavmesh).",
        TeleportRefusal.Permission => "they don't allow Teleport right now.",
        TeleportRefusal.Busy => "they're busy (in combat, in a duty, or already traveling).",
        TeleportRefusal.Caged => "they're caged.",
        TeleportRefusal.NoAetheryte => "they have no aetheryte attuned in your zone.",
        TeleportRefusal.Interior => "you're inside a house - step outside first.",
        TeleportRefusal.Travel => "their travel failed on the way.",
        TeleportRefusal.NoPath => "they couldn't find a path to you.",
        TeleportRefusal.Stopped => "they stopped the journey.",
        _ => "it didn't work on their side.",
    };
}
