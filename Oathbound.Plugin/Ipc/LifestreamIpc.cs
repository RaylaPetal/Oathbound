using System;
using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace Oathbound.Plugin.Ipc;

/// Hand-rolled mirror of Lifestream's IPC tags (it ships no API package). Optional dependency: every call fails
/// soft, so a Sub without Lifestream gets a clean refusal for Teleport, not a crash.
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
        // The call gate converts the int to Lifestream's own enum, so no compile-time reference is needed.
        getResidentialTerritory = Plugin.PluginInterface.GetIpcSubscriber<int, uint>("Lifestream.GetResidentialTerritory");
        getPlotEntrance = Plugin.PluginInterface.GetIpcSubscriber<uint, int, Vector3?>("Lifestream.GetPlotEntrance");
        // Held as an opaque object and handed straight back to Lifestream, never inspected.
        buildAddressBookEntry = Plugin.PluginInterface.GetIpcSubscriber<string, string, string, string, bool, bool, object?>("Lifestream.BuildAddressBookEntry");
        goToHousingAddress = Plugin.PluginInterface.GetIpcSubscriber<object, object>("Lifestream.GoToHousingAddress");
        isHere = Plugin.PluginInterface.GetIpcSubscriber<object, bool>("Lifestream.IsHere");
    }

    /// Probes IsBusy, the cheapest read; any failure means unavailable.
    public bool IsAvailable { get { try { isBusy.InvokeFunc(); return true; } catch { return false; } } }

    public bool TryIsBusy() { try { return isBusy.InvokeFunc(); } catch { return true; } }

    public void TryAbort() { try { abort.InvokeAction(); } catch (Exception ex) { Plugin.Log.Warning(ex, "Lifestream Abort failed."); } }

    public bool TryChangeWorld(string world) { try { return changeWorld.InvokeFunc(world); } catch (Exception ex) { Plugin.Log.Error(ex, $"Lifestream ChangeWorld(\"{world}\") failed."); return false; } }

    public bool TryTeleport(uint destination, byte subIndex) { try { return teleport.InvokeFunc(destination, subIndex); } catch (Exception ex) { Plugin.Log.Error(ex, $"Lifestream Teleport({destination}, {subIndex}) failed."); return false; } }

    public bool TryCanChangeInstance() { try { return canChangeInstance.InvokeFunc(); } catch { return false; } }

    /// 0 when the zone isn't split into instances, or on failure.
    public int TryGetCurrentInstance() { try { return getCurrentInstance.InvokeFunc(); } catch { return 0; } }

    public bool TryChangeInstance(int number) { try { changeInstance.InvokeAction(number); return true; } catch (Exception ex) { Plugin.Log.Error(ex, $"Lifestream ChangeInstance({number}) failed."); return false; } }

    /// 0 on failure.
    public uint TryGetResidentialTerritory(int kind) { try { return getResidentialTerritory.InvokeFunc(kind); } catch { return 0; } }

    /// `plot` is 0-based over the whole district: 0-29 main division, 30-59 subdivision.
    public Vector3? TryGetPlotEntrance(uint territory, int plot) { try { return getPlotEntrance.InvokeFunc(territory, plot); } catch { return null; } }

    /// `plot` is 1-based and selects the division by itself (31-60 = subdivision). Null if the world/district doesn't parse.
    public object? TryBuildAddressBookEntry(string world, string district, int ward, int plot)
    {
        try { return buildAddressBookEntry.InvokeFunc(world, district, ward.ToString(), plot.ToString(), false, false); }
        catch (Exception ex) { Plugin.Log.Error(ex, $"Lifestream BuildAddressBookEntry({world}, {district}, w{ward}, p{plot}) failed."); return null; }
    }

    public bool TryGoToHousingAddress(object entry) { try { goToHousingAddress.InvokeAction(entry); return true; } catch (Exception ex) { Plugin.Log.Error(ex, "Lifestream GoToHousingAddress failed."); return false; } }

    public bool TryIsHere(object entry) { try { return isHere.InvokeFunc(entry); } catch { return false; } }
}
