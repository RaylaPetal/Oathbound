using System.Collections.Generic;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;

namespace Oathbound.Plugin.UI;

/// collar/ui-organization "Sub-side modules are gated on their required dependencies" (design.md D14): the one
/// table of which module/permission needs which external plugin. UI-only - command handlers already fail
/// closed on a missing IPC and don't consult this, so the two can't disagree about what's *allowed*, only
/// briefly about what's *shown* (DependencyStatusService's refresh window).
public static class DependencyGates
{
    /// Modules that can't work at all without these. Anything not listed is never tile-gated (partly dependent
    /// modules gate individual features inline instead; Toy Control is where Intiface gets connected).
    private static readonly Dictionary<string, DependencyId[]> ModuleRequirements = new()
    {
        ["title"] = [DependencyId.Honorific],
        ["outfit"] = [DependencyId.Glamourer],
        ["animation"] = [DependencyId.Penumbra],
        ["moodles"] = [DependencyId.Moodles],
        ["restraints"] = [DependencyId.Glamourer, DependencyId.Penumbra],
    };

    public static readonly DependencyId[] Teleport = [DependencyId.Lifestream, DependencyId.Vnavmesh];

    /// Why `moduleId`'s nav tile/window is unavailable right now, or null when it's usable. Only ever gates
    /// while there is an active pairing and it's Sub-side (spec: never for Owner-side pairings or with no
    /// active pairing) - an Owner sends commands without needing any plugin.
    public static string? ModuleBlockedReason(Plugin plugin, string moduleId)
    {
        if (plugin.Configuration.ActivePairing is not { Direction: PairingDirection.SubSide })
            return null;
        return ModuleRequirements.TryGetValue(moduleId, out var required) ? plugin.DependencyStatus.MissingReason(required) : null;
    }

    /// Red note for a Permissions row whose category needs a plugin that isn't detected, or null.
    public static string? PermissionNote(Plugin plugin, params DependencyId[] required) => plugin.DependencyStatus.MissingReason(required);

    /// What a Sub-side Custom Trigger part needs, by its category (Chat needs nothing).
    public static DependencyId[] CustomTriggerPart(CustomTriggerActionKind kind) => kind switch
    {
        CustomTriggerActionKind.Title => [DependencyId.Honorific],
        CustomTriggerActionKind.Outfit => [DependencyId.Glamourer],
        CustomTriggerActionKind.Gesture => [DependencyId.Penumbra],
        CustomTriggerActionKind.Moodle => [DependencyId.Moodles],
        CustomTriggerActionKind.Restraint => [DependencyId.Glamourer, DependencyId.Penumbra],
        _ => [],
    };

    /// Inline feature gating: the reason a single feature is unavailable, or null. Unlike tile gating this
    /// applies in both roles - callers only use it on features that run on the local client.
    public static string? FeatureBlockedReason(Plugin plugin, DependencyId required) => plugin.DependencyStatus.MissingReason([required]);
}
