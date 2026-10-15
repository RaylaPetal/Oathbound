using System;
using System.Numerics;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.UI;

/// The cage's bounds as a rectangle on the ground with short corner posts, through WorldStrokeRenderer. The Sub sees their
/// closed cage; the Owner sees the cage they sent while it's reported armed or caged, and a dimmer preview of every
/// saved cage here while the Cage module is open. Only drawn while the viewer is in the cage's place.
public sealed class CageRenderer
{
    private const float PostHeight = 2f;
    /// Each edge is split so the flat fallback can drop just the part behind the camera.
    private const int EdgeSegments = 8;
    private const float GroundLift = 0.05f;
    private static readonly Vector3 CageColor = new(0.85f, 0.70f, 0.25f);

    private readonly Plugin plugin;
    private readonly WorldStrokeRenderer strokes;
    private readonly Vector3[] outline = new Vector3[EdgeSegments * 4];
    private readonly Vector3[] post = new Vector3[2];

    public CageRenderer(Plugin plugin, WorldStrokeRenderer strokes)
    {
        this.plugin = plugin;
        this.strokes = strokes;
    }

    public void Draw()
    {
        try
        {
            var config = plugin.Configuration;
            var preview = plugin.ModuleWindow.CagePreviewActive;
            if (Plugin.ObjectTable.LocalPlayer is null || CagePlaces.Current() is not { } here)
                return;
            WorldStrokeRenderer.Batch? batch = null;
            try
            {
                bool Ready(out WorldStrokeRenderer.Batch b)
                {
                    // Null in cutscenes or while the screen is faded.
                    batch ??= strokes.Begin();
                    b = batch ?? default;
                    return batch is not null;
                }

                if (plugin.CageService.Cage is { Phase: CagePhase.Caged } own && own.Place.SameAs(here) && Ready(out var subBatch))
                    DrawCage(subBatch, own.Position, own.HalfExtents, 1f);

                var sentId = config.ActivePairing is { Direction: PairingDirection.OwnerSide } pairing && SentIsHolding(pairing)
                    ? plugin.OwnerCageStatus.LastSent(pairing)?.CageId
                    : null;
                if (plugin.ModuleWindow.NewCagePreviewHalf is { } newHalf && Plugin.ObjectTable.LocalPlayer is { } owner && Ready(out var newBatch))
                    DrawCage(newBatch, owner.Position, newHalf, 0.7f);

                foreach (var cage in config.SavedCages)
                {
                    var sent = cage.Id == sentId;
                    if ((!sent && !preview) || !cage.Place.SameAs(here) || !Ready(out var ownerBatch))
                        continue;
                    DrawCage(ownerBatch, cage.Position, CageLimits.HalfExtents(cage.Radius, cage.RadiusZ), sent ? 1f : 0.45f);
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
            Plugin.Log.Warning(ex, "Drawn cage failed this frame.");
        }
    }

    private bool SentIsHolding(PairingState pairing) =>
        plugin.OwnerCageStatus.LastSent(pairing) is { Open: false } && plugin.OwnerCageStatus.For(pairing)?.State is not CageStatusReporter.None;

    private void DrawCage(WorldStrokeRenderer.Batch batch, Vector3 center, Vector2 half, float opacity)
    {
        var y = center.Y + GroundLift;
        Span<Vector3> corners =
        [
            new(center.X - half.X, y, center.Z - half.Y),
            new(center.X + half.X, y, center.Z - half.Y),
            new(center.X + half.X, y, center.Z + half.Y),
            new(center.X - half.X, y, center.Z + half.Y),
        ];
        for (var edge = 0; edge < 4; edge++)
        {
            var from = corners[edge];
            var to = corners[(edge + 1) % 4];
            for (var i = 0; i < EdgeSegments; i++)
                outline[edge * EdgeSegments + i] = Vector3.Lerp(from, to, i / (float)EdgeSegments);
        }

        var color = WorldStrokeRenderer.Color(CageColor, 1f, opacity);
        batch.Stroke(outline, color, WorldStrokeRenderer.Thickness(center, 24f, 1.5f, 5f), closed: true);
        foreach (var corner in corners)
        {
            post[0] = corner;
            post[1] = corner with { Y = corner.Y + PostHeight };
            batch.Stroke(post, color, WorldStrokeRenderer.Thickness(corner, 24f, 1.5f, 5f));
        }
    }
}
