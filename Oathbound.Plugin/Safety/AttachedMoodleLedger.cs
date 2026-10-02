using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.GameHelpers;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;

namespace Oathbound.Plugin.Safety;

/// Which source holds which Moodles status, so a status is only removed once nothing else holds it. Source keys:
/// outfit, restraint:<deviceId>, follow, collar, manual:<statusId>. Persisted so a moodle whose source didn't survive
/// a reload can still be removed.
public sealed class AttachedMoodleLedger
{
    public const string OutfitSource = "outfit";
    public const string FollowSource = "follow";
    public const string CollarSource = "collar";
    public const string RestraintPrefix = "restraint:";
    private const string ManualPrefix = "manual:";

    private readonly PluginConfig config;
    private readonly MoodlesIpc moodles;
    private bool reconciledAfterLoad;

    public AttachedMoodleLedger(PluginConfig config, MoodlesIpc moodles)
    {
        this.config = config;
        this.moodles = moodles;
    }

    private Dictionary<string, Guid> Holds => config.AttachedMoodleHolds;

    public static string RestraintSource(string deviceId) => RestraintPrefix + deviceId;
    public static string ManualSource(Guid statusId) => ManualPrefix + statusId;

    /// Re-holding re-applies (the collar's reassertion); holding a different status releases the previous one.
    /// False, recording nothing, if Moodles couldn't apply it.
    public bool Hold(string source, Guid statusId)
    {
        if (Holds.TryGetValue(source, out var previous) && previous != statusId)
            Release(source);

        if (!moodles.ApplyStatus(statusId))
            return false;

        if (!Holds.TryGetValue(source, out var existing) || existing != statusId)
        {
            Holds[source] = statusId;
            config.Save();
        }
        return true;
    }

    /// Removes the status only if no other source still holds it.
    public bool Release(string source)
    {
        if (!Holds.Remove(source, out var statusId))
            return false;

        config.Save();
        if (!Holds.ContainsValue(statusId))
            moodles.RemoveStatus(statusId);
        return true;
    }

    public void ReleaseAllWithPrefix(string prefix)
    {
        foreach (var source in Holds.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            Release(source);
    }

    /// Clear-then-reapply, since listing the Sub's current statuses would mean mirroring Moodles' huge tuple.
    /// Re-applied moodles get their duration reset.
    public bool ClearUnheld()
    {
        foreach (var source in Holds.Keys.Where(k => k.StartsWith(ManualPrefix, StringComparison.Ordinal)).ToList())
            Holds.Remove(source);
        config.Save();

        if (!moodles.ClearStatus())
            return false;

        foreach (var statusId in Holds.Values.Distinct().ToList())
            moodles.ApplyStatus(statusId);
        return true;
    }

    /// Everything goes except the collar's, which is re-applied.
    public bool ClearAllExceptCollar()
    {
        foreach (var source in Holds.Keys.Where(k => k != CollarSource).ToList())
            Holds.Remove(source);
        config.Save();

        if (!moodles.ClearStatus())
            return false;

        if (Holds.TryGetValue(CollarSource, out var collarStatus))
            moodles.ApplyStatus(collarStatus);
        return true;
    }

    public void ClearAllForPanic()
    {
        Holds.Clear();
        config.Save();
        moodles.ClearStatus();
    }

    /// Restraint and leash holds don't survive a reload, so their moodles are removed once the player exists.
    public void OnFrameworkUpdate()
    {
        if (reconciledAfterLoad || Player.Object is null)
            return;

        reconciledAfterLoad = true;
        ReleaseAllWithPrefix(RestraintPrefix);
        Release(FollowSource);
    }
}
