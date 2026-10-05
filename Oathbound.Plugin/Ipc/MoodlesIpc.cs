using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Ipc;
using ECommons.GameHelpers;
using Oathbound.Plugin.Commands;
using MoodleTuple = (int Version, System.Guid GUID, int IconID, string Title, string Description, string CustomVFXPath, long ExpireTicks,
    Oathbound.Plugin.Ipc.MoodlesStatusType Type, int Stacks, int StackSteps, uint Modifiers, System.Guid ChainedStatus,
    Oathbound.Plugin.Ipc.MoodlesChainTrigger ChainTrigger, string Applier, string Dispeller, bool Permanent);

namespace Oathbound.Plugin.Ipc;

public readonly record struct MoodlesStatus(Guid Id, string Name);

/// Copies of Moodles' own enums, same values; Dalamud's call gate converts across the assembly boundary.
public enum MoodlesStatusType { Positive, Negative, Special }
public enum MoodlesChainTrigger { Dispel, HitMaxStacks, TimerExpired }
public enum MoodlesScanStatus { Success, Unavailable, Failed }
public readonly record struct MoodlesScanResult(MoodlesScanStatus Status, IReadOnlyList<MoodlesStatus> Statuses, string? Error = null);

/// Mirrors Moodles' IPC surface. Reads individual statuses, not presets. Player operations take the status GUID,
/// then the IPlayerCharacter.
public sealed class MoodlesIpc
{
    private readonly ICallGateSubscriber<List<(Guid ID, uint IconID, string FullPath, string Title)>> getRegisteredMoodles;
    private readonly ICallGateSubscriber<Guid, IPlayerCharacter, object> addOrUpdateMoodleByPlayer;
    private readonly ICallGateSubscriber<IPlayerCharacter, object> clearStatusManagerByPlayer;
    private readonly ICallGateSubscriber<Guid, IPlayerCharacter, object> removeMoodleByPlayer;
    private readonly ICallGateSubscriber<MoodleTuple, IPlayerCharacter, object> addOrUpdateMoodleByDataByPlayer;
    private readonly ICallGateSubscriber<List<MoodleTuple>> getClientStatusManagerInfo;

    /// Moodles' status tuple version this matches.
    private const int MoodleTupleVersion = 2;

    /// Probes the status list the catalog scan uses; any exception means unavailable.
    public bool IsAvailable { get { try { getRegisteredMoodles.InvokeFunc(); return true; } catch { return false; } } }

    public MoodlesIpc()
    {
        getRegisteredMoodles = Plugin.PluginInterface.GetIpcSubscriber<List<(Guid, uint, string, string)>>("Moodles.GetRegisteredMoodlesV2");
        addOrUpdateMoodleByPlayer = Plugin.PluginInterface.GetIpcSubscriber<Guid, IPlayerCharacter, object>("Moodles.AddOrUpdateMoodleByPlayerV2");
        removeMoodleByPlayer = Plugin.PluginInterface.GetIpcSubscriber<Guid, IPlayerCharacter, object>("Moodles.RemoveMoodleByPlayerV2");
        clearStatusManagerByPlayer = Plugin.PluginInterface.GetIpcSubscriber<IPlayerCharacter, object>("Moodles.ClearStatusManagerByPlayerV2");
        addOrUpdateMoodleByDataByPlayer = Plugin.PluginInterface.GetIpcSubscriber<MoodleTuple, IPlayerCharacter, object>("Moodles.AddOrUpdateMoodleByDataByPlayerV2");
        getClientStatusManagerInfo = Plugin.PluginInterface.GetIpcSubscriber<List<MoodleTuple>>("Moodles.GetClientStatusManagerInfoV2");
    }

    /// Applies a moodle from its data, never touching the library. `applier` is `Name@World`, which Moodles checks
    /// against its own allow list (friends, party or everyone) and drops silently on a miss - so the result is
    /// read back. Null on success, otherwise why not.
    public string? ApplyData(CustomMoodle moodle, string applier)
    {
        var player = Player.Object;
        if (player is null)
            return "Your character isn't ready.";
        MoodleTuple data = (MoodleTupleVersion, moodle.Id, moodle.IconId, moodle.Title, moodle.Description, "",
            moodle.Seconds == 0 ? -1L : moodle.Seconds * 1000L, (MoodlesStatusType)moodle.Kind, 1, 0, 0u, Guid.Empty,
            MoodlesChainTrigger.Dispel, applier, "", false);
        try
        {
            addOrUpdateMoodleByDataByPlayer.InvokeAction(data, player);
            if (getClientStatusManagerInfo.InvokeFunc().Any(s => s.GUID == moodle.Id))
                return null;
            return "Moodles didn't accept it. In Moodles' settings, turn on \"Allow other plugins apply Moodles.\" and allow your Owner (friends, party members or everyone).";
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to apply a custom moodle.");
            return "Moodles isn't available, or is too old for moodles your Owner writes.";
        }
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

    /// Never falls back to ClearStatus - removing just this one status is the point.
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
