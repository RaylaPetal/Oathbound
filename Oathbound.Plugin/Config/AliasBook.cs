using System;
using System.Collections.Generic;
using System.Numerics;

namespace Oathbound.Plugin.Config;

/// What each Sub alias does. Never transmitted - only alias names cross chat during commanding.

[Serializable]
public class TitleAliasDefinition
{
    public string Alias { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsPrefix { get; set; }
    public Vector3 Color { get; set; } = new(1, 1, 1);

    /// Null means no glow.
    public Vector3? Glow { get; set; }
}

[Serializable]
public class OutfitAliasDefinition
{
    public string Alias { get; set; } = "";
    public Guid DesignId { get; set; }

    /// Display only, not used for matching.
    public string DesignName { get; set; } = "";
    public bool Locked { get; set; }

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

    /// Display only, not used for matching.
    public string StatusName { get; set; } = "";
}

/// The engage/release word properties are no longer read; they remain so older configs still deserialize.
[Serializable]
public class FollowAliasWords
{
    [Obsolete("collar/control-vocabulary: the leash word is fixed - use ControlWords.Leash.")]
    public string EngageAlias { get; set; } = "leash";
    [Obsolete("collar/control-vocabulary: the unleash word is fixed - use ControlWords.Unleash.")]
    public string ReleaseAlias { get; set; } = "unleash";

    public AttachedMoodleRef? AttachedMoodle { get; set; }

    /// A longer requested length is shortened to this.
    public int MaxLeashLengthYalms { get; set; } = 15;
}

/// Fixed words every client understands, so Owner and Sub never have to agree on custom ones.
public static class ControlWords
{
    public const string Unlock = "unlock";
    public const string ClearTitle = "clear-title";
    public const string Leash = "leash";
    public const string Unleash = "unleash";
    public const string ClearMoodle = "clear-moodle";

    public static readonly string[] All = [Unlock, ClearTitle, Leash, Unleash, ClearMoodle];
}

public enum CustomTriggerActionKind
{
    Title,
    Outfit,
    Gesture,
    Moodle,
    Restraint,
    Chat,
}

/// Kind plus only the fields that kind uses; every other field is ignored.
[Serializable]
public class CustomTriggerAction
{
    public CustomTriggerActionKind Kind { get; set; }

    public string TitleText { get; set; } = "";
    public bool TitleIsPrefix { get; set; }
    public Vector3 TitleColor { get; set; } = new(1, 1, 1);
    public Vector3? TitleGlow { get; set; }

    public Guid OutfitDesignId { get; set; }
    public string OutfitDesignName { get; set; } = "";

    public string GestureId { get; set; } = "";
    public string GestureAnimationName { get; set; } = "";

    public string MoodleStatusId { get; set; } = "";
    public string MoodleStatusName { get; set; } = "";

    public string RestraintDeviceId { get; set; } = "";
    public string RestraintDeviceName { get; set; } = "";
    public string RestraintCatalogId { get; set; } = "";
    public ulong RestraintItemId { get; set; }

    /// Self-contained shared copy: the rules travel with the action, so it works after the Sub deletes the original.
    /// Null on a Sub's own trigger, which looks its restraint up locally.
    public List<RestraintRuleAssignment>? RestraintRules { get; set; }
    public bool RestraintRulesOnly { get; set; }
    public Glamourer.Api.Enums.ApiEquipSlot? RestraintSlot { get; set; }

    // Sent verbatim to any channel; gated at apply time.
    public string ChatText { get; set; } = "";
}

/// A Sub-defined bundle of actions fired together as one alias.
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

    [Obsolete("collar/control-vocabulary: the clear-moodle word is fixed - use ControlWords.ClearMoodle.")]
    public string ClearMoodleAlias { get; set; } = "clear-moodle";

    // No restraint alias list: a restraint's own name or alias is its word. An older saved list is ignored.

    public List<CustomTriggerDefinition> CustomTriggers { get; set; } = new();
}
