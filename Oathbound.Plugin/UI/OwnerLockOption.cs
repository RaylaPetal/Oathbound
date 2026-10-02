using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// Owner-side picker for the `lockfor:<seconds>` option, saved per command. Permanent (null) sends the command unchanged.
public static class OwnerLockOption
{
    private const string CastPrefix = "customtrigger cast ";
    private static readonly string[] ModeNames = ["Permanent", "Timed"];
    private const int DefaultTimedMinutes = 30;

    public static bool Accepts(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith("restraint lock ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("restraint catalog ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("restraint wear ", StringComparison.OrdinalIgnoreCase))
            return true;

        // A bundle with no restraint action would lock nothing.
        return trimmed.StartsWith(CastPrefix, StringComparison.OrdinalIgnoreCase)
            && CustomTriggerCommand.TryParseCastCommand(trimmed[CastPrefix.Length..], out _, out var actions)
            && actions.Any(a => a.Kind == CustomTriggerActionKind.Restraint);
    }

    public static string Apply(string command, int? lockSeconds) =>
        lockSeconds is not null && Accepts(command)
            ? LockTimerOption.Insert(command, RestraintLock.FromSeconds(lockSeconds))
            : command;

    /// Null when Permanent or not applicable.
    public static string? DescribeFavorite(QuickCommand cmd) =>
        cmd.FavoriteLockSeconds is { } seconds && Accepts(cmd.Command)
            ? $"locks {RestraintLock.Format(RestraintLock.FromSeconds(seconds).Duration!.Value)}"
            : null;

    private const string HelpText = "Permanent: stays locked until you send `restraint unlock` (or your Sub panics). Timed: your Sub's restraints come off by themselves when the time runs out - the timer keeps counting even if your Sub logs out. Sending another restraint command replaces the timer. Needs your Sub on a plugin version that understands timers - older versions ignore the whole command.";

    private const float InlineFieldWidth = 100f;

    /// So a caller can shorten the label before it instead of pushing the picker off the window.
    public static float InlineWidth(QuickCommand cmd)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = spacing + InlineFieldWidth;
        if (cmd.LockSeconds is not null)
            width += spacing + InlineFieldWidth + spacing + ImGui.CalcTextSize("min").X;
        return width;
    }

    /// Saves straight onto `cmd.LockSeconds`.
    public static void DrawInline(string id, QuickCommand cmd, PluginConfig config)
    {
        var mode = cmd.LockSeconds is null ? 0 : 1;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(InlineFieldWidth);
        if (ImGui.Combo($"##ownerLockInline_{id}", ref mode, ModeNames, ModeNames.Length))
        {
            cmd.LockSeconds = mode == 0 ? null : DefaultTimedMinutes * 60;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(HelpText);

        if (cmd.LockSeconds is not { } seconds)
            return;
        var minutes = Math.Max(1, seconds / 60);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(InlineFieldWidth);
        if (ImGui.InputInt($"##ownerLockInlineMinutes_{id}", ref minutes, 15, 60))
        {
            cmd.LockSeconds = Math.Clamp(minutes, (int)RestraintLock.MinDuration.TotalMinutes, (int)RestraintLock.MaxDuration.TotalMinutes) * 60;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Minutes ({RestraintLock.Format(TimeSpan.FromSeconds(cmd.LockSeconds.Value))})");
        ImGui.SameLine();
        ImGui.TextDisabled("min");
    }

    /// Returns true when the value changed.
    public static bool Draw(string id, ref int? lockSeconds)
    {
        var before = lockSeconds;
        var mode = lockSeconds is null ? 0 : 1;
        ImGui.SetNextItemWidth(120);
        if (ImGui.Combo($"Lock##ownerLock_{id}", ref mode, ModeNames, ModeNames.Length))
            lockSeconds = mode == 0 ? null : DefaultTimedMinutes * 60;
        IconGlyph.HelpMarker(HelpText);

        if (lockSeconds is { } seconds)
        {
            var minutes = Math.Max(1, seconds / 60);
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt($"Minutes##ownerLockMinutes_{id}", ref minutes, 15, 60))
            {
                var clamped = Math.Clamp(minutes, (int)RestraintLock.MinDuration.TotalMinutes, (int)RestraintLock.MaxDuration.TotalMinutes);
                lockSeconds = clamped * 60;
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"= {RestraintLock.Format(TimeSpan.FromSeconds(lockSeconds.Value))}");
        }

        return lockSeconds != before;
    }
}
