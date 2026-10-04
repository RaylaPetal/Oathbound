using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// Owner-side picker for the `moodle:"..."` option, choosing from the Sub's imported moodle commands.
/// "Sub's default" (null) sends the command unchanged.
public static class OwnerMoodleOverride
{
    private const string MoodleApplyPrefix = "moodle apply ";

    /// Every Owner send surface goes through this, so a moodle travels only with the command it was picked for.
    /// The fixed `leash` also carries the saved length, before the moodle.
    public static string ForSend(PluginConfig config, string command, string? commandMoodle)
    {
        if (!command.Trim().Equals(ControlWords.Leash, StringComparison.OrdinalIgnoreCase))
            return Apply(command, commandMoodle);
        var withLength = LengthOption.Append(command.Trim(), config.QuickCommands.LeashLengthYalms);
        return MoodleOption.Append(withLength, commandMoodle ?? config.QuickCommands.LeashMoodleOverride);
    }

    /// Also where its lock timer is added.
    public static string ForSend(PluginConfig config, QuickCommand cmd) =>
        OwnerLockOption.Apply(ForSend(config, cmd.Command, cmd.MoodleOverride), cmd.LockSeconds);

    /// Uses the timer snapshotted when favorited.
    public static string ForFavoriteSend(PluginConfig config, QuickCommand cmd) =>
        OwnerLockOption.Apply(ForSend(config, cmd.Command, cmd.MoodleOverride), cmd.FavoriteLockSeconds);

    public static IReadOnlyList<(string Label, string Selector)> Choices(PluginConfig config) =>
        config.QuickCommands.Moodles
            .Select(c => c.Command.StartsWith(MoodleApplyPrefix, StringComparison.OrdinalIgnoreCase)
                         && CommandSelector.TryRead(c.Command[MoodleApplyPrefix.Length..], out var selector, out _)
                         && !selector.Contains('"')
                ? (Label: MoodlesTextFormat.StripMarkup(c.Label), Selector: selector)
                : (Label: "", Selector: ""))
            .Where(c => c.Selector.Length > 0)
            .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool Accepts(string command)
    {
        var trimmed = command.Trim();
        return trimmed.Equals(ControlWords.Leash, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("outfit lock ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("outfit wear ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("restraint lock ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("restraint catalog ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("restraint wear ", StringComparison.OrdinalIgnoreCase);
    }

    public static string Apply(string command, string? chosen) =>
        chosen is not null && Accepts(command) ? MoodleOption.Append(command, chosen) : command;

    /// `chosen` stays null for "Sub's default".
    public static void Draw(string id, PluginConfig config, ref string? chosen)
    {
        var choices = Choices(config);
        var current = chosen;
        var preview = current is null
            ? "Sub's default"
            : choices.Where(c => c.Selector == current).Select(c => c.Label).FirstOrDefault() ?? current;
        Layout.ItemWidth(200);
        if (ImGui.BeginCombo($"Moodle##ownerMoodle_{id}", preview))
        {
            if (ImGui.Selectable($"Sub's default##ownerMoodle_{id}", chosen is null))
                chosen = null;
            if (choices.Count == 0)
                ImGui.TextDisabled("No Moodles imported from your Sub yet (Sync tab).");
            foreach (var (label, selector) in choices)
            {
                if (ImGui.Selectable($"{label}##ownerMoodle_{id}_{selector}", chosen == selector))
                    chosen = selector;
            }
            ImGui.EndCombo();
        }
        IconGlyph.HelpMarker("Which moodle goes on your Sub with this. \"Sub's default\" uses their own pick.");
    }
}
