using System;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Oathbound.Plugin.Commands;

/// While caged, refuses aetheryte teleports (every route - the map, /tp, Lifestream - goes through Telepo.Teleport)
/// and interacting with aetherytes or event objects (house and apartment doors). Furniture is a different object kind,
/// so the Sub can still sit. A hook that fails only weakens this: leaving the place re-arms the cage, never opens it.
public sealed unsafe class CageExitGuard : IDisposable
{
    private readonly Func<bool> isCaged;
    private readonly Hook<Telepo.Delegates.Teleport>? teleportHook;
    private readonly Hook<TargetSystem.Delegates.InteractWithObject>? interactHook;

    public CageExitGuard(Func<bool> isCaged)
    {
        this.isCaged = isCaged;
        try
        {
            teleportHook = ECommons.DalamudServices.Svc.Hook.HookFromAddress<Telepo.Delegates.Teleport>(
                (nint)Telepo.MemberFunctionPointers.Teleport, TeleportDetour);
            teleportHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "CageExitGuard: failed to hook Telepo.Teleport - a caged Sub's teleports aren't blocked this session.");
        }
        try
        {
            interactHook = ECommons.DalamudServices.Svc.Hook.HookFromAddress<TargetSystem.Delegates.InteractWithObject>(
                (nint)TargetSystem.MemberFunctionPointers.InteractWithObject, InteractDetour);
            interactHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "CageExitGuard: failed to hook TargetSystem.InteractWithObject - a caged Sub's doors aren't blocked this session.");
        }
    }

    private bool TeleportDetour(Telepo* telepo, uint aetheryteId, byte subIndex)
    {
        if (isCaged())
        {
            Plugin.Log.Information("Blocked a teleport: the Sub is caged.");
            return false;
        }
        return teleportHook!.Original(telepo, aetheryteId, subIndex);
    }

    private ulong InteractDetour(TargetSystem* targetSystem, GameObject* obj, bool checkLineOfSight)
    {
        if (obj != null && isCaged() && obj->ObjectKind is ObjectKind.Aetheryte or ObjectKind.EventObj)
        {
            Plugin.Log.Information("Blocked an interaction: the Sub is caged.");
            return 0;
        }
        return interactHook!.Original(targetSystem, obj, checkLineOfSight);
    }

    public void Dispose()
    {
        teleportHook?.Dispose();
        interactHook?.Dispose();
    }
}
