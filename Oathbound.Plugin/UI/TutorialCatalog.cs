using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Oathbound.Plugin.UI;

/// Anchor keys for controls that aren't a Section. Every Section anchors itself as `Section(id)`.
public static class TutorialAnchors
{
    public const string QuickAccess = "dtr:quickAccess";
    public const string MainCharacter = "main:character";
    public const string MainPairing = "main:pairing";
    public const string MainChannel = "main:channel";
    public const string MainSafeword = "main:safeword";
    public const string MainRevertAll = "main:revertAll";
    public const string MainSubControl = "main:subControl";
    public const string MainNav = "main:nav";
    public const string MainSettings = "main:settings";
    public const string NavFavorites = "nav:favorites";
    public const string ModuleHelp = "module:help";
    public const string QuickRow = "quick:row";
    public const string QuickStar = "quick:star";
    public const string QuickSend = "quick:send";
    public const string QuickLock = "quick:lock";
    public const string ReactionNew = "reaction:new";
    public const string FavoritesHelp = "favorites:help";
    public const string FavoritesRow = "favorites:row";

    public static string Section(string id) => "section:" + id;
    public static string Fixed(string fixedActionId) => "fixed:" + fixedActionId;
}

/// Every tour's content. Keep in step with the UI: when a module's sections change, update its tour here.
public static class TutorialCatalog
{
    public const string OverviewId = "overview";

    private static string S(string id) => TutorialAnchors.Section(id);

    /// Debug builds only: every tour exists for both roles, is non-empty, and has no repeated step titles.
    [Conditional("DEBUG")]
    public static void Validate()
    {
        foreach (var id in OwnerTours.Keys.Union(SubTours.Keys))
        {
            foreach (var (role, tours) in new[] { ("Owner", OwnerTours), ("Sub", SubTours) })
            {
                if (!tours.TryGetValue(id, out var steps) || steps.Count == 0)
                    Plugin.Log.Error($"Tutorial catalog: no {role} tour for \"{id}\".");
                else if (steps.GroupBy(s => s.Title).FirstOrDefault(g => g.Count() > 1) is { } dup)
                    Plugin.Log.Error($"Tutorial catalog: {role} tour \"{id}\" repeats the step \"{dup.Key}\".");
            }
        }
    }

    public static Tour? Get(string id, bool owner) =>
        (owner ? OwnerTours : SubTours).TryGetValue(id, out var steps) ? new Tour(id, owner, steps) : null;

    private static TourStep Help(string module, string what) => new(
        "This module's tour",
        $"{what}\n\nThe ? button up here starts this tour again any time.",
        TutorialAnchors.ModuleHelp, n => n.Module(module));

    private static TourStep Star() => new(
        "Favorites",
        "The star marks a command as a favorite. Favorites show up in the Favorites window and in the Quick Access entry at the top of your screen, so you can send them without opening this module.",
        TutorialAnchors.QuickStar, MissingText: "No saved command is listed yet, so there's no star to point at.");

    private static TourStep SendRow(string what) => new(
        "Send and Copy",
        $"Send tells your Sub to {what} right away. Copy puts the same command on your clipboard instead, for a macro or to send it yourself.",
        TutorialAnchors.QuickSend, MissingText: "No saved command is listed yet.");

    private static readonly Dictionary<string, IReadOnlyList<TourStep>> OwnerTours = new()
    {
        [OverviewId] =
        [
            new("Quick Access", "The star entry in the server info bar - top-right of your screen, next to the clock - opens your favorite commands from anywhere, plus shortcuts to this window and Settings. It's pulsing now so you can spot it.", TutorialAnchors.QuickAccess),
            new("Opening Oathbound", "Type /ob to open this window, and /obsettings to open Settings."),
            new("You", "Your character. Everything here acts as and for this character.", TutorialAnchors.MainCharacter, n => n.Main()),
            new("Your pairing", "Who you're paired with. With more than one pairing, pick the active one here: commands go to it, and each module shows its Owner or Sub view.", TutorialAnchors.MainPairing, n => n.Main()),
            new("Sending channel", "Which chat channel your commands travel on. Your Sub listens on all of them, so nothing changes on their side.", TutorialAnchors.MainChannel, n => n.Main(), "Shown once you're paired as an Owner."),
            new("Revert all", "Takes everything off your active Sub at once: restraints, outfit, title, leash, animation, toys and moodles. It needs two clicks. The collar and the pairing are never touched.", TutorialAnchors.MainRevertAll, n => n.Main(), "Shown once you're paired as an Owner."),
            new("Sub Control", "Opens every command you have, from every module, in one console docked beside this window.", TutorialAnchors.MainSubControl, n => n.Main(), "Shown once you're paired as an Owner."),
            new("Safeword", "Your own safeword. Typing /obpanic always takes everything off you, whatever role you're in.", TutorialAnchors.MainSafeword, n => n.Main()),
            new("Modules", "Each tile opens a module: titles, outfits, animations, moodles, restraints, toys, custom triggers, the collar, the leash, reactions and sync. A greyed-out tile names the plugin it still needs - hover it.", TutorialAnchors.MainNav, n => n.Main()),
            new("Favorites", "Everything you've starred, in one window.", TutorialAnchors.NavFavorites, n => n.Main()),
            new("Settings", "The cog opens Settings. The next steps walk through it.", TutorialAnchors.MainSettings, n => n.Main()),
            new("Role & trigger phrase", "Your role (Owner, Sub or Switch) and the trigger phrase that starts every command tell.", S("roleTrigger"), n => n.Settings(SettingsTab.Identity)),
            new("Pairing", "Pair with someone by code: one of you creates an invitation, the other enters the code, and you each confirm the other's character.", S("pairWith"), n => n.Settings(SettingsTab.Identity)),
            new("Recovery code", "Keeps your pairings safe if you reinstall or move PCs. Write it down somewhere private.", S("recoveryCard"), n => n.Settings(SettingsTab.Identity)),
            new("Acknowledgements", "Some features automate your character more than others. Each one has to be acknowledged here before it can be used.", S("tosCard"), n => n.Settings(SettingsTab.Tos)),
            new("In-world visuals", "The leash line and the status icons next to your Sub's name. Turn either off here; it only changes what you see.", S("worldVisualsCard"), n => n.Settings(SettingsTab.Identity)),
            new("Test Commands", "Run a command on yourself, exactly as if it arrived in a tell, without anyone sending it.", S("testCommandCard"), n => n.Settings(SettingsTab.Test)),
            new("Every module has a tour", "Open any module and click the ? in its title bar for a tour of that module. Rerun this overview from Settings any time.", TutorialAnchors.MainNav, n => n.Main()),
            new("The safeword", "/obpanic takes everything off the person who types it, except a locked collar - only its Owner or unpairing removes that. It never needs the other person."),
        ],
        ["title"] =
        [
            Help("title", "Send your Sub a title to wear."),
            new("Commands", "Ready-made title commands, like clearing your Sub's title.", S("titleQuickFixed"), n => n.Module("title")),
            new("Add a title command", "Type a title, choose prefix or suffix and an optional glow, and save it for one-click sending.", S("titleQuickAdd"), n => n.Module("title")),
            new("Saved titles", "Your saved titles. Each row can be sent, copied, starred, edited or deleted.", S("titleQuickSaved"), n => n.Module("title"), "Nothing saved yet - add one above."),
            SendRow("wear that title"),
            Star(),
        ],
        ["outfit"] =
        [
            Help("outfit", "Dress your Sub in one of their own Glamourer designs."),
            new("Commands", "Unlock lets your Sub change outfits again after a locked one.", S("outfitQuickFixed"), n => n.Module("outfit")),
            new("Saved outfits", "Your Sub's designs, synced from their catalog. Each row can be sent locked (they can't change out of it) or unlocked.", TutorialAnchors.QuickRow, n => n.Module("outfit"), "No outfits yet - they arrive when your Sub's catalog syncs (see the Sync module)."),
            SendRow("put on that outfit"),
            Star(),
        ],
        ["animation"] =
        [
            Help("animation", "Make your Sub perform an emote or pose from their Penumbra animation mods."),
            new("Stop animation", "An animation you send holds your Sub in place until you stop it. This sends the stop.", TutorialAnchors.Fixed(Config.FixedActionIds.StopAnimation), n => n.Module("animation")),
            new("Search", "Find an animation by mod, group or name.", S("gestureQuickSearchBox"), n => n.Module("animation"), "No animations yet - they arrive when your Sub's catalog syncs."),
            new("Animations", "Grouped by mod. Each row plays that animation on your Sub.", TutorialAnchors.QuickRow, n => n.Module("animation"), "No animations yet."),
            SendRow("play that animation"),
            Star(),
        ],
        ["moodles"] =
        [
            Help("moodles", "Put one of your Sub's own Moodles statuses on them, or clear it."),
            new("Commands", "Clear moodle, and every status your Sub has shared. Each row can be sent, copied or starred.", S("moodlesQuickFixed"), n => n.Module("moodles")),
            SendRow("show that status"),
            Star(),
        ],
        ["restraints"] =
        [
            Help("restraints", "Restrain your Sub with gear or rules - forced pose, walk only, blocked actions, gagged chat, cuffs."),
            new("Commands", "Restraint unlock takes every restraint you put on them off again.", S("restraintQuickCommands"), n => n.Module("restraints")),
            new("Available restraint mods", "Restraint mods your Sub has. Configure one here to give it rules.", S("restraintQuickBrowser"), n => n.Module("restraints"), "Nothing shared yet - see the Sync module."),
            new("Configured mod restraints", "Mods you've set up with rules, ready to send.", S("restraintQuickConfigured"), n => n.Module("restraints"), "None configured yet."),
            new("Shared rules-only restraints", "Restraints your Sub set up themselves, with rules but no gear.", S("restraintQuickShared"), n => n.Module("restraints"), "Your Sub hasn't shared any."),
            new("Rules-only restraint", "Build a restraint from rules alone, without any gear, and send it.", S("restraintQuickAdHoc"), n => n.Module("restraints")),
            new("Lock timer", "Locked restraints stay on until you unlock them, or until a timer you set here runs out.", TutorialAnchors.QuickLock, n => n.Module("restraints"), "Appears on a restraint row."),
            Star(),
        ],
        ["toycontrol"] =
        [
            Help("toycontrol", "Control your Sub's connected toy, when they allow it."),
            new("Status", "Whether your Sub's toy is connected, as far as your client knows.", S("toyQuickStatus"), n => n.Module("toycontrol")),
            new("Vibrate", "Pick an intensity and a duration, or run it until stopped, and send.", S("toyQuickVibrate"), n => n.Module("toycontrol")),
            new("Patterns & stop", "Send one of the patterns, or stop the toy.", S("toyQuickPatterns"), n => n.Module("toycontrol")),
        ],
        ["customtriggers"] =
        [
            Help("customtriggers", "Bundle several actions into one command."),
            new("Revert", "Undoes what custom triggers put on your Sub: title, outfit, animation, moodles and restraints.", S("ctqRevert"), n => n.Module("customtriggers")),
            new("Saved", "Bundles you've saved, and your Sub's own custom trigger words.", S("ctqSaved"), n => n.Module("customtriggers")),
            new("Build a bundle", "Add actions - a title, an outfit, an animation, a moodle, a restraint, a chat line - and save or send them together.", S("ctqBuilder"), n => n.Module("customtriggers")),
            new("One-off command", "Type any command and send it once without saving it.", S("freeformComposer"), n => n.Module("customtriggers")),
        ],
        ["collar"] =
        [
            Help("collar", "Your Sub's collar - a piece of their gear that only you can take off."),
            new("Commands", "Lock the collar on, or unlock it. While locked, it survives your Sub's safeword - only you or unpairing removes it.", S("collarQuick"), n => n.Module("collar")),
        ],
        ["follow"] =
        [
            Help("follow", "Put your Sub on a leash."),
            new("Commands", "Leash with a length, or unleash. A leashed Sub moves freely inside the length and is pulled along when you walk further. It stays on through teleports and houses until you unleash.", S("followQuickCommands"), n => n.Module("follow")),
            Star(),
        ],
        ["reactions"] =
        [
            Help("reactions", "Make your own character react by itself when someone uses an emote on you or says a phrase."),
            new("New reaction", "Start a reaction: choose what triggers it and who may trigger it.", TutorialAnchors.ReactionNew, n => n.Module("reactions")),
            new("Your reactions", "Each reaction can play an animation, use an item, turn on a mod, add a moodle or send a chat line. Test actions tries them on you now.", S("reactionList"), n => n.Module("reactions"), "No reactions yet."),
            new("Mods turned on by reactions", "Mods a reaction switched on, so you can see them and turn them back off.", S("reactionActiveMods"), n => n.Module("reactions"), "Shown while a reaction has a mod on."),
        ],
        ["sync"] =
        [
            Help("sync", "Get your Sub's outfits, animations, moodles and restraints into your modules."),
            new("Cloud catalog sync", "Your Sub's catalog arrives here by itself, end-to-end encrypted, about once an hour.", S("catalogRelay"), n => n.Module("sync")),
            new("Import from a file", "If your Sub sends you an export file instead, import it here.", S("catalogFileFallback"), n => n.Module("sync")),
        ],
        ["favorites"] =
        [
            new("Favorites", "Every command you've starred, grouped by module, with Send on each.", TutorialAnchors.FavoritesHelp, n => n.Favorites()),
            new("A favorite", "Send it straight from here. To add more, use the star on any saved command in a module.", TutorialAnchors.FavoritesRow, n => n.Favorites(), "Nothing starred yet - star a saved command in any module."),
            new("Quick Access", "The same favorites open from the star entry at the top of your screen, without opening any window. It's pulsing now.", TutorialAnchors.QuickAccess),
        ],
    };

    private static readonly Dictionary<string, IReadOnlyList<TourStep>> SubTours = new()
    {
        [OverviewId] =
        [
            new("Quick Access", "The star entry in the server info bar - top-right of your screen, next to the clock - opens this window and Settings from anywhere. It's pulsing now so you can spot it.", TutorialAnchors.QuickAccess),
            new("Opening Oathbound", "Type /ob to open this window, and /obsettings to open Settings."),
            new("You", "Your character. Everything your Owner sends is applied here, by your own plugin.", TutorialAnchors.MainCharacter, n => n.Main()),
            new("Your pairing", "Who owns you. With more than one pairing, pick the active one here.", TutorialAnchors.MainPairing, n => n.Main()),
            new("Safeword", "Your safeword. Typing /obpanic takes everything off you at once, except a locked collar.", TutorialAnchors.MainSafeword, n => n.Main()),
            new("Modules", "Each tile is something your Owner can do to you. In each one you choose what's allowed - which outfits, animations, moodles, restraints. A greyed-out tile names the plugin it still needs - hover it.", TutorialAnchors.MainNav, n => n.Main()),
            new("Favorites", "Starred commands are an Owner feature; as a Switch you'll find yours here.", TutorialAnchors.NavFavorites, n => n.Main()),
            new("Settings", "The cog opens Settings. The next steps walk through it.", TutorialAnchors.MainSettings, n => n.Main()),
            new("Role & trigger phrase", "Your role and the trigger phrase every command tell must start with.", S("roleTrigger"), n => n.Settings(SettingsTab.Identity)),
            new("Pairing", "Pair with your Owner by code: one of you creates an invitation, the other enters the code, and you each confirm the other's character.", S("pairWith"), n => n.Settings(SettingsTab.Identity)),
            new("Recovery code", "Keeps your pairings safe if you reinstall or move PCs. Write it down somewhere private.", S("recoveryCard"), n => n.Settings(SettingsTab.Identity)),
            new("Permissions", "Nothing applies to you unless its category is on here. Turn each one on only when you're comfortable.", S("permissionsCard"), n => n.Settings(SettingsTab.Permissions)),
            new("Acknowledgements", "The heavier features - animations, leash, restraints, teleport, custom chat, toys - each need an acknowledgement here first.", S("tosCard"), n => n.Settings(SettingsTab.Tos)),
            new("In-world visuals", "The leash line and status icons. Turn either off here; it only changes what you see.", S("worldVisualsCard"), n => n.Settings(SettingsTab.Identity)),
            new("Test Commands", "Try a command on yourself exactly as if your Owner sent it - nobody else is involved.", S("testCommandCard"), n => n.Settings(SettingsTab.Test)),
            new("Every module has a tour", "Open any module and click the ? in its title bar for a tour of that module. Rerun this overview from Settings any time.", TutorialAnchors.MainNav, n => n.Main()),
            new("The safeword", "/obpanic takes everything off you - outfit, title, animation, leash, restraints, toys - except a locked collar. It works any time, without your Owner."),
        ],
        ["title"] =
        [
            Help("title", "Titles your Owner can put on you, through Honorific."),
            new("Fixed words", "Words your Owner can always use, like clearing your title.", S("titleFixed"), n => n.Module("title")),
            new("Your title aliases", "Short words you've defined that put a specific title on you.", S("titleAliases"), n => n.Module("title"), "None yet - add one below."),
            new("Add a title alias", "Pick a word, the title text, prefix or suffix and an optional glow.", S("titleAdd"), n => n.Module("title")),
        ],
        ["outfit"] =
        [
            Help("outfit", "Outfits your Owner can put on you, from your own Glamourer designs."),
            new("Fixed words", "Words your Owner can always use, like unlocking your outfit.", S("outfitFixed"), n => n.Module("outfit")),
            new("Your outfit aliases", "Words that put a specific design on you.", S("outfitAliases"), n => n.Module("outfit"), "None yet - add one below."),
            new("Add an outfit alias", "Pick a word and a design, and whether wearing it locks you in it until your Owner unlocks it.", S("outfitAdd"), n => n.Module("outfit")),
        ],
        ["animation"] =
        [
            Help("animation", "Animations your Owner can play on you, from your Penumbra animation mods. An animation holds you in place until your Owner stops it or you use your safeword."),
            new("Your animation aliases", "Words that play a specific animation on you.", S("gestureAliases"), n => n.Module("animation"), "None yet - add one below."),
            new("Add an animation alias", "Pick a word and one of your animations.", S("gestureAdd"), n => n.Module("animation")),
            new("Active animation", "Shows whether an animation is active. While your Owner holds you in one, only they, revert all or your safeword can end it.", S("gestureActive"), n => n.Module("animation")),
        ],
        ["moodles"] =
        [
            Help("moodles", "Moodles statuses your Owner can put on you."),
            new("Fixed words", "Words your Owner can always use, like clearing a moodle.", S("moodleFixed"), n => n.Module("moodles")),
            new("Your moodle aliases", "Words that apply one of your statuses.", S("moodleAliases"), n => n.Module("moodles"), "None yet - add one below."),
            new("Add a moodle alias", "Pick a word and a status.", S("moodleAdd"), n => n.Module("moodles")),
        ],
        ["restraints"] =
        [
            Help("restraints", "Restraints your Owner can put on you."),
            new("Rules-only restraints", "Restraints made of rules without gear - forced pose, walk only, blocked actions, gagged. Your Owner sees these.", S("capturedDevices"), n => n.Module("restraints"), "None yet."),
            new("Detected restraint mods", "Restraint mods found in Penumbra. Configure one to make it usable.", S("detectedRestraintMods"), n => n.Module("restraints"), "Shown when Penumbra has restraint mods."),
            new("My configured mod restraints", "Mods you've set up, each with its own rules.", S("configuredRestraintMods"), n => n.Module("restraints"), "None configured yet."),
        ],
        ["toycontrol"] =
        [
            Help("toycontrol", "Your toy, through Intiface. Nothing reaches it unless you connect it and turn Toy control on."),
            new("Connection", "The Intiface address to connect to, and connect/disconnect.", S("toyConnection"), n => n.Module("toycontrol")),
            new("Status", "What's connected, and Stop now.", S("toyStatus"), n => n.Module("toycontrol")),
            new("Limits", "The most your Owner can ever do - maximum intensity and duration.", S("toyLimits"), n => n.Module("toycontrol")),
            new("Patterns", "Patterns your Owner can play.", S("toyPatterns"), n => n.Module("toycontrol")),
            new("Automatic triggers", "Make your toy react to your own game - health, emotes, spells. These run without anyone clicking.", S("toyTriggers"), n => n.Module("toycontrol")),
        ],
        ["customtriggers"] =
        [
            Help("customtriggers", "One word that does several things at once."),
            new("Your custom triggers", "Each trigger is a list of actions: a title, an outfit, an animation, a moodle, a restraint, or a chat line (which needs its own acknowledgement).", S("customTriggerList"), n => n.Module("customtriggers"), "None yet."),
        ],
        ["collar"] =
        [
            Help("collar", "Your collar - a piece of gear your Owner can lock on you."),
            new("Collar item", "The gear piece that is your collar.", S("collarItem"), n => n.Module("collar")),
            new("Ring", "An optional left-hand ring that locks and unlocks together with your collar.", S("collarRing"), n => n.Module("collar")),
            new("Collar moodle", "A status to show while the collar is locked.", S("collarMoodle"), n => n.Module("collar")),
        ],
        ["follow"] =
        [
            Help("follow", "Your leash."),
            new("Fixed words", "The leash and unleash words your Owner uses.", S("leashFixed"), n => n.Module("follow")),
            new("Leash length", "The longest leash you'll accept. A longer one is shortened to this.", S("leashLimit"), n => n.Module("follow")),
            new("Attached moodle", "A status shown while you're leashed.", S("leashMoodle"), n => n.Module("follow")),
        ],
        ["reactions"] = OwnerTours["reactions"],
        ["sync"] =
        [
            Help("sync", "Share what you've allowed with your Owner, so it appears in their modules."),
            new("Cloud catalog sync", "Your catalog reaches your Owner by itself, end-to-end encrypted.", S("subRelayInfo"), n => n.Module("sync")),
            new("Scan", "Find your designs, animations, restraints and moodles. Scan all refreshes everything.", S("subScan"), n => n.Module("sync")),
            new("Not shared", "What never leaves your PC.", S("subShareLimits"), n => n.Module("sync")),
            new("Offline / manual export", "Export a file instead, if you'd rather hand it over yourself.", S("subExport"), n => n.Module("sync")),
        ],
        ["favorites"] =
        [
            new("Favorites", "Favorites are an Owner feature: an Owner stars commands in each module and sends them from here and from the Quick Access entry.", TutorialAnchors.FavoritesHelp, n => n.Favorites()),
        ],
    };
}
