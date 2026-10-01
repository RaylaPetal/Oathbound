using System;
using System.Collections.Generic;
using System.Numerics;

namespace Oathbound.Plugin.Config;

/// Sub-side alias definitions (collar/chat-transport's "Alias resolution against a locally-defined
/// dictionary") - what each short alias actually does. Never transmitted over chat - only the alias name
/// crosses the wire during live commanding, resolved locally against this dictionary on the Sub's own
/// client. The Sub's Scan & Export file separately carries a human-readable summary of what each alias
/// does (see CatalogSyncService's Aliases export/import), so an Owner who imports it can see what an entry
/// actually does before sending it - a deliberate choice, not an oversight; see collar/catalog-sync.

[Serializable]
public class TitleAliasDefinition
{
    public string Alias { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsPrefix { get; set; }
    public Vector3 Color { get; set; } = new(1, 1, 1);

    /// collar/title: optional glow color passed through to Honorific's own `Glow` field - null means no
    /// glow, matching Honorific's own semantics (see HonorificIpc.HonorificTitleData.Glow).
    public Vector3? Glow { get; set; }
}

[Serializable]
public class OutfitAliasDefinition
{
    public string Alias { get; set; } = "";
    public Guid DesignId { get; set; }

    /// Display only, so the Sub can recognize the entry in Settings - not used for matching.
    public string DesignName { get; set; } = "";
    public bool Locked { get; set; }

    /// collar/attached-moodles: the Sub's default moodle while this outfit is current, if any.
    public AttachedMoodleRef? AttachedMoodle { get; set; }
}

[Serializable]
public class GestureAliasDefinition
{
    public string Alias { get; set; } = "";
    public string GestureId { get; set; } = "";
    public string AnimationName { get; set; } = "";
    public string ModDirectory { get; set; } = "";
    public string ModName { get; set; } = "";
    public string EmoteName { get; set; } = "";
}

[Serializable]
public class MoodlesAliasDefinition
{
    public string Alias { get; set; } = "";
    public string StatusId { get; set; } = "";

    /// Display only, so the Sub can recognize the entry in Settings - not used for matching.
    public string StatusName { get; set; } = "";
}

/// Follow's Sub-side settings. The engage/release words used to be renamable here; collar/control-
/// vocabulary fixed them to `leash`/`unleash` (ControlWords), so the two alias properties are no longer read
/// and only remain so older saved configs still deserialize.
[Serializable]
public class FollowAliasWords
{
    [Obsolete("collar/control-vocabulary: the leash word is fixed - use ControlWords.Leash.")]
    public string EngageAlias { get; set; } = "leash";
    [Obsolete("collar/control-vocabulary: the unleash word is fixed - use ControlWords.Unleash.")]
    public string ReleaseAlias { get; set; } = "unleash";

    /// collar/attached-moodles: the Sub's default moodle while leashed, if any.
    public AttachedMoodleRef? AttachedMoodle { get; set; }

    /// collar/leash "Sub's longest accepted leash": a longer requested length is shortened to this.
    public int MaxLeashLengthYalms { get; set; } = 15;
}

/// collar/control-vocabulary: the fixed control words every Oathbound client understands. Not renamable, so
/// an Owner and Sub never have to agree on custom words for releasing or engaging state.
public static class ControlWords
{
    public const string Unlock = "unlock";
    public const string ClearTitle = "clear-title";
    public const string Leash = "leash";
    public const string Unleash = "unleash";
    public const string ClearMoodle = "clear-moodle";

    public static readonly string[] All = [Unlock, ClearTitle, Leash, Unleash, ClearMoodle];
}

/// collar/custom-triggers: the fixed set of action kinds a Custom Trigger's bundle may carry.
public enum CustomTriggerActionKind
{
    Title,
    Outfit,
    Gesture,
    Moodle,
    Restraint,
    Chat,
}

/// One action within a Custom Trigger's bundle. Mirrors RestraintRuleAssignment's own "Kind + only the
/// fields that kind uses" shape rather than a class hierarchy - every field below is ignored by every kind
/// that doesn't use it.
[Serializable]
public class CustomTriggerAction
{
    public CustomTriggerActionKind Kind { get; set; }

    // Title
    public string TitleText { get; set; } = "";
    public bool TitleIsPrefix { get; set; }
    public Vector3 TitleColor { get; set; } = new(1, 1, 1);
    public Vector3? TitleGlow { get; set; }

    // Outfit
    public Guid OutfitDesignId { get; set; }
    public string OutfitDesignName { get; set; } = "";

    // Gesture
    public string GestureId { get; set; } = "";
    public string GestureAnimationName { get; set; } = "";

    // Moodle
    public string MoodleStatusId { get; set; } = "";
    public string MoodleStatusName { get; set; } = "";

    // Restraint
    public string RestraintDeviceId { get; set; } = "";
    public string RestraintDeviceName { get; set; } = "";
    public string RestraintCatalogId { get; set; } = "";
    public ulong RestraintItemId { get; set; }

    /// Self-contained form (a shared copy of a Sub's trigger, see SharedPresetCommands): the restraint's
    /// rules travel with the action, so the Sub's plugin applies it without looking the restraint up - it
    /// keeps working after the Sub deletes the original. Null on a Sub's own trigger, which looks its
    /// restraint up locally as before. `RestraintRulesOnly` = no mod, just rules (plus the optional older
    /// gear in RestraintSlot/RestraintItemId).
    public List<RestraintRuleAssignment>? RestraintRules { get; set; }
    public bool RestraintRulesOnly { get; set; }
    public Glamourer.Api.Enums.ApiEquipSlot? RestraintSlot { get; set; }

    // Chat - collar/custom-triggers "Sending a chat message requires its own dedicated permission and
    // acknowledgement": sent verbatim, any channel, any text - gated at apply time, never here.
    public string ChatText { get; set; } = "";
}

/// collar/custom-triggers: a Sub-defined bundle of actions fired together as one alias, resolved through
/// the same alias dictionary every other category already uses. Only the alias name crosses the wire
/// during live commanding - the Owner's Scan & Export import additionally shows a summary of the bundle's
/// actions for their own reference, the same as every other category (see AliasBook's own doc comment).
[Serializable]
public class CustomTriggerDefinition
{
    public string Alias { get; set; } = "";
    public List<CustomTriggerAction> Actions { get; set; } = new();
}

[Serializable]
public class AliasBook
{
    public List<TitleAliasDefinition> Titles { get; set; } = new();
    [Obsolete("collar/control-vocabulary: the clear-title word is fixed - use ControlWords.ClearTitle.")]
    public string ClearTitleAlias { get; set; } = "clear-title";

    public List<OutfitAliasDefinition> Outfits { get; set; } = new();

    public List<GestureAliasDefinition> Gestures { get; set; } = new();
    public FollowAliasWords Follow { get; set; } = new();

    public List<MoodlesAliasDefinition> Moodles { get; set; } = new();

    /// Removes the Sub's currently active Moodle - the same "one dedicated clear word" shape
    /// ClearTitleAlias and the fixed wardrobe `unlock` action already use.
    [Obsolete("collar/control-vocabulary: the clear-moodle word is fixed - use ControlWords.ClearMoodle.")]
    public string ClearMoodleAlias { get; set; } = "clear-moodle";

    // No restraint alias list: a restraint's own name (captured device) or alias (configured mod restraint)
    // is its word - see RestraintCommand.ToggleByWord. A saved "Restraints" list from older configs is
    // simply ignored on load.

    public List<CustomTriggerDefinition> CustomTriggers { get; set; } = new();
}
