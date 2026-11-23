namespace Oathbound.Plugin.Commands;

/// Why the Sub's client refused an Owner command. Category-level only: the Sub's settings and aliases stay theirs.
public enum RefusalCode
{
    Permission,
    Unknown,
    Unconfirmed,
    Caged,
    Unavailable,
    Unsupported,
    Failed,
    Other,
}

public static class RefusalCodes
{
    public static string ToWire(this RefusalCode code) => code switch
    {
        RefusalCode.Permission => "permission",
        RefusalCode.Unknown => "unknown",
        RefusalCode.Unconfirmed => "unconfirmed",
        RefusalCode.Caged => "caged",
        RefusalCode.Unavailable => "unavailable",
        RefusalCode.Unsupported => "unsupported",
        RefusalCode.Failed => "failed",
        _ => "other",
    };

    /// A code this version doesn't know reads as Other, so a newer Sub's receipt still resolves.
    public static RefusalCode FromWire(string word) => word.ToLowerInvariant() switch
    {
        "permission" => RefusalCode.Permission,
        "unknown" => RefusalCode.Unknown,
        "unconfirmed" => RefusalCode.Unconfirmed,
        "caged" => RefusalCode.Caged,
        "unavailable" => RefusalCode.Unavailable,
        "unsupported" => RefusalCode.Unsupported,
        "failed" => RefusalCode.Failed,
        _ => RefusalCode.Other,
    };

    /// Follows "refused, ".
    public static string ToOwnerText(this RefusalCode code) => code switch
    {
        RefusalCode.Permission => "that permission is off on their side",
        RefusalCode.Unknown => "it isn't one of their aliases or commands",
        RefusalCode.Unconfirmed => "they haven't confirmed the character you sent it from",
        RefusalCode.Caged => "their cage holds them against it",
        RefusalCode.Unavailable => "a plugin it needs isn't available on their side",
        RefusalCode.Unsupported => "their version of Oathbound can't read it",
        RefusalCode.Failed => "it failed on their side",
        _ => "it didn't apply on their side",
    };

    public static RefusalCode ToRefusalCode(this TeleportRefusal code) => code switch
    {
        TeleportRefusal.Caged => RefusalCode.Caged,
        TeleportRefusal.Permission => RefusalCode.Permission,
        TeleportRefusal.Setup => RefusalCode.Unavailable,
        _ => RefusalCode.Other,
    };
}
