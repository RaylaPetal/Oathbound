using System;
using System.Numerics;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.Safety;

/// What an Owner has applied, so PanicHandler can revert from local state alone. Slot locks live in SlotLockManager.
public sealed class SubRuntimeState
{
    private readonly PluginConfig config;

    public SubRuntimeState(PluginConfig config)
    {
        this.config = config;
    }

    public bool TitleApplied { get; set; }
    public bool MovementLockActive { get; set; }

    /// While true, the Sub's own alias-triggered changes are refused.
    public bool TitleForceLocked { get; set; }

    /// Replayed by TitleCommand while TitleForceLocked. In-memory only, like the lock.
    public string? TitleForceText { get; set; }
    public bool TitleForceIsPrefix { get; set; }
    public Vector3 TitleForceColor { get; set; } = new(1, 1, 1);
    public Vector3? TitleForceGlow { get; set; }

    public bool OutfitForceLocked
    {
        get => config.OutfitForceLocked;
        set { config.OutfitForceLocked = value; config.Save(); }
    }

    /// Released only by ForceUnlock or ReleaseOnUnpair - never by panic, so Reset() leaves it.
    public bool CollarForceLocked
    {
        get => config.CollarForceLocked;
        set { config.CollarForceLocked = value; config.Save(); }
    }

    public bool RestraintsForceLocked
    {
        get => config.RestraintsForceLocked;
        set
        {
            config.RestraintsForceLocked = value;
            // Every path that clears the lock also discards its timer, so a stale end time can never fire.
            if (!value)
                config.RestraintsLockExpiresAtUtc = null;
            config.Save();
        }
    }

    /// Null for a Permanent (or no) lock.
    public DateTime? RestraintsLockExpiresAtUtc
    {
        get => config.RestraintsLockExpiresAtUtc;
        set { config.RestraintsLockExpiresAtUtc = value; config.Save(); }
    }

    /// Persisted so a toy left running after a crash can still be cleared by panic.
    public bool ToyControlForceLocked
    {
        get => config.ToyControlForceLocked;
        set { config.ToyControlForceLocked = value; config.Save(); }
    }

    /// Not cleared by Reset(), or a trigger could re-fire right after panic. Only an explicit Sub action clears it.
    public bool ToyTriggersSuspended
    {
        get => config.ToyTriggersSuspended;
        set { config.ToyTriggersSuspended = value; config.Save(); }
    }

    /// Not cleared by Reset(); only the user's Resume clears it.
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
        // ToyTriggersSuspended and CollarForceLocked are deliberately not cleared.
    }
}
