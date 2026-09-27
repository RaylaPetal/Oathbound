using System;
using System.Numerics;

namespace Oathbound.Plugin.Safety;

/// collar/reactions "Emote trigger with direction filter": which side of the local player a reaction's
/// emote must come from. `Any` is the zero value so a reaction saved without it matches from anywhere.
public enum ReactionDirection
{
    Any,
    InFront,
    Behind,
}

/// Classifies where a source stands relative to an observer's position and facing - ported from ReactToMe's
/// `DirectionClassifier`. Only the horizontal (X/Z) plane counts. In front and behind are each a 120-degree
/// arc (60 degrees either side of straight ahead / straight back, matching FFXIV's own rear-positional
/// convention); the two 60-degree arcs left over at the sides are neither.
///
/// Forward is (sin(rotation), cos(rotation)) in X/Z. ReactToMe never confirmed this sign convention in-game:
/// if testing shows In front and Behind swapped, negate the rotation here.
public static class DirectionClassifier
{
    private const float ConeHalfWidthDegrees = 60f;

    public static bool Matches(ReactionDirection filter, Vector3 observerPosition, float observerRotation, Vector3 sourcePosition)
    {
        if (filter == ReactionDirection.Any)
            return true;

        var toSource = new Vector2(sourcePosition.X - observerPosition.X, sourcePosition.Z - observerPosition.Z);
        if (toSource.LengthSquared() < 0.0001f)
            return filter == ReactionDirection.InFront; // standing on top of each other: treat as in front

        toSource = Vector2.Normalize(toSource);
        var forward = new Vector2(MathF.Sin(observerRotation), MathF.Cos(observerRotation));
        var degrees = MathF.Acos(Math.Clamp(Vector2.Dot(forward, toSource), -1f, 1f)) * (180f / MathF.PI);

        return filter switch
        {
            ReactionDirection.InFront => degrees <= ConeHalfWidthDegrees,
            ReactionDirection.Behind => degrees >= 180f - ConeHalfWidthDegrees,
            _ => false,
        };
    }
}
