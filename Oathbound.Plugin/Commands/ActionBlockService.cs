using System;
using Oathbound.Plugin.Safety;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Oathbound.Plugin.Commands;

/// The Action Block rule. Hooks UseAction through FFXIVClientStructs' member-function pointer, which tracks game
/// patches better than a signature. Movement is untouched.
public sealed class ActionBlockService : IRestrictionEnforcer, IDisposable
{
    private readonly Hook<ActionManager.Delegates.UseAction>? useActionHook;
    private readonly WalkOnlyService walkOnly;
    private bool active;

    public unsafe ActionBlockService(WalkOnlyService walkOnly)
    {
        this.walkOnly = walkOnly;
        try
        {
            useActionHook = ECommons.DalamudServices.Svc.Hook.HookFromAddress<ActionManager.Delegates.UseAction>(
                (nint)ActionManager.MemberFunctionPointers.UseAction, UseActionDetour);
            IsAvailable = true;
            useActionHook.Enable();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "ActionBlockService: failed to hook ActionManager.UseAction - action-block restriction is disabled for this session.");
            IsAvailable = false;
        }
    }

    /// Fail closed: if the hook couldn't be established, never claim to enforce anything.
    public bool IsAvailable { get; }

    /// Shown only while the block is really enforcing.
    public HotbarBlockVisuals Visuals { get; } = new();

    public void Engage()
    {
        if (!IsAvailable)
            return;
        active = true;
        Visuals.Show();
    }

    public void Release()
    {
        active = false;
        Visuals.Hide();
    }

    public void OnFrameworkUpdate() => Visuals.OnFrameworkUpdate();

    private unsafe bool UseActionDetour(ActionManager* am, ActionType actionType, uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        if (active || walkOnly.IsActive && actionType == ActionType.GeneralAction && actionId == 4)
            return false;

        return useActionHook!.Original(am, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
    }

    public void Dispose()
    {
        active = false;
        Visuals.Hide();
        useActionHook?.Dispose();
    }
}
