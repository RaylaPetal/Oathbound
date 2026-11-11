using System;
using System.Collections.Generic;
using System.Linq;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Safety;

namespace Oathbound.Plugin.Achievements;

/// One unlock to show. `Def` null is the first run's summary.
public sealed record AchievementToast(AchievementDef? Def, int Tier, string Title, string Text);

/// Counts what this client already sees and unlocks achievements. Never sends anything and never touches the
/// character. Deliberately not hooked to panic: nothing counts, rewards or resets on the safeword. Framework thread.
public sealed class AchievementService
{
    private const int SeenCap = 500;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly PluginConfig config;
    private readonly FollowCommand follow;
    private readonly CageService cage;
    private readonly RestraintCommand restraints;
    private readonly RestrictionRuleManager restrictionRules;
    private readonly SubRuntimeState runtimeState;
    private readonly Queue<AchievementToast> toasts = new();
    private readonly Dictionary<Guid, (long Published, long Imported)> syncSeen = new();

    private DateTime nextTickUtc = DateTime.MinValue;
    private PluginRole? evaluatedRole;
    private long currentCageStay;
    private bool seeding;
    private int seededUnlocks;

    private AchievementState State => config.Achievements;

    public AchievementService(PluginConfig config, FollowCommand follow, CageService cageService, RestraintCommand restraints,
        RestrictionRuleManager restrictionRules, SubRuntimeState runtimeState, OwnerStatusEstimateTracker estimates,
        ChatCommandListener listener, OutfitCommand outfit, GestureCommand gesture, TeleportCommand teleport, ChatGagService gag,
        Rulebook.RulebookService rulebook, ReactionService reactions, CustomTriggerCommand customTriggers)
    {
        this.config = config;
        this.follow = follow;
        cage = cageService;
        this.restraints = restraints;
        this.restrictionRules = restrictionRules;
        this.runtimeState = runtimeState;

        estimates.CommandSent += OnOwnerCommand;
        listener.CommandApplied += () => Add(StatKey.CommandsApplied);
        outfit.OwnerDesignApplied += (_, _, _) => Add(StatKey.OutfitsApplied);
        gesture.OwnerAnimationPlayed += () => Add(StatKey.AnimationsApplied);
        teleport.OwnerTeleported += () => Add(StatKey.Teleported);
        teleport.LeashJourneyEnded += outcome => { if (outcome == LeashJourneyOutcome.Arrived) Add(StatKey.LeashArrivals); };
        gag.GaggedLineSent += () => Add(StatKey.GaggedLines);
        restraints.RestraintWorn += reference => { Mark(StatKey.RestraintsApplied); See(StatKey.DistinctRestraints, reference); };
        restraints.StruggledFree += (_, _) => Add(StatKey.StruggledFree);
        restraints.StruggleWon += level => { if (level == StruggleLevel.Hard) Mark(StatKey.HardStruggle); };
        restraints.KeyUnlocked += (_, _) => Add(StatKey.KeyUnlocks);
        cageService.Closed += OnCageClosed;
        rulebook.Logged += OnRulebookLogged;
        rulebook.Bought += () => Add(StatKey.ShopPurchases);
        rulebook.Published += () => Add(StatKey.RulebooksPublished);
        rulebook.GameEvents.TerritoryEntered += (territory, _) => { if (follow.IsLeashed) See(StatKey.LeashAreas, territory.ToString()); };
        rulebook.GameEvents.DutyCompleted += () => { if (follow.IsLeashed) Mark(StatKey.DungeonWalk); };
        rulebook.GameEvents.DutyWiped += () => { if (restraints.Engaged.Count > 0) Mark(StatKey.WipeRestrained); };
        reactions.Fired += () => Add(StatKey.ReactionsFired);
        customTriggers.Applied += () => Add(StatKey.TriggersFired);
        Games.GamesService.GameEnded += OnGameEnded;
    }

    public long Get(StatKey key) => State.Stats.GetValueOrDefault(key.ToString());

    public DateTime? UnlockedAt(AchievementDef def, int tier) =>
        State.Unlocked.TryGetValue($"{def.Id}:{tier}", out var at) ? at : null;

    public int UnlockedTiers(AchievementDef def)
    {
        var n = 0;
        while (n < def.Tiers.Length && UnlockedAt(def, n) is not null)
            n++;
        return n;
    }

    public bool TryDequeueToast(out AchievementToast toast) => toasts.TryDequeue(out toast!);

    public void Add(StatKey key, long n = 1) => Set(key, Get(key) + n);

    public void Max(StatKey key, long value)
    {
        if (value > Get(key))
            Set(key, value);
    }

    public void Mark(StatKey key) => Max(key, 1);

    private void See(StatKey key, string value)
    {
        if (!State.Seen.TryGetValue(key.ToString(), out var set))
            State.Seen[key.ToString()] = set = new HashSet<string>();
        if (set.Count < SeenCap && set.Add(value))
            Max(key, set.Count);
    }

    private void Set(StatKey key, long value)
    {
        State.Stats[key.ToString()] = value;
        config.Save();
        Evaluate(key, value);
    }

    private void Evaluate(StatKey key, long value)
    {
        if (!AchievementCatalog.ByStat.TryGetValue(key, out var defs))
            return;
        foreach (var def in defs)
        {
            // Counted anyway, so it unlocks the moment the player takes a role that can earn it.
            if (!AchievementCatalog.ForRole(def, config.Role))
                continue;
            for (var tier = 0; tier < def.Tiers.Length; tier++)
            {
                if (value < def.Tiers[tier] || UnlockedAt(def, tier) is not null)
                    continue;
                State.Unlocked[$"{def.Id}:{tier}"] = DateTime.UtcNow;
                config.Save();
                Plugin.Log.Information($"Achievement unlocked: {def.Id} tier {tier + 1}.");
                if (seeding)
                    seededUnlocks++;
                else
                    toasts.Enqueue(new AchievementToast(def, tier, Title(def, tier), def.Description));
            }
        }
    }

    public static string Title(AchievementDef def, int tier) =>
        def.Tiered ? $"{def.Name} {(tier switch { 0 => "I", 1 => "II", _ => "III" })}" : def.Name;

    private void OnOwnerCommand(string body)
    {
        Add(StatKey.CommandsSent);
        var words = body.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return;
        var category = words[0].ToLowerInvariant();
        bool Has(string verb) => words.Skip(1).Any(w => w.Equals(verb, StringComparison.OrdinalIgnoreCase));
        switch (category)
        {
            case ControlWords.Leash:
                Add(StatKey.LeashSent);
                break;
            case "restraint" when Has("lock") || Has("catalog") || Has("wear"):
                Add(StatKey.RestraintSent);
                if (words.Any(w => w.StartsWith("key:", StringComparison.OrdinalIgnoreCase)))
                    Add(StatKey.KeyedLockSent);
                break;
            case "cage" when Has("close"):
                Add(StatKey.CagesSent);
                break;
            case "collar" when Has("lock"):
                Add(StatKey.CollarLocksSent);
                break;
            case "outfit" when Has("lock") || Has("wear"):
                Add(StatKey.OutfitsSent);
                break;
            case "title" when !Has("clear"):
                Add(StatKey.TitlesSent);
                break;
            case "moodle" when Has(CustomMoodle.CustomWord):
                Add(StatKey.MoodlesSent);
                Add(StatKey.CustomMoodlesSent);
                break;
            case "moodle" when Has("apply"):
                Add(StatKey.MoodlesSent);
                break;
            case "toy":
                Add(StatKey.ToySent);
                break;
            case "deck" when Has("draw"):
                Add(StatKey.CardsDrawn);
                break;
        }
    }

    private void OnCageClosed(Config.SubCageState closed, bool brought)
    {
        Mark(StatKey.Caged);
        See(StatKey.DistinctCages, $"{closed.Name}|{closed.Place.ToWire()}");
        if (brought)
            Add(StatKey.BroughtHome);
        if (closed.Place.Kind == CagePlaceKind.House)
            Mark(StatKey.HouseCage);
    }

    private void OnRulebookLogged(PairingState pairing, Rulebook.RulebookEventKind kind)
    {
        if (pairing.Direction != PairingDirection.SubSide)
            return;
        switch (kind)
        {
            case Rulebook.RulebookEventKind.VersionAccepted: Mark(StatKey.RulebookAccepted); break;
            case Rulebook.RulebookEventKind.OathKept: Add(StatKey.OathsKept); break;
            case Rulebook.RulebookEventKind.RitualDone: Add(StatKey.RitualsDone); break;
        }
    }

    private void OnGameEnded(bool lost, bool outOfClothes)
    {
        Add(lost ? StatKey.GamesLost : StatKey.GamesWon);
        if (outOfClothes)
            Mark(StatKey.NothingLeft);
    }

    public void OnFrameworkUpdate()
    {
        var now = DateTime.UtcNow;
        if (now < nextTickUtc || !Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer is null)
            return;
        var firstRun = !State.Seeded;
        if (firstRun)
        {
            seeding = true;
            seededUnlocks = 0;
        }
        if (evaluatedRole != config.Role)
        {
            evaluatedRole = config.Role;
            foreach (var key in Enum.GetValues<StatKey>())
                Evaluate(key, Get(key));
        }
        if (!firstRun)
            Tick(now);
        ReadState(now);
        if (firstRun)
        {
            seeding = false;
            State.Seeded = true;
            config.Save();
            if (seededUnlocks > 0)
                toasts.Enqueue(new AchievementToast(null, 0, $"{seededUnlocks} achievement{(seededUnlocks == 1 ? "" : "s")} unlocked",
                    "Oathbound now has achievements. Open them from the main window."));
        }
        nextTickUtc = now + TickInterval;
    }

    /// One minute of whatever is on right now.
    private void Tick(DateTime now)
    {
        if (follow.IsLeashed) Add(StatKey.MinutesLeashed);
        if (restraints.Engaged.Count > 0) Add(StatKey.MinutesRestrained);
        if (restrictionRules.IsActive(RestraintRuleKind.Gagged)) Add(StatKey.MinutesGagged);
        if (restrictionRules.IsActive(RestraintRuleKind.Blindfolded)) Add(StatKey.MinutesBlindfolded);
        if (cage.IsCaged)
        {
            Add(StatKey.MinutesCaged);
            Max(StatKey.LongestCageStay, ++currentCageStay);
        }
        else
            currentCageStay = 0;
        if (DateTime.Now.Hour < 5 && config.Pairings.Any(p => p.IsPaired))
            Add(StatKey.NightOwlMinutes);
    }

    /// Things that are simply true right now. Also the whole of the first run's seeding.
    private void ReadState(DateTime now)
    {
        var paired = config.Pairings.Where(p => p.IsPaired).ToList();
        var today = (long)(now - DateTime.UnixEpoch).TotalDays;
        if (paired.Count > 0) Mark(StatKey.PairingsActivated);
        Max(StatKey.MostPairings, paired.Count);
        Max(StatKey.MostSubs, paired.Count(p => p.Direction == PairingDirection.OwnerSide));
        foreach (var pairing in paired)
        {
            if (!State.PairingFirstDay.ContainsKey(pairing.Id))
            {
                State.PairingFirstDay[pairing.Id] = today;
                config.Save();
            }
            Max(StatKey.LongestPairingDays, today - State.PairingFirstDay[pairing.Id]);
            if (pairing.Direction == PairingDirection.SubSide)
                Max(StatKey.LedgerHigh, pairing.Rulebook.LedgerScore);
            CountSyncs(pairing);
        }

        if (config.Recovery.CodeAcknowledged) Mark(StatKey.RecoverySaved);
        if (!string.IsNullOrWhiteSpace(config.PanicSafeword)) Mark(StatKey.SafewordSet);
        Max(StatKey.CagesSaved, config.SavedCages.Count);
        if (follow.IsLeashed) Mark(StatKey.Leashed);
        if (cage.IsCaged) Mark(StatKey.Caged);

        var collared = runtimeState.CollarForceLocked && config.CollarOwningPairingId is { } owner && config.FindPairingById(owner) is { IsPaired: true };
        if (collared)
        {
            Mark(StatKey.Collared);
            if (State.CollaredLastDay != today)
            {
                // A day without the collar starts the streak again; panic leaves the collar on, so it never breaks one.
                State.CollaredStreak = State.CollaredLastDay == today - 1 ? State.CollaredStreak + 1 : 1;
                State.CollaredLastDay = today;
                config.Save();
                Max(StatKey.CollaredDays, State.CollaredStreak);
            }
            var local = DateTime.Now;
            if (local is { Month: 1, Day: 1, Hour: 0 })
                Mark(StatKey.NewYearsVow);
        }

        var cuffed = restrictionRules.IsActive(RestraintRuleKind.ArmsCuffed) || restrictionRules.IsActive(RestraintRuleKind.LegsCuffed)
            || restrictionRules.IsActive(RestraintRuleKind.FullBodyCuffed);
        if (cuffed && restrictionRules.IsActive(RestraintRuleKind.Gagged) && restrictionRules.IsActive(RestraintRuleKind.Blindfolded))
            Mark(StatKey.FullSet);
    }

    /// Counts a publish or import when the pairing's timestamp moves on while the plugin runs.
    private void CountSyncs(PairingState pairing)
    {
        var current = (pairing.LastPublishedCatalogUnixSeconds, pairing.LastAcceptedCatalogSyncUnixSeconds);
        if (syncSeen.TryGetValue(pairing.Id, out var seen))
        {
            if (current.LastPublishedCatalogUnixSeconds > seen.Published) Add(StatKey.CatalogSyncs);
            if (current.LastAcceptedCatalogSyncUnixSeconds > seen.Imported) Add(StatKey.CatalogSyncs);
        }
        syncSeen[pairing.Id] = current;
    }
}
