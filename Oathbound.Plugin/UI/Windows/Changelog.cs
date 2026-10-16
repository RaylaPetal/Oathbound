using System.Collections.Generic;

namespace Oathbound.Plugin.UI;

public sealed record ChangelogSection(string Heading, string[] Items);

public sealed record ChangelogEntry(string Version, string Date, string Title, ChangelogSection[] Sections);

/// Newest first. Add an entry with every release; the window opens once for a version that has one.
public static class Changelog
{
    public static readonly IReadOnlyList<ChangelogEntry> Entries =
    [
        new("0.2.6.0", "2026-10-16", "Cage, Games and more",
        [
            new("New",
            [
                "This window: it shows what changed once after every update, and you can open it again from the ? in the main window.",
                "Everything from the testing builds below is now in the stable release: Cage with Bring & lock, Games, exact gear restores, gag speech and the restyled Sub Control.",
            ]),
        ]),
        new("0.2.5.9", "2026-10-16", "Bring & lock finds the door",
        [
            new("Fixed",
            [
                "Bring & lock to a house cage now finds the house's door instead of the plot's placard, and walks up to it before opening it.",
            ]),
        ]),
        new("0.2.5.8", "2026-10-15", "Bring & lock",
        [
            new("New",
            [
                "Cage: Bring & lock. Like Send, but if your Sub is elsewhere their character travels to the cage by itself (world, teleport, then walking) and is locked in when it arrives. It needs their Teleport permission.",
                "For a cage inside a house, your Sub is taken through the door and walked to the cage. Apartments can't be travelled to; send those normally.",
            ]),
        ]),
        new("0.2.5.7", "2026-10-15", "Games that end",
        [
            new("New",
            [
                "Games: choose when a game ends - when someone runs out of clothes, after 3, 5 or 10 lost rounds, or only when you quit. The last forfeit can still be given before you end it.",
                "Truth or Dare: both of you roll each round; the lower roll picks truth or dare and the winner asks.",
                "Attaching a command to a card opens a searchable picker instead of a long dropdown.",
            ]),
            new("Changed",
            [
                "The spicy and explicit starter cards are spicier and more explicit, and there are more of them.",
            ]),
        ]),
        new("0.2.5.6", "2026-10-15", "Games fixes, commands on cards",
        [
            new("New",
            [
                "As an Owner, your dares and forfeits can carry one of your saved commands (a gag, cuffs, an outfit...). It's sent with the card and only applies within your Sub's permissions.",
            ]),
            new("Fixed",
            [
                "Stripping works over an outfit that isn't locked, and when nothing can come off, the game says why.",
                "Rolls are counted straight from the game's roll lines, so games send far fewer tells. Both players need this version.",
                "The leash line no longer disappears while cage bounds or drawn cuffs are showing.",
            ]),
        ]),
        new("0.2.5.5", "2026-10-15", "Under the hood",
        [
            new("Changed",
            [
                "A large internal reorganization. Nothing should look or behave differently - tell us if something does.",
                "A new plugin description in the installer.",
            ]),
        ]),
        new("0.2.5.4", "2026-10-15", "Cage and Games",
        [
            new("New",
            [
                "Cage: save cage areas where you stand - in the world, a housing ward or inside one specific house - as a square or rectangle you size, or pinned in place. Send one for a set time or until you open it. If your Sub is there it closes now and walks them in; otherwise it waits for them to arrive.",
                "While caged, your Sub can't leave, change their permissions, struggle, use keys or take restraints off. Only the Owner who caged them can open it, and their safeword always does.",
                "The cage's bounds are drawn on the ground, and \"Show cage bounds\" previews a new cage before you save it.",
                "Games: Deathroll, High Roll, Higher or Lower, Twenty-One and Truth or Dare with your partner, rolling with the game's own /random. Either of you can lose: a piece of your own clothing comes off, you take a forfeit, or both - whatever you agreed to. Everything comes back when the game ends.",
            ]),
            new("Changed",
            [
                "Favorites moved from the title bar to the last tile of the main window's grid.",
                "A help button with the bug tracker and Discord in the main window's title bar.",
            ]),
        ]),
        new("0.2.5.3", "2026-10-14", "Gag speech and Sub Control",
        [
            new("New",
            [
                "Owner restraints can ask for a redraw, and restraints redraw when they come off too.",
                "Gagged chat sounds out each word through the gag, mixed with your own gag sounds.",
            ]),
            new("Changed",
            [
                "Sub Control is restyled: tiled rows, unlocks at the top only when something is on, per-restraint unlocks inside Restraints.",
                "The Ko-fi and Patreon icons moved to the header under the relay count.",
            ]),
        ]),
        new("0.2.5.2", "2026-10-14", "Support links",
        [
            new("New",
            [
                "Ko-fi and Patreon links in the main window, if you'd like to support Oathbound.",
            ]),
        ]),
        new("0.2.5.1", "2026-10-14", "Gear comes back exactly",
        [
            new("Changed",
            [
                "Taking a restraint, outfit or collar off puts back exactly what was in each slot before, instead of reverting to Glamourer automation. Only panic still reverts everything.",
                "Unlocking an outfit keeps it on; it just stops being locked.",
                "One restraint per gear slot: a second one for an occupied slot is refused and says which restraint is there.",
            ]),
        ]),
    ];
}
