using System;
using System.Collections.Generic;
using Glamourer.Api.Enums;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.Config;

/// collar/reactions: what fires a reaction. Only that kind's own fields below are used.
public enum ReactionTriggerKind
{
    /// Another player character uses `EmoteId` while targeting the local player.
    Emote,

    /// A chat message starts with the local user's own trigger word followed by `ChatPhrase`.
    ChatPhrase,
}

/// collar/reactions: one local, automatic reaction - a trigger plus one or more fire-and-forget actions.
/// Configured and run only on this client; nothing about it is ever sent to, or driven by, a paired peer.
/// Every action field is optional; `HasAnyAction` must be true for a reaction to be saved or to fire.
[Serializable]
public class ReactionRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;

    /// Optional display name; the list falls back to a summary of the trigger when empty.
    public string Name { get; set; } = "";

    public ReactionTriggerKind TriggerKind { get; set; } = ReactionTriggerKind.Emote;

    /// Emote trigger: Lumina `Emote` RowId (the game's own EmoteController.EmoteId). 0 = unset.
    public uint EmoteId { get; set; }

    /// Emote trigger: which side of the local player the emoting character must be on.
    public ReactionDirection Direction { get; set; } = ReactionDirection.Any;

    /// Chat trigger: the words after the trigger word, matched case-insensitively and exactly (surrounding
    /// whitespace ignored).
    public string ChatPhrase { get; set; } = "";

    /// False (default): only characters this user is currently paired with, in either direction, can fire
    /// it. True: anyone.
    public bool AllowAnyone { get; set; }

    /// Gesture action: an emote/pose to play. Null = none.
    public GestureTrigger? Gesture { get; set; }

    /// Gesture action: clear the current target around the gesture so the character doesn't turn to face it.
    public bool KeepFacing { get; set; }

    /// Item action: one Glamourer item for its slot. Null ItemId = none. Never locked, never reverted.
    public ApiEquipSlot? ItemSlot { get; set; }
    public ulong? ItemId { get; set; }
    public string ItemLabel { get; set; } = "";

    /// Mod action: a Penumbra mod turned on with these option selections, until turned off again. Null
    /// directory = none.
    public string? ModDirectory { get; set; }
    public string ModName { get; set; } = "";
    public Dictionary<string, List<string>> ModSelections { get; set; } = new();

    /// Moodle action: one of the user's own Moodles statuses. Null = none. Its own Moodles duration applies.
    public Guid? MoodleId { get; set; }
    public string MoodleLabel { get; set; } = "";

    /// Chat action: text or a /command sent as typed. Empty = none.
    public string ChatMessage { get; set; } = "";

    public int CooldownSeconds { get; set; } = 10;

    public const int MinimumCooldownSeconds = 2;
    public const int MinimumChatCooldownSeconds = 10;

    public bool HasAnyAction =>
        Gesture is not null || ItemId is > 0 || !string.IsNullOrWhiteSpace(ModDirectory) || MoodleId is not null || !string.IsNullOrWhiteSpace(ChatMessage);

    /// The cooldown actually enforced: never below 2s, and never below 10s when a chat message is sent.
    public int EffectiveCooldownSeconds =>
        Math.Max(CooldownSeconds, string.IsNullOrWhiteSpace(ChatMessage) ? MinimumCooldownSeconds : MinimumChatCooldownSeconds);
}
