using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// collar/attached-moodles "Owner can override the attached moodle per command": the Owner-side picker for
/// the optional `moodle:"..."` option on outfit lock / restraint lock|catalog|wear / leash. The Owner only
/// knows the Sub's moodles as the `moodle apply "<selector>"` quick commands imported through catalog sync,
/// so choices come from those - the selector is exactly what the Sub's side resolves. "Sub's default" (null)
/// sends the command unchanged, byte-for-byte what older Sub clients already understand.
public static class OwnerMoodleOverride
{
    private const string MoodleApplyPrefix = "moodle apply ";

    /// The text actually sent for a command: its own moodle pick when it has one, the saved leash pick for
    /// the fixed `leash` command, otherwise the command unchanged. Every Owner send surface (module tabs, Sub
    /// Control, Favorites, the quick-access menu) goes through this, so a moodle always travels with the
    /// one command it was picked for - never a tab- or window-wide setting.
    /// The fixed `leash` also always carries the Owner's saved length (collar/leash), before the moodle.
    public static string ForSend(PluginConfig config, string command, string? commandMoodle)
    {
        if (!command.Trim().Equals(ControlWords.Leash, StringComparison.OrdinalIgnoreCase))
            return Apply(command, commandMoodle);
        var withLength = LengthOption.Append(command.Trim(), config.QuickCommands.LeashLengthYalms);
        return MoodleOption.Append(withLength, commandMoodle ?? config.QuickCommands.LeashMoodleOverride);
    }

    /// A saved command's full outgoing text. Also where its lock timer (collar/restraint-lock-timer) is added,
    /// since every send surface for a saved command already comes through here.
    public static string ForSend(PluginConfig config, QuickCommand cmd) =>
        OwnerLockOption.Apply(ForSend(config, cmd.Command, cmd.MoodleOverride), cmd.LockSeconds);

    /// What a favorite sends (Favorites window, header quick-access menu): the same, but with the timer
    /// snapshotted when it was favorited (QuickCommand.FavoriteLockSeconds) rather than the row's live one.
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

    /// Whether `command` is one of the commands that carry the option.
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

    /// Draws the picker; `chosen` stays null for "Sub's default".
    public static void Draw(string id, PluginConfig config, ref string? chosen)
    {
        var choices = Choices(config);
        var current = chosen;
        var preview = current is null
            ? "Sub's default"
            : choices.Where(c => c.Selector == current).Select(c => c.Label).FirstOrDefault() ?? current;
        ImGui.SetNextItemWidth(200);
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
        IconGlyph.HelpMarker("Which moodle goes on your Sub along with this command. \"Sub's default\" uses whatever your Sub attached themselves. A different pick only applies if your Sub has Moodles permission on, and needs your Sub on a plugin version that understands it - older versions ignore the whole command.");
    }
}
