using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.UI;

/// Drawn cuffs and chains on wrists and ankles, through WorldStrokeRenderer. Appears and disappears with the restraint,
/// with no animation. Never touches the restraint itself.
public sealed class RestraintRenderer
{
    private const int RingPoints = 12;
    private const int ChainSegments = 8;

    // Fractions of torso length (neck to waist), so cuffs fit every race and height.
    private const float WristRadius = 0.10f;
    private const float AnkleRadius = 0.12f;
    /// How far up the forearm or shin from the joint a cuff sits.
    private const float WristOffset = 0.06f;
    private const float AnkleOffset = 0.10f;
    /// Used only for sizing when the torso can't be measured; the anchors still come from the bones.
    private const float FallbackTorso = 0.45f;

    /// A chain is this much longer than the gap it spans, so it hangs; the link from wrists to ankles hangs less.
    private const float ChainSlack = 1.3f;
    private const float LinkSlack = 1.1f;
    private const float MinChainLength = 0.08f;
    private const float ChainClearance = 0.02f;

    private readonly PluginConfig config;
    private readonly StatusIndicatorState state;
    private readonly WorldStrokeRenderer strokes;
    private readonly HashSet<ulong> warned = new();
    private readonly Vector3[] ring = new Vector3[RingPoints];
    private readonly Vector3[] wristChain = new Vector3[ChainSegments + 1];
    private readonly Vector3[] ankleChain = new Vector3[ChainSegments + 1];
    private readonly Vector3[] link = new Vector3[ChainSegments + 1];

    private readonly record struct Cuff(Vector3 Center, Vector3 Axis, float Radius);

    public RestraintRenderer(PluginConfig config, StatusIndicatorState state, WorldStrokeRenderer strokes)
    {
        this.config = config;
        this.state = state;
        this.strokes = strokes;
    }

    public void Draw()
    {
        try
        {
            if (!config.ShowDrawnRestraints || Plugin.ObjectTable.LocalPlayer is null) return;
            WorldStrokeRenderer.Batch? batch = null;
            try
            {
                foreach (var (sub, cuffs) in state.ActiveCuffs())
                {
                    // Not drawn on this client (out of range, hidden): nothing to anchor to and nothing to warn about.
                    if (!BoneLocator.TryGetPose(sub, out var pose)) continue;
                    // Null in cutscenes or while the screen is faded.
                    batch ??= strokes.Begin();
                    if (batch is not { } b) return;
                    DrawCharacter(b, sub, pose, cuffs);
                }
            }
            finally
            {
                batch?.Dispose();
            }
        }
        catch (Exception ex)
        {
            // A game-structure surprise must never take down the draw loop.
            Plugin.Log.Warning(ex, "Drawn restraints failed this frame.");
        }
    }

    private void DrawCharacter(WorldStrokeRenderer.Batch batch, ICharacter sub, BoneLocator.Pose pose, CuffSet cuffs)
    {
        var torso = pose.TryGetWorld(BoneLocator.Neck, out var neck) && pose.TryGetWorld(BoneLocator.Waist, out var waist)
            ? Vector3.Distance(neck, waist) : 0f;
        if (torso < 0.05f) torso = FallbackTorso;

        var color = WorldStrokeRenderer.Color(config.RestraintColor, config.RestraintBrightness, config.RestraintOpacity);
        var groundY = sub.Position.Y;
        var missing = false;
        var wristsDrawn = false;
        var anklesDrawn = false;

        if (cuffs.HasFlag(CuffSet.Wrists))
        {
            if (TryCuff(pose, BoneLocator.LeftArm.Forearm, BoneLocator.LeftArm.Hand, torso * WristRadius, torso * WristOffset, out var left)
                && TryCuff(pose, BoneLocator.RightArm.Forearm, BoneLocator.RightArm.Hand, torso * WristRadius, torso * WristOffset, out var right))
            {
                DrawPair(batch, left, right, wristChain, groundY, color);
                wristsDrawn = true;
            }
            else missing = true;
        }

        if (cuffs.HasFlag(CuffSet.Ankles))
        {
            if (TryCuff(pose, BoneLocator.LeftLeg.Knee, BoneLocator.LeftLeg.Ankle, torso * AnkleRadius, torso * AnkleOffset, out var left)
                && TryCuff(pose, BoneLocator.RightLeg.Knee, BoneLocator.RightLeg.Ankle, torso * AnkleRadius, torso * AnkleOffset, out var right))
            {
                DrawPair(batch, left, right, ankleChain, groundY, color);
                anklesDrawn = true;
            }
            else missing = true;
        }

        if (cuffs.HasFlag(CuffSet.Linked) && wristsDrawn && anklesDrawn)
        {
            var top = wristChain[ChainSegments / 2];
            var bottom = ankleChain[ChainSegments / 2];
            WorldStrokeRenderer.SagCurve(link, top, bottom, MathF.Max(Vector3.Distance(top, bottom) * LinkSlack, MinChainLength), groundY, ChainClearance);
            batch.Stroke(link, color, ChainThickness((top + bottom) / 2f));
        }

        if (missing && warned.Add(sub.GameObjectId))
            Plugin.Log.Warning("Drawn restraints: couldn't find a wrist or ankle position on this character - drawing only what can be anchored.");
    }

    private void DrawPair(WorldStrokeRenderer.Batch batch, Cuff left, Cuff right, Vector3[] chain, float groundY, uint color)
    {
        DrawRing(batch, left, color);
        DrawRing(batch, right, color);
        var a = EdgeToward(left, right.Center);
        var b = EdgeToward(right, left.Center);
        WorldStrokeRenderer.SagCurve(chain, a, b, MathF.Max(Vector3.Distance(a, b) * ChainSlack, MinChainLength), groundY, ChainClearance);
        batch.Stroke(chain, color, ChainThickness((a + b) / 2f));
    }

    /// The joint's own bone gives the anchor; the bone above it gives the limb's direction.
    private static bool TryCuff(BoneLocator.Pose pose, string upper, string joint, float radius, float offset, out Cuff cuff)
    {
        cuff = default;
        if (!pose.TryGetWorld(upper, out var from) || !pose.TryGetWorld(joint, out var at)) return false;
        var axis = at - from;
        if (axis.LengthSquared() < 1e-6f) return false;
        axis = Vector3.Normalize(axis);
        cuff = new Cuff(at - axis * offset, axis, radius);
        return true;
    }

    private void DrawRing(WorldStrokeRenderer.Batch batch, Cuff cuff, uint color)
    {
        var (u, v) = Basis(cuff.Axis);
        for (var i = 0; i < RingPoints; i++)
        {
            var angle = MathF.Tau * i / RingPoints;
            ring[i] = cuff.Center + cuff.Radius * (MathF.Cos(angle) * u + MathF.Sin(angle) * v);
        }
        batch.Stroke(ring, color, WorldStrokeRenderer.Thickness(cuff.Center, 30f, 2f, 7f), closed: true);
    }

    /// The point on the cuff's ring facing `target`, so a chain leaves from the cuff rather than from inside the limb.
    private static Vector3 EdgeToward(Cuff cuff, Vector3 target)
    {
        var toward = target - cuff.Center;
        toward -= Vector3.Dot(toward, cuff.Axis) * cuff.Axis;
        if (toward.LengthSquared() < 1e-8f) toward = Basis(cuff.Axis).U;
        return cuff.Center + Vector3.Normalize(toward) * cuff.Radius;
    }

    private static (Vector3 U, Vector3 V) Basis(Vector3 axis)
    {
        var reference = MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(axis, reference));
        return (u, Vector3.Cross(axis, u));
    }

    private static float ChainThickness(Vector3 at) => WorldStrokeRenderer.Thickness(at, 18f, 1.2f, 4f);
}
