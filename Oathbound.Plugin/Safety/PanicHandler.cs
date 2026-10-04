using System;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.Safety;

/// The safeword: reverts every local restriction/effect except a locked collar. Never ends a pairing.
/// Each step is isolated so one failure never stops the rest.
public sealed class PanicHandler
{
    private readonly PairingService pairing;
    private readonly GlamourerIpc glamourer;
    private readonly SlotLockManager slotLocks;
    private readonly HonorificIpc honorific;
    private readonly MovementLockService movementLock;
    private readonly RestrictionRuleManager restrictionRules;
    private readonly RestraintCommand restraints;
    private readonly ToyControlCommand toyControl;
    private readonly SubRuntimeState runtimeState;
    private readonly FollowCommand follow;
    private readonly HotbarBlockVisuals hotbarVisuals;
    private readonly AttachedMoodleLedger moodleLedger;
    private readonly GestureCommand gesture;
    private readonly ReactionService reactions;
    private readonly TeleportCommand teleport;

    public PanicHandler(PairingService pairing, GlamourerIpc glamourer, SlotLockManager slotLocks, HonorificIpc honorific, MovementLockService movementLock, RestrictionRuleManager restrictionRules, RestraintCommand restraints, ToyControlCommand toyControl, SubRuntimeState runtimeState, FollowCommand follow, HotbarBlockVisuals hotbarVisuals, AttachedMoodleLedger moodleLedger, GestureCommand gesture, ReactionService reactions, TeleportCommand teleport)
    {
        this.hotbarVisuals = hotbarVisuals;
        this.moodleLedger = moodleLedger;
        this.gesture = gesture;
        this.reactions = reactions;
        this.teleport = teleport;
        this.pairing = pairing;
        this.glamourer = glamourer;
        this.slotLocks = slotLocks;
        this.honorific = honorific;
        this.movementLock = movementLock;
        this.restrictionRules = restrictionRules;
        this.restraints = restraints;
        this.toyControl = toyControl;
        this.runtimeState = runtimeState;
        this.follow = follow;
    }

    /// Extra resets that must run with every local revert.
    public Action? AfterLocalRevert { get; set; }

    /// Panic only, not unpair: suspends rulebooks on every pairing.
    public Action? OnPanic { get; set; }

    public void Panic()
    {
        RevertLocalState(LeashEnd.Panic);
        if (OnPanic is { } onPanic)
            RunStep("suspend rulebooks", onPanic);
        Plugin.Log.Information("Panic triggered: outfit reverted, title cleared, leash and movement lock released, all slot locks except the collar's and all restriction rules released. Every pairing and the collar remain.");
    }

    /// Ends one pairing and reverts local state like panic, since applied state can't be attributed to a pairing.
    public void ReleasePairing(PairingState target)
    {
        RunStep("unpair", () => pairing.ReleasePeer(target));
        RevertLocalState(follow.LeashedPairingId == target.Id ? LeashEnd.PairingEnded : LeashEnd.Other);
    }

    private void RevertLocalState(LeashEnd leashReason)
    {
        if (AfterLocalRevert is { } after)
            RunStep("forget custom trigger effects", after);
        // First, so a leash journey it stops isn't reported as a travel failure.
        RunStep("release leash", () => follow.Release(leashReason));
        // The collar's lock is kept, so enforcement puts the collar piece back after this revert.
        RunStep("revert outfit", () => glamourer.RevertToAutomationFull());
        RunStep("release slot locks except the collar", () => slotLocks.ReleaseAllForPanic(keepOwner: CollarCommand.Owner));
        RunStep("clear all moodles except the collar's", () => moodleLedger.ClearAllExceptCollar());

        RunStep("clear title", () =>
        {
            if (runtimeState.TitleApplied)
                honorific.ClearTitle();
        });

        // Before the lock release, or vnavmesh would keep walking the Sub with nothing holding them.
        RunStep("stop teleport", () => teleport.Stop("panic"));
        RunStep("release movement lock", movementLock.ReleaseAll);
        RunStep("release restriction rules", restrictionRules.ReleaseAllForPanic);
        // Repeated directly so a failed rule release can never leave the hotbars greyed out.
        RunStep("restore hotbars", hotbarVisuals.Hide);
        RunStep("release restraint bound animations", restraints.ReleaseAllBoundAnimationsForPanic);
        // Oathbound locks the mods it holds, so only panic can turn them off.
        RunStep("release gesture animation", gesture.ResetActiveTemporary);
        RunStep("stop toy control", toyControl.ReleaseAllForPanic);
        RunStep("suspend toy triggers", () => runtimeState.ToyTriggersSuspended = true);
        RunStep("suspend reactions and turn off reaction mods", reactions.ReleaseAllForPanic);

        runtimeState.Reset();
    }

    private static void RunStep(string name, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, $"Panic step '{name}' failed - continuing with remaining steps.");
        }
    }
}
