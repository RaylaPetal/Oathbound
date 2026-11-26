using System;
using System.Collections.Generic;
using System.Linq;

namespace Oathbound.Plugin.Config;

/// The fields a Looking for pair profile shows. protocol/schemas/lfp.schema.json `fields`; never a character name or world.
public sealed class LfpProfileFields
{
    public string DisplayName { get; set; } = "";
    public string? Pronouns { get; set; }
    public string Race { get; set; } = "miqote";
    public string Gender { get; set; } = "woman";
    public string Role { get; set; } = "sub";
    public string Seeking { get; set; } = "owner";
    public string? DataCenter { get; set; }
    public string Bio { get; set; } = "";

    public LfpProfileFields Clone() => (LfpProfileFields)MemberwiseClone();
}

/// One mutual match, kept locally so its chat, keys and seq survive restarts.
public sealed class LfpMatchState
{
    /// Local id; also the chat history file's name.
    public Guid LocalId { get; set; } = Guid.NewGuid();
    public string MatchId { get; set; } = "";
    /// "a" or "b": which side of the match this install is.
    public string Side { get; set; } = "a";
    public string PeerProfileId { get; set; } = "";
    public LfpProfileFields Peer { get; set; } = new();
    /// Relay path of the peer's picture, or null.
    public string? PeerPicture { get; set; }
    public string PeerMatchKeyX { get; set; } = "";
    public string PeerMatchKeyY { get; set; } = "";
    public long CreatedAt { get; set; }
    public long? EndedAt { get; set; }
    /// The match toast was shown.
    public bool Announced { get; set; }
    /// The Matches tab has shown it since it was made; until then it's highlighted as new.
    public bool Seen { get; set; }

    /// Set once the peer shares their character in the chat.
    public string? PeerCharacterName { get; set; }
    public string? PeerCharacterWorld { get; set; }
    /// This install shared its current character in the chat.
    public bool SharedMyCharacter { get; set; }

    public long OutgoingSeq { get; set; }
    public long LastAcceptedSeq { get; set; }

    public bool IsActive => EndedAt is null;
    public string PeerSide => Side == "a" ? "b" : "a";

    public long NextOutgoingSeq()
    {
        OutgoingSeq = Math.Max(OutgoingSeq + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return OutgoingSeq;
    }

    /// The shared character once there is one, else the display name.
    public string Label => PeerCharacterName is { Length: > 0 } name ? name : Peer.DisplayName;
}

/// collar/looking-for-pair, per install.
public sealed class LookingForPairState
{
    /// When the 18+ and community rules confirmation was given; null shows the gate.
    public long? AdultAttestedAt { get; set; }
    /// The editor's last saved fields, also what's republished if the relay dropped the profile.
    public LfpProfileFields Fields { get; set; } = new();
    public string Visibility { get; set; } = "public";
    public bool Hidden { get; set; }
    /// The profile exists on the relay as far as this install knows.
    public bool Published { get; set; }
    public string? ProfileId { get; set; }
    public string? ProfileCode { get; set; }
    /// This install's own copy of its profile picture (file name under lfp/), re-uploaded after the relay drops it.
    public string? PictureFile { get; set; }
    public long LastRefreshAt { get; set; }
    public bool ShowPictures { get; set; } = true;
    public List<string> SkippedProfileIds { get; set; } = new();
    public List<LfpMatchState> Matches { get; set; } = new();

    /// The match key (protocol/constants.json matchChat): separate from the device key so a match never learns it.
    public string? MatchKeyX { get; set; }
    public string? MatchKeyY { get; set; }
    public byte[]? ProtectedMatchKey { get; set; }
    public bool MatchKeyProtected { get; set; }

    public bool HasActiveMatch => Matches.Any(m => m.IsActive);

    public LfpMatchState? FindMatch(string matchId) => Matches.FirstOrDefault(m => m.MatchId == matchId);
    public LfpMatchState? FindMatch(Guid localId) => Matches.FirstOrDefault(m => m.LocalId == localId);
}
