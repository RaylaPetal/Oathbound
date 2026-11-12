using System.Collections.Generic;

namespace Oathbound.Plugin.UI;

public sealed record ChangelogSection(string Heading, string[] Items);

public sealed record ChangelogEntry(string Version, string Date, string Title, ChangelogSection[] Sections);

/// Newest first. Add an entry with every release; the window opens once for a version that has one.
public static class Changelog
{
    public static readonly IReadOnlyList<ChangelogEntry> Entries =
    [
        new("0.2.7.1", "2026-11-12", "Alt characters, teleport notices, a simpler safeword",
        [
            new("New",
            [
                "Alt characters: you pair once, as a person, not once per character. The first time you log into another character, Oathbound asks whether to link it to your pairings - Link, Not now, or Never for this character.",
                "A linked character counts as you: your Owner's commands and your notices find you on whichever one you're playing, and tells go to the character the other person was last on.",
                "A Sub's new characters are accepted by the Owner's plugin automatically. An Owner's new character can't send the Sub anything until the Sub confirms it, and the Sub can remove it again at any time.",
                "Owners with a Sub on more than one character can pick which one commands go to, under Send commands via in the main window. Automatic follows the Sub.",
                "Settings has a new Characters tab: link or unlink your own characters, and see and confirm each partner's.",
                "Everything applied to a Sub - collar, restraints, cage, locks - follows them to every character, linked or not.",
                "Owners: when you send Teleport and your Sub doesn't arrive, you're told why - their setup is missing Lifestream or vnavmesh, Teleport isn't allowed, they're busy or caged, there's no aetheryte or path, or they stopped it.",
                "Owners: when a leashed Sub can't follow you to a new area, you're told why, and their leash is shown as paused rather than off.",
            ]),
            new("Changed",
            [
                "Safeword: your Owners are now only told that you used it - the choice between \"I'm okay, just stop\" and \"Please check on me\" is gone, and so is the option to turn the notice off. An Owner who's online still gets a short tell at once, and one who's offline sees it at their next check.",
                "Pairing again with someone you're already paired with is refused, with a pointer to linking the character instead.",
            ]),
        ]),
        new("0.2.6.9", "2026-11-11", "A new icon",
        [
            new("New",
            [
                "Oathbound has its own icon in the plugin installer: a collar with a heart padlock.",
            ]),
        ]),
        new("0.2.6.8", "2026-11-11", "Safer rulebook templates",
        [
            new("Changed",
            [
                "Rulebook for Owners: picking a template now always asks first, and says plainly that it deletes everything in your draft - oaths, rules, cards, shop and milestones - and can't be undone. Your Sub keeps the rulebook you last published until you publish again.",
            ]),
        ]),
        new("0.2.6.7", "2026-11-11", "Others can see your leash",
        [
            new("New",
            [
                "With SimpleHeels installed, Oathbound players you're synced with now see your status icons - gagged, restrained, leashed - and your leash line when your Owner is nearby. You see theirs the same way, in your own leash colour.",
                "It's always on while SimpleHeels is installed. It carries no names in the clear, can't make anyone's game do anything, and clears at once when you're unleashed, use your safeword or unpair.",
            ]),
            new("Changed",
            [
                "Restraints for Owners: a rules-only restraint you build can now be saved to your list, with its lock, key, struggle and moodle, sent again with one click, and edited later.",
            ]),
        ]),
        new("0.2.6.6", "2026-11-11", "Achievements, a clearer rulebook",
        [
            new("New",
            [
                "Achievements: about sixty milestones for Owners and Subs, from your first leash to a month collared or a thousand commands sent, most with Bronze, Silver and Gold tiers, plus a few secret ones. Open them from the Achievements tile in the main window.",
                "A popup and a sound mark each unlock. They wait until you're out of combat and cutscenes. Turn the popup off or pick the sound in Settings > Identity & Pairing.",
                "They're private: kept on your own computer, never sent to anyone, your partner included. Using your safeword never counts against, or towards, any of them.",
            ]),
            new("Changed",
            [
                "Main window: the modules are now two to a row, so every name shows in full and no row is left half empty.",
                "Rulebook: every rule now reads as one plain sentence - what sets it off and what happens, points and card draws included - with the full details one click away.",
                "Rulebook: a Points panel on both sides shows how points are earned and lost, what they buy, and how far each milestone is.",
                "Rulebook for Owners: a bar at the top shows where your latest version is - unpublished changes, sent, picked up, accepted - with Publish right on it.",
                "Rulebook for Subs: a Today section at the top lists what's due now, what's about to break, and what's done, with your balance. The full list of oaths, rules, shop and activity is folded underneath.",
                "Rulebook for Owners: four tabs instead of eight - Overview, Rules, Points, and Rewards & Punishments. Oaths, place, presence and schedule rules are one list now.",
                "Rulebook for Owners: + Add rule walks you through a new rule step by step, and ends with the rule as a sentence before you add it.",
                "Rulebook for Owners: start from a template - Gentle, Strict or Point economy - then change anything. Nothing is sent until you publish.",
                "Rulebook for Owners: every outcome starts with Give points, Take points, Draw a reward and Draw a punishment, with everything else under More.",
                "Rulebook for Owners: strikes, streaks, repeats, cooldowns and card weights are tucked away until you tick Show advanced settings. Rules that already use them still show them.",
            ]),
        ]),
        new("0.2.6.5", "2026-11-11", "Blindfolds, safeword notice",
        [
            new("New",
            [
                "Restraints: a Blindfolded rule. It darkens the game world on the Sub's own screen - Full (black), Heavy, or Light with a small clear circle around them - while chat, hotbars and the rest of the UI stay visible. Full and Heavy also hide nameplates. It lifts in cutscenes and GPose, and panic always takes it off. Locks, timers, keys and struggling work like any other rule.",
                "Moodles: a built-in Blindfolded moodle for a blindfold with no moodle of its own.",
                "Safeword: when you use your safeword, each of your Owners is told, with one short tell and a note left for an Owner who's offline. It says only \"I'm okay, just stop\" or \"Please check on me\" - pick which under your safeword, or turn it off there.",
                "Owners: a red banner at the top of the window shows when your Sub used their safeword, until you dismiss it, and what you'd last sent them is cleared.",
            ]),
            new("Changed",
            [
                "Reactions: a gesture now goes to the person who set it off. If someone dotes on you while you're looking at someone else, your /dote goes back to them. It's aimed at no one if they aren't nearby.",
                "Reactions: Keep my current facing is now a Face choice: Who set it off, My current target (how it worked before), or No one. Reactions that kept your facing are set to No one. Test actions uses your current target as who set it off.",
            ]),
        ]),
        new("0.2.6.4", "2026-11-11", "Oathbound's own moodles",
        [
            new("New",
            [
                "Moodles: while you're collared, leashed, caged, restrained or gagged, you now show a Collared, Leashed, Caged, Restrained or Gagged moodle, with nothing to set up. They're never added to your Moodles library. A moodle you picked yourself for your collar, leash or a restraint is still used instead, as is one your Owner chooses.",
                "They're on for everyone. To turn them off, untick Use Oathbound's moodles in Settings > Permissions. If they don't show, turn on \"Allow other plugins apply Moodles\" in Moodles' own settings.",
            ]),
        ]),
        new("0.2.6.3", "2026-11-07", "Flying cages",
        [
            new("Changed",
            [
                "Cage: when your cage is somewhere you can't walk to, like a rooftop or a ledge, you're mounted and flown there, landed on the spot and dismounted. The same happens if you fall off it. Bring & lock keeps you in the air all the way to the cage instead of landing you at the zone.",
                "Cage: where flying isn't possible, a cage you can't reach on foot moves to where you stand within a few seconds, instead of after a minute stuck.",
            ]),
            new("Fixed",
            [
                "Cage: standing under a cage placed up high no longer counts as being in it.",
            ]),
        ]),
        new("0.2.6.2", "2026-10-24", "Party rolls, moved installs",
        [
            new("New",
            [
                "Games: party rolls work. Pick Party (/dice) and the game rolls in party chat, seen only by your party. You both need to be in one party. Roll switches your chat to party first; if you type /dice yourself, do it in party chat.",
            ]),
            new("Fixed",
            [
                "After moving your game to a new computer or Wine prefix, pairing and backups could fail with an error every time, and Reset device identity didn't help. The plugin now tells you to reset, and the reset works.",
                "A recovery backup left over from an old device identity no longer fails with \"unauthorized\" over and over.",
                "Before your first pairing, the recovery section says your backup will be saved once you pair, instead of \"Backup is being updated...\" forever.",
            ]),
        ]),
        new("0.2.6.1", "2026-10-16", "Game penalties",
        [
            new("New",
            [
                "Games: a strip game can carry a penalty for losing it - 15 minutes, 30 minutes, 1 hour or 2 hours. Whoever loses the game has everything left in their strip order taken off and stays locked naked that long, even after the game ends. It's shown on the invite before you accept, keeps counting while you're logged out, and panic ends it early.",
                "Quitting before the game is lost never brings a penalty.",
            ]),
        ]),
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
