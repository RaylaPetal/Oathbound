using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Safety;

/// The Blindfolded rule's enforcer. Drawing is the overlay's job; this only says whether, and how strongly.
public sealed class BlindfoldService : IRestrictionEnforcer
{
    private bool engaged;

    /// Set once by Plugin: the rules of every engaged restraint.
    public Func<IEnumerable<RestraintRuleAssignment>> ActiveRules { get; set; } = static () => [];

    /// Set once by Plugin: whether the overlay could be created.
    public Func<bool> OverlayAvailable { get; set; } = static () => false;

    public bool IsAvailable => OverlayAvailable();

    public void Engage() => engaged = true;

    public void Release() => engaged = false;

    /// The strongest active blindfold, or null when none is on.
    public BlindfoldLevel? Level
    {
        get
        {
            if (!engaged)
                return null;
            var levels = ActiveRules().Where(r => r.Kind == RestraintRuleKind.Blindfolded).Select(r => r.BlindfoldLevel).ToList();
            return levels.Count > 0 ? levels.Min() : BlindfoldLevel.Full;
        }
    }
}
