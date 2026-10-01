using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.Config;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
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
    // collar/leash steering: the walk-input sum vnavmesh rewrites to drive the character (awgil/ffxiv_navmesh,
    // Movement/OverrideMovement.cs) - keyboard, controller and mouse movement are all summed into it.
    private const string SigRmiWalk = "E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D";
    private const string SigRmiWalkIsInputEnabled1 = "E8 ?? ?? ?? ?? 84 C0 75 10 38 43 3C";
    private const string SigRmiWalkIsInputEnabled2 = "E8 ?? ?? ?? ?? 84 C0 75 03 88 47 3F";
    // collar/leash-mounts: the flying counterpart vnavmesh rewrites (OverrideMovement.RMIFlyDetour).
    private const string SigRmiFly = "E8 ?? ?? ?? ?? 0F B6 0D ?? ?? ?? ?? B8";

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

    public unsafe delegate void RmiWalkDelegate(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk);
    [Signature(SigRmiWalk, DetourName = nameof(RmiWalkDetour), Fallibility = Fallibility.Auto)]
    private readonly Hook<RmiWalkDelegate>? rmiWalkHook;

    private delegate byte RmiWalkIsInputEnabledDelegate(void* self);
    private readonly RmiWalkIsInputEnabledDelegate? rmiWalkIsInputEnabled1;
    private readonly RmiWalkIsInputEnabledDelegate? rmiWalkIsInputEnabled2;

    /// collar/leash: rewrites this frame's walk direction. Given the player's own wish as a world-space
    /// ground vector (X, Z), returns the one to actually walk.
    public delegate Vector2 SteerFunction(Vector2 worldWish);

    public unsafe delegate void RmiFlyDelegate(void* self, OathboundFlyInput* result);
    [Signature(SigRmiFly, DetourName = nameof(RmiFlyDetour), Fallibility = Fallibility.Auto)]
    private readonly Hook<RmiFlyDelegate>? rmiFlyHook;

    /// collar/leash-mounts (design D3): the flying version - the player's horizontal wish as a world-space ground
    /// vector plus their pitch (radians, positive up, as vnavmesh writes it), returning the ones to fly.
    public delegate (Vector2 Horizontal, float Pitch) FlySteerFunction(Vector2 worldWish, float pitch);

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
    /// teleport-lifestream-autorun D1: SuppressInput owners that currently let autorun through, so Lifestream's
    /// `/automove on` aetheryte approach works. Only honored while every claim is one of these - an
    /// immobilize or follow-preserve claim (Leash, a restraint) always keeps autorun blocked.
    private readonly HashSet<string> autorunAllowedBy = new();
    /// collar/leash (design D1): at most one caller rewrites the walk vector at a time (only the leash does).
    /// Never counts as a lock, and never applies while an immobilize or input-suppress claim exists - a
    /// restraint (nothing moves) or teleport navigation (vnavmesh owns the vector) always wins over it.
    private string? steerOwner;
    private SteerFunction? steer;
    private FlySteerFunction? flySteer;
    private bool legacyMoveMode;
    private bool ownsForceDisable;

    public MovementLockService()
    {
        Svc.Hook.InitializeFromAttributes(this);
        if (Svc.SigScanner.TryScanText(SigRmiWalkIsInputEnabled1, out var inputEnabled1))
            rmiWalkIsInputEnabled1 = Marshal.GetDelegateForFunctionPointer<RmiWalkIsInputEnabledDelegate>(inputEnabled1);
        if (Svc.SigScanner.TryScanText(SigRmiWalkIsInputEnabled2, out var inputEnabled2))
            rmiWalkIsInputEnabled2 = Marshal.GetDelegateForFunctionPointer<RmiWalkIsInputEnabledDelegate>(inputEnabled2);

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

        IsSteerAvailable = IsAvailable && rmiWalkHook is not null && rmiWalkIsInputEnabled1 is not null && rmiWalkIsInputEnabled2 is not null;
        if (IsSteerAvailable)
        {
            rmiWalkHook!.Enable();
            Plugin.GameConfig.UiControlChanged += OnUiControlChanged;
            UpdateLegacyMoveMode();
        }
        else Plugin.Log.Error("Leash steering unavailable: walk-input hook did not resolve.");

        IsFlySteerAvailable = IsSteerAvailable && rmiFlyHook is not null;
        if (IsFlySteerAvailable) rmiFlyHook!.Enable();
        else Plugin.Log.Error("Leash flying unavailable: fly-input hook did not resolve.");
    }

    public bool IsAvailable { get; }
    public bool IsImmobilizeAvailable { get; }
    public bool IsSteerAvailable { get; }
    public bool IsFlySteerAvailable { get; }

    /// Whether a steer function would run this frame - false while a stronger claim holds movement.
    public bool IsSteerSuspended => immobilizedBy.Count > 0 || inputSuppressedBy.Count > 0;

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
    public void ReleaseSuppressInput(string owner)
    {
        inputSuppressedBy.Remove(owner);
        autorunAllowedBy.Remove(owner);
    }
    public void SetSteering(string owner, SteerFunction function)
    {
        if (!IsSteerAvailable) return;
        steerOwner = owner;
        steer = function;
    }
    public void ClearSteering(string owner)
    {
        if (steerOwner != owner) return;
        steerOwner = null;
        steer = null;
        flySteer = null;
    }

    /// Same owner as SetSteering (the leash): adds the flying counterpart. Cleared with ClearSteering.
    public void SetFlySteering(string owner, FlySteerFunction function)
    {
        if (IsFlySteerAvailable && steerOwner == owner)
            flySteer = function;
    }
    public void AllowAutorun(string owner) => autorunAllowedBy.Add(owner);
    public void DisallowAutorun(string owner) => autorunAllowedBy.Remove(owner);

    /// Panic's own release: drops every caller's claim unconditionally, regardless of who engaged it -
    /// same "full teardown, nothing needs preserving" shape as SlotLockManager.ReleaseAllForPanic.
    public void ReleaseAll()
    {
        immobilizedBy.Clear();
        followPreservedBy.Clear();
        inputSuppressedBy.Clear();
        autorunAllowedBy.Clear();
        steerOwner = null;
        steer = null;
        flySteer = null;
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
        if (IsLocked && !AutorunAllowed && request != 0 && *(byte*)(request + 8) == 3) return;
        autoMoveHook!.Original(state, request);
    }

    private void RmiWalkDetour(void* self, float* sumLeft, float* sumForward, float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk)
    {
        rmiWalkHook!.Original(self, sumLeft, sumForward, sumTurnLeft, haveBackwardOrStrafe, a6, bAdditiveUnk);
        var function = steer;
        if (function is null || IsSteerSuspended) return;
        // Same guard vnavmesh uses: writing a non-zero vector on a frame the game skipped reading input breaks movement.
        if (bAdditiveUnk != 0 || rmiWalkIsInputEnabled1!(self) == 0 || rmiWalkIsInputEnabled2!(self) == 0) return;
        if (Plugin.ObjectTable.LocalPlayer is not { } player) return;

        // The walk sums are relative to a reference yaw: the character's facing in Standard movement, the
        // camera's (plus 180 degrees) in Legacy - vnavmesh's DirectionToDestination.
        if (!TryReferenceYaw(player.Rotation, out var referenceYaw)) return;

        var world = Rotate(new Vector2(*sumLeft, *sumForward), referenceYaw);
        var local = Rotate(function(world), -referenceYaw);
        *sumLeft = local.X;
        *sumForward = local.Y;
    }

    private void RmiFlyDetour(void* self, OathboundFlyInput* result)
    {
        rmiFlyHook!.Original(self, result);
        var function = flySteer;
        if (function is null || IsSteerSuspended) return;
        if (Plugin.ObjectTable.LocalPlayer is not { } player) return;
        if (!TryReferenceYaw(player.Rotation, out var referenceYaw)) return;

        var world = Rotate(new Vector2(result->Left, result->Forward), referenceYaw);
        var (horizontal, pitch) = function(world, result->Up);
        var local = Rotate(horizontal, -referenceYaw);
        result->Left = local.X;
        result->Forward = local.Y;
        result->Up = pitch;
    }

    private bool TryReferenceYaw(float playerRotation, out float yaw)
    {
        yaw = playerRotation;
        if (!legacyMoveMode) return true;
        var camera = (OathboundCameraEx*)CameraManager.Instance()->GetActiveCamera();
        if (camera == null) return false;
        yaw = camera->DirH + MathF.PI;
        return true;
    }

    /// (left, forward) at reference yaw `yaw` to world (X, Z), where a world direction's yaw is atan2(X, Z).
    /// The same rotation with -yaw goes back.
    private static Vector2 Rotate(Vector2 v, float yaw)
    {
        var (sin, cos) = MathF.SinCos(yaw);
        return new Vector2(v.X * cos + v.Y * sin, v.Y * cos - v.X * sin);
    }

    private void OnUiControlChanged(object? sender, ConfigChangeEvent e) => UpdateLegacyMoveMode();
    private void UpdateLegacyMoveMode() =>
        legacyMoveMode = Plugin.GameConfig.UiControl.TryGetUInt("MoveMode", out var mode) && mode == 1;

    private bool AutorunAllowed => immobilizedBy.Count == 0 && followPreservedBy.Count == 0 && autorunAllowedBy.IsSupersetOf(inputSuppressedBy);

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
        rmiWalkHook?.Dispose();
        rmiFlyHook?.Dispose();
        if (IsSteerAvailable) Plugin.GameConfig.UiControlChanged -= OnUiControlChanged;
    }
}
