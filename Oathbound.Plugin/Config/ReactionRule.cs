using System;
using System.Collections.Generic;
using Glamourer.Api.Enums;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.Config;

/// Only that kind's own fields are used.
public enum ReactionTriggerKind
{
    /// Another player uses `EmoteId` while targeting the local player.
    Emote,

    /// A message starts with the user's own trigger word followed by `ChatPhrase`.
    ChatPhrase,
}

/// A trigger plus fire-and-forget actions, run only on this client. `HasAnyAction` must be true to save or fire.
[Serializable]
public class ReactionRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;

    /// The list falls back to a summary of the trigger when empty.
    public string Name { get; set; } = "";

    public ReactionTriggerKind TriggerKind { get; set; } = ReactionTriggerKind.Emote;

    /// Lumina Emote RowId. 0 = unset.
    public uint EmoteId { get; set; }

    /// Which side of the local player the emoting character must be on.
    public ReactionDirection Direction { get; set; } = ReactionDirection.Any;

    /// Matched case-insensitively and exactly.
    public string ChatPhrase { get; set; } = "";

    /// False: only currently paired characters (either direction) can fire it.
    public bool AllowAnyone { get; set; }

    /// Null = none.
    public GestureTrigger? Gesture { get; set; }

    /// Clears the target around the gesture so the character doesn't turn to face it.
    public bool KeepFacing { get; set; }

    /// Null ItemId = none. Never locked, never reverted.
    public ApiEquipSlot? ItemSlot { get; set; }
    public ulong? ItemId { get; set; }
    public string ItemLabel { get; set; } = "";

    /// On until turned off again. Null directory = none.
    public string? ModDirectory { get; set; }
    public string ModName { get; set; } = "";
    public Dictionary<string, List<string>> ModSelections { get; set; } = new();

    /// Null = none. Its own Moodles duration applies.
    public Guid? MoodleId { get; set; }
    public string MoodleLabel { get; set; } = "";

    /// Sent as typed. Empty = none.
    public string ChatMessage { get; set; } = "";

    public int CooldownSeconds { get; set; } = 10;

    public const int MinimumCooldownSeconds = 2;
    public const int MinimumChatCooldownSeconds = 10;

    public bool HasAnyAction =>
        Gesture is not null || ItemId is > 0 || !string.IsNullOrWhiteSpace(ModDirectory) || MoodleId is not null || !string.IsNullOrWhiteSpace(ChatMessage);

    /// Never below 2 s, and never below 10 s when a chat message is sent.
    public int EffectiveCooldownSeconds =>
        Math.Max(CooldownSeconds, string.IsNullOrWhiteSpace(ChatMessage) ? MinimumCooldownSeconds : MinimumChatCooldownSeconds);
}
