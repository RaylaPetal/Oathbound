using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// Owner-side picker for the `lockfor:<seconds>` option, saved per command. Permanent (null) sends the command unchanged.
/// On a gesture the same option is a hold in seconds rather than a lock in minutes; on a title, outfit or moodle it's
/// how long that stays locked on the Sub before it comes off.
public static class OwnerLockOption
{
    private const string CastPrefix = "customtrigger cast ";
    private const string GesturePrefix = "gesture ";
    private static readonly string[] ModeNames = ["Permanent", "Timed"];
    private static readonly string[] GestureModeNames = ["Until stopped", "Timed"];
    private static readonly string[] OwnerLockModeNames = ["Until cleared", "Timed"];
    private static readonly string[] OwnerLockPrefixes = ["title create ", "title style ", "outfit lock ", "outfit wear ", "moodle apply ", $"moodle {CustomMoodle.CustomWord} "];
    private const int DefaultTimedMinutes = 30;
    private const int DefaultHoldSeconds = 30;

    public static bool IsGesture(string command)
    {
        var trimmed = command.Trim();
        return trimmed.StartsWith(GesturePrefix, StringComparison.OrdinalIgnoreCase)
            && !trimmed[GesturePrefix.Length..].Trim().Equals(ChatComposer.StopGestureWord, StringComparison.OrdinalIgnoreCase);
    }

    /// A title, outfit or moodle the Owner puts on, which a timer locks for a while and then takes off.
    public static bool IsOwnerLock(string command)
    {
        var trimmed = command.Trim();
        return OwnerLockPrefixes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Accepts(string command)
    {
        if (IsGesture(command) || IsOwnerLock(command))
            return true;
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

    /// `key` is the Owner's password; a fresh salt is drawn for every send.
    public static string Apply(string command, int? lockSeconds, string? key = null)
    {
        if (!Accepts(command))
            return command;
        if (IsGesture(command))
            return lockSeconds is { } hold ? LockTimerOption.InsertSeconds(command, ClampHold(hold)) : command;
        if (IsOwnerLock(command))
            return RestraintLock.FromSeconds(lockSeconds).Seconds is { } lockFor ? LockTimerOption.InsertSeconds(command, lockFor) : command;
        var restraintLock = RestraintLock.FromSeconds(lockSeconds);
        if (RestraintKey.IsValidPassword(key))
            restraintLock = restraintLock.WithKey(RestraintKey.ToWire(key!));
        return LockTimerOption.Insert(command, restraintLock);
    }

    /// Null when Permanent or not applicable.
    public static string? DescribeFavorite(QuickCommand cmd)
    {
        if (!Accepts(cmd.Command))
            return null;
        var keyed = !IsGesture(cmd.Command) && !IsOwnerLock(cmd.Command) && RestraintKey.IsValidPassword(cmd.FavoriteLockKey);
        if (cmd.FavoriteLockSeconds is not { } seconds)
            return keyed ? "with a key" : null;
        return IsGesture(cmd.Command) ? $"holds {RestraintLock.Format(TimeSpan.FromSeconds(ClampHold(seconds)))}"
            : $"locks {RestraintLock.Format(RestraintLock.FromSeconds(seconds).Duration!.Value)}{(keyed ? ", with a key" : "")}";
    }

    private static int ClampHold(int seconds) => Math.Clamp(seconds, GestureCommand.MinHoldSeconds, GestureCommand.MaxHoldSeconds);

    private const string GestureHelpText = "Until stopped: a looping animation or pose holds your Sub in place until you send Stop animation, and a one-shot until it finishes. Timed: your Sub is held for exactly this long, then the animation stops and they can move again by themselves. Needs your Sub on a plugin version that understands timed holds - older versions reject the command.";

    private const string OwnerLockHelpText = "Until cleared: stays on, locked, until you clear it (or your Sub panics). Timed: locked for this long, then it comes off by itself - a title is removed, an outfit goes back to your Sub's own look, a moodle comes off. The timer keeps counting while your Sub is logged out. Needs your Sub on a plugin version that understands timers - older versions ignore a timed command.";

    private const string HelpText = "Permanent: stays locked until you send `restraint unlock` (or your Sub panics). Timed: your Sub's restraints come off by themselves when the time runs out - the timer keeps counting even if your Sub logs out. Sending another restraint command replaces the timer. Needs your Sub on a plugin version that understands timers - older versions ignore the whole command.";

    private static float InlineFieldWidth => Layout.Scaled(100f);

    /// Wide enough for its longest choice plus the arrow, whatever the font size.
    private static float ModeComboWidth(string[] names) =>
        names.Max(n => ImGui.CalcTextSize(n).X) + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetFrameHeight();

    private static string[] ModeNamesFor(string command) =>
        IsGesture(command) ? GestureModeNames : IsOwnerLock(command) ? OwnerLockModeNames : ModeNames;

    /// So a caller can shorten the label before it instead of pushing the picker off the window.
    public static float InlineWidth(QuickCommand cmd)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = spacing + ModeComboWidth(ModeNamesFor(cmd.Command));
        if (cmd.LockSeconds is not null)
            width += spacing + InlineFieldWidth + spacing + ImGui.CalcTextSize(IsGesture(cmd.Command) ? "sec" : "min").X;
        if (IsOwnerLock(cmd.Command))
            return width;
        if (!IsGesture(cmd.Command) && RestraintStruggle.Accepts(cmd.Command))
            width += spacing + InlineFieldWidth + (cmd.Struggle != StruggleLevel.None && cmd.LockSeconds is not null ? spacing + InlineFieldWidth : 0);
        if (!IsGesture(cmd.Command))
            width += spacing + InlineFieldWidth;
        return width;
    }

    private const string StruggleHelpText = "Whether your Sub may try to struggle free of this lock: one roll per try, with a wait between tries. Getting free takes the restraints off and tells you. A Sub on an older Oathbound version just can't struggle.";

    private const string KeyHelpText = "Optional key (4-20 characters). Your Sub can type it to unlock this restraint, and you're told when they do. It never shows in chat, but a determined Sub could work out a short key from their own files.";

    /// Saves straight onto `cmd.LockSeconds`, and for a restraint its key and struggle setting.
    public static void DrawInline(string id, QuickCommand cmd, PluginConfig config)
    {
        DrawLockInline(id, cmd, config);
        if (IsGesture(cmd.Command) || IsOwnerLock(cmd.Command))
            return;
        var key = cmd.LockKey ?? "";
        Layout.ContinueRowOrWrap(InlineFieldWidth);
        ImGui.SetNextItemWidth(InlineFieldWidth);
        if (ImGui.InputTextWithHint($"##ownerKeyInline_{id}", "Key (optional)", ref key, RestraintKey.MaxLength))
        {
            cmd.LockKey = key.Trim().Length == 0 ? null : key;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cmd.LockKey is { } k && !RestraintKey.IsValidPassword(k) ? $"Too short - not sent. {KeyHelpText}" : KeyHelpText);
        if (!RestraintStruggle.Accepts(cmd.Command))
            return;
        var level = (int)cmd.Struggle;
        Layout.ContinueRowOrWrap(InlineFieldWidth);
        ImGui.SetNextItemWidth(InlineFieldWidth);
        if (ImGui.Combo($"##ownerStruggleInline_{id}", ref level, RestraintStruggle.LevelNames, RestraintStruggle.LevelNames.Length))
        {
            cmd.Struggle = (StruggleLevel)level;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(cmd.Struggle == StruggleLevel.None ? StruggleHelpText : RestraintStruggle.Describe(cmd.StruggleSetting));
        if (cmd.Struggle == StruggleLevel.None || cmd.LockSeconds is null)
            return;
        var penalty = cmd.StrugglePenaltyMinutes;
        Layout.ContinueRowOrWrap(InlineFieldWidth);
        ImGui.SetNextItemWidth(InlineFieldWidth);
        if (ImGui.InputInt($"##ownerStrugglePenalty_{id}", ref penalty, 1, 5))
        {
            cmd.StrugglePenaltyMinutes = Math.Clamp(penalty, 0, RestraintStruggle.MaxPenaltyMinutes);
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Minutes added to the timer for each failed try (0 for none).");
    }

    /// For editors that keep the setting themselves. Returns true when it changed.
    public static bool DrawStruggle(string id, ref StruggleLevel level, ref int penaltyMinutes, bool timed)
    {
        var before = (level, penaltyMinutes);
        var index = (int)level;
        Layout.ItemWidth(140);
        if (ImGui.Combo($"Struggle##ownerStruggle_{id}", ref index, RestraintStruggle.LevelNames, RestraintStruggle.LevelNames.Length))
            level = (StruggleLevel)index;
        IconGlyph.HelpMarker(level == StruggleLevel.None ? StruggleHelpText : $"{RestraintStruggle.Describe(new StruggleSetting(level, penaltyMinutes))}. {StruggleHelpText}");
        if (level != StruggleLevel.None && timed)
        {
            Layout.ItemWidth(120);
            if (ImGui.InputInt($"min per failed try##ownerStrugglePenalty_{id}", ref penaltyMinutes, 1, 5))
                penaltyMinutes = Math.Clamp(penaltyMinutes, 0, RestraintStruggle.MaxPenaltyMinutes);
        }
        return (level, penaltyMinutes) != before;
    }

    private static void DrawLockInline(string id, QuickCommand cmd, PluginConfig config)
    {
        var gesture = IsGesture(cmd.Command);
        var ownerLock = IsOwnerLock(cmd.Command);
        var names = gesture ? GestureModeNames : ownerLock ? OwnerLockModeNames : ModeNames;
        var mode = cmd.LockSeconds is null ? 0 : 1;
        var comboWidth = ModeComboWidth(names);
        Layout.ContinueRowOrWrap(comboWidth);
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.Combo($"##ownerLockInline_{id}", ref mode, names, names.Length))
        {
            cmd.LockSeconds = mode == 0 ? null : gesture ? DefaultHoldSeconds : DefaultTimedMinutes * 60;
            config.Save();
        }
        TutorialService.Anchor(TutorialAnchors.QuickLock);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(gesture ? GestureHelpText : ownerLock ? OwnerLockHelpText : HelpText);

        if (cmd.LockSeconds is not { } seconds)
            return;
        if (gesture)
        {
            var hold = ClampHold(seconds);
            Layout.ContinueRowOrWrap(InlineFieldWidth);
            ImGui.SetNextItemWidth(InlineFieldWidth);
            if (ImGui.InputInt($"##ownerHoldInlineSeconds_{id}", ref hold, 5, 30))
            {
                cmd.LockSeconds = ClampHold(hold);
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Seconds ({RestraintLock.Format(TimeSpan.FromSeconds(ClampHold(cmd.LockSeconds.Value)))})");
            ImGui.SameLine();
            ImGui.TextDisabled("sec");
            return;
        }
        var minutes = Math.Max(1, seconds / 60);
        Layout.ContinueRowOrWrap(InlineFieldWidth);
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

    /// For editors that keep the key themselves. Returns true when it changed.
    public static bool DrawKey(string id, ref string key)
    {
        Layout.ItemWidth(140);
        var changed = ImGui.InputTextWithHint($"Key##ownerKey_{id}", "optional", ref key, RestraintKey.MaxLength);
        IconGlyph.HelpMarker(KeyHelpText);
        if (key.Trim().Length > 0 && !RestraintKey.IsValidPassword(key))
            IconGlyph.WrappedColored(Theme.Warning, $"A key needs {RestraintKey.MinLength} to {RestraintKey.MaxLength} characters - this one won't be sent.");
        return changed;
    }

    /// Returns true when the value changed.
    public static bool Draw(string id, ref int? lockSeconds) => Draw(id, ref lockSeconds, gesture: false);

    public static bool Draw(string id, ref int? lockSeconds, bool gesture, bool ownerLock = false)
    {
        var before = lockSeconds;
        var mode = lockSeconds is null ? 0 : 1;
        var names = gesture ? GestureModeNames : ownerLock ? OwnerLockModeNames : ModeNames;
        ImGui.SetNextItemWidth(Math.Max(Layout.Scaled(120), ModeComboWidth(names)));
        if (ImGui.Combo(gesture ? $"Hold##ownerHold_{id}" : $"Lock##ownerLock_{id}", ref mode, names, names.Length))
            lockSeconds = mode == 0 ? null : gesture ? DefaultHoldSeconds : DefaultTimedMinutes * 60;
        IconGlyph.HelpMarker(gesture ? GestureHelpText : ownerLock ? OwnerLockHelpText : HelpText);

        if (gesture && lockSeconds is { } holdSeconds)
        {
            var hold = ClampHold(holdSeconds);
            Layout.ItemWidth(120);
            if (ImGui.InputInt($"Seconds##ownerHoldSeconds_{id}", ref hold, 5, 30))
                lockSeconds = ClampHold(hold);
            ImGui.SameLine();
            ImGui.TextDisabled($"= {RestraintLock.Format(TimeSpan.FromSeconds(lockSeconds!.Value))}");
        }
        else if (lockSeconds is { } seconds)
        {
            var minutes = Math.Max(1, seconds / 60);
            Layout.ItemWidth(120);
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
