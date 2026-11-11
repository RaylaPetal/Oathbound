using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace Oathbound.Plugin.Commands;

public enum BuiltinMoodleKind { Collared, Leashed, Caged, Restrained, Gagged }

/// Oathbound's own moodles, applied from data so nothing is added to the Sub's Moodles library. The IDs are fixed
/// forever: held IDs are persisted, and leftovers from an earlier session are found by them.
public static class BuiltinMoodles
{
    private const int Negative = 1;
    private const int Special = 2;

    private sealed record Definition(Guid Id, string Title, string Description, int Kind, string[] IconNames);

    private static readonly Dictionary<BuiltinMoodleKind, Definition> Definitions = new()
    {
        [BuiltinMoodleKind.Collared] = new(new Guid("0a7b0a7b-0000-4c11-a0a0-000000000001"), "Collared",
            "Wearing a collar that isn't theirs to take off.", Special, ["Prey", "Fetters", "Bind"]),
        [BuiltinMoodleKind.Leashed] = new(new Guid("0a7b0a7b-0000-4c11-a0a0-000000000002"), "Leashed",
            "On a leash, following where it leads.", Special, ["Fetters", "Bind"]),
        [BuiltinMoodleKind.Caged] = new(new Guid("0a7b0a7b-0000-4c11-a0a0-000000000003"), "Caged",
            "Locked in a cage.", Negative, ["Heavy", "Bind", "Fetters"]),
        [BuiltinMoodleKind.Restrained] = new(new Guid("0a7b0a7b-0000-4c11-a0a0-000000000004"), "Restrained",
            "Bound and unable to get free.", Negative, ["Bind", "Fetters"]),
        [BuiltinMoodleKind.Gagged] = new(new Guid("0a7b0a7b-0000-4c11-a0a0-000000000005"), "Gagged",
            "Gagged and unable to speak clearly.", Negative, ["Silence", "Pacification"]),
    };

    private static readonly Dictionary<Guid, BuiltinMoodleKind> ById = Definitions.ToDictionary(d => d.Value.Id, d => d.Key);

    private static Dictionary<BuiltinMoodleKind, int>? icons;

    public static IEnumerable<Guid> AllIds => ById.Keys;

    public static Guid IdOf(BuiltinMoodleKind kind) => Definitions[kind].Id;

    public static bool IsBuiltin(Guid id) => ById.ContainsKey(id);

    public static bool TryGet(Guid id, out CustomMoodle moodle)
    {
        moodle = null!;
        if (!ById.TryGetValue(id, out var kind))
            return false;
        var d = Definitions[kind];
        moodle = new CustomMoodle { Id = d.Id, IconId = IconFor(kind), Kind = d.Kind, Seconds = 0, Title = d.Title, Description = d.Description };
        return true;
    }

    private static int IconFor(BuiltinMoodleKind kind)
    {
        icons ??= ResolveIcons();
        return icons.GetValueOrDefault(kind);
    }

    /// By English name so any client language works; the first listed status that exists, else any with an icon.
    private static Dictionary<BuiltinMoodleKind, int> ResolveIcons()
    {
        var result = new Dictionary<BuiltinMoodleKind, int>();
        try
        {
            var byName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in Plugin.DataManager.GetExcelSheet<Status>(ClientLanguage.English))
            {
                if (row.Icon != 0)
                    byName.TryAdd(row.Name.ExtractText(), row.Icon);
            }
            var fallback = byName.Values.FirstOrDefault();
            foreach (var (kind, d) in Definitions)
            {
                var icon = d.IconNames.Select(n => byName.GetValueOrDefault(n)).FirstOrDefault(i => i != 0);
                result[kind] = (int)(icon != 0 ? icon : fallback);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't read status icons for Oathbound's moodles.");
        }
        return result;
    }
}
