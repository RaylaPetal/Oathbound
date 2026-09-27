using System;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Oathbound.Plugin.Relay;

namespace Oathbound.Plugin.Safety;

/// The always-available panic/safeword. Reverts every local restriction/effect this device currently has
/// applied - unconditional whole-actor Glamourer revert, then simply dropping every tracked lock, since
/// nothing needs preserving when everything is being reverted anyway (design.md: "Panic keeps a single,
/// unconditional whole-actor revert") - but, per product decision, no longer ends any pairing. Panic is a
/// pure "clear my current state" safety valve: every relationship this device holds stays exactly as
/// paired as it was before. Ending a specific pairing is now a separate, deliberate action
/// (`ReleasePairing`, exposed from Settings) - see that method for the "AND remove restrictions" +
/// peer-notification behavior a deliberate unpair still needs. Each step is isolated in its own try/catch
/// so one failure (an IPC call throwing, a send failing) never stops the rest of the sequence from running.
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
    private readonly CollarCommand collar;
    private readonly HotbarBlockVisuals hotbarVisuals;
    private readonly AttachedMoodleLedger moodleLedger;
    private readonly GestureCommand gesture;
    private readonly ReactionService reactions;

    public PanicHandler(PairingService pairing, GlamourerIpc glamourer, SlotLockManager slotLocks, HonorificIpc honorific, MovementLockService movementLock, RestrictionRuleManager restrictionRules, RestraintCommand restraints, ToyControlCommand toyControl, SubRuntimeState runtimeState, CollarCommand collar, HotbarBlockVisuals hotbarVisuals, AttachedMoodleLedger moodleLedger, GestureCommand gesture, ReactionService reactions)
    {
        this.hotbarVisuals = hotbarVisuals;
        this.moodleLedger = moodleLedger;
        this.gesture = gesture;
        this.reactions = reactions;
        this.pairing = pairing;
        this.glamourer = glamourer;
        this.slotLocks = slotLocks;
        this.honorific = honorific;
        this.movementLock = movementLock;
        this.restrictionRules = restrictionRules;
        this.restraints = restraints;
        this.toyControl = toyControl;
        this.runtimeState = runtimeState;
        this.collar = collar;
    }

    /// Reverts every local restriction/effect - does not touch any pairing. Every relationship this device
    /// holds remains exactly as paired as it was before.
    public void Panic()
    {
        RevertLocalState();
        Plugin.Log.Information("Panic triggered: outfit/collar reverted, title cleared, movement lock released, all slot locks and restriction rules released. Every pairing remains active.");
    }

    /// Deliberate, explicit unpair of exactly one pairing (Settings' "select a pairing, then Unpair" - any
    /// direction, Owner-side or Sub-side alike). Ends that one pairing - clearing its peer identity, best-
    /// effort notifying that peer over tell (collar/pairing "Panic notifies the peer, best-effort", now
    /// reused for a deliberate unpair instead of panic) and publishing a revocation - and, in the same
    /// action, reverts every local restriction/effect exactly as panic does, since there is no reliable way
    /// to attribute outfit/title/movement-lock/restraint state to the one pairing that applied it. Every
    /// *other* pairing this device holds is untouched by either half of this action.
    public void ReleasePairing(PairingState target)
    {
        RunStep("unpair", () => pairing.ReleasePeer(target));
        RevertLocalState();
    }

    private void RevertLocalState()
    {
        RunStep("revert outfit/collar", () => glamourer.RevertToAutomationFull());
        RunStep("release slot locks", slotLocks.ReleaseAllForPanic);
        RunStep("clear collar moodle", collar.PanicRelease);
        // collar/attached-moodles "Panic clears everything": every moodle goes, attached or not.
        RunStep("clear all moodles", moodleLedger.ClearAllForPanic);

        RunStep("clear title", () =>
        {
            if (runtimeState.TitleApplied)
                honorific.ClearTitle();
        });

        RunStep("release movement lock", movementLock.ReleaseAll);
        RunStep("release restriction rules", restrictionRules.ReleaseAllForPanic);
        // Releasing the Action Block rule above already hides these - repeated directly so a failure in that
        // step can never leave the Sub's hotbars greyed out after panic.
        RunStep("restore hotbars", hotbarVisuals.Hide);
        RunStep("release restraint bound animations", restraints.ReleaseAllBoundAnimationsForPanic);
        // Oathbound locks the mods it holds, so the Sub can't turn them off from Penumbra - panic must.
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
