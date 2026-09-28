using System;
using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace Oathbound.Plugin.Ipc;

/// Thin wrapper around Lifestream's (NightmareXIV/Lifestream) EzIPC surface - ships no NuGet API package,
/// so this is a hand-rolled mirror of its documented `Lifestream.<Method>` IPC tags, same "no compile-time
/// reference" shape as HonorificIpc. Unlike Glamourer/Penumbra/Honorific/Moodles, Lifestream is optional -
/// collar/teleport is the only feature that depends on it, and every call here fails soft (returns
/// false/null, never throws) so a Sub without Lifestream installed gets a clean refusal, not a crash.
/// Only wraps what collar/teleport's travel stages need: world change, aetheryte teleport, public-instance
/// change, and outdoor housing-ward travel - never chara-select automation, property shortcuts, or aliases.
public sealed class LifestreamIpc
{
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<object> abort;
    private readonly ICallGateSubscriber<string, bool> changeWorld;
    private readonly ICallGateSubscriber<uint, byte, bool> teleport;
    private readonly ICallGateSubscriber<bool> canChangeInstance;
    private readonly ICallGateSubscriber<int> getCurrentInstance;
    private readonly ICallGateSubscriber<int, object> changeInstance;
    private readonly ICallGateSubscriber<int, uint> getResidentialTerritory;
    private readonly ICallGateSubscriber<uint, int, Vector3?> getPlotEntrance;
    private readonly ICallGateSubscriber<string, string, string, string, bool, bool, object?> buildAddressBookEntry;
    private readonly ICallGateSubscriber<object, object> goToHousingAddress;
    private readonly ICallGateSubscriber<object, bool> isHere;

    public LifestreamIpc()
    {
        isBusy = Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        abort = Plugin.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
        changeWorld = Plugin.PluginInterface.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
        teleport = Plugin.PluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        canChangeInstance = Plugin.PluginInterface.GetIpcSubscriber<bool>("Lifestream.CanChangeInstance");
        getCurrentInstance = Plugin.PluginInterface.GetIpcSubscriber<int>("Lifestream.GetCurrentInstance");
        changeInstance = Plugin.PluginInterface.GetIpcSubscriber<int, object>("Lifestream.ChangeInstance");
        // Lifestream's parameter is its own `ResidentialAetheryteKind` enum; Dalamud's call gate converts the
        // int across, so this never needs a compile-time reference to Lifestream's types.
        getResidentialTerritory = Plugin.PluginInterface.GetIpcSubscriber<int, uint>("Lifestream.GetResidentialTerritory");
        getPlotEntrance = Plugin.PluginInterface.GetIpcSubscriber<uint, int, Vector3?>("Lifestream.GetPlotEntrance");
        // design.md D12: the address entry is Lifestream's own `AddressBookEntryTuple` - held here only as an
        // opaque object and handed straight back to GoToHousingAddress/IsHere, never inspected.
        buildAddressBookEntry = Plugin.PluginInterface.GetIpcSubscriber<string, string, string, string, bool, bool, object?>("Lifestream.BuildAddressBookEntry");
        goToHousingAddress = Plugin.PluginInterface.GetIpcSubscriber<object, object>("Lifestream.GoToHousingAddress");
        isHere = Plugin.PluginInterface.GetIpcSubscriber<object, bool>("Lifestream.IsHere");
    }

    /// collar/teleport "Refuses when travel cannot safely happen" (Lifestream unavailable case): probes
    /// `IsBusy` - the cheapest read-only call on the surface - and treats any failure (not installed, not
    /// loaded yet, IPC contract mismatch) as unavailable rather than letting an IpcNotReadyError propagate.
    public bool IsAvailable { get { try { isBusy.InvokeFunc(); return true; } catch { return false; } } }

    public bool TryIsBusy() { try { return isBusy.InvokeFunc(); } catch { return true; } }

    public void TryAbort() { try { abort.InvokeAction(); } catch (Exception ex) { Plugin.Log.Warning(ex, "Lifestream Abort failed."); } }

    public bool TryChangeWorld(string world) { try { return changeWorld.InvokeFunc(world); } catch (Exception ex) { Plugin.Log.Error(ex, $"Lifestream ChangeWorld(\"{world}\") failed."); return false; } }

    public bool TryTeleport(uint destination, byte subIndex) { try { return teleport.InvokeFunc(destination, subIndex); } catch (Exception ex) { Plugin.Log.Error(ex, $"Lifestream Teleport({destination}, {subIndex}) failed."); return false; } }

    public bool TryCanChangeInstance() { try { return canChangeInstance.InvokeFunc(); } catch { return false; } }

    /// 0 when the zone isn't split into instances or on failure.
    public int TryGetCurrentInstance() { try { return getCurrentInstance.InvokeFunc(); } catch { return 0; } }

    public bool TryChangeInstance(int number) { try { changeInstance.InvokeAction(number); return true; } catch (Exception ex) { Plugin.Log.Error(ex, $"Lifestream ChangeInstance({number}) failed."); return false; } }

    /// Territory id of a residential district, keyed by Lifestream's ResidentialAetheryteKind value; 0 on failure.
    public uint TryGetResidentialTerritory(int kind) { try { return getResidentialTerritory.InvokeFunc(kind); } catch { return 0; } }

    /// World position of a plot's entrance. `plot` is 0-based over the whole district: 0-29 main division,
    /// 30-59 subdivision (Lifestream's HousingData list index).
    public Vector3? TryGetPlotEntrance(uint territory, int plot) { try { return getPlotEntrance.InvokeFunc(territory, plot); } catch { return null; } }

    /// `plot` here is 1-based and, for houses, selects the division by itself (31-60 = subdivision) -
    /// Lifestream only reads `isSubdivision` for apartments. Returns null when Lifestream can't parse the
    /// world or district name.
    public object? TryBuildAddressBookEntry(string world, string district, int ward, int plot)
    {
        try { return buildAddressBookEntry.InvokeFunc(world, district, ward.ToString(), plot.ToString(), false, false); }
        catch (Exception ex) { Plugin.Log.Error(ex, $"Lifestream BuildAddressBookEntry({world}, {district}, w{ward}, p{plot}) failed."); return null; }
    }

    public bool TryGoToHousingAddress(object entry) { try { goToHousingAddress.InvokeAction(entry); return true; } catch (Exception ex) { Plugin.Log.Error(ex, "Lifestream GoToHousingAddress failed."); return false; } }

    public bool TryIsHere(object entry) { try { return isHere.InvokeFunc(entry); } catch { return false; } }
}
