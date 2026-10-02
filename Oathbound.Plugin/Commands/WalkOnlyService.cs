using Oathbound.Plugin.Safety;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace Oathbound.Plugin.Commands;

/// The walk-only rule: re-forces Control.IsWalking every frame. No hook needed; directional input is untouched.
public sealed class WalkOnlyService : IRestrictionEnforcer
{
    private bool active;
    private bool wasWalking;

    public bool IsAvailable => SprintInterceptorAvailable;
    public bool SprintInterceptorAvailable { get; set; }
    public bool IsActive => active;

    public unsafe void Engage()
    {
        var control = Control.Instance();
        wasWalking = control != null && control->IsWalking;
        active = true;
    }
    public unsafe void Release()
    {
        active = false;
        var control = Control.Instance();
        if (control != null && !wasWalking)
        {
            control->IsWalking = false;
            control->IsWalkingDuringAutorun = false;
        }
    }

    public unsafe void OnFrameworkUpdate()
    {
        if (!active)
            return;

        var control = Control.Instance();
        if (control == null)
            return;
        if (control->IsWalking && control->IsWalkingDuringAutorun)
            return;

        control->IsWalking = true;
        control->IsWalkingDuringAutorun = true;
    }
}
