using System;
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
    /// What's showing, for the Active banner. In memory only, like TitleApplied.
    public string? TitleText { get; set; }
    public string? OutfitName { get; set; }
    public bool MovementLockActive { get; set; }

    /// While true, the Sub's own title aliases are refused.
    public bool TitleForceLocked => config.OwnerLocks.Title is not null;

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
        TitleText = null;
        OutfitName = null;
        MovementLockActive = false;
        // Every Owner lock ends with panic; the moodles themselves are cleared by the ledger.
        config.OwnerLocks = new OwnerLockState();
        OutfitForceLocked = false;
        ToyControlForceLocked = false;
        // ToyTriggersSuspended and CollarForceLocked are deliberately not cleared.
    }
}
