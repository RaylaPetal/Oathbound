using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Ipc;

namespace Oathbound.Plugin.Ipc;

public readonly record struct CustomizePlusProfile(Guid UniqueId, string Name);
public enum CustomizePlusScanStatus { Success, Unavailable, Failed }
public readonly record struct CustomizePlusScanResult(CustomizePlusScanStatus Status, IReadOnlyList<CustomizePlusProfile> Profiles, string? Error = null);

/// Mirrors Customize+'s profile IPC ("CustomizePlus.Profile.*"; ErrorCode 0 = Success). Toggles the Sub's own
/// saved profiles by id rather than submitting a temporary profile.
public sealed class CustomizePlusIpc
{
    private readonly ICallGateSubscriber<IList<(Guid UniqueId, string Name, string VirtualPath, List<(string Name, ushort WorldId, byte CharacterType, ushort CharacterSubType)> Characters, int Priority, bool IsEnabled)>> getProfileList;
    private readonly ICallGateSubscriber<Guid, int> enableProfileByUniqueId;
    private readonly ICallGateSubscriber<Guid, int> disableProfileByUniqueId;

    /// Any exception means unavailable.
    public bool IsAvailable { get { try { getProfileList.InvokeFunc(); return true; } catch { return false; } } }

    public CustomizePlusIpc()
    {
        getProfileList = Plugin.PluginInterface.GetIpcSubscriber<IList<(Guid, string, string, List<(string, ushort, byte, ushort)>, int, bool)>>("CustomizePlus.Profile.GetList");
        enableProfileByUniqueId = Plugin.PluginInterface.GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.EnableByUniqueId");
        disableProfileByUniqueId = Plugin.PluginInterface.GetIpcSubscriber<Guid, int>("CustomizePlus.Profile.DisableByUniqueId");
    }

    public CustomizePlusScanResult GetOwnProfiles()
    {
        try
        {
            var profiles = getProfileList.InvokeFunc().Select(p => new CustomizePlusProfile(p.UniqueId, p.Name)).ToList();
            return new CustomizePlusScanResult(CustomizePlusScanStatus.Success, profiles);
        }
        catch (Dalamud.Plugin.Ipc.Exceptions.IpcNotReadyError ex)
        {
            Plugin.Log.Warning(ex, "Customize+ is not available while reading profiles.");
            return new CustomizePlusScanResult(CustomizePlusScanStatus.Unavailable, [], "Customize+ is not installed or ready.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to read the local Customize+ profile library.");
            return new CustomizePlusScanResult(CustomizePlusScanStatus.Failed, [], ex.Message);
        }
    }

    public bool ApplyProfile(Guid profileId)
    {
        try { return enableProfileByUniqueId.InvokeFunc(profileId) == 0; }
        catch (Exception ex) { Plugin.Log.Error(ex, "Failed to enable a Customize+ profile."); return false; }
    }

    public bool RevertProfile(Guid profileId)
    {
        try { return disableProfileByUniqueId.InvokeFunc(profileId) == 0; }
        catch (Exception ex) { Plugin.Log.Error(ex, "Failed to disable a Customize+ profile."); return false; }
    }
}
