using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// collar/onboarding: one step of the guided tutorial - `TabId` must match one of `CollarWindow.NavItems`'s
/// ids. `OwnerText`/`SubText` are each required (no silent fallback to the other Role's copy per design.md)
/// for any tab that Role's sequence includes; a step with `OwnerText` null is skipped entirely when building
/// the Owner sequence (see `permissions`, which is Sub-only), and likewise for `SubText`.
public sealed record TutorialStep(string TabId, string TabLabel, string? OwnerText, string? SubText);

/// collar/onboarding: drives `CollarWindow`'s `activeModule` from outside the window itself, so the Welcome
/// window, Settings' "Rerun Tutorial" button, and a first-ever Role switch can all start the same tutorial
/// without `CollarWindow` needing to know about any of those three callers (design.md's "Tutorial driver
/// lives outside CollarWindow" decision). One shared step list is filtered per Role at start time, rather
/// than two hand-maintained lists, so the sequence can't silently drift out of sync with `NavItems`.
public sealed class TutorialDriver
{
    private static readonly List<TutorialStep> AllSteps =
    [
        new("title", "Title",
            "Browse and send saved titles to your Sub, or type one and click Send.",
            "Configure alias words that create/apply/clear an Honorific title on your own character when your Owner sends them."),
        new("outfit", "Outfit",
            "Browse and send saved Glamourer outfits to your Sub, or lock/unlock the current one.",
            "Pick which of your saved Glamourer designs an alias can apply, and whether applying it locks Outfit changes until Unlock."),
        new("animation", "Animation",
            "Browse and send saved Penumbra mod-swap animations to your Sub.",
            "Choose which installed Penumbra animation mods and options an alias can trigger on your own character."),
        new("moodles", "Moodles",
            "Browse and send saved Moodles statuses to your Sub.",
            "Pick which of your own Moodles statuses an alias can apply or clear."),
        new("restraints", "Restraints",
            "Browse and send saved restraint devices, each with its own restriction rules, to your Sub.",
            "Capture a gear piece as a restraint device and assign restriction rules (forced pose, walk-only, action block, Gagged, cuffed) to it."),
        new("customtriggers", "Custom Triggers",
            "Compose a bundle of actions across multiple categories, save it, or send it one-off.",
            "Define an alias that fires a bundle of actions across multiple categories in one command."),
        new("collar", "Collar",
            "Send lock/unlock commands for your Sub's configured collar item.",
            "Pick the item that represents your collar and, optionally, a Moodle to apply while it's locked."),
        new("follow", "Follow / Leash",
            "Send engage/release commands for your Sub's follow behavior.",
            "Set the alias words that make your character follow or stop following your Owner."),
        new("permissions", "Permissions",
            null,
            "Permissions live in Settings, on the Permissions tab (opened for you now). Turn each category on or off there - nothing in any other tab can ever apply to your character unless its permission is enabled."),
        new("sync", "Sync",
            "Sync your Sub's exported catalog so their saved outfits, animations, and Moodles show up as one-click sends above.",
            "Scan your own mods/designs/statuses and export a catalog file for your Owner, or wait for them to sync it over the relay."),
    ];

    private readonly Plugin plugin;
    private readonly CollarWindow collarWindow;
    private List<TutorialStep> activeSteps = [];
    private int stepIndex;

    public TutorialDriver(Plugin plugin, CollarWindow collarWindow)
    {
        this.plugin = plugin;
        this.collarWindow = collarWindow;
    }

    public bool IsActive { get; private set; }
    public PairingDirection? ActiveDirection { get; private set; }

    /// collar/multi-pairing: set only while chaining from the Owner tutorial into the Sub tutorial for a
    /// Switch's first-ever Role selection (see StartIfUnseenForRole) - consumed and cleared as soon as the
    /// chained tutorial starts.
    private PairingDirection? pendingChainDirection;

    public TutorialStep? CurrentStep => IsActive && stepIndex < activeSteps.Count ? activeSteps[stepIndex] : null;
    public int CurrentStepNumber => stepIndex + 1;
    public int TotalSteps => activeSteps.Count;
    public bool IsLastStep => stepIndex >= activeSteps.Count - 1;

    /// Starts (or restarts) the guided tutorial for `direction` unconditionally - used by Settings' "Rerun
    /// Tutorial" button, which must replay regardless of whether it has already been seen.
    public void Start(PairingDirection direction)
    {
        ActiveDirection = direction;
        activeSteps = direction == PairingDirection.OwnerSide
            ? AllSteps.Where(s => s.OwnerText is not null).ToList()
            : AllSteps.Where(s => s.SubText is not null).ToList();
        stepIndex = 0;
        IsActive = activeSteps.Count > 0;
        if (!IsActive)
            return;

        collarWindow.OpenMainWindow();
        collarWindow.SetActiveModuleForTutorial(activeSteps[0].TabId);
    }

    /// collar/onboarding "Tutorial completion is tracked independently per Role": only starts the tutorial
    /// for `direction` if its `HasSeen*Tutorial` flag is still false.
    public void StartIfUnseen(PairingDirection direction)
    {
        var seen = direction == PairingDirection.OwnerSide ? plugin.Configuration.HasSeenOwnerTutorial : plugin.Configuration.HasSeenSubTutorial;
        if (!seen)
            Start(direction);
    }

    /// collar/onboarding "Tutorial completion is tracked independently per Role" / "First-ever switch to
    /// Switch triggers both tutorials": the path used by the Welcome window's "Continue" action and by a
    /// Role change elsewhere (Settings' Role combo). For Owner/Sub this is just StartIfUnseen on the
    /// matching direction; for Switch, it starts whichever of the two tutorials is still unseen, and if
    /// both are, chains the Sub tutorial to start automatically once the Owner one finishes or is exited.
    public void StartIfUnseenForRole(PluginRole role)
    {
        switch (role)
        {
            case PluginRole.Owner:
                StartIfUnseen(PairingDirection.OwnerSide);
                break;
            case PluginRole.Sub:
                StartIfUnseen(PairingDirection.SubSide);
                break;
            default:
                var ownerUnseen = !plugin.Configuration.HasSeenOwnerTutorial;
                var subUnseen = !plugin.Configuration.HasSeenSubTutorial;
                if (ownerUnseen)
                {
                    pendingChainDirection = subUnseen ? PairingDirection.SubSide : null;
                    Start(PairingDirection.OwnerSide);
                }
                else if (subUnseen)
                {
                    Start(PairingDirection.SubSide);
                }
                break;
        }
    }

    public void Advance()
    {
        if (!IsActive)
            return;

        stepIndex++;
        if (stepIndex >= activeSteps.Count)
        {
            Complete();
            return;
        }

        collarWindow.SetActiveModuleForTutorial(activeSteps[stepIndex].TabId);
    }

    /// collar/onboarding "User can exit the tutorial early": still marks the current Role's tutorial as
    /// seen, same as completing every step, so exiting early never leaves the tutorial re-triggering later.
    public void ExitEarly() => Complete();

    private void Complete()
    {
        if (ActiveDirection == PairingDirection.OwnerSide)
            plugin.Configuration.HasSeenOwnerTutorial = true;
        else if (ActiveDirection == PairingDirection.SubSide)
            plugin.Configuration.HasSeenSubTutorial = true;
        plugin.Configuration.Save();

        IsActive = false;
        ActiveDirection = null;
        stepIndex = 0;
        activeSteps = [];

        if (pendingChainDirection is { } next)
        {
            pendingChainDirection = null;
            Start(next);
        }
    }
}
