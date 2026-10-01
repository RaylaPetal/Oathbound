using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;
using Pictomancy;

namespace Oathbound.Plugin.UI;

/// collar/leash-visual: the neck-to-hand leash line, drawn from UiBuilder.Draw (so Dalamud's own UI hiding
/// already covers cutscenes, GPose and a hidden UI). Depth-aware through Pictomancy when it initialized;
/// otherwise a flat projected ImGui line with the same anchors (spec: "falls back to a simpler on-screen
/// line"). Never sends anything and never touches the leash itself.
public sealed class LeashRenderer : IDisposable
{
    /// The curve's lowest point never goes below this height above the lower character's feet (about waist
    /// height), so a close pair's slack can't dip into the ground.
    private const float MinClearance = 0.5f;
    private const int Segments = 16;

    // Dark leather. ImGui packs colors as ABGR.
    private static readonly uint LeashColor = ImGui.GetColorU32(new Vector4(0.28f, 0.16f, 0.09f, 1f));

    private static readonly PctDrawHints Hints = new()
    {
        // Additive blending (Pictomancy's default) would make a dark line vanish.
        AlphaBlendMode = AlphaBlendMode.None,
        // Pictomancy's default OccludedAlpha is 1 (no occlusion); the spec wants the line hidden or
        // strongly faded behind walls.
        DefaultParams = new PctDxParams { OccludedAlpha = 0.15f, OcclusionTolerance = 0.05f },
    };

    private readonly PluginConfig config;
    private readonly StatusIndicatorState state;
    private readonly PctContext? pictomancy;
    private readonly HashSet<(ulong, ulong)> warnedPairs = new();
    private readonly Vector3[] samples = new Vector3[Segments + 1];

    public LeashRenderer(PluginConfig config, StatusIndicatorState state)
    {
        this.config = config;
        this.state = state;
        try
        {
            // The VFX renderer is off: we only draw strokes, so its signature scans are pure risk here.
            pictomancy = PctService.Initialize(Plugin.PluginInterface, new PctOptions { EnableVfxRenderer = false });
        }
        catch (Exception ex)
        {
            pictomancy = null;
            Plugin.Log.Warning(ex, "Leash line: 3D drawing (Pictomancy) failed to initialize - falling back to a flat on-screen line.");
        }
    }

    public void Draw()
    {
        if (!config.ShowLeashLine) return;

        try
        {
            foreach (var (sub, owner, length) in state.LeashedPairs())
                DrawPair(sub, owner, length);
        }
        catch (Exception ex)
        {
            // A game-structure surprise must never take down the draw loop (spec: fails closed).
            Plugin.Log.Warning(ex, "Leash line drawing failed this frame.");
        }
    }

    private void DrawPair(ICharacter sub, ICharacter owner, float length)
    {
        if (!BoneLocator.TryGetWorld(sub, BoneLocator.Neck, out var neck) ||
            !BoneLocator.TryGetWorld(owner, BoneLocator.RightHand, out var hand))
        {
            if (warnedPairs.Add((sub.GameObjectId, owner.GameObjectId)))
                Plugin.Log.Warning($"Leash line: couldn't find the neck/hand bone for this pair - not drawing it.");
            return;
        }

        BuildCurve(neck, hand, length, MathF.Min(sub.Position.Y, owner.Position.Y));

        if (pictomancy is not null)
        {
            using var drawList = PctService.Draw(hints: Hints);
            // Null in cutscenes / while the screen is faded - the spec wants no line then anyway.
            if (drawList is null) return;
            foreach (var point in samples)
                drawList.PathLineTo(point);
            drawList.PathStroke(LeashColor, PctStrokeFlags.None, Thickness(neck, hand));
            return;
        }

        DrawFlat(Thickness(neck, hand));
    }

    /// A parabola hanging between the anchors: sag is half the "spare" leash (sqrt(L^2 - d^2) / 2), so it
    /// droops when close and is exactly straight at or beyond the leash length (collar/leash: the engaged
    /// length, so a long leash hangs slack well before the Sub reaches its end). Capped so the midpoint stays
    /// MinClearance above the ground.
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

    /// Pixel thickness, thinner the farther the camera is from the leash's midpoint.
    private static unsafe float Thickness(Vector3 neck, Vector3 hand)
    {
        var camera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
        if (camera == null || camera->CurrentCamera == null) return 3f;
        var p = camera->CurrentCamera->Position;
        var distance = Vector3.Distance(new Vector3(p.X, p.Y, p.Z), (neck + hand) / 2f);
        return Math.Clamp(24f / MathF.Max(distance, 1f), 1.5f, 6f);
    }

    private void DrawFlat(float thickness)
    {
        var drawList = ImGui.GetBackgroundDrawList();
        for (var i = 0; i < Segments; i++)
        {
            // Skip any segment with an end behind the camera rather than drawing it to a wrapped point.
            if (!Plugin.GameGui.WorldToScreen(samples[i], out var a) || !Plugin.GameGui.WorldToScreen(samples[i + 1], out var b))
                continue;
            drawList.AddLine(a, b, LeashColor, thickness);
        }
    }

    public void Dispose() => pictomancy?.Dispose();
}
