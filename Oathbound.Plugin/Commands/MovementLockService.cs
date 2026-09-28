using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.System.Input;

namespace Oathbound.Plugin.Commands;

#pragma warning disable CS0649 // assigned via reflection by Svc.Hook.InitializeFromAttributes, not by the compiler

/// The actual leash mechanism for collar/follow: suppresses movement input at the same low-level input-
/// polling functions the game itself reads, for both direct movement keys and whatever internal logic
/// decides to cancel an active follow/auto-move. Isolated into its own module per design.md's risk-tier
/// decision - a broken signature after a game patch degrades only this module, and IsAvailable lets the
/// rest of the plugin fail closed (task 7.5) rather than leaving a lock silently unenforced.
///
/// Hook target signatures are the same, actively-maintained ones GagSpeak uses for this exact purpose
/// (Project-GagSpeak/client, ProjectGagSpeak/GameInternals/Signatures.cs) - per design.md's explicit
/// recommendation to build on GagSpeak's proven approach rather than re-deriving new signatures.
public sealed unsafe class MovementLockService : IDisposable
{
    private static readonly InputId[] MovementInputs =
    [
        InputId.MOVE_FORE, InputId.MOVE_BACK, InputId.MOVE_STRIFE_L, InputId.MOVE_STRIFE_R,
        InputId.MOVE_LEFT, InputId.MOVE_RIGHT, InputId.MOVE_AND_STEER,
        InputId.MOVE_ANGLE_DESCENT, InputId.MOVE_ANGLE_RISING, InputId.MOVE_DESCENT, InputId.MOVE_RETENTION,
    ];

    private const string SigIsInputIdPressed = "E8 ?? ?? ?? ?? 84 C0 74 ?? 8D 93";
    private const string SigIsInputIdDown = "E8 ?? ?? ?? ?? 48 8B 75 ?? BB";
    private const string SigIsInputIdHeld = "E8 ?? ?? ?? ?? 84 C0 74 ?? EB ?? BE";
    private const string SigIsInputIdUnknown = "E8 ?? ?? ?? ?? 84 C0 8B EF";
    private const string SigForceDisableMovement = "F3 0F 10 05 ?? ?? ?? ?? 0F 2E C7";
    private const string SigMouseMoveBlock = "48 8b c4 4c 89 48 ?? 53 55 57 41 54 48 81 ec ?? 00 00 00";
    private const string SigUnfollowTarget = "48 89 5c 24 ?? 48 89 74 24 ?? 57 48 83 ec ?? 48 8b d9 48 8b fa 0f b6 89 ?? ?? 00 00 be 00 00 00 e0";
    private const string SigAutoMoveUpdate = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 83 EC 20 44 0F B6 7A ?? 48 8B D9";

    public unsafe delegate byte IsInputIdDelegate(void* unk, InputId inputId);

    [Signature(SigIsInputIdPressed, DetourName = nameof(IsInputIdPressedDetour), Fallibility = Fallibility.Auto)]
    private readonly Hook<IsInputIdDelegate>? isInputIdPressedHook;

    [Signature(SigIsInputIdDown, DetourName = nameof(IsInputIdDownDetour), Fallibility = Fallibility.Auto)]
    private readonly Hook<IsInputIdDelegate>? isInputIdDownHook;

    [Signature(SigIsInputIdHeld, DetourName = nameof(IsInputIdHeldDetour), Fallibility = Fallibility.Auto)]
    private readonly Hook<IsInputIdDelegate>? isInputIdHeldHook;

    [Signature(SigIsInputIdUnknown, DetourName = nameof(IsInputIdUnknownDetour), Fallibility = Fallibility.Auto)]
    private readonly Hook<IsInputIdDelegate>? isInputIdUnknownHook;

    [Signature(SigForceDisableMovement, ScanType = ScanType.StaticAddress, Fallibility = Fallibility.Auto)]
    private readonly nint forceDisableMovementPtr;

    public unsafe delegate void MovementDirectionUpdateDelegate(OathboundMoveController* self, float* horizontal, float* vertical, float* rotation, byte* alignCamera, byte* autorun, byte dontRotate);
    [Signature(SigMouseMoveBlock, DetourName = nameof(MovementDirectionUpdateDetour), Fallibility = Fallibility.Auto)]
    private readonly Hook<MovementDirectionUpdateDelegate>? mouseMoveHook;

    public unsafe delegate void UnfollowDelegate(OathboundFollowState* state, nint arg);
    [Signature(SigUnfollowTarget, DetourName = nameof(UnfollowDetour), Fallibility = Fallibility.Auto)]
    private readonly Hook<UnfollowDelegate>? unfollowHook;

    public unsafe delegate void AutoMoveDelegate(void* state, nint request);
    [Signature(SigAutoMoveUpdate, DetourName = nameof(AutoMoveDetour), Fallibility = Fallibility.Auto)]
    private readonly Hook<AutoMoveDelegate>? autoMoveHook;

    /// Which independent callers currently want movement suppressed - a Set rather than a bare bool so two
    /// unrelated callers (Follow's leash, a forced-pose restraint device - collar/restraints) can each
    /// Engage/Release their own claim without one caller's Release prematurely lifting the other's.
    /// Add/Remove are naturally idempotent for repeat same-token calls, unlike a naive increment/decrement
    /// counter would be.
    private readonly HashSet<string> immobilizedBy = new();
    private readonly HashSet<string> followPreservedBy = new();
    /// collar/teleport's navigation leg (design.md D5): same input suppression as the other two sets, but
    /// never the game's force-disable flag - vnavmesh and Lifestream drive the character by rewriting the
    /// walk vector, and force-disable would freeze that too - and never the Follow-only unfollow block.
    private readonly HashSet<string> inputSuppressedBy = new();
    private bool ownsForceDisable;

    public MovementLockService()
    {
        Svc.Hook.InitializeFromAttributes(this);

        // task 7.5: fail closed. If any signature didn't resolve on this game version, never claim the
        // lock works - IsAvailable stays false and FollowCommand must refuse to engage it.
        IsAvailable = isInputIdPressedHook is not null && isInputIdDownHook is not null && isInputIdHeldHook is not null && isInputIdUnknownHook is not null
            && mouseMoveHook is not null && unfollowHook is not null && autoMoveHook is not null;
        IsImmobilizeAvailable = IsAvailable && forceDisableMovementPtr != 0;

        if (IsAvailable)
        {
            isInputIdPressedHook!.Enable();
            isInputIdDownHook!.Enable();
            isInputIdHeldHook!.Enable();
            isInputIdUnknownHook!.Enable();
            mouseMoveHook!.Enable();
            unfollowHook!.Enable();
            autoMoveHook!.Enable();
        }
        else
        {
            if (isInputIdPressedHook is null) Plugin.Log.Error("MovementLockService unavailable: pressed-input hook did not resolve.");
            if (isInputIdDownHook is null) Plugin.Log.Error("MovementLockService unavailable: down-input hook did not resolve.");
            if (isInputIdHeldHook is null) Plugin.Log.Error("MovementLockService unavailable: held-input hook did not resolve.");
            if (isInputIdUnknownHook is null) Plugin.Log.Error("MovementLockService unavailable: essential fourth input hook did not resolve.");
            if (mouseMoveHook is null) Plugin.Log.Error("MovementLockService unavailable: mouse-movement hook did not resolve.");
            if (unfollowHook is null) Plugin.Log.Error("MovementLockService unavailable: unfollow-protection hook did not resolve.");
            if (autoMoveHook is null) Plugin.Log.Error("MovementLockService unavailable: autorun hook did not resolve.");
        }
        if (forceDisableMovementPtr == 0) Plugin.Log.Error("Movement immobilization unavailable: complete-movement-disable state did not resolve.");
    }

    public bool IsAvailable { get; }
    public bool IsImmobilizeAvailable { get; }

    public bool IsLocked => IsAvailable && AnyInputClaim;

    private bool AnyInputClaim => immobilizedBy.Count > 0 || followPreservedBy.Count > 0 || inputSuppressedBy.Count > 0;

    /// collar/follow: "Movement lock releases on panic, unpair, or Owner release" - all three paths call
    /// Release(owner) for their own token, and it is safe to call Engage even if IsAvailable is false
    /// (engagedBy just never gains an entry that would suppress anything).
    public void EngageImmobilize(string owner) { if (IsImmobilizeAvailable) immobilizedBy.Add(owner); }
    public void ReleaseImmobilize(string owner) => immobilizedBy.Remove(owner);
    public void EngagePreserveFollow(string owner) { if (IsAvailable) followPreservedBy.Add(owner); }
    public void ReleasePreserveFollow(string owner) => followPreservedBy.Remove(owner);
    public void EngageSuppressInput(string owner) { if (IsAvailable) inputSuppressedBy.Add(owner); }
    public void ReleaseSuppressInput(string owner) => inputSuppressedBy.Remove(owner);

    /// Panic's own release: drops every caller's claim unconditionally, regardless of who engaged it -
    /// same "full teardown, nothing needs preserving" shape as SlotLockManager.ReleaseAllForPanic.
    public void ReleaseAll()
    {
        immobilizedBy.Clear();
        followPreservedBy.Clear();
        inputSuppressedBy.Clear();
        ClearForceDisable();
    }

    public unsafe void OnFrameworkUpdate()
    {
        if (!IsImmobilizeAvailable) return;
        ref var disabled = ref *(int*)(forceDisableMovementPtr + 4);
        if (immobilizedBy.Count > 0)
        {
            if (disabled == 0) { disabled = 1; ownsForceDisable = true; }
        }
        else ClearForceDisable();
    }

    private byte IsInputIdPressedDetour(void* unk, InputId inputId) => Suppress(inputId) ? (byte)0 : isInputIdPressedHook!.Original(unk, inputId);
    private byte IsInputIdDownDetour(void* unk, InputId inputId) => Suppress(inputId) ? (byte)0 : isInputIdDownHook!.Original(unk, inputId);
    private byte IsInputIdHeldDetour(void* unk, InputId inputId) => Suppress(inputId) ? (byte)0 : isInputIdHeldHook!.Original(unk, inputId);
    private byte IsInputIdUnknownDetour(void* unk, InputId inputId) => Suppress(inputId) ? (byte)0 : isInputIdUnknownHook!.Original(unk, inputId);

    private bool Suppress(InputId inputId) => AnyInputClaim && Array.IndexOf(MovementInputs, inputId) >= 0;

    private void MovementDirectionUpdateDetour(OathboundMoveController* self, float* horizontal, float* vertical, float* rotation, byte* alignCamera, byte* autorun, byte dontRotate)
    {
        mouseMoveHook!.Original(self, horizontal, vertical, rotation, alignCamera, autorun, dontRotate);
        if (!IsLocked || self->MouseRunning == 0) return;
        self->MouseRunning = 0;
        self->WishdirChanged = 0;
        *horizontal = 0;
        *vertical = 0;
    }

    private void UnfollowDetour(OathboundFollowState* state, nint arg)
    {
        if (followPreservedBy.Count > 0) return;
        unfollowHook!.Original(state, arg);
    }

    private void AutoMoveDetour(void* state, nint request)
    {
        if (IsLocked && request != 0 && *(byte*)(request + 8) == 3) return;
        autoMoveHook!.Original(state, request);
    }

    private unsafe void ClearForceDisable()
    {
        if (ownsForceDisable && forceDisableMovementPtr != 0)
        {
            ref var disabled = ref *(int*)(forceDisableMovementPtr + 4);
            if (disabled == 1) disabled = 0;
        }
        ownsForceDisable = false;
    }

    public void Dispose()
    {
        ReleaseAll();
        isInputIdPressedHook?.Dispose();
        isInputIdDownHook?.Dispose();
        isInputIdHeldHook?.Dispose();
        isInputIdUnknownHook?.Dispose();
        mouseMoveHook?.Dispose();
        unfollowHook?.Dispose();
        autoMoveHook?.Dispose();
    }
}
