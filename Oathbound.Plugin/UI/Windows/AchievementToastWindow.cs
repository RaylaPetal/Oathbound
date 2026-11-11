using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI;
using Oathbound.Plugin.Achievements;

namespace Oathbound.Plugin.UI;

/// One unlock at a time, top centre. Waits out combat, cutscenes, GPose and area changes, so it never covers anything
/// that matters.
public sealed class AchievementToastWindow : Window
{
    private const float SlideSeconds = 0.25f;
    private const float HoldSeconds = 5f;
    private const float FadeSeconds = 0.4f;

    private readonly Plugin plugin;
    private AchievementToast? current;
    private float elapsed;
    private bool soundFailed;

    public AchievementToastWindow(Plugin plugin) : base("###OathboundAchievementToast",
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
        ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoDocking)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        IsOpen = true;
    }

    private static bool Busy()
    {
        var c = Plugin.Condition;
        return c[ConditionFlag.InCombat] || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51]
            || c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78]
            || Plugin.ClientState.IsGPosing;
    }

    public override bool DrawConditions()
    {
        var state = plugin.Configuration.Achievements;
        if (current is null)
        {
            if (Busy() || !plugin.Achievements.TryDequeueToast(out var next))
                return false;
            if (!state.ShowToasts)
            {
                // The sound is its own setting; drained so turning popups back on doesn't replay a backlog.
                PlaySound(state.Sound);
                while (plugin.Achievements.TryDequeueToast(out _)) { }
                return false;
            }
            current = next;
            elapsed = 0f;
            PlaySound(state.Sound);
        }
        return true;
    }

    public void PlaySound(int chatSound)
    {
        if (chatSound is < 1 or > 16 || soundFailed)
            return;
        try
        {
            UIGlobals.PlayChatSoundEffect((uint)chatSound);
        }
        catch (Exception ex)
        {
            soundFailed = true;
            Plugin.Log.Warning(ex, "Achievement sound failed - sound is off for this session.");
        }
    }

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        var slide = Math.Clamp(elapsed / SlideSeconds, 0f, 1f);
        var y = viewport.Pos.Y + viewport.Size.Y * 0.12f - (1f - slide) * Layout.Scaled(30f);
        Position = new Vector2(viewport.Pos.X + viewport.Size.X / 2f, y);
        PositionCondition = ImGuiCond.Always;
        ImGui.SetNextWindowPos(Position.Value, ImGuiCond.Always, new Vector2(0.5f, 0f));
        Position = null;
        var fadeStart = SlideSeconds + HoldSeconds;
        var alpha = elapsed < SlideSeconds ? slide : elapsed > fadeStart ? 1f - Math.Clamp((elapsed - fadeStart) / FadeSeconds, 0f, 1f) : 1f;
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Max(0.01f, alpha));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Theme.TileRounding);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.Scaled(14f), Layout.Scaled(10f)));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.CardBg);
        ImGui.PushStyleColor(ImGuiCol.Border, TierColor(current));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
    }

    public override void Draw()
    {
        if (current is not { } toast)
            return;
        var hovered = ImGui.IsWindowHovered();
        if (!hovered)
            elapsed += ImGui.GetIO().DeltaTime;

        var color = TierColor(toast);
        using (ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon))
            ImGui.TextColored(color, (toast.Def?.Icon ?? FontAwesomeIcon.Trophy).ToIconString());
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextColored(Theme.TextMuted, toast.Def is null ? "ACHIEVEMENTS" : $"ACHIEVEMENT UNLOCKED{TierSuffix(toast)}");
        ImGui.TextColored(color, toast.Title);
        ImGui.TextUnformatted(toast.Text);
        ImGui.EndGroup();

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            plugin.ModuleWindow.Show("achievements");
            elapsed = SlideSeconds + HoldSeconds + FadeSeconds;
        }
        if (elapsed >= SlideSeconds + HoldSeconds + FadeSeconds)
            current = null;
    }

    private static string TierSuffix(AchievementToast toast) =>
        toast.Def is { Tiered: true } def ? $" - {AchievementCatalog.TierName(toast.Tier, def.Tiers.Length).ToUpperInvariant()}" : "";

    public static Vector4 TierColor(AchievementToast? toast) =>
        toast?.Def is { Tiered: true } ? Theme.Tier(toast.Tier) : Theme.Accent;
}
