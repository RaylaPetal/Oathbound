using System;
using System.Numerics;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Safety;

/// What an Owner has currently applied to this Sub, kept purely so PanicHandler can revert everything
/// using only local state - no relay round-trip, no Owner cooperation. Which Glamourer slots are actually
/// locked lives in SlotLockManager (collar/slot-locking) now, not here - this only tracks the
/// Owner-override "force locked" bookkeeping (persisted through PluginConfig so it survives a reload, same
/// as the slot locks themselves) plus a few other in-memory-only flags with no external state to lose.
public sealed class SubRuntimeState
{
    private readonly PluginConfig config;

    public SubRuntimeState(PluginConfig config)
    {
        this.config = config;
    }

    public bool TitleApplied { get; set; }
    public bool MovementLockActive { get; set; }

    /// Set by TitleCommand.ForceApply/OutfitCommand.ForceApply (the Owner's "joker" override - see
    /// ChatCommandListener's reserved-keyword grammar). While true, the Sub's own alias-triggered
    /// Apply/Clear/Unlock for that category is refused - only the matching Force* release (or panic,
    /// which always works regardless) can undo it.
    public bool TitleForceLocked { get; set; }

    /// collar/title "Force-applied title reasserts if removed or changed": the style TitleCommand.ForceApply
    /// most recently sent to Honorific, replayed by TitleCommand.OnFrameworkUpdate for as long as
    /// TitleForceLocked stays true. In-memory only, matching TitleForceLocked itself - there's nothing to
    /// reassert after a reload since the lock doesn't survive one either.
    public string? TitleForceText { get; set; }
    public bool TitleForceIsPrefix { get; set; }
    public Vector3 TitleForceColor { get; set; } = new(1, 1, 1);
    public Vector3? TitleForceGlow { get; set; }

    public bool OutfitForceLocked
    {
        get => config.OutfitForceLocked;
        set { config.OutfitForceLocked = value; config.Save(); }
    }

    /// collar/collaring: set when CollarCommand.ForceApply locks the Sub's configured collar at pairing
    /// acceptance. Released only by CollarCommand.ForceUnlock (the Owner's `collar unlock` override) or
    /// ReleaseOnUnpair - never by panic (collar/collaring "Panic leaves the collar on"), so Reset() leaves it.
    public bool CollarForceLocked
    {
        get => config.CollarForceLocked;
        set { config.CollarForceLocked = value; config.Save(); }
    }

    /// collar/restraints: set by RestraintCommand.ForceApply (the Owner's "joker" override). While true,
    /// the Sub's own alias-triggered device apply/release is refused - only the matching ForceUnlock (or
    /// panic) can undo it, same pattern as OutfitForceLocked/CollarForceLocked.
    public bool RestraintsForceLocked
    {
        get => config.RestraintsForceLocked;
        set
        {
            config.RestraintsForceLocked = value;
            // collar/restraint-lock-timer: every path that clears the lock (ForceUnlock, panic's Reset, pairing
            // end) also discards any timer, so a stale end time can never fire later.
            if (!value)
                config.RestraintsLockExpiresAtUtc = null;
            config.Save();
        }
    }

    /// collar/restraint-lock-timer: the Timed lock's end time, or null for a Permanent (or no) lock.
    public DateTime? RestraintsLockExpiresAtUtc
    {
        get => config.RestraintsLockExpiresAtUtc;
        set { config.RestraintsLockExpiresAtUtc = value; config.Save(); }
    }

    /// collar/toy-control: set while an Owner-initiated vibrate/pattern command is active (either until
    /// the Sub's own local auto-stop timer fires, an explicit stop is received, or panic). Persisted like
    /// RestraintsForceLocked so a stuck-vibrating toy after a crash/reload can still be cleared by panic.
    public bool ToyControlForceLocked
    {
        get => config.ToyControlForceLocked;
        set { config.ToyControlForceLocked = value; config.Save(); }
    }

    /// collar/toy-control "Panic suspends automatic triggers, not just active device output": deliberately
    /// excluded from Reset() below - every other force-lock flag is meant to come back the moment its
    /// underlying condition reasserts itself, but re-arming a trigger the instant panic's own revert
    /// sequence finishes would let the same trigger immediately re-fire within the same tick that caused
    /// the panic in the first place. Only an explicit Sub UI action (not Reset/panic) clears this.
    public bool ToyTriggersSuspended
    {
        get => config.ToyTriggersSuspended;
        set { config.ToyTriggersSuspended = value; config.Save(); }
    }

    /// collar/reactions "Panic suspends reactions": excluded from Reset() for the same reason as
    /// ToyTriggersSuspended - only the user's explicit Resume clears it.
    public bool ReactionsSuspended
    {
        get => config.ReactionsSuspended;
        set { config.ReactionsSuspended = value; config.Save(); }
    }

    public void Reset()
    {
        TitleApplied = false;
        MovementLockActive = false;
        TitleForceLocked = false;
        TitleForceText = null;
        OutfitForceLocked = false;
        RestraintsForceLocked = false;
        ToyControlForceLocked = false;
        // ToyTriggersSuspended is deliberately NOT cleared here - see its own doc comment above.
        // CollarForceLocked neither: the collar outlives panic (collar/collaring).
    }
}
