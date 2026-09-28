using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Ipc;
using ECommons.GameHelpers;

namespace Oathbound.Plugin.Ipc;

public readonly record struct MoodlesStatus(Guid Id, string Name);
public enum MoodlesScanStatus { Success, Unavailable, Failed }
public readonly record struct MoodlesScanResult(MoodlesScanStatus Status, IReadOnlyList<MoodlesStatus> Statuses, string? Error = null);

/// Exact consumer-side mirror of kawaii/Moodles' current IPCProcessor surface. Reads individual registered
/// statuses (buffs/debuffs) via `GetRegisteredMoodlesV2` rather than bundled presets via
/// `GetPresetsInfoListV2` - collar/moodles wants the Owner commanding a single status, not a preset. Status
/// enumeration is local-library-wide and takes no character argument. Player operations take GUID first,
/// then the actual IPlayerCharacter (not a character-name string).
public sealed class MoodlesIpc
{
    private readonly ICallGateSubscriber<List<(Guid ID, uint IconID, string FullPath, string Title)>> getRegisteredMoodles;
    private readonly ICallGateSubscriber<Guid, IPlayerCharacter, object> addOrUpdateMoodleByPlayer;
    private readonly ICallGateSubscriber<IPlayerCharacter, object> clearStatusManagerByPlayer;
    private readonly ICallGateSubscriber<Guid, IPlayerCharacter, object> removeMoodleByPlayer;

    /// collar/ui-organization dependency status: probes the same read-only status list the catalog scan uses
    /// (the one contract this wrapper already depends on); any exception means unavailable.
    public bool IsAvailable { get { try { getRegisteredMoodles.InvokeFunc(); return true; } catch { return false; } } }

    public MoodlesIpc()
    {
        getRegisteredMoodles = Plugin.PluginInterface.GetIpcSubscriber<List<(Guid, uint, string, string)>>("Moodles.GetRegisteredMoodlesV2");
        addOrUpdateMoodleByPlayer = Plugin.PluginInterface.GetIpcSubscriber<Guid, IPlayerCharacter, object>("Moodles.AddOrUpdateMoodleByPlayerV2");
        removeMoodleByPlayer = Plugin.PluginInterface.GetIpcSubscriber<Guid, IPlayerCharacter, object>("Moodles.RemoveMoodleByPlayerV2");
        clearStatusManagerByPlayer = Plugin.PluginInterface.GetIpcSubscriber<IPlayerCharacter, object>("Moodles.ClearStatusManagerByPlayerV2");
    }

    public MoodlesScanResult GetOwnStatuses()
    {
        try
        {
            var statuses = getRegisteredMoodles.InvokeFunc().Select(s => new MoodlesStatus(s.ID, s.Title)).ToList();
            return new MoodlesScanResult(MoodlesScanStatus.Success, statuses);
        }
        catch (Dalamud.Plugin.Ipc.Exceptions.IpcNotReadyError ex)
        {
            Plugin.Log.Warning(ex, "Moodles is not available while reading statuses.");
            return new MoodlesScanResult(MoodlesScanStatus.Unavailable, [], "Moodles is not installed or ready.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to read the local Moodles status library.");
            return new MoodlesScanResult(MoodlesScanStatus.Failed, [], ex.Message);
        }
    }

    public bool ApplyStatus(Guid statusId)
    {
        var player = Player.Object;
        if (player is null) return false;
        try { addOrUpdateMoodleByPlayer.InvokeAction(statusId, player); return true; }
        catch (Exception ex) { Plugin.Log.Error(ex, "Failed to apply a Moodles status."); return false; }
    }

    /// collar/attached-moodles: removes exactly one status, leaving every other moodle on the Sub alone -
    /// never falls back to ClearStatus, since wiping everything is precisely what this exists to avoid.
    public bool RemoveStatus(Guid statusId)
    {
        var player = Player.Object;
        if (player is null) return false;
        try { removeMoodleByPlayer.InvokeAction(statusId, player); return true; }
        catch (Exception ex) { Plugin.Log.Error(ex, "Failed to remove a single Moodles status (Moodles may be too old to expose RemoveMoodleByPlayerV2)."); return false; }
    }

    public bool ClearStatus()
    {
        var player = Player.Object;
        if (player is null) return false;
        try { clearStatusManagerByPlayer.InvokeAction(player); return true; }
        catch (Exception ex) { Plugin.Log.Error(ex, "Failed to clear the Sub's Moodles status."); return false; }
    }
}
