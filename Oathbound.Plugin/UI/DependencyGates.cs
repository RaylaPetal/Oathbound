using System.Collections.Generic;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;

namespace Oathbound.Plugin.UI;

/// Which module/permission needs which external plugin. UI-only: command handlers fail closed on their own.
public static class DependencyGates
{
    /// Modules that can't work at all without these. Partly dependent modules gate features inline instead.
    private static readonly Dictionary<string, DependencyId[]> ModuleRequirements = new()
    {
        ["title"] = [DependencyId.Honorific],
        ["outfit"] = [DependencyId.Glamourer],
        ["animation"] = [DependencyId.Penumbra],
        ["moodles"] = [DependencyId.Moodles],
        ["restraints"] = [DependencyId.Glamourer, DependencyId.Penumbra],
    };

    public static readonly DependencyId[] Teleport = [DependencyId.Lifestream, DependencyId.Vnavmesh];

    /// Null when usable. Only gates for an active Sub-side pairing - an Owner needs no plugin to send.
    public static string? ModuleBlockedReason(Plugin plugin, string moduleId)
    {
        if (plugin.Configuration.ActivePairing is not { Direction: PairingDirection.SubSide })
            return null;
        return ModuleRequirements.TryGetValue(moduleId, out var required) ? plugin.DependencyStatus.MissingReason(required) : null;
    }

    public static string? PermissionNote(Plugin plugin, params DependencyId[] required) => plugin.DependencyStatus.MissingReason(required);

    /// Chat needs nothing.
    public static DependencyId[] CustomTriggerPart(CustomTriggerActionKind kind) => kind switch
    {
        CustomTriggerActionKind.Title => [DependencyId.Honorific],
        CustomTriggerActionKind.Outfit => [DependencyId.Glamourer],
        CustomTriggerActionKind.Gesture => [DependencyId.Penumbra],
        CustomTriggerActionKind.Moodle => [DependencyId.Moodles],
        CustomTriggerActionKind.Restraint => [DependencyId.Glamourer, DependencyId.Penumbra],
        _ => [],
    };

    /// Unlike tile gating this applies in both roles, for features that run on the local client.
    public static string? FeatureBlockedReason(Plugin plugin, DependencyId required) => plugin.DependencyStatus.MissingReason([required]);
}
