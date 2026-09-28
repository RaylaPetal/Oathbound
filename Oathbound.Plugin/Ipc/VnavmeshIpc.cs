using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Ipc;

namespace Oathbound.Plugin.Ipc;

/// Thin wrapper around vnavmesh's (awgil/ffxiv_navmesh) IPC surface - no NuGet API package, so this mirrors
/// its `vnavmesh.<Name>` call gates by hand, same shape as LifestreamIpc. collar/teleport's navigation leg
/// is the only consumer; every call fails soft (returns false/null, never throws) so a Sub without vnavmesh
/// gets a clean refusal instead of a crash.
///
/// Deliberately uses `Nav.PathfindCancelable` + `Path.MoveTo` rather than `SimpleMove.*`: SimpleMove queues
/// a task that starts walking whenever pathfinding finishes, which could be after the Sub already pressed
/// Stop (design.md D6). Owning the token and the returned Task lets TeleportCommand discard a stale path.
public sealed class VnavmeshIpc
{
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<float> buildProgress;
    private readonly ICallGateSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> pathfindCancelable;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> nearestPointReachable;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> pointOnFloor;
    private readonly ICallGateSubscriber<List<Vector3>, bool, object> moveTo;
    private readonly ICallGateSubscriber<object> stop;
    private readonly ICallGateSubscriber<bool> isRunning;

    public VnavmeshIpc()
    {
        isReady = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        buildProgress = Plugin.PluginInterface.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress");
        pathfindCancelable = Plugin.PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>>("vnavmesh.Nav.PathfindCancelable");
        nearestPointReachable = Plugin.PluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable");
        pointOnFloor = Plugin.PluginInterface.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        moveTo = Plugin.PluginInterface.GetIpcSubscriber<List<Vector3>, bool, object>("vnavmesh.Path.MoveTo");
        stop = Plugin.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        isRunning = Plugin.PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
    }

    /// Probes `Nav.IsReady` - cheap and read-only, and it answers (true or false) whenever vnavmesh is loaded,
    /// so any exception means "not installed / not loaded / contract mismatch".
    public bool IsAvailable { get { try { isReady.InvokeFunc(); return true; } catch { return false; } } }

    /// Whether the current zone's navmesh is built and loaded. False (not "unknown") on failure, so callers
    /// keep waiting rather than pathing against a mesh that isn't there.
    public bool TryIsReady() { try { return isReady.InvokeFunc(); } catch { return false; } }

    /// 0..1 build progress for the current zone's mesh, or -1 when unavailable/not building.
    public float TryGetBuildProgress() { try { return buildProgress.InvokeFunc(); } catch { return -1f; } }

    public Task<List<Vector3>>? TryPathfind(Vector3 from, Vector3 to, bool fly, CancellationToken cancel)
    {
        try { return pathfindCancelable.InvokeFunc(from, to, fly, cancel); }
        catch (Exception ex) { Plugin.Log.Error(ex, "vnavmesh PathfindCancelable failed."); return null; }
    }

    public Vector3? TryNearestPointReachable(Vector3 point, float halfExtentXZ, float halfExtentY)
    {
        try { return nearestPointReachable.InvokeFunc(point, halfExtentXZ, halfExtentY); } catch { return null; }
    }

    public Vector3? TryPointOnFloor(Vector3 point, bool allowUnlandable, float halfExtentXZ)
    {
        try { return pointOnFloor.InvokeFunc(point, allowUnlandable, halfExtentXZ); } catch { return null; }
    }

    public bool TryMoveTo(List<Vector3> waypoints, bool fly)
    {
        try { moveTo.InvokeAction(waypoints, fly); return true; }
        catch (Exception ex) { Plugin.Log.Error(ex, "vnavmesh Path.MoveTo failed."); return false; }
    }

    public void TryStop() { try { stop.InvokeAction(); } catch { } }

    public bool TryIsRunning() { try { return isRunning.InvokeFunc(); } catch { return false; } }
}
