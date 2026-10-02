using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Commands;

/// What a Sub's preset becomes when shared: a self-contained Owner command, so the copy keeps working after the
/// original changes. Copies carry Owner force semantics. Attached moodles travel separately as the Owner's moodle pick.
public static class SharedPresetCommands
{
    /// Room for the /tell target and trigger phrase.
    private const int ComposeMargin = 64;

    public static bool FitsInOneMessage(string command) => command.Length + ComposeMargin <= CommandSelector.MaxCommandLength;

    /// Null when there's none or it contains a double quote.
    public static string? MoodleName(AttachedMoodleRef? moodle)
    {
        if (moodle is null)
            return null;
        var name = MoodlesTextFormat.StripMarkup(moodle.StatusName).Trim();
        return name.Length == 0 || name.Contains('"') ? null : name;
    }

    public static string Title(TitleAliasDefinition alias) =>
        TitleCommand.BuildStyleCommand(alias.Text, alias.IsPrefix, alias.Color, alias.Glow);

    /// The Sub's plugin finds the design by name.
    public static string? Outfit(OutfitAliasDefinition alias, PluginConfig config)
    {
        var name = config.WardrobeMapping.LocalDesigns.TryGetValue(alias.DesignId, out var design) ? design.Name : alias.DesignName;
        if (string.IsNullOrWhiteSpace(name) || name.Contains('"'))
            return null;
        return $"outfit {(alias.Locked ? "lock" : "wear")} \"{name}\"";
    }

    public static string? Gesture(GestureAliasDefinition alias, PluginConfig config)
    {
        if (!config.GestureMapping.LocalCatalog.TryGetValue(alias.GestureId, out var entry))
            return null;
        var catalog = config.GestureMapping.LocalCatalog.Values.Select(GestureExportEntry.From).ToList();
        return $"gesture {CommandSelector.Quote(CommandSelector.GestureSelector(GestureExportEntry.From(entry), catalog))}";
    }

    public static string? Moodle(MoodlesAliasDefinition alias, PluginConfig config)
    {
        var name = config.MoodlesMapping.LocalCatalog.TryGetValue(alias.StatusId, out var status) ? status.Name : alias.StatusName;
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var allNames = config.MoodlesMapping.LocalCatalog.Values.Select(s => s.Name);
        return $"moodle apply {CommandSelector.Quote(CommandSelector.MoodleSelector(name, allNames))}";
    }

    public static string RulesOnlyRestraint(RestraintDeviceDefinition device) =>
        RestraintCommand.BuildWearCommand(device.Slot, device.ItemId, device.Name, device.Rules);

    /// Oversized bundles go out one message per action, so only each action must fit.
    private static bool BundleFits(string command) =>
        FitsInOneMessage(command) || CustomTriggerCommand.SplitCastCommand(command) is { Count: > 1 } parts && parts.All(FitsInOneMessage);

    /// Null when it can't be self-contained, or one action alone won't fit.
    public static string? CustomTrigger(CustomTriggerDefinition trigger, PluginConfig config)
    {
        var actions = new List<CustomTriggerAction>();
        foreach (var action in trigger.Actions)
        {
            if (SelfContained(action, config) is not { } copy)
                return null;
            actions.Add(copy);
        }
        if (actions.Count == 0 || trigger.Alias.Contains('"'))
            return null;
        var command = CustomTriggerCommand.BuildCastCommand(trigger.Alias, actions);
        return BundleFits(command) ? command : null;
    }

    public static bool IsTooLongToShare(CustomTriggerDefinition trigger, PluginConfig config)
    {
        var actions = trigger.Actions.Select(a => SelfContained(a, config)).ToList();
        if (actions.Count == 0 || actions.Any(a => a is null))
            return false;
        return !BundleFits(CustomTriggerCommand.BuildCastCommand(trigger.Alias, actions!));
    }

    private static CustomTriggerAction? SelfContained(CustomTriggerAction action, PluginConfig config)
    {
        if (action.Kind != CustomTriggerActionKind.Restraint)
            return action;

        if (action.RestraintCatalogId.Length > 0)
        {
            var configured = config.RestraintMapping.ConfiguredMods.FirstOrDefault(m => m.CatalogId == action.RestraintCatalogId && m.ItemId == action.RestraintItemId)
                ?? config.RestraintMapping.ConfiguredMods.FirstOrDefault(m => m.CatalogId == action.RestraintCatalogId);
            if (configured is null || configured.Rules.Count == 0)
                return null;
            return new CustomTriggerAction
            {
                Kind = CustomTriggerActionKind.Restraint,
                RestraintCatalogId = action.RestraintCatalogId,
                RestraintItemId = action.RestraintItemId,
                RestraintDeviceName = action.RestraintDeviceName,
                RestraintRules = configured.Rules,
            };
        }

        if (!config.RestraintMapping.Devices.TryGetValue(action.RestraintDeviceId, out var device) || device.Rules.Count == 0)
            return null;
        return new CustomTriggerAction
        {
            Kind = CustomTriggerActionKind.Restraint,
            RestraintDeviceName = device.Name,
            RestraintRules = device.Rules,
            RestraintRulesOnly = true,
            RestraintSlot = device.Slot,
            RestraintItemId = device.ItemId ?? 0,
        };
    }
}
