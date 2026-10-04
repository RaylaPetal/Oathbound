using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Oathbound.Plugin.Config;
using Pictomancy;

namespace Oathbound.Plugin.UI;

/// World-space strokes for the leash line and drawn restraints, drawn from UiBuilder.Draw so Dalamud's UI hiding covers
/// cutscenes and GPose. Depth-aware via Pictomancy when available, else flat projected lines.
public sealed class WorldStrokeRenderer : IDisposable
{
    /// A game window bigger than this share of the screen is a full-screen layer (nameplates, fades, screen text);
    /// cutting it out would hide every stroke.
    private const float FullScreenFraction = 0.5f;

    private static readonly PctDrawHints Hints = new()
    {
        // Additive blending washes colors out against bright backgrounds.
        AlphaBlendMode = AlphaBlendMode.None,
        // The default OccludedAlpha of 1 means no occlusion; fade strongly behind walls.
        DefaultParams = new PctDxParams { OccludedAlpha = 0.15f, OcclusionTolerance = 0.05f },
    };

    private readonly PctContext? pictomancy;
    /// Screen rectangles of visible game windows, collected once per frame however many batches draw.
    private readonly List<(Vector2 Min, Vector2 Max)> uiRects = new();
    private int uiRectsFrame = -1;
    private bool warnedUiRects;

    public WorldStrokeRenderer()
    {
        try
        {
            // Only strokes are drawn, so the VFX renderer's signature scans are pure risk.
            pictomancy = PctService.Initialize(Plugin.PluginInterface, new PctOptions { EnableVfxRenderer = false });
        }
        catch (Exception ex)
        {
            pictomancy = null;
            Plugin.Log.Warning(ex, "3D drawing (Pictomancy) failed to initialize - falling back to flat on-screen lines.");
        }
    }

    /// Null while nothing may be drawn (cutscenes, faded screen). Dispose the batch to submit it.
    public Batch? Begin()
    {
        if (pictomancy is null) return new Batch(null);
        CollectUiRects();
        var drawList = PctService.Draw(hints: Hints);
        if (drawList is null) return null;
        foreach (var (min, max) in uiRects)
            drawList.AddClipZone(min, max);
        return new Batch(drawList);
    }

    public readonly struct Batch : IDisposable
    {
        private readonly PctDrawList? drawList;

        internal Batch(PctDrawList? drawList) => this.drawList = drawList;

        public void Stroke(ReadOnlySpan<Vector3> points, uint color, float thickness, bool closed = false)
        {
            if (points.Length < 2) return;
            if (drawList is not null)
            {
                foreach (var point in points)
                    drawList.PathLineTo(point);
                drawList.PathStroke(color, closed ? PctStrokeFlags.Closed : PctStrokeFlags.None, thickness);
                return;
            }

            var flat = ImGui.GetBackgroundDrawList();
            var segments = closed ? points.Length : points.Length - 1;
            for (var i = 0; i < segments; i++)
            {
                // Skip a segment with an end behind the camera rather than draw it to a wrapped point.
                if (!Plugin.GameGui.WorldToScreen(points[i], out var a) || !Plugin.GameGui.WorldToScreen(points[(i + 1) % points.Length], out var b))
                    continue;
                flat.AddLine(a, b, color, thickness);
            }
        }

        public void Dispose() => drawList?.Dispose();
    }

    /// Pictomancy's own UI mask switches off under DLSS/FSR or 3D resolution scaling, so the game's visible windows are
    /// also cut out. Any failure here only loses the extra clipping, never the strokes.
    private unsafe void CollectUiRects()
    {
        var frame = ImGui.GetFrameCount();
        if (frame == uiRectsFrame) return;
        uiRectsFrame = frame;
        uiRects.Clear();
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
                Plugin.Log.Warning(ex, "Couldn't read the game's windows - leash and restraint lines may draw over the UI under DLSS/FSR.");
            }
        }
    }

    /// Not GetColorU32: that multiplies in the current ImGui style alpha. Clamped here too, for hand-edited configs.
    public static uint Color(Vector3 color, float brightness, float opacity) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(
            Vector3.Clamp(color, Vector3.Zero, Vector3.One) * Math.Clamp(brightness, PluginConfig.MinLeashBrightness, 1f),
            Math.Clamp(opacity, PluginConfig.MinLeashOpacity, 1f)));

    /// Thinner the farther the camera is: `near` pixels at 1 yalm, clamped to [min, max].
    public static unsafe float Thickness(Vector3 at, float near, float min, float max)
    {
        var camera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
        if (camera == null || camera->CurrentCamera == null) return Math.Clamp(near / 8f, min, max);
        var p = camera->CurrentCamera->Position;
        var distance = Vector3.Distance(new Vector3(p.X, p.Y, p.Z), at);
        return Math.Clamp(near / MathF.Max(distance, 1f), min, max);
    }

    /// Sag is half the spare length (sqrt(L^2 - d^2) / 2): slack when close, straight at the full length.
    /// Capped so the midpoint stays `minClearance` above `groundY`.
    public static void SagCurve(Span<Vector3> output, Vector3 a, Vector3 b, float length, float groundY, float minClearance)
    {
        var distance = Vector3.Distance(a, b);
        var sag = 0.5f * MathF.Sqrt(MathF.Max(length * length - distance * distance, 0f));
        var midY = (a.Y + b.Y) / 2f;
        sag = Math.Clamp(sag, 0f, MathF.Max(midY - (groundY + minClearance), 0f));
        var last = output.Length - 1;
        for (var i = 0; i <= last; i++)
        {
            var t = i / (float)last;
            output[i] = Vector3.Lerp(a, b, t) - Vector3.UnitY * (sag * 4f * t * (1f - t));
        }
    }

    public void Dispose() => pictomancy?.Dispose();
}
