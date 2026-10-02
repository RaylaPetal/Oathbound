using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
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

    // Body volumes as fractions of torso length (neck to waist), so they fit every race and height.
    private const float NeckRadius = 0.11f;
    private const float ChestRadius = 0.32f;
    private const float WaistRadius = 0.34f;
    private const float HeadRadius = 0.24f;
    private const float HipRadius = 0.20f;
    private const float KneeRadius = 0.14f;
    private const float AnkleRadius = 0.10f;
    /// Clear of the skin rather than touching it, since the line has width on screen.
    private const float BodyMargin = 0.02f;
    private const int ClearancePasses = 3;
    /// Keeps a bent-around point from ending up under the floor.
    private const float MinPointClearance = 0.05f;

    /// A game window bigger than this share of the screen is a full-screen layer (nameplates, fades, screen text);
    /// cutting it out would hide the leash entirely.
    private const float FullScreenFraction = 0.5f;

    // Deep crimson. ImGui packs colors as ABGR.
    private static readonly uint LeashColor = ImGui.GetColorU32(new Vector4(0.68f, 0.07f, 0.10f, 1f));

    private static readonly PctDrawHints Hints = new()
    {
        // Additive blending washes the red out to pink against bright backgrounds.
        AlphaBlendMode = AlphaBlendMode.None,
        // The default OccludedAlpha of 1 means no occlusion; fade strongly behind walls.
        DefaultParams = new PctDxParams { OccludedAlpha = 0.15f, OcclusionTolerance = 0.05f },
    };

    /// A tapered capsule: radius goes from RadiusA at A to RadiusB at B. A sphere when A == B.
    private readonly record struct Capsule(Vector3 A, Vector3 B, float RadiusA, float RadiusB);

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
    private readonly Vector3[] smoothed = new Vector3[Segments + 1];
    /// Head, neck-to-chest, chest-to-waist and a thigh and shin per leg, for each of the two characters.
    private readonly Capsule[] capsules = new Capsule[14];
    private int capsuleCount;
    /// Screen rectangles of visible game windows, collected once per frame.
    private readonly List<(Vector2 Min, Vector2 Max)> uiRects = new();
    private bool warnedUiRects;
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
            if (!config.ShowLeashLine || entries.Count == 0) return;
            CollectUiRects();
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

    /// Pictomancy's own UI mask switches off under DLSS/FSR or 3D resolution scaling, so the game's visible windows are
    /// also cut out of the line. Any failure here only loses the extra clipping, never the line.
    private unsafe void CollectUiRects()
    {
        uiRects.Clear();
        if (pictomancy is null) return;
        try
        {
            var stage = AtkStage.Instance();
            if (stage == null || stage->RaptureAtkUnitManager == null) return;
            var screen = ImGuiHelpers.MainViewport.Size;
            var fullScreenArea = screen.X * screen.Y * FullScreenFraction;
            ref var loaded = ref stage->RaptureAtkUnitManager->AtkUnitManager.AllLoadedUnitsList;
            for (var i = 0; i < loaded.Count; i++)
            {
                var unit = loaded.Entries[i].Value;
                if (unit == null || !unit->IsVisible || unit->Alpha == 0) continue;
                var root = unit->RootNode;
                if (root == null || !root->IsVisible()) continue;
                // Always visible and screen-sized, but backstops the size check in case its root node is ever smaller.
                if (unit->Name.StartsWith("NamePlate\0"u8)) continue;
                var size = new Vector2(root->Width, root->Height) * unit->Scale;
                if (size.X <= 0f || size.Y <= 0f || size.X * size.Y > fullScreenArea) continue;
                var min = new Vector2(unit->X, unit->Y);
                uiRects.Add((min, min + size));
            }
        }
        catch (Exception ex)
        {
            uiRects.Clear();
            if (!warnedUiRects)
            {
                warnedUiRects = true;
                Plugin.Log.Warning(ex, "Leash line: couldn't read the game's windows - the line may draw over the UI under DLSS/FSR.");
            }
        }
    }

    /// Ease-out cubic: the line slows as it reaches the collar.
    private static float Ease(float p) => 1f - MathF.Pow(1f - p, 3f);

    private void DrawPair(ICharacter sub, ICharacter owner, float length, float extent)
    {
        if (extent < MinVisibleExtent) return;
        if (!BoneLocator.TryGetPose(sub, out var subPose) || !subPose.TryGetWorld(BoneLocator.Neck, out var neck) ||
            !BoneLocator.TryGetPose(owner, out var ownerPose) || !ownerPose.TryGetWorld(BoneLocator.RightHand, out var hand))
        {
            if (warnedPairs.Add((sub.GameObjectId, owner.GameObjectId)))
                Plugin.Log.Warning($"Leash line: couldn't find the neck/hand bone for this pair - not drawing it.");
            return;
        }

        capsuleCount = 0;
        var subTorso = AddBody(subPose);
        AddBody(ownerPose);

        // The neck bone sits inside the neck; start from its surface on the Owner's side.
        var toHand = new Vector3(hand.X - neck.X, 0f, hand.Z - neck.Z);
        if (subTorso > 0f && toHand.LengthSquared() > 1e-6f)
            neck += Vector3.Normalize(toHand) * (subTorso * NeckRadius);

        var groundY = MathF.Min(sub.Position.Y, owner.Position.Y);
        BuildCurve(neck, hand, length, groundY);
        KeepClearOfBodies(hand - neck, groundY);
        var count = VisibleFromHand(extent);

        if (pictomancy is not null)
        {
            using var drawList = PctService.Draw(hints: Hints);
            // Null in cutscenes or while the screen is faded.
            if (drawList is null) return;
            foreach (var (min, max) in uiRects)
                drawList.AddClipZone(min, max);
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

    /// Adds the character's body volumes and returns its torso length, or 0 when the torso can't be measured, in which
    /// case its body is skipped and the line is simply drawn without clearance there.
    private float AddBody(BoneLocator.Pose pose)
    {
        if (!pose.TryGetWorld(BoneLocator.Neck, out var neck) || !pose.TryGetWorld(BoneLocator.Waist, out var waist))
            return 0f;
        var torso = Vector3.Distance(neck, waist);
        if (torso < 0.05f) return 0f;

        if (pose.TryGetWorld(BoneLocator.UpperChest, out var chest))
        {
            capsules[capsuleCount++] = new Capsule(neck, chest, torso * NeckRadius, torso * ChestRadius);
            capsules[capsuleCount++] = new Capsule(chest, waist, torso * ChestRadius, torso * WaistRadius);
        }
        else
        {
            capsules[capsuleCount++] = new Capsule(neck, waist, torso * NeckRadius, torso * WaistRadius);
        }
        if (pose.TryGetWorld(BoneLocator.Head, out var head))
            capsules[capsuleCount++] = new Capsule(head, head, torso * HeadRadius, torso * HeadRadius);
        AddLeg(pose, BoneLocator.LeftLeg, torso);
        AddLeg(pose, BoneLocator.RightLeg, torso);
        return torso;
    }

    private void AddLeg(BoneLocator.Pose pose, BoneLocator.LegBones leg, float torso)
    {
        if (!pose.TryGetWorld(leg.Hip, out var hip) || !pose.TryGetWorld(leg.Knee, out var knee))
            return;
        capsules[capsuleCount++] = new Capsule(hip, knee, torso * HipRadius, torso * KneeRadius);
        if (pose.TryGetWorld(leg.Ankle, out var ankle))
            capsules[capsuleCount++] = new Capsule(knee, ankle, torso * KneeRadius, torso * AnkleRadius);
    }

    /// Pushes the curve's inner points out of every body volume, smoothing between passes so it drapes around a body
    /// instead of kinking. The two ends stay where they are.
    private void KeepClearOfBodies(Vector3 span, float groundY)
    {
        if (capsuleCount == 0) return;
        // For a point right on a bone axis, where there's no outward direction to push along.
        var fallback = new Vector3(span.X, 0f, span.Z);
        fallback = fallback.LengthSquared() > 1e-6f ? Vector3.Normalize(fallback) : Vector3.UnitX;

        for (var pass = 0; pass < ClearancePasses; pass++)
        {
            for (var i = 1; i < Segments; i++)
                for (var c = 0; c < capsuleCount; c++)
                    samples[i] = PushOut(samples[i], capsules[c], fallback);

            smoothed[0] = samples[0];
            smoothed[Segments] = samples[Segments];
            for (var i = 1; i < Segments; i++)
                smoothed[i] = 0.25f * samples[i - 1] + 0.5f * samples[i] + 0.25f * samples[i + 1];
            Array.Copy(smoothed, samples, Segments + 1);
        }

        // Last, so smoothing can't pull a point back inside.
        for (var i = 1; i < Segments; i++)
        {
            for (var c = 0; c < capsuleCount; c++)
                samples[i] = PushOut(samples[i], capsules[c], fallback);
            samples[i].Y = MathF.Max(samples[i].Y, groundY + MinPointClearance);
        }
    }

    private static Vector3 PushOut(Vector3 point, Capsule capsule, Vector3 fallback)
    {
        var axis = capsule.B - capsule.A;
        var lengthSquared = axis.LengthSquared();
        var t = lengthSquared > 1e-8f ? Math.Clamp(Vector3.Dot(point - capsule.A, axis) / lengthSquared, 0f, 1f) : 0f;
        var closest = capsule.A + axis * t;
        var radius = float.Lerp(capsule.RadiusA, capsule.RadiusB, t) + BodyMargin;
        var offset = point - closest;
        var distanceSquared = offset.LengthSquared();
        if (distanceSquared >= radius * radius) return point;
        var direction = distanceSquared > 1e-8f ? offset / MathF.Sqrt(distanceSquared) : fallback;
        return closest + direction * radius;
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
