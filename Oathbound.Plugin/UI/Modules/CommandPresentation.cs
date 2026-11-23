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
        CustomTriggerActionKind.Moodle => action.CustomMoodle is { } custom
            ? $"Moodle · {MoodlesTextFormat.StripMarkup(custom.Title)} (Owner's)"
            : $"Moodle · {MoodlesTextFormat.StripMarkup(action.MoodleStatusName)}",
        CustomTriggerActionKind.Restraint => $"Restraint · {action.RestraintDeviceName}",
        CustomTriggerActionKind.Chat => $"Chat · \"{action.ChatText}\"",
        _ => "Unknown action",
    };

    public static string RuleName(RestraintRuleKind kind) => kind switch
    {
        RestraintRuleKind.ForcedPose => "Forced Pose",
        RestraintRuleKind.WalkOnly => "Walk Only",
        RestraintRuleKind.ActionBlock => "Action Block",
        RestraintRuleKind.Blindfolded => "Blindfolded",
        RestraintRuleKind.Gagged => "Gagged",
        RestraintRuleKind.ArmsCuffed => "Arms Cuffed",
        RestraintRuleKind.LegsCuffed => "Legs Cuffed",
        RestraintRuleKind.FullBodyCuffed => "Full Body Cuffed",
        _ => "Unknown rule",
    };

    public static string Rule(RestraintRuleAssignment rule)
    {
        var name = RuleName(rule.Kind);
        return rule.Kind switch
        {
            RestraintRuleKind.ForcedPose => rule.PoseModeId == 0 ? $"{name} · Animation mod" : $"{name} · {Pose(rule.PoseModeId)}",
            RestraintRuleKind.Blindfolded => rule.BlindfoldLevel == BlindfoldLevel.Full ? name : $"{name} · {rule.BlindfoldLevel}",
            RestraintRuleKind.Gagged => (rule.GagLevel == GagLevel.Heavy ? name : $"{name} · {rule.GagLevel}") + (rule.CustomizePresetId is null ? "" : " · Customize+"),
            _ => name,
        };
    }

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
