using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// One step: highlights the control registered under `Anchor`, after `Open` has brought it on screen.
/// A null Anchor is a text-only step, shown centered.
public sealed record TourStep(string Title, string Text, string? Anchor = null, Action<TutorialNavigator>? Open = null, string? MissingText = null);

public sealed record Tour(string Id, bool Owner, IReadOnlyList<TourStep> Steps)
{
    public bool IsOverview => Id == TutorialCatalog.OverviewId;
}

/// Only opens windows and tabs. Never sends, applies or changes anything, so a tour can't act for the user.
public sealed class TutorialNavigator
{
    private readonly Plugin plugin;

    public TutorialNavigator(Plugin plugin) => this.plugin = plugin;

    public void Main() => plugin.CollarWindow.OpenMainWindow();
    public void Module(string moduleId) => plugin.ModuleWindow.Show(moduleId);
    public void RulebookTab(string tab) => plugin.ModuleWindow.ShowRulebookTab(tab);
    public void Settings(SettingsTab tab) => plugin.SettingsWindow.ShowTab(tab);
    public void Favorites() => plugin.FavoritesWindow.IsOpen = true;
    public void SubControl() => plugin.SubControlWindow.IsOpen = true;
}

/// Guided tours: outlines one control per step and shows a bubble beside it. Draw code reports controls with
/// Anchor/AnchorRect right after drawing them; Draw runs after every window, so the rectangle is from this frame.
public sealed class TutorialService : IDisposable
{
    /// Frames to wait for an anchor before falling back to a centered bubble.
    private const int MissingAfterFrames = 10;
    private const float BubbleWidth = 340f;
    private const float Gap = 12f;

    public static TutorialService? Current { get; private set; }

    private readonly Plugin plugin;
    private readonly TutorialNavigator navigator;
    private readonly Action<bool> setQuickAccessHighlight;
    private readonly Func<bool> quickAccessHidden;

    private Tour? tour;
    private int stepIndex;
    private int stepStartFrame;
    private bool scrolledToAnchor;
    private bool quickAccessHighlighted;

    private int anchorFrame = -1;
    private Vector2 anchorMin;
    private Vector2 anchorMax;
    private Vector2 lastBubbleSize = new(BubbleWidth, 160f);

    /// Set only while chaining a Switch's Owner overview into the Sub one.
    private bool chainSubOverview;

    public TutorialService(Plugin plugin, Action<bool> setQuickAccessHighlight, Func<bool> quickAccessHidden)
    {
        this.plugin = plugin;
        this.setQuickAccessHighlight = setQuickAccessHighlight;
        this.quickAccessHidden = quickAccessHidden;
        navigator = new TutorialNavigator(plugin);
        Current = this;
        TutorialCatalog.Validate();
    }

    public bool IsRunning => tour is not null;

    private TourStep? CurrentStep => tour is { } t && stepIndex < t.Steps.Count ? t.Steps[stepIndex] : null;

    /// Records the last drawn item if it is the current step's control. The first report in a frame wins.
    public static void Anchor(string key) => Current?.Report(key, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), scroll: true);

    /// For areas that aren't an ImGui item, like title-bar buttons. Screen coordinates.
    public static void AnchorRect(string key, Vector2 min, Vector2 max) => Current?.Report(key, min, max, scroll: false);

    private void Report(string key, Vector2 min, Vector2 max, bool scroll)
    {
        if (CurrentStep?.Anchor != key)
            return;
        var frame = ImGui.GetFrameCount();
        if (anchorFrame == frame)
            return;
        anchorFrame = frame;
        anchorMin = min;
        anchorMax = max;
        if (scroll && !scrolledToAnchor)
        {
            scrolledToAnchor = true;
            ImGuiP.ScrollToItem(ImGuiScrollFlags.KeepVisibleCenterY);
        }
    }

    public void Start(string tourId, bool owner)
    {
        if (TutorialCatalog.Get(tourId, owner) is not { Steps.Count: > 0 } next)
            return;
        EndQuickAccessHighlight();
        tour = next;
        GoTo(0);
    }

    public void StartOverview(PairingDirection direction) => Start(TutorialCatalog.OverviewId, direction == PairingDirection.OwnerSide);

    /// For Switch with both unseen, the Owner overview runs first and the Sub one follows it.
    public void StartOverviewIfUnseen(PluginRole role)
    {
        var config = plugin.Configuration;
        switch (role)
        {
            case PluginRole.Owner:
                if (!config.HasSeenOwnerTutorial) StartOverview(PairingDirection.OwnerSide);
                break;
            case PluginRole.Sub:
                if (!config.HasSeenSubTutorial) StartOverview(PairingDirection.SubSide);
                break;
            default:
                if (!config.HasSeenOwnerTutorial)
                {
                    chainSubOverview = !config.HasSeenSubTutorial;
                    StartOverview(PairingDirection.OwnerSide);
                }
                else if (!config.HasSeenSubTutorial)
                {
                    StartOverview(PairingDirection.SubSide);
                }
                break;
        }
    }

    private void GoTo(int index)
    {
        stepIndex = index;
        stepStartFrame = ImGui.GetFrameCount();
        scrolledToAnchor = false;
        anchorFrame = -1;
        try
        {
            CurrentStep?.Open?.Invoke(navigator);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Tutorial: a step couldn't open what it needs.");
        }
    }

    private void Next()
    {
        if (tour is not { } t)
            return;
        if (stepIndex + 1 < t.Steps.Count)
            GoTo(stepIndex + 1);
        else
            End();
    }

    /// Finishing, skipping and exiting all count as having seen the overview.
    public void End()
    {
        var ended = tour;
        tour = null;
        EndQuickAccessHighlight();
        if (ended is not { IsOverview: true })
            return;

        if (ended.Owner)
            plugin.Configuration.HasSeenOwnerTutorial = true;
        else
            plugin.Configuration.HasSeenSubTutorial = true;
        plugin.Configuration.Save();

        if (chainSubOverview && ended.Owner)
        {
            chainSubOverview = false;
            StartOverview(PairingDirection.SubSide);
        }
    }

    public void Draw()
    {
        if (CurrentStep is not { } step || tour is not { } t)
            return;

        try
        {
            var frame = ImGui.GetFrameCount();
            var anchored = step.Anchor is not null && anchorFrame == frame;
            UpdateQuickAccessHighlight(step);

            if (anchored)
                DrawOutline();

            var waiting = step.Anchor is not null && !anchored && frame - stepStartFrame < MissingAfterFrames;
            if (!waiting)
                DrawBubble(t, step, anchored);
        }
        catch (Exception ex)
        {
            // A tutorial problem must never take down the UI; drop the tour instead.
            Plugin.Log.Warning(ex, "Tutorial drawing failed - ending the tour.");
            tour = null;
            EndQuickAccessHighlight();
        }
    }

    private void DrawOutline()
    {
        var pulse = 0.65f + 0.35f * MathF.Sin((float)ImGui.GetTime() * 5f);
        var color = ImGui.GetColorU32(Theme.AccentHover with { W = pulse });
        var pad = new Vector2(4f, 3f);
        ImGui.GetForegroundDrawList().AddRect(anchorMin - pad, anchorMax + pad, color, 5f, ImDrawFlags.RoundCornersAll, 3f);
    }

    private void DrawBubble(Tour t, TourStep step, bool anchored)
    {
        ImGui.SetNextWindowPos(BubblePosition(anchored), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(new Vector2(BubbleWidth, 0), new Vector2(BubbleWidth, float.MaxValue));

        using var border = ImRaii.PushColor(ImGuiCol.Border, Theme.Accent);
        using var background = ImRaii.PushColor(ImGuiCol.WindowBg, Theme.CardBg);
        using var borderSize = ImRaii.PushStyle(ImGuiStyleVar.WindowBorderSize, 2f);
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding, 6f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;
        if (!ImGui.Begin("##oathboundTour", flags))
        {
            ImGui.End();
            return;
        }

        // Stay above other windows without taking focus from the control being explained.
        ImGuiP.BringWindowToDisplayFront(ImGuiP.GetCurrentWindow());

        ImGui.PushStyleColor(ImGuiCol.Text, Theme.AccentHover);
        ImGui.TextUnformatted(step.Title);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        var counter = $"{stepIndex + 1} / {t.Steps.Count}";
        ImGui.SetCursorPosX(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(counter).X);
        ImGui.TextDisabled(counter);
        ImGui.Separator();

        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + BubbleWidth - 2 * ImGui.GetStyle().WindowPadding.X);
        ImGui.TextUnformatted(StepText(step));
        if (step.Anchor is not null && !anchored)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warning);
            ImGui.TextUnformatted(step.MissingText ?? "This part isn't on screen right now - it may need a plugin, a pairing, or something added first. Next keeps going.");
            ImGui.PopStyleColor();
        }
        ImGui.PopTextWrapPos();

        ImGui.Spacing();
        var last = stepIndex + 1 >= t.Steps.Count;
        if (ImGui.Button(last ? "Finish" : "Next"))
            Next();
        ImGui.SameLine();
        if (ImGui.Button("Skip tour"))
            End();
        ImGui.SameLine();
        if (ImGui.Button("Exit"))
            End();

        lastBubbleSize = ImGui.GetWindowSize();
        ImGui.End();
    }

    private string StepText(TourStep step) =>
        step.Anchor == TutorialAnchors.QuickAccess && quickAccessHidden()
            ? step.Text + "\n\nIt's hidden right now: turn it back on in Dalamud's settings, under Server Info Bar."
            : step.Text;

    /// Right of the control, else left, else below; clamped to the screen. Centered when there's nothing to point at.
    private Vector2 BubblePosition(bool anchored)
    {
        var viewport = ImGui.GetMainViewport();
        var screenMin = viewport.WorkPos;
        var screenMax = viewport.WorkPos + viewport.WorkSize;
        Vector2 pos;
        if (!anchored)
        {
            var main = plugin.CollarWindow;
            var center = main.IsOpen && main.LastSize.X > 0 ? main.LastPosition + main.LastSize / 2f : screenMin + viewport.WorkSize / 2f;
            pos = center - lastBubbleSize / 2f;
        }
        else if (anchorMax.X + Gap + BubbleWidth <= screenMax.X)
            pos = new Vector2(anchorMax.X + Gap, anchorMin.Y);
        else if (anchorMin.X - Gap - BubbleWidth >= screenMin.X)
            pos = new Vector2(anchorMin.X - Gap - BubbleWidth, anchorMin.Y);
        else
            pos = new Vector2(anchorMin.X, anchorMax.Y + Gap);

        return Vector2.Clamp(pos, screenMin, Vector2.Max(screenMin, screenMax - lastBubbleSize));
    }

    private void UpdateQuickAccessHighlight(TourStep step)
    {
        var want = step.Anchor == TutorialAnchors.QuickAccess;
        if (want == quickAccessHighlighted)
            return;
        quickAccessHighlighted = want;
        setQuickAccessHighlight(want);
    }

    private void EndQuickAccessHighlight()
    {
        if (!quickAccessHighlighted)
            return;
        quickAccessHighlighted = false;
        setQuickAccessHighlight(false);
    }

    public void Dispose()
    {
        EndQuickAccessHighlight();
        if (Current == this)
            Current = null;
    }
}
