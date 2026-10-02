using System;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// User-facing vocabulary only. Wire serializers and stable identities never consume these strings.
public static class CommandPresentation
{
    public static string Action(CustomTriggerAction action) => action.Kind switch
    {
        CustomTriggerActionKind.Title => $"Title · \"{action.TitleText}\"",
        CustomTriggerActionKind.Outfit => $"Outfit · {action.OutfitDesignName}",
        CustomTriggerActionKind.Gesture => $"Gesture · {action.GestureAnimationName}",
        CustomTriggerActionKind.Moodle => $"Moodle · {MoodlesTextFormat.StripMarkup(action.MoodleStatusName)}",
        CustomTriggerActionKind.Restraint => $"Restraint · {action.RestraintDeviceName}",
        CustomTriggerActionKind.Chat => $"Chat · \"{action.ChatText}\"",
        _ => "Unknown action",
    };

    public static string Rule(RestraintRuleAssignment rule) => rule.Kind switch
    {
        RestraintRuleKind.ForcedPose => rule.PoseModeId == 0 ? "Forced Pose · Animation mod" : $"Forced Pose · {Pose(rule.PoseModeId)}",
        RestraintRuleKind.WalkOnly => "Walking Only",
        RestraintRuleKind.ActionBlock => "Actions Blocked",
        RestraintRuleKind.Gagged => rule.CustomizePresetId is null ? "Gagged" : "Gagged · Customize+",
        RestraintRuleKind.ArmsCuffed => "Arms Cuffed",
        RestraintRuleKind.LegsCuffed => "Legs Cuffed",
        RestraintRuleKind.FullBodyCuffed => "Fully Restrain",
        _ => "Unknown rule",
    };

    /// Mods often name the group after the animation and give it plain "Enable"/"Disabled" options.
    private static readonly string[] GenericOptionNames = ["enable", "enabled", "disable", "disabled", "on", "off", "yes", "no", "none", "default"];

    /// The group when the option name is generic, otherwise "Group - Option".
    public static string AnimationDisplayName(string groupName, string animationName)
    {
        var group = groupName.Trim();
        var option = animationName.Trim();
        if (group.Length == 0)
            return option;
        if (option.Length == 0 || Array.Exists(GenericOptionNames, n => string.Equals(n, option, StringComparison.OrdinalIgnoreCase)))
            return group;
        if (string.Equals(group, option, StringComparison.OrdinalIgnoreCase))
            return option;
        return $"{group} · {option}";
    }

    public static string Pose(int id) => id switch { 1 => "Ground Sit", 2 => "Sit", 3 => "Doze", _ => "Unknown Pose" };
}
