using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;

namespace Oathbound.Plugin.Ipc;

public enum DependencyId
{
    Glamourer,
    Penumbra,
    Honorific,
    Moodles,
    CustomizePlus,
    Lifestream,
    Vnavmesh,
    Intiface,
}

public enum DependencyState
{
    Ready,
    NotInstalled,
    Disabled,
    NotResponding,
    /// Intiface Central only: this plugin can't tell whether the app is installed, only whether it's connected.
    NotConnected,
}

public sealed record DependencyInfo(DependencyId Id, string Name, string Purpose, string WhoNeedsIt);

/// collar/ui-organization "Settings shows a dependency status header" (design.md D13): the one place that
/// decides whether each external plugin/app is usable. The Settings header, nav tile gating, inline feature
/// gating and permission notes all read this; command handlers deliberately don't (they keep their own live
/// IPC probes and fail closed on their own).
///
/// Installed/disabled comes from Dalamud's InstalledPlugins, which an IPC probe alone can't tell apart; a
/// working probe always wins, so a plugin that answers is never reported missing over an internal-name
/// mismatch. Recomputed when Dalamud reports a plugin change and otherwise at most every RefreshInterval, so
/// UI reads every frame stay cheap.
public sealed class DependencyStatusService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    public static readonly IReadOnlyList<DependencyInfo> All =
    [
        new(DependencyId.Glamourer, "Glamourer", "Outfits, restraint gear, collar piece, Reaction outfit/item actions", "Sub (required). Owners don't need it to send commands."),
        new(DependencyId.Penumbra, "Penumbra", "Animations, mod-based restraints, Reaction mod actions", "Sub (required). Owners don't need it to send commands."),
        new(DependencyId.Honorific, "Honorific", "Titles", "Sub, for titles. Owners don't need it to send commands."),
        new(DependencyId.Moodles, "Moodles", "Moodles, attached moodles, Reaction moodle actions", "Sub (optional). Owners don't need it to send commands."),
        new(DependencyId.CustomizePlus, "Customize+", "Restraints' gag profile", "Sub (optional). Owners don't need it to send commands."),
        new(DependencyId.Lifestream, "Lifestream", "Teleport (world change, aetheryte and housing-ward travel)", "Sub, for Teleport. Owners don't need it to send Teleport."),
        new(DependencyId.Vnavmesh, "vnavmesh", "Teleport (navigating to the Owner's side)", "Sub, for Teleport. Owners don't need it to send Teleport."),
        new(DependencyId.Intiface, "Intiface Central", "Toy control (desktop app, connected from Toy Control)", "Sub, for toys."),
    ];

    private static readonly Dictionary<DependencyId, string> InternalNames = new()
    {
        [DependencyId.Glamourer] = "Glamourer",
        [DependencyId.Penumbra] = "Penumbra",
        [DependencyId.Honorific] = "Honorific",
        [DependencyId.Moodles] = "Moodles",
        [DependencyId.CustomizePlus] = "CustomizePlus",
        [DependencyId.Lifestream] = "Lifestream",
        [DependencyId.Vnavmesh] = "vnavmesh",
    };


    private readonly Dictionary<DependencyId, Func<bool>> probes;
    private readonly Func<bool> intifaceConnected;
    private readonly Dictionary<DependencyId, DependencyState> states = new();
    private DateTime lastRefresh = DateTime.MinValue;
    private bool dirty = true;

    public DependencyStatusService(GlamourerIpc glamourer, PenumbraIpc penumbra, HonorificIpc honorific, MoodlesIpc moodles, CustomizePlusIpc customizePlus, LifestreamIpc lifestream, VnavmeshIpc vnavmesh, IntifaceIpc intiface)
    {
        probes = new()
        {
            [DependencyId.Glamourer] = () => glamourer.IsAvailable,
            [DependencyId.Penumbra] = () => penumbra.IsAvailable,
            [DependencyId.Honorific] = () => honorific.IsAvailable,
            [DependencyId.Moodles] = () => moodles.IsAvailable,
            [DependencyId.CustomizePlus] = () => customizePlus.IsAvailable,
            [DependencyId.Lifestream] = () => lifestream.IsAvailable,
            [DependencyId.Vnavmesh] = () => vnavmesh.IsAvailable,
        };
        intifaceConnected = () => intiface.IsConnected;
        Plugin.PluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
    }

    public void Dispose() => Plugin.PluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args) => dirty = true;

    public DependencyState Get(DependencyId id)
    {
        EnsureFresh();
        return states.TryGetValue(id, out var state) ? state : DependencyState.NotInstalled;
    }

    public bool IsReady(DependencyId id) => Get(id) == DependencyState.Ready;

    /// The ids among `required` that aren't Ready, in the order given.
    public IReadOnlyList<DependencyId> Missing(IEnumerable<DependencyId> required) => required.Where(id => !IsReady(id)).ToList();

    /// e.g. "Requires Honorific (installed but disabled)." or null when nothing is missing.
    public string? MissingReason(IEnumerable<DependencyId> required)
    {
        var missing = Missing(required);
        if (missing.Count == 0)
            return null;
        return "Requires " + string.Join(" and ", missing.Select(id => $"{Info(id).Name} ({Describe(Get(id))})")) + ".";
    }

    public static DependencyInfo Info(DependencyId id) => All.First(d => d.Id == id);

    public static string Describe(DependencyState state) => state switch
    {
        DependencyState.Ready => "detected",
        DependencyState.NotInstalled => "not installed",
        DependencyState.Disabled => "installed but disabled",
        DependencyState.NotResponding => "loaded but not responding - possibly an incompatible version",
        DependencyState.NotConnected => "not connected",
        _ => "unknown",
    };

    private void EnsureFresh()
    {
        var now = DateTime.UtcNow;
        if (!dirty && now - lastRefresh < RefreshInterval)
            return;
        dirty = false;
        lastRefresh = now;

        var installed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var plugin in Plugin.PluginInterface.InstalledPlugins)
                installed[plugin.InternalName] = installed.GetValueOrDefault(plugin.InternalName) || plugin.IsLoaded;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Dependency status: couldn't read the installed plugin list.");
        }

        foreach (var (id, probe) in probes)
            states[id] = Resolve(probe, installed, InternalNames[id]);

        states[DependencyId.Intiface] = SafeProbe(intifaceConnected) ? DependencyState.Ready : DependencyState.NotConnected;
    }

    /// design.md D13 resolution order: a working probe wins; otherwise the installed list says why it's missing.
    private static DependencyState Resolve(Func<bool> probe, Dictionary<string, bool> installed, string internalName)
    {
        if (SafeProbe(probe))
            return DependencyState.Ready;
        if (!installed.TryGetValue(internalName, out var loaded))
            return DependencyState.NotInstalled;
        return loaded ? DependencyState.NotResponding : DependencyState.Disabled;
    }

    private static bool SafeProbe(Func<bool> probe)
    {
        try { return probe(); } catch { return false; }
    }
}
