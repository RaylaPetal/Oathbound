using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Oathbound.Plugin.Safety;

/// What a Sub's `oathbound` SimpleHeels tag says: `1;g;r;l=<yalms>;o=<owner hash>`. Fields come in any order, an
/// unknown field is skipped, and an unknown version makes the whole tag unreadable.
public sealed record SharedStatus(bool Gagged, bool Restrained, int? LeashLength, string? OwnerHash)
{
    public const string TagKey = "oathbound";
    private const string Version = "1";

    public bool Leashed => LeashLength is not null;

    /// Null means the tag should be removed.
    public string? Format()
    {
        if (!Gagged && !Restrained && !Leashed)
            return null;
        var parts = new List<string> { Version };
        if (Gagged) parts.Add("g");
        if (Restrained) parts.Add("r");
        if (LeashLength is { } length)
        {
            parts.Add($"l={length.ToString(CultureInfo.InvariantCulture)}");
            if (OwnerHash is not null)
                parts.Add($"o={OwnerHash}");
        }
        return string.Join(';', parts);
    }

    public static SharedStatus? TryParse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        var parts = tag.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0] != Version)
            return null;
        bool gagged = false, restrained = false;
        int? length = null;
        string? owner = null;
        foreach (var part in parts[1..])
        {
            if (part == "g") gagged = true;
            else if (part == "r") restrained = true;
            else if (part.StartsWith("l=", StringComparison.Ordinal)
                     && int.TryParse(part.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var l) && l is > 0 and <= 100)
                length = l;
            else if (part.StartsWith("o=", StringComparison.Ordinal) && part.Length == 10)
                owner = part[2..].ToLowerInvariant();
        }
        var status = new SharedStatus(gagged, restrained, length, length is null ? null : owner);
        return status.Gagged || status.Restrained || status.Leashed ? status : null;
    }

    /// The first 8 hex characters of SHA-256 over the name@world. It hides the name in the tag, but anyone who already
    /// suspects who it is can confirm it by hashing that name.
    public static string OwnerHashOf(string name, string world)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"oathbound-owner:{name.Trim().ToLowerInvariant()}@{world.Trim().ToLowerInvariant()}"));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

#if DEBUG
    static SharedStatus()
    {
        var hash = OwnerHashOf("Some One", "World");
        var cases = new[]
        {
            new SharedStatus(true, false, null, null),
            new SharedStatus(false, true, 10, hash),
            new SharedStatus(true, true, 3, null),
        };
        foreach (var c in cases)
            if (TryParse(c.Format()) != c)
                throw new InvalidOperationException($"SharedStatus round trip failed for {c}");
        if (TryParse("2;g") is not null || TryParse("1") is not null || TryParse("1;g;zz=9")?.Gagged != true)
            throw new InvalidOperationException("SharedStatus parsing self-check failed.");
    }
#endif
}
