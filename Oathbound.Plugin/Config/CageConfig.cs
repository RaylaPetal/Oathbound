using System;
using System.Globalization;
using System.Numerics;

namespace Oathbound.Plugin.Config;

public enum CagePlaceKind
{
    /// An open-world or city territory, on any world.
    Territory,
    /// A residential district ward outdoors, on one world.
    Ward,
    /// One specific house or apartment, by the game's house id.
    House,
}

/// Where a cage is. A house is matched by its full house id, since every house of a size shares one territory.
[Serializable]
public sealed class CagePlace
{
    public CagePlaceKind Kind { get; set; }
    public uint Territory { get; set; }
    public uint WorldId { get; set; }
    /// 1-based.
    public int Ward { get; set; }
    public bool Subdivision { get; set; }
    public ulong HouseId { get; set; }

    /// `t<territory>`, `w<world>.<territory>.<ward>.<1|2>` or `h<house id hex>`.
    public string ToWire() => Kind switch
    {
        CagePlaceKind.Territory => string.Create(CultureInfo.InvariantCulture, $"t{Territory}"),
        CagePlaceKind.Ward => string.Create(CultureInfo.InvariantCulture, $"w{WorldId}.{Territory}.{Ward}.{(Subdivision ? 2 : 1)}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"h{HouseId:x}"),
    };

    public static bool TryParse(string token, out CagePlace place)
    {
        place = null!;
        if (token.Length < 2)
            return false;
        var body = token[1..];
        switch (char.ToLowerInvariant(token[0]))
        {
            case 't' when uint.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out var territory) && territory > 0:
                place = new CagePlace { Kind = CagePlaceKind.Territory, Territory = territory };
                return true;
            case 'w':
                var parts = body.Split('.');
                if (parts.Length != 4
                    || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var world) || world == 0
                    || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var wardTerritory) || wardTerritory == 0
                    || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ward) || ward is < 1 or > 30
                    || parts[3] is not ("1" or "2"))
                    return false;
                place = new CagePlace { Kind = CagePlaceKind.Ward, WorldId = world, Territory = wardTerritory, Ward = ward, Subdivision = parts[3] == "2" };
                return true;
            case 'h' when ulong.TryParse(body, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var house) && house != 0 && house != ulong.MaxValue:
                place = new CagePlace { Kind = CagePlaceKind.House, HouseId = house };
                return true;
            default:
                return false;
        }
    }

    public bool SameAs(CagePlace other) => ToWire() == other.ToWire();
}

public enum CagePhase
{
    /// Waiting for the Sub to be in the cage's place.
    Armed,
    /// Closed: walking in, or held inside.
    Caged,
}

/// The Sub's one cage. `EndsAtUtc` is set the first time it closes and never moves after that, except when the
/// holding Owner re-sends it; null with `LockSeconds` null means until opened.
[Serializable]
public sealed class SubCageState
{
    public Guid? PairingId { get; set; }
    public string OwnerName { get; set; } = "";
    public string Name { get; set; } = "";
    public CagePlace Place { get; set; } = new();
    public Vector3 Position { get; set; }
    /// Half the width along X; 0 = pinned.
    public float Radius { get; set; }
    /// Half the depth along Z; null = the same as `Radius`.
    public float? RadiusZ { get; set; }
    public int? LockSeconds { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public DateTime ArmExpiresAtUtc { get; set; }
    public CagePhase Phase { get; set; }
    /// Once closed, leaving re-arms the cage and its lock alone decides when it ends.
    public bool HasClosed { get; set; }

    public bool IsPinned => Radius <= 0;
    public Vector2 HalfExtents => CageLimits.HalfExtents(Radius, RadiusZ);
}

/// An Owner's saved cage. Owner config only; its numbers travel in the `cage` command.
[Serializable]
public sealed class SavedCage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// Shown in the list so the Owner can tell places apart; never sent.
    public string PlaceLabel { get; set; } = "";
    public CagePlace Place { get; set; } = new();
    public Vector3 Position { get; set; }
    /// Half the width along X; 0 = pinned.
    public float Radius { get; set; } = 2;
    /// Half the depth along Z; null = the same as `Radius`.
    public float? RadiusZ { get; set; }
    /// Null = until opened.
    public int? LockSeconds { get; set; }
    public int ArmSeconds { get; set; } = CageLimits.DefaultArmSeconds;
}

public static class CageLimits
{
    public const float MinRadius = 0.5f;
    public const float MaxRadius = 15f;
    public const int MaxNameLength = 32;
    public const int MinArmSeconds = 3600;
    public const int MaxArmSeconds = 7 * 86400;
    public const int DefaultArmSeconds = 3 * 86400;

    /// A pinned cage still gets a 2-yalm square, the walk-in's stopping distance.
    public const float PinnedHalfSide = 1f;

    /// The cage's half-size along X and Z, as a rectangle around its spot.
    public static Vector2 HalfExtents(float radius, float? radiusZ) => radius <= 0
        ? new Vector2(PinnedHalfSide)
        : new Vector2(RoundSize(radius), RoundSize(radiusZ ?? radius));

    /// Sizes go in 0.1-yalm steps, the precision they travel with.
    public static float RoundSize(float yalms) => Math.Clamp(MathF.Round(yalms * 10f) / 10f, MinRadius, MaxRadius);

    /// Half-sizes as the Owner sets them: a radius for a square, otherwise how far it reaches along X and along Z.
    public static string DescribeSize(float radius, float? radiusZ)
    {
        if (radius <= 0)
            return "pinned";
        var half = HalfExtents(radius, radiusZ);
        return half.X == half.Y ? $"{half.X:0.#} yalm radius" : $"{half.X:0.#} yalms on X, {half.Y:0.#} on Z";
    }

    public static int ClampArmSeconds(int seconds) => Math.Clamp(seconds, MinArmSeconds, MaxArmSeconds);

    public static bool IsValidName(string name) =>
        name.Trim().Length is > 0 and <= MaxNameLength && name.IndexOf('"') < 0;
}
