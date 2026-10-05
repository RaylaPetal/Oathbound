using System.Collections.Generic;
using System.Linq;

namespace Oathbound.Plugin.Rulebook;

public enum JobRole
{
    Tank,
    Healer,
    Melee,
    Ranged,
    Caster,
}

/// Combat jobs for the Job or role lock oath, from the game's ClassJob sheet.
public static class Jobs
{
    // ClassJob.Role and BaseParam rows.
    private const byte TankRole = 1;
    private const byte MeleeRole = 2;
    private const byte RangedRole = 3;
    private const byte HealerRole = 4;
    private const byte Intelligence = 4;

    public readonly record struct Job(uint Id, string Abbreviation, JobRole Role);

    private static List<Job>? all;

    /// Jobs only, not their base classes; a base class counts as the jobs it becomes (see IsAllowed).
    public static IReadOnlyList<Job> All => all ??= Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>()
        .Where(j => j.JobIndex > 0)
        .Select(j => new Job(j.RowId, j.Abbreviation.ExtractText(), j.Role switch
        {
            TankRole => JobRole.Tank,
            HealerRole => JobRole.Healer,
            MeleeRole => JobRole.Melee,
            RangedRole when j.PrimaryStat == Intelligence => JobRole.Caster,
            _ => JobRole.Ranged,
        }))
        .OrderBy(j => j.Role).ThenBy(j => j.Abbreviation)
        .ToList();

    public static IEnumerable<uint> OfRole(JobRole role) => All.Where(j => j.Role == role).Select(j => j.Id);

    public static string RoleName(JobRole role) => role switch
    {
        JobRole.Tank => "tank",
        JobRole.Healer => "healer",
        JobRole.Melee => "melee",
        JobRole.Ranged => "physical ranged",
        _ => "caster",
    };

    /// A base class is allowed when a job it becomes is (Conjurer for White Mage).
    public static bool IsAllowed(IReadOnlyCollection<uint> allowed, uint current)
    {
        if (current == 0 || allowed.Contains(current))
            return true;
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
        return allowed.Any(id => sheet.GetRowOrDefault(id)?.ClassJobParent.RowId == current);
    }

    public static string Name(uint id) =>
        Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(id)?.Abbreviation.ExtractText() is { Length: > 0 } abbr
            ? abbr
            : $"job {id}";

    /// Whole roles by name, the rest by abbreviation: "healers, DRK".
    public static string Describe(IReadOnlyCollection<uint> ids)
    {
        var parts = new List<string>();
        var rest = ids.ToHashSet();
        foreach (var role in new[] { JobRole.Tank, JobRole.Healer, JobRole.Melee, JobRole.Ranged, JobRole.Caster })
        {
            var members = OfRole(role).ToList();
            if (members.Count > 0 && members.All(rest.Contains))
            {
                parts.Add(RoleName(role) + "s");
                rest.ExceptWith(members);
            }
        }
        parts.AddRange(rest.Select(Name).OrderBy(n => n));
        return parts.Count == 0 ? "(no jobs)" : string.Join(", ", parts);
    }
}
