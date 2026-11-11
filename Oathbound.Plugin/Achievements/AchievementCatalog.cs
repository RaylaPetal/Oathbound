using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface;

namespace Oathbound.Plugin.Achievements;

public enum AchievementCategory { Bonds, Leash, Restraints, Cage, Rulebook, Owner, Play, Secret }

public enum AchievementRole { Any, Owner, Sub }

/// Persisted by name, so members can be added freely; never rename one.
public enum StatKey
{
    PairingsActivated, MostPairings, MostSubs, LongestPairingDays, RecoverySaved, SafewordSet,
    Collared, CollaredDays, CollarLocksSent,
    Leashed, MinutesLeashed, LeashAreas, LeashArrivals, LeashSent,
    RestraintsApplied, StruggledFree, HardStruggle, KeyUnlocks, MinutesRestrained, MinutesGagged, MinutesBlindfolded,
    FullSet, DistinctRestraints, RestraintSent, KeyedLockSent,
    Caged, MinutesCaged, DistinctCages, LongestCageStay, BroughtHome, CagesSent, CagesSaved,
    RulebookAccepted, OathsKept, RitualsDone, LedgerHigh, ShopPurchases, RulebooksPublished, CardsDrawn,
    CommandsSent, OutfitsSent, TitlesSent, MoodlesSent, CustomMoodlesSent, ToySent,
    CommandsApplied, OutfitsApplied, AnimationsApplied, Teleported,
    GamesWon, GamesLost, NothingLeft, ReactionsFired, TriggersFired, CatalogSyncs,
    DungeonWalk, NewYearsVow, HouseCage, GaggedLines, WipeRestrained, NightOwlMinutes,
}

/// `Tiers` are thresholds on `Stat`, Bronze/Silver/Gold when there are three. `Unit` only changes how progress reads.
public sealed record AchievementDef(
    string Id, AchievementCategory Category, AchievementRole Role, string Name, string Description, StatKey Stat,
    long[] Tiers, FontAwesomeIcon Icon, bool Secret = false, AchievementUnit Unit = AchievementUnit.Count)
{
    public bool Tiered => Tiers.Length > 1;
}

public enum AchievementUnit { Count, Minutes, Days }

public static class AchievementCatalog
{
    private static AchievementDef D(string id, AchievementCategory c, AchievementRole r, string name, string desc, StatKey s,
        long[] tiers, FontAwesomeIcon icon, bool secret = false, AchievementUnit unit = AchievementUnit.Count) =>
        new(id, c, r, name, desc, s, tiers, icon, secret, unit);

    private const AchievementCategory Bonds = AchievementCategory.Bonds, Leash = AchievementCategory.Leash,
        Restraints = AchievementCategory.Restraints, Cage = AchievementCategory.Cage, Rulebook = AchievementCategory.Rulebook,
        Owner = AchievementCategory.Owner, Play = AchievementCategory.Play, Secret = AchievementCategory.Secret;
    private const AchievementRole A = AchievementRole.Any, O = AchievementRole.Owner, S = AchievementRole.Sub;
    private const AchievementUnit Min = AchievementUnit.Minutes, Days = AchievementUnit.Days;

    /// Ids are stored with unlock times; never change or reuse one.
    public static readonly IReadOnlyList<AchievementDef> All =
    [
        D("first-oath", Bonds, A, "First Oath", "Pair with someone for the first time.", StatKey.PairingsActivated, [1], FontAwesomeIcon.Handshake),
        D("full-house", Bonds, A, "Full House", "Hold several pairings at once.", StatKey.MostPairings, [2, 3, 5], FontAwesomeIcon.Users),
        D("anniversary", Bonds, A, "Anniversary", "Keep one pairing going.", StatKey.LongestPairingDays, [30, 180, 365], FontAwesomeIcon.BirthdayCake, unit: Days),
        D("insurance", Bonds, A, "Insurance", "Save your recovery code.", StatKey.RecoverySaved, [1], FontAwesomeIcon.Shield),
        D("safe-word", Bonds, S, "Safe Word", "Set a safeword.", StatKey.SafewordSet, [1], FontAwesomeIcon.ShieldAlt),
        D("claimed", Bonds, S, "Claimed", "Wear a locked collar for the first time.", StatKey.Collared, [1], FontAwesomeIcon.Lock),
        D("devoted", Bonds, S, "Devoted", "Stay collared day after day.", StatKey.CollaredDays, [7, 30, 100], FontAwesomeIcon.Heart, unit: Days),
        D("claim-staked", Bonds, O, "Claim Staked", "Lock a collar on your Sub.", StatKey.CollarLocksSent, [1, 5, 25], FontAwesomeIcon.Key),
        D("stable", Bonds, O, "Stable", "Own several Subs at once.", StatKey.MostSubs, [2, 3, 5], FontAwesomeIcon.Crown),

        D("short-leash", Leash, S, "On a Short Leash", "Be leashed for the first time.", StatKey.Leashed, [1], FontAwesomeIcon.Link),
        D("walkies", Leash, S, "Walkies", "Time spent on a leash.", StatKey.MinutesLeashed, [60, 600, 6000], FontAwesomeIcon.Paw, unit: Min),
        D("globetrotter", Leash, S, "Globetrotter", "Enter different areas while leashed.", StatKey.LeashAreas, [10, 50, 150], FontAwesomeIcon.GlobeEurope),
        D("heel", Leash, S, "Heel", "Follow your Owner's leash all the way to them.", StatKey.LeashArrivals, [5, 25, 100], FontAwesomeIcon.Route),
        D("walker", Leash, O, "Walker", "Put your Sub on the leash.", StatKey.LeashSent, [1, 25, 100], FontAwesomeIcon.Walking),

        D("tied-up", Restraints, S, "Tied Up", "Wear a restraint for the first time.", StatKey.RestraintsApplied, [1], FontAwesomeIcon.Handcuffs),
        D("houdini", Restraints, S, "Houdini", "Struggle free of a lock.", StatKey.StruggledFree, [1, 10, 50], FontAwesomeIcon.Magic),
        D("hard-mode", Restraints, S, "Hard Mode", "Struggle free of a Hard lock.", StatKey.HardStruggle, [1], FontAwesomeIcon.FistRaised),
        D("keyholder", Restraints, S, "Keyholder", "Unlock a restraint with its key.", StatKey.KeyUnlocks, [1, 10, 25], FontAwesomeIcon.Key),
        D("knotted", Restraints, S, "Knotted", "Time spent restrained.", StatKey.MinutesRestrained, [60, 600, 6000], FontAwesomeIcon.Handcuffs, unit: Min),
        D("silent-treatment", Restraints, S, "Silent Treatment", "Time spent gagged.", StatKey.MinutesGagged, [60, 600, 3000], FontAwesomeIcon.CommentSlash, unit: Min),
        D("blind-faith", Restraints, S, "Blind Faith", "Time spent blindfolded.", StatKey.MinutesBlindfolded, [60, 600, 3000], FontAwesomeIcon.EyeSlash, unit: Min),
        D("full-set", Restraints, S, "Full Set", "Be gagged, blindfolded and cuffed at once.", StatKey.FullSet, [1], FontAwesomeIcon.LayerGroup),
        D("chain-wardrobe", Restraints, S, "Wardrobe of Chains", "Wear different restraints.", StatKey.DistinctRestraints, [5, 15, 40], FontAwesomeIcon.Boxes),
        D("rigger", Restraints, O, "Rigger", "Put restraints on your Sub.", StatKey.RestraintSent, [1, 50, 250], FontAwesomeIcon.Tools),
        D("locksmith", Restraints, O, "Locksmith", "Lock a restraint with a key.", StatKey.KeyedLockSent, [1, 10, 50], FontAwesomeIcon.UnlockAlt),

        D("kept", Cage, S, "Kept", "Be caged for the first time.", StatKey.Caged, [1], FontAwesomeIcon.Dungeon),
        D("homebody", Cage, S, "Homebody", "Time spent in a cage.", StatKey.MinutesCaged, [60, 600, 3000], FontAwesomeIcon.Home, unit: Min),
        D("pet-shop", Cage, S, "Pet Shop", "Be kept in different cages.", StatKey.DistinctCages, [3, 10, 25], FontAwesomeIcon.Store),
        D("long-stay", Cage, S, "Long Stay", "Stay in one cage for an hour.", StatKey.LongestCageStay, [60], FontAwesomeIcon.HourglassHalf, unit: Min),
        D("brought-home", Cage, S, "Brought Home", "Be brought to your cage and locked in.", StatKey.BroughtHome, [1, 10, 25], FontAwesomeIcon.TruckMoving),
        D("zookeeper", Cage, O, "Zookeeper", "Cage your Sub.", StatKey.CagesSent, [1, 25, 100], FontAwesomeIcon.Dungeon),
        D("architect", Cage, O, "Architect", "Save cages of your own.", StatKey.CagesSaved, [1, 5, 15], FontAwesomeIcon.DraftingCompass),

        D("sworn", Rulebook, S, "Sworn", "Accept a rulebook.", StatKey.RulebookAccepted, [1], FontAwesomeIcon.FileSignature),
        D("oathkeeper", Rulebook, S, "Oathkeeper", "Keep your oaths.", StatKey.OathsKept, [1, 10, 50], FontAwesomeIcon.CheckDouble),
        D("ritualist", Rulebook, S, "Ritualist", "Do your daily rituals.", StatKey.RitualsDone, [7, 30, 100], FontAwesomeIcon.CalendarCheck),
        D("golden-star", Rulebook, S, "Golden Star", "Reach a high ledger score.", StatKey.LedgerHigh, [100, 1000, 5000], FontAwesomeIcon.Star),
        D("retail-therapy", Rulebook, S, "Retail Therapy", "Buy from your Owner's shop.", StatKey.ShopPurchases, [1, 10, 50], FontAwesomeIcon.ShoppingBag),
        D("lawgiver", Rulebook, O, "Lawgiver", "Publish a rulebook.", StatKey.RulebooksPublished, [1, 10, 50], FontAwesomeIcon.Gavel),
        D("card-shark", Rulebook, O, "Card Shark", "Draw cards from your deck by hand.", StatKey.CardsDrawn, [1, 25, 100], FontAwesomeIcon.Clone),

        D("commander", Owner, O, "Commander", "Send commands to your Sub.", StatKey.CommandsSent, [100, 1000, 10000], FontAwesomeIcon.Bullhorn),
        D("stylist", Owner, O, "Stylist", "Dress your Sub.", StatKey.OutfitsSent, [1, 25, 100], FontAwesomeIcon.Tshirt),
        D("name-caller", Owner, O, "Name Caller", "Give your Sub a title.", StatKey.TitlesSent, [1, 25, 100], FontAwesomeIcon.Heading),
        D("mood-setter", Owner, O, "Mood Setter", "Put moodles on your Sub.", StatKey.MoodlesSent, [1, 25, 100], FontAwesomeIcon.Smile),
        D("wordsmith", Owner, O, "Wordsmith", "Send moodles you wrote yourself.", StatKey.CustomMoodlesSent, [1, 10, 25], FontAwesomeIcon.PenFancy),
        D("remote-control", Owner, O, "Remote Control", "Take control of your Sub's toys.", StatKey.ToySent, [1, 50, 250], FontAwesomeIcon.Plug),
        D("good-pet", Owner, S, "Good Pet", "Have your Owner's commands applied.", StatKey.CommandsApplied, [50, 500, 5000], FontAwesomeIcon.Bone),
        D("dressed-up", Owner, S, "Dressed Up", "Be dressed by your Owner.", StatKey.OutfitsApplied, [1, 25, 100], FontAwesomeIcon.Tshirt),
        D("puppet", Owner, S, "Puppet", "Be made to move by your Owner.", StatKey.AnimationsApplied, [1, 25, 100], FontAwesomeIcon.TheaterMasks),
        D("summoned", Owner, S, "Summoned", "Be teleported by your Owner.", StatKey.Teleported, [1, 10, 50], FontAwesomeIcon.Magic),

        D("lucky-streak", Play, A, "Lucky Streak", "Win games.", StatKey.GamesWon, [1, 10, 50], FontAwesomeIcon.Dice),
        D("sore-loser", Play, A, "Sore Loser", "Lose games.", StatKey.GamesLost, [1, 10, 50], FontAwesomeIcon.SadTear),
        D("twitchy", Play, A, "Twitchy", "Have your reactions fire.", StatKey.ReactionsFired, [10, 100, 1000], FontAwesomeIcon.Bolt),
        D("trigger-happy", Play, A, "Trigger Happy", "Run custom triggers.", StatKey.TriggersFired, [10, 100, 500], FontAwesomeIcon.BoltLightning),
        D("in-sync", Play, A, "In Sync", "Publish or import a catalog.", StatKey.CatalogSyncs, [1, 10, 50], FontAwesomeIcon.Sync),

        D("nothing-left", Secret, A, "Nothing Left", "Lose a game with every piece already gone.", StatKey.NothingLeft, [1], FontAwesomeIcon.Feather, secret: true),
        D("dungeon-walk", Secret, S, "Dungeon Walk", "Finish a duty while leashed.", StatKey.DungeonWalk, [1], FontAwesomeIcon.Dungeon, secret: true),
        D("new-years-vow", Secret, S, "New Year's Vow", "Be collared at midnight on New Year's.", StatKey.NewYearsVow, [1], FontAwesomeIcon.GlassCheers, secret: true),
        D("home-sweet-home", Secret, S, "Home Sweet Home", "Be caged inside a house.", StatKey.HouseCage, [1], FontAwesomeIcon.HouseUser, secret: true),
        D("mmmph", Secret, S, "Mmmph", "Say a hundred things through a gag.", StatKey.GaggedLines, [100], FontAwesomeIcon.CommentDots, secret: true),
        D("couldnt-help-it", Secret, S, "Couldn't Help It", "Wipe in a duty while restrained.", StatKey.WipeRestrained, [1], FontAwesomeIcon.Skull, secret: true),
        D("night-owl", Secret, A, "Night Owl", "Spend four hours paired between midnight and 5am.", StatKey.NightOwlMinutes, [240], FontAwesomeIcon.Moon, secret: true, unit: Min),
    ];

    public static readonly IReadOnlyDictionary<StatKey, AchievementDef[]> ByStat =
        All.GroupBy(a => a.Stat).ToDictionary(g => g.Key, g => g.ToArray());

    public static int TotalUnlocks => All.Sum(a => a.Tiers.Length);

    /// A Sub earns and sees Sub and shared achievements, an Owner Owner and shared, a Switch all of them.
    public static bool ForRole(AchievementDef def, Config.PluginRole role) => def.Role == AchievementRole.Any || role switch
    {
        Config.PluginRole.Owner => def.Role == AchievementRole.Owner,
        Config.PluginRole.Sub => def.Role == AchievementRole.Sub,
        _ => true,
    };

    static AchievementCatalog()
    {
        var dupes = All.GroupBy(a => a.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0)
            throw new InvalidOperationException($"Duplicate achievement ids: {string.Join(", ", dupes)}");
    }

    public static string TierName(int tier, int tiers) => tiers <= 1 ? "" : tier switch { 0 => "Bronze", 1 => "Silver", _ => "Gold" };

    public static string Format(long value, AchievementUnit unit) => unit switch
    {
        AchievementUnit.Minutes => value < 60 ? $"{value} min" : $"{value / 60} h",
        AchievementUnit.Days => $"{value} d",
        _ => value.ToString("N0"),
    };
}
