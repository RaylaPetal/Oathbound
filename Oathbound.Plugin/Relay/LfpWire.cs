using System.Collections.Generic;
using System.Text.Json.Serialization;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Relay;

// protocol/schemas/lfp.schema.json. Null properties are left off the wire, which the relay reads as absent.

public sealed class LfpFieldsWire
{
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("pronouns")] public string? Pronouns { get; set; }
    [JsonPropertyName("race")] public string Race { get; set; } = "";
    [JsonPropertyName("gender")] public string Gender { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("seeking")] public string Seeking { get; set; } = "";
    [JsonPropertyName("dataCenter")] public string? DataCenter { get; set; }
    [JsonPropertyName("bio")] public string Bio { get; set; } = "";

    public static LfpFieldsWire From(LfpProfileFields f) => new()
    {
        DisplayName = f.DisplayName.Trim(),
        Pronouns = string.IsNullOrWhiteSpace(f.Pronouns) ? null : f.Pronouns.Trim(),
        Race = f.Race,
        Gender = f.Gender,
        Role = f.Role,
        Seeking = f.Seeking,
        DataCenter = string.IsNullOrEmpty(f.DataCenter) ? null : f.DataCenter,
        Bio = f.Bio.Trim(),
    };

    public LfpProfileFields ToFields() => new()
    {
        DisplayName = DisplayName,
        Pronouns = Pronouns,
        Race = Race,
        Gender = Gender,
        Role = Role,
        Seeking = Seeking,
        DataCenter = DataCenter,
        Bio = Bio,
    };
}

/// What any player sees of a profile, plus (on the caller's own) what only its owner sees.
public sealed class LfpProfileWire
{
    [JsonPropertyName("profileId")] public string ProfileId { get; set; } = "";
    [JsonPropertyName("fields")] public LfpFieldsWire Fields { get; set; } = new();
    [JsonPropertyName("picture")] public string? Picture { get; set; }
    [JsonPropertyName("activeAt")] public long ActiveAt { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("visibility")] public string? Visibility { get; set; }
    [JsonPropertyName("hidden")] public bool? Hidden { get; set; }
    [JsonPropertyName("listed")] public bool? Listed { get; set; }
}

public sealed class LfpMatchWire
{
    [JsonPropertyName("matchId")] public string MatchId { get; set; } = "";
    [JsonPropertyName("side")] public string Side { get; set; } = "a";
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
    [JsonPropertyName("endedAt")] public long? EndedAt { get; set; }
    [JsonPropertyName("peer")] public LfpProfileWire Peer { get; set; } = new();
    [JsonPropertyName("peerMatchPublicKey")] public EcPublicKeyJwk PeerMatchPublicKey { get; set; } = new();
}

public sealed class LfpBlockWire
{
    [JsonPropertyName("blockId")] public string BlockId { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("createdAt")] public long CreatedAt { get; set; }
}

internal sealed class LfpMeBody
{
    [JsonPropertyName("type")] public string Type { get; set; } = "lfp-me";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("devicePublicKey")] public EcPublicKeyJwk DevicePublicKey { get; set; } = new();
}

internal sealed class PresenceHelloBody
{
    [JsonPropertyName("type")] public string Type { get; set; } = "presence-hello";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("devicePublicKey")] public EcPublicKeyJwk DevicePublicKey { get; set; } = new();
}

internal sealed class LfpProfileBody
{
    [JsonPropertyName("type")] public string Type { get; set; } = "lfp-profile";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("adultAttested")] public bool AdultAttested { get; set; } = true;
    [JsonPropertyName("visibility")] public string Visibility { get; set; } = "public";
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }
    [JsonPropertyName("fields")] public LfpFieldsWire Fields { get; set; } = new();
    [JsonPropertyName("matchPublicKey")] public EcPublicKeyJwk MatchPublicKey { get; set; } = new();
}

internal sealed class LfpPictureBody
{
    [JsonPropertyName("type")] public string Type { get; set; } = "lfp-picture";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("contentType")] public string ContentType { get; set; } = "image/png";
    [JsonPropertyName("dataBase64Url")] public string DataBase64Url { get; set; } = "";
}

internal sealed class LfpBlockBody
{
    [JsonPropertyName("type")] public string Type { get; set; } = "lfp-block";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("profileId")] public string? ProfileId { get; set; }
    [JsonPropertyName("matchId")] public string? MatchId { get; set; }
}

internal sealed class LfpMeResult
{
    [JsonPropertyName("profile")] public LfpProfileWire? Profile { get; set; }
}

internal sealed class LfpProfileResult
{
    [JsonPropertyName("profile")] public LfpProfileWire Profile { get; set; } = new();
}

public sealed class LfpBrowseResult
{
    [JsonPropertyName("profiles")] public List<LfpProfileWire> Profiles { get; set; } = new();
    [JsonPropertyName("nextCursor")] public string? NextCursor { get; set; }
}

public sealed class LfpLikeResult
{
    [JsonPropertyName("matched")] public bool Matched { get; set; }
    [JsonPropertyName("match")] public LfpMatchWire? Match { get; set; }
}

internal sealed class LfpProfilesResult
{
    [JsonPropertyName("profiles")] public List<LfpProfileWire> Profiles { get; set; } = new();
}

internal sealed class LfpMatchesResult
{
    [JsonPropertyName("matches")] public List<LfpMatchWire> Matches { get; set; } = new();
}

internal sealed class LfpBlocksResult
{
    [JsonPropertyName("blocks")] public List<LfpBlockWire> Blocks { get; set; } = new();
}

internal sealed class LfpDone
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}
