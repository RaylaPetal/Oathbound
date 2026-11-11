using System;
using System.Collections.Generic;

namespace Oathbound.Plugin.Achievements;

/// Keyed by name so new stats and achievements need no migration.
[Serializable]
public sealed class AchievementState
{
    public Dictionary<string, long> Stats { get; set; } = new();

    /// `"{id}:{tier}"` to when it unlocked.
    public Dictionary<string, DateTime> Unlocked { get; set; } = new();

    /// For "different areas / cages / restraints" stats. Capped per stat.
    public Dictionary<string, HashSet<string>> Seen { get; set; } = new();

    /// Each pairing's first day seen paired (UTC day number), for Anniversary.
    public Dictionary<Guid, long> PairingFirstDay { get; set; } = new();

    public long CollaredLastDay { get; set; }
    /// The current run of days; the CollaredDays stat keeps the best.
    public long CollaredStreak { get; set; }

    /// The first pass after the feature arrived has run.
    public bool Seeded { get; set; }

    public bool ShowToasts { get; set; } = true;
    /// The chat sound played on unlock, 1-16 as <se.1>-<se.16>; 0 = none.
    public int Sound { get; set; } = 7;
}
