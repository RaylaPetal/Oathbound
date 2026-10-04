using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Safety;

/// Engage/Release must be idempotent.
public interface IRestrictionEnforcer
{
    bool IsAvailable { get; }
    void Engage();
    void Release();
}

/// Per-owner claims for restriction rule kinds. Kinds with per-instance config (pose target, chosen animation,
/// Gagged's Customize+ preset) conflict-check on it; the rest are reference-counted and release with the last holder.
public sealed class RestrictionRuleManager
{
    private readonly Dictionary<RestraintRuleKind, Dictionary<string, string>> activeByKind = new();
    private readonly Dictionary<RestraintRuleKind, IRestrictionEnforcer> enforcers = new();

    public void RegisterEnforcer(RestraintRuleKind kind, IRestrictionEnforcer enforcer) => enforcers[kind] = enforcer;

    public bool IsActive(RestraintRuleKind kind) => activeByKind.TryGetValue(kind, out var owners) && owners.Count > 0;

    public bool CanActivate(IEnumerable<RestraintRuleAssignment> rules, out RestraintRuleKind unavailable)
    {
        foreach (var rule in rules)
        {
            // Arms/Legs Cuffed only hold an animation (or are only drawn) and have no enforcer. Full Body Cuffed and mod Forced Pose also immobilize,
            // and Gagged always needs ChatGagService.
            if (rule.Kind is RestraintRuleKind.ArmsCuffed or RestraintRuleKind.LegsCuffed)
                continue;

            if (!enforcers.TryGetValue(rule.Kind, out var enforcer) || !enforcer.IsAvailable)
            {
                unavailable = rule.Kind;
                return false;
            }
        }
        unavailable = default;
        return true;
    }

    /// Null for kinds/instances with no such configuration. Gagged's animation is cosmetic and never checked.
    private static string? ConfigKey(RestraintRuleAssignment rule) => rule.Kind switch
    {
        RestraintRuleKind.ForcedPose => rule.PoseModeId == 0 ? $"mod:{rule.AnimationId}" : rule.PoseModeId.ToString(),
        RestraintRuleKind.ArmsCuffed or RestraintRuleKind.LegsCuffed or RestraintRuleKind.FullBodyCuffed => rule.AnimationId,
        RestraintRuleKind.Gagged => rule.CustomizePresetId,
        _ => null,
    };

    /// Only a config-checked kind can conflict, and only with a different owner. A cuff with no animation holds
    /// nothing, so it never conflicts with one that does.
    public bool WouldConflict(IEnumerable<RestraintRuleAssignment> rules, string owner)
    {
        foreach (var rule in rules)
        {
            var configKey = ConfigKey(rule);
            if (configKey is null)
                continue;
            if (!activeByKind.TryGetValue(rule.Kind, out var owners) || owners.Count == 0)
                continue;
            if (owners.Any(kv => kv.Key != owner && kv.Value != configKey && !(CuffSets.IsCuff(rule.Kind) && kv.Value.Length == 0)))
                return true;
        }
        return false;
    }

    /// Refuses, activating nothing, on any conflict.
    public bool TryActivate(string owner, IReadOnlyList<RestraintRuleAssignment> rules)
    {
        if (rules.Count == 0)
            return false;
        if (WouldConflict(rules, owner))
        {
            Plugin.Log.Warning($"RestrictionRuleManager: \"{owner}\" refused - a rule conflicts with a different configuration already active.");
            return false;
        }
        if (!CanActivate(rules, out var unavailable))
        {
            Plugin.Log.Warning($"RestrictionRuleManager: '{owner}' refused - {unavailable} enforcement is unavailable.");
            return false;
        }

        foreach (var rule in rules)
        {
            var owners = activeByKind.TryGetValue(rule.Kind, out var existing) ? existing : activeByKind[rule.Kind] = new Dictionary<string, string>();
            var wasEmpty = owners.Count == 0;
            owners[owner] = ConfigKey(rule) ?? "";
            if (wasEmpty && enforcers.TryGetValue(rule.Kind, out var enforcer))
                enforcer.Engage();
        }

        return true;
    }

    /// An enforcer only disengages once no other owner holds that kind.
    public void Release(string owner)
    {
        foreach (var (kind, owners) in activeByKind)
        {
            if (!owners.Remove(owner))
                continue;
            if (owners.Count == 0 && enforcers.TryGetValue(kind, out var enforcer))
                enforcer.Release();
        }
    }

    /// Releases every enforcer regardless of refcount.
    public void ReleaseAllForPanic()
    {
        activeByKind.Clear();
        foreach (var enforcer in enforcers.Values)
            enforcer.Release();
    }
}
