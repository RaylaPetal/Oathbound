using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;
using Pictomancy;

namespace Oathbound.Plugin.UI;

/// The neck-to-hand leash line, drawn from UiBuilder.Draw so Dalamud's UI hiding covers cutscenes and GPose.
/// Depth-aware via Pictomancy when available, else a flat projected line. Never touches the leash itself.
/// Draws out from the hand when a leash starts and withdraws into it when one ends; only real on/off animates.
public sealed class LeashRenderer : IDisposable
{
    /// Keeps a close pair's slack from dipping into the ground.
    private const float MinClearance = 0.5f;
    private const int Segments = 16;
    private const float AnimationMs = 1750f;
    /// A longer pause between draws (cutscene, hidden UI, loading) finishes in-flight animations instead of replaying them.
    private const long GapSnapMs = 500;
    private const float MinVisibleExtent = 0.01f;

    // Bright red. ImGui packs colors as ABGR.
    private static readonly uint LeashColor = ImGui.GetColorU32(new Vector4(0.95f, 0.08f, 0.10f, 1f));

    private static readonly PctDrawHints Hints = new()
    {
        // Additive blending washes the red out to pink against bright backgrounds.
        AlphaBlendMode = AlphaBlendMode.None,
        // The default OccludedAlpha of 1 means no occlusion; fade strongly behind walls.
        DefaultParams = new PctDxParams { OccludedAlpha = 0.15f, OcclusionTolerance = 0.05f },
    };

    private sealed class Entry
    {
        /// Linear 0..1; eased when drawn.
        public float Progress;
        public bool Active;
        public ulong SubId;
        public ulong OwnerId;
        public float Length;
        public ICharacter? Sub;
        public ICharacter? Owner;
    }

    private readonly PluginConfig config;
    private readonly StatusIndicatorState state;
    private readonly FollowCommand follow;
    private readonly PctContext? pictomancy;
    private readonly HashSet<(ulong, ulong)> warnedPairs = new();
    private readonly Vector3[] samples = new Vector3[Segments + 1];
    private readonly Vector3[] visible = new Vector3[Segments + 1];
    private readonly Dictionary<Guid, Entry> entries = new();
    private readonly HashSet<Guid> seen = new();
    private readonly HashSet<Guid> instantEnds = new();
    private readonly List<Guid> finished = new();
    private long lastUpdateAt;

    public LeashRenderer(PluginConfig config, StatusIndicatorState state, FollowCommand follow)
    {
        this.config = config;
        this.state = state;
        this.follow = follow;
        follow.LeashEnded += OnLeashEnded;
        try
        {
            // Only strokes are drawn, so the VFX renderer's signature scans are pure risk.
            pictomancy = PctService.Initialize(Plugin.PluginInterface, new PctOptions { EnableVfxRenderer = false });
        }
        catch (Exception ex)
        {
            pictomancy = null;
            Plugin.Log.Warning(ex, "Leash line: 3D drawing (Pictomancy) failed to initialize - falling back to a flat on-screen line.");
        }
    }

    /// Panic is the safeword: its line goes at once rather than withdrawing.
    private void OnLeashEnded(Guid pairingId, LeashEnd reason)
    {
        if (reason == LeashEnd.Panic)
            instantEnds.Add(pairingId);
    }

    public void Draw()
    {
        try
        {
            // No local player while loading, so no leash would read as on; treat it as a gap instead.
            if (Plugin.ObjectTable.LocalPlayer is null) return;
            Update();
            if (!config.ShowLeashLine) return;
            foreach (var entry in entries.Values)
            {
                if (entry.Sub is not null && entry.Owner is not null)
                    DrawPair(entry.Sub, entry.Owner, entry.Length, Ease(entry.Progress));
            }
        }
        catch (Exception ex)
        {
            // A game-structure surprise must never take down the draw loop.
            Plugin.Log.Warning(ex, "Leash line drawing failed this frame.");
        }
    }

    /// Tracked even while the line is turned off, so turning it back on doesn't replay a draw-out.
    private void Update()
    {
        var now = Environment.TickCount64;
        var elapsed = lastUpdateAt == 0 ? 0 : now - lastUpdateAt;
        lastUpdateAt = now;
        var step = elapsed / AnimationMs;
        if (elapsed > GapSnapMs)
        {
            step = 0f;
            finished.Clear();
            foreach (var (id, entry) in entries)
            {
                if (entry.Active) entry.Progress = 1f;
                else finished.Add(id);
            }
            foreach (var id in finished) entries.Remove(id);
        }

        seen.Clear();
        foreach (var (id, sub, owner, length) in state.ActiveLeashes())
        {
            seen.Add(id);
            if (!entries.TryGetValue(id, out var entry))
                entries[id] = entry = new Entry();
            entry.Active = true;
            entry.Length = length;
            entry.Sub = sub;
            entry.Owner = owner;
            if (sub is null || owner is null)
            {
                // Hidden but still leashed: comes back fully drawn, and ends without a withdraw if it ends now.
                entry.Progress = 1f;
                entry.SubId = entry.OwnerId = 0;
                continue;
            }
            entry.SubId = sub.GameObjectId;
            entry.OwnerId = owner.GameObjectId;
        }

        finished.Clear();
        foreach (var (id, entry) in entries)
        {
            if (!seen.Contains(id))
            {
                if (instantEnds.Contains(id) || entry.SubId == 0 || entry.OwnerId == 0)
                {
                    finished.Add(id);
                    continue;
                }
                entry.Active = false;
                entry.Sub = Plugin.ObjectTable.SearchById(entry.SubId) as ICharacter;
                entry.Owner = Plugin.ObjectTable.SearchById(entry.OwnerId) as ICharacter;
                if (entry.Sub is null || entry.Owner is null)
                {
                    finished.Add(id);
                    continue;
                }
            }
            entry.Progress = Math.Clamp(entry.Progress + (entry.Active ? step : -step), 0f, 1f);
            if (!entry.Active && entry.Progress <= 0f)
                finished.Add(id);
        }
        foreach (var id in finished) entries.Remove(id);
        instantEnds.Clear();
    }

    /// Ease-out cubic: the line slows as it reaches the collar.
    private static float Ease(float p) => 1f - MathF.Pow(1f - p, 3f);

    private void DrawPair(ICharacter sub, ICharacter owner, float length, float extent)
    {
        if (extent < MinVisibleExtent) return;
        if (!BoneLocator.TryGetWorld(sub, BoneLocator.Neck, out var neck) ||
            !BoneLocator.TryGetWorld(owner, BoneLocator.RightHand, out var hand))
        {
            if (warnedPairs.Add((sub.GameObjectId, owner.GameObjectId)))
                Plugin.Log.Warning($"Leash line: couldn't find the neck/hand bone for this pair - not drawing it.");
            return;
        }

        BuildCurve(neck, hand, length, MathF.Min(sub.Position.Y, owner.Position.Y));
        var count = VisibleFromHand(extent);

        if (pictomancy is not null)
        {
            using var drawList = PctService.Draw(hints: Hints);
            // Null in cutscenes or while the screen is faded.
            if (drawList is null) return;
            for (var i = 0; i < count; i++)
                drawList.PathLineTo(visible[i]);
            drawList.PathStroke(LeashColor, PctStrokeFlags.None, Thickness(neck, hand));
            return;
        }

        DrawFlat(count, Thickness(neck, hand));
    }

    /// Sag is half the spare leash (sqrt(L^2 - d^2) / 2): slack when close, straight at the full length.
    /// Capped so the midpoint stays MinClearance above the ground.
    private void BuildCurve(Vector3 neck, Vector3 hand, float length, float groundY)
    {
        var distance = Vector3.Distance(neck, hand);
        var sag = 0.5f * MathF.Sqrt(MathF.Max(length * length - distance * distance, 0f));
        var midY = (neck.Y + hand.Y) / 2f;
        sag = Math.Clamp(sag, 0f, MathF.Max(midY - (groundY + MinClearance), 0f));
        for (var i = 0; i <= Segments; i++)
        {
            var t = i / (float)Segments;
            samples[i] = Vector3.Lerp(neck, hand, t) - Vector3.UnitY * (sag * 4f * t * (1f - t));
        }
    }

    /// The hand-side part of the curve covering `extent` of it, with the leading end interpolated within its
    /// segment so it moves smoothly. Returns the number of points written to `visible`.
    private int VisibleFromHand(float extent)
    {
        var start = (1f - Math.Clamp(extent, 0f, 1f)) * Segments;
        var first = Math.Min((int)start, Segments - 1);
        visible[0] = Vector3.Lerp(samples[first], samples[first + 1], start - first);
        var count = 1;
        for (var i = first + 1; i <= Segments; i++)
            visible[count++] = samples[i];
        return count;
    }

    /// Thinner the farther the camera is.
    private static unsafe float Thickness(Vector3 neck, Vector3 hand)
    {
        var camera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
        if (camera == null || camera->CurrentCamera == null) return 3f;
        var p = camera->CurrentCamera->Position;
        var distance = Vector3.Distance(new Vector3(p.X, p.Y, p.Z), (neck + hand) / 2f);
        return Math.Clamp(24f / MathF.Max(distance, 1f), 1.5f, 6f);
    }

    private void DrawFlat(int count, float thickness)
    {
        var drawList = ImGui.GetBackgroundDrawList();
        for (var i = 0; i < count - 1; i++)
        {
            // Skip a segment with an end behind the camera rather than draw it to a wrapped point.
            if (!Plugin.GameGui.WorldToScreen(visible[i], out var a) || !Plugin.GameGui.WorldToScreen(visible[i + 1], out var b))
                continue;
            drawList.AddLine(a, b, LeashColor, thickness);
        }
    }

    public void Dispose()
    {
        follow.LeashEnded -= OnLeashEnded;
        pictomancy?.Dispose();
    }
}
