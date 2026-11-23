using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// Which choices a lock control offers: restraints (timer, key, struggle), gestures (a hold), titles and moodles
/// (until cleared or timed) and outfits (also "not locked").
public enum LockKind
{
    Restraint,
    Gesture,
    OwnerLock,
    Outfit,
}

/// Inline continues the current row (list rows); Stacked puts each setting on its own labelled line.
public enum LockLayout
{
    Inline,
    Stacked,
}

/// What a restraint lock control offers beside the timer: a key, and a struggle setting where the command carries one.
public enum LockExtras
{
    None,
    Key,
    KeyAndStruggle,
}

/// One command's lock settings as a lock control edits them. `NotLocked` is an outfit sent as `outfit wear`.
public record struct LockSettings(int? Seconds, string? Key = null, StruggleLevel Struggle = StruggleLevel.None, int PenaltyMinutes = 0,
    bool NotLocked = false);

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
    private static readonly string[] OutfitModeNames = ["Not locked", "Until cleared", "Timed"];
    private const string OutfitLockPrefix = "outfit lock ";
    private const string OutfitWearPrefix = "outfit wear ";
    private static readonly string[] OwnerLockPrefixes = ["title create ", "title style ", "outfit lock ", "outfit wear ", "moodle apply ", $"moodle {CustomMoodle.CustomWord} "];
    private const int DefaultTimedMinutes = 30;
    private const int DefaultHoldSeconds = 30;

    public static bool IsGesture(string command)
    {
        var trimmed = command.Trim();
        return trimmed.StartsWith(GesturePrefix, StringComparison.OrdinalIgnoreCase)
            && !trimmed[GesturePrefix.Length..].Trim().Equals(CommandComposer.StopGestureWord, StringComparison.OrdinalIgnoreCase);
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
        return IsGesture(cmd.Command) ? $"holds {TimeText.Duration(TimeSpan.FromSeconds(ClampHold(seconds)))}"
            : $"locks {TimeText.Duration(RestraintLock.FromSeconds(seconds).Duration!.Value)}{(keyed ? ", with a key" : "")}";
    }

    private static int ClampHold(int seconds) => Math.Clamp(seconds, GestureCommand.MinHoldSeconds, GestureCommand.MaxHoldSeconds);

    public static bool IsOutfit(string command)
    {
        var trimmed = command.Trim();
        return trimmed.StartsWith(OutfitLockPrefix, StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith(OutfitWearPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static LockKind KindOf(string command) =>
        IsGesture(command) ? LockKind.Gesture : IsOutfit(command) ? LockKind.Outfit : IsOwnerLock(command) ? LockKind.OwnerLock : LockKind.Restraint;

    /// The command text stays the source of truth: `outfit wear` with no timer is the only "not locked" outfit.
    public static bool IsOutfitNotLocked(QuickCommand cmd) =>
        cmd.LockSeconds is null && cmd.Command.Trim().StartsWith(OutfitWearPrefix, StringComparison.OrdinalIgnoreCase);

    public static LockSettings SettingsOf(QuickCommand cmd) => new(cmd.LockSeconds, cmd.LockKey, cmd.Struggle, cmd.StrugglePenaltyMinutes,
        cmd.Command.Trim().StartsWith(OutfitWearPrefix, StringComparison.OrdinalIgnoreCase));

    /// An outfit's "not locked" is written back as its verb, so an outfit that never changed sends the same text.
    public static void Store(QuickCommand cmd, LockSettings settings)
    {
        cmd.LockSeconds = settings.Seconds;
        cmd.LockKey = settings.Key;
        cmd.Struggle = settings.Struggle;
        cmd.StrugglePenaltyMinutes = settings.PenaltyMinutes;
        if (!IsOutfit(cmd.Command) || settings.NotLocked == SettingsOf(cmd).NotLocked)
            return;
        var trimmed = cmd.Command.Trim();
        cmd.Command = (settings.NotLocked ? OutfitWearPrefix : OutfitLockPrefix) + trimmed[OutfitLockPrefix.Length..];
    }

    private const string GestureHelpText = "Until stopped: a looping animation or pose holds your Sub in place until you send Stop animation, and a one-shot until it finishes. Timed: your Sub is held for exactly this long, then the animation stops and they can move again by themselves. Needs your Sub on a plugin version that understands timed holds - older versions reject the command.";

    private const string OwnerLockHelpText = "Until cleared: stays on, locked, until you clear it (or your Sub panics). Timed: locked for this long, then it comes off by itself - a title is removed, an outfit goes back to your Sub's own look, a moodle comes off. The timer keeps counting while your Sub is logged out. Needs your Sub on a plugin version that understands timers - older versions ignore a timed command.";

    private const string OutfitHelpText = "Not locked: your Sub wears it and can change it freely. Until cleared: locked on until you unlock it (or your Sub panics). Timed: locked for this long, then it goes back to your Sub's own look - the timer keeps counting while your Sub is logged out. Needs your Sub on a plugin version that understands timers - older versions ignore a timed command.";

    private const string HelpText = "Permanent: stays locked until you use \"Unlock restraints\" (or your Sub panics). Timed: your Sub's restraints come off by themselves when the time runs out - the timer keeps counting even if your Sub logs out. Sending another restraint command replaces the timer. Needs your Sub on a plugin version that understands timers - older versions ignore the whole command.";

    private static float InlineFieldWidth => Layout.Scaled(100f);

    /// Wide enough for its longest choice plus the arrow, whatever the font size.
    private static float ModeComboWidth(string[] names) =>
        names.Max(n => ImGui.CalcTextSize(n).X) + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetFrameHeight();

    private const string StruggleHelpText = "Whether your Sub may try to struggle free of this lock: one roll per try, with a wait between tries. Getting free takes the restraints off and tells you. A Sub on an older Oathbound version just can't struggle.";

    private const string KeyHelpText = "Optional key (4-20 characters). Your Sub can type it to unlock this restraint, and you're told when they do. It never shows in chat, but a determined Sub could work out a short key from their own files.";

    /// Saves straight onto `cmd`: lock settings on a saved command are send options, not drafts.
    public static void DrawInline(string id, QuickCommand cmd, PluginConfig config) => DrawSaved(id, cmd, config, LockLayout.Inline);

    /// The same settings as DrawInline, one labeled line each.
    public static void DrawStacked(string id, QuickCommand cmd, PluginConfig config) => DrawSaved(id, cmd, config, LockLayout.Stacked);

    private static void DrawSaved(string id, QuickCommand cmd, PluginConfig config, LockLayout layout)
    {
        var settings = SettingsOf(cmd);
        if (!Draw(id, ref settings, KindOf(cmd.Command), layout, RestraintStruggle.Accepts(cmd.Command) ? LockExtras.KeyAndStruggle : LockExtras.Key))
            return;
        Store(cmd, settings);
        config.Save();
    }

    /// The one lock control. Returns true when anything changed. `extras` only applies to restraints.
    public static bool Draw(string id, ref LockSettings settings, LockKind kind, LockLayout layout, LockExtras extras = LockExtras.KeyAndStruggle)
    {
        var before = settings;
        var restraint = kind == LockKind.Restraint && extras != LockExtras.None;
        var struggle = restraint && extras == LockExtras.KeyAndStruggle;
        var stacked = layout == LockLayout.Stacked;
        var labelWidth = 0f;
        if (stacked)
        {
            string[] labels = [kind == LockKind.Gesture ? "Hold" : "Lock", "Key", "Struggle"];
            labelWidth = labels.Take(restraint ? struggle ? 3 : 2 : 1).Max(l => ImGui.CalcTextSize(l).X) + ImGui.GetStyle().ItemSpacing.X * 2;
            StackedLabel(labels[0], labelWidth);
        }

        DrawMode(id, ref settings, kind, stacked ? FirstThenSameLine() : Layout.ContinueRowOrWrap);
        if (!restraint)
            return settings != before;

        if (stacked)
            StackedLabel("Key", labelWidth);
        else
            Layout.ContinueRowOrWrap(InlineFieldWidth);
        DrawKeyField(id, ref settings, stacked ? Layout.Scaled(160f) : InlineFieldWidth);
        if (stacked && settings.Key is { } key && key.Trim().Length > 0 && !RestraintKey.IsValidPassword(key))
            Status.Line(StatusKind.Caution, $"A key needs {RestraintKey.MinLength} to {RestraintKey.MaxLength} characters - this one won't be sent.");

        if (!struggle)
            return settings != before;
        if (stacked)
            StackedLabel("Struggle", labelWidth);
        DrawStruggleFields(id, ref settings, stacked ? FirstThenSameLine() : Layout.ContinueRowOrWrap, stacked ? "min per failed try" : null);
        return settings != before;
    }

    private static void StackedLabel(string text, float width)
    {
        var x = ImGui.GetCursorPosX();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(text);
        ImGui.SameLine();
        ImGui.SetCursorPosX(x + width);
    }

    /// The first control stays where the label left the cursor; the rest follow on the same line.
    private static Action<float> FirstThenSameLine()
    {
        var first = true;
        return _ =>
        {
            if (first)
                first = false;
            else
                ImGui.SameLine();
        };
    }

    private static void DrawKeyField(string id, ref LockSettings settings, float width)
    {
        var key = settings.Key ?? "";
        ImGui.SetNextItemWidth(width);
        if (ImGui.InputTextWithHint($"##ownerKeyInline_{id}", "Key (optional)", ref key, RestraintKey.MaxLength))
            settings.Key = key.Trim().Length == 0 ? null : key;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(settings.Key is { } k && !RestraintKey.IsValidPassword(k) ? $"Too short - not sent. {KeyHelpText}" : KeyHelpText);
    }

    /// `place` positions each control before it's drawn; `penaltyUnit` labels the penalty field when given.
    private static void DrawStruggleFields(string id, ref LockSettings settings, Action<float> place, string? penaltyUnit)
    {
        var level = (int)settings.Struggle;
        place(InlineFieldWidth);
        ImGui.SetNextItemWidth(InlineFieldWidth);
        if (ImGui.Combo($"##ownerStruggleInline_{id}", ref level, RestraintStruggle.LevelNames, RestraintStruggle.LevelNames.Length))
            settings.Struggle = (StruggleLevel)level;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(settings.Struggle == StruggleLevel.None ? StruggleHelpText
                : RestraintStruggle.Describe(new StruggleSetting(settings.Struggle, settings.PenaltyMinutes)));
        if (settings.Struggle == StruggleLevel.None || settings.Seconds is null)
            return;
        var penalty = settings.PenaltyMinutes;
        place(InlineFieldWidth);
        ImGui.SetNextItemWidth(InlineFieldWidth);
        if (ImGui.InputInt($"##ownerStrugglePenalty_{id}", ref penalty, 1, 5))
            settings.PenaltyMinutes = Math.Clamp(penalty, 0, RestraintStruggle.MaxPenaltyMinutes);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Minutes added to the timer for each failed try (0 for none).");
        if (penaltyUnit is null)
            return;
        ImGui.SameLine();
        ImGui.TextDisabled(penaltyUnit);
    }

    private static void DrawMode(string id, ref LockSettings settings, LockKind kind, Action<float> place)
    {
        var gesture = kind == LockKind.Gesture;
        var names = kind switch
        {
            LockKind.Gesture => GestureModeNames,
            LockKind.OwnerLock => OwnerLockModeNames,
            LockKind.Outfit => OutfitModeNames,
            _ => ModeNames,
        };
        var timedMode = names.Length - 1;
        var mode = settings.Seconds is not null ? timedMode : kind == LockKind.Outfit && !settings.NotLocked ? 1 : 0;
        var comboWidth = ModeComboWidth(names);
        place(comboWidth);
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.Combo($"##ownerLockInline_{id}", ref mode, names, names.Length))
        {
            if (mode == timedMode)
                settings.Seconds ??= gesture ? DefaultHoldSeconds : DefaultTimedMinutes * 60;
            else
            {
                settings.Seconds = null;
                if (kind == LockKind.Outfit)
                    settings.NotLocked = mode == 0;
            }
        }
        TutorialService.Anchor(TutorialAnchors.QuickLock);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(kind switch
            {
                LockKind.Gesture => GestureHelpText,
                LockKind.OwnerLock => OwnerLockHelpText,
                LockKind.Outfit => OutfitHelpText,
                _ => HelpText,
            });

        if (settings.Seconds is not { } seconds)
            return;
        if (gesture)
        {
            var hold = ClampHold(seconds);
            place(InlineFieldWidth);
            ImGui.SetNextItemWidth(InlineFieldWidth);
            if (ImGui.InputInt($"##ownerHoldInlineSeconds_{id}", ref hold, 5, 30))
                settings.Seconds = ClampHold(hold);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Seconds ({TimeText.Duration(ClampHold(settings.Seconds.Value))})");
            ImGui.SameLine();
            ImGui.TextDisabled("sec");
            return;
        }
        var minutes = Math.Max(1, seconds / 60);
        place(InlineFieldWidth);
        ImGui.SetNextItemWidth(InlineFieldWidth);
        if (ImGui.InputInt($"##ownerLockInlineMinutes_{id}", ref minutes, 15, 60))
            settings.Seconds = Math.Clamp(minutes, (int)RestraintLock.MinDuration.TotalMinutes, (int)RestraintLock.MaxDuration.TotalMinutes) * 60;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Minutes ({TimeText.Duration(settings.Seconds.Value)})");
        ImGui.SameLine();
        ImGui.TextDisabled("min");
    }
}
