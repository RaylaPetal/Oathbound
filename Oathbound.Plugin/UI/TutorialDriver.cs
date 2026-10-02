using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// `TabId` must match a CollarWindow nav id. A step with null text for a role is left out of that role's sequence.
public sealed record TutorialStep(string TabId, string TabLabel, string? OwnerText, string? SubText);

/// Drives the module window from outside CollarWindow, so any caller can start a tutorial. One step list,
/// filtered per role, so it can't drift from the nav items.
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

    /// Set only while chaining a Switch's Owner tutorial into the Sub one.
    private PairingDirection? pendingChainDirection;

    public TutorialStep? CurrentStep => IsActive && stepIndex < activeSteps.Count ? activeSteps[stepIndex] : null;
    public int CurrentStepNumber => stepIndex + 1;
    public int TotalSteps => activeSteps.Count;
    public bool IsLastStep => stepIndex >= activeSteps.Count - 1;

    /// Unconditional, for "Rerun Tutorial".
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

    public void StartIfUnseen(PairingDirection direction)
    {
        var seen = direction == PairingDirection.OwnerSide ? plugin.Configuration.HasSeenOwnerTutorial : plugin.Configuration.HasSeenSubTutorial;
        if (!seen)
            Start(direction);
    }

    /// For Switch, starts whichever tutorial is unseen; with both unseen, chains the Sub one after the Owner one.
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

    /// Still marks the tutorial as seen, so it never re-triggers.
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
