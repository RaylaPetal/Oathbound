using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Glamourer.Api.Enums;
using Oathbound.Plugin.Ipc;

namespace Oathbound.Plugin.Commands;

/// Works out which game items a restraint mod's options replace, by the model set in each replaced file's path, and
/// names each one as a piece: after its option in a pack, otherwise after the mod or the one group that changes it.
public static class RestraintPieceNamer
{
    public const int MaxNameLength = 60;

    // Variant is ignored: a model applies to every variant of its set, and packs often ship materials for only some.
    private static readonly Regex GearPath = new(
        @"^chara/(?:equipment|accessory)/[ea](\d{4})/.*?[ea]\1_(met|top|glv|dwn|sho|ear|nek|wrs|rir|ril)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Dictionary<(ApiEquipSlot Slot, ushort Set), List<uint>>? itemIndex;

    public static (IReadOnlySet<uint> Offered, IReadOnlyDictionary<uint, string> Names) Resolve(
        string modName, IReadOnlyList<ModOptionFiles> options, IReadOnlySet<uint> penumbraChanged)
    {
        var index = itemIndex ??= BuildIndex();
        // By reference: two options can be identical (e.g. "-----" separators in one group).
        var keysOf = new Dictionary<ModOptionFiles, HashSet<(ApiEquipSlot, ushort)>>(ReferenceEqualityComparer.Instance);
        foreach (var option in options)
            keysOf[option] = ModelKeys(option.Paths);

        // A pieces group puts its options on different models (a gag pack's "Gag Type"); in any other group the options
        // are settings of the same piece (colour, size), whose names would mislabel it.
        var piecesGroups = options.Where(o => !o.Default && keysOf[o].Count > 0).GroupBy(o => o.GroupName)
            .Where(g => g.Select(o => string.Join(",", keysOf[o].Order())).Distinct().Count() >= 2)
            .Select(g => g.Key).ToHashSet();
        var groupKeys = options.Where(o => !o.Default).GroupBy(o => o.GroupName)
            .ToDictionary(g => g.Key, g => g.SelectMany(o => keysOf[o]).ToHashSet());

        var keyOfItem = new Dictionary<uint, (ApiEquipSlot, ushort)>();
        var touching = new Dictionary<uint, List<ModOptionFiles>>();
        foreach (var option in options.Where(o => o.Active))
            foreach (var key in keysOf[option])
                foreach (var item in index.GetValueOrDefault(key) ?? [])
                {
                    if (penumbraChanged.Count > 0 && !penumbraChanged.Contains(item))
                        continue;
                    keyOfItem[item] = key;
                    if (!touching.TryGetValue(item, out var list))
                        touching[item] = list = [];
                    if (!option.Default)
                        list.Add(option);
                }

        // Nothing matched (no manifest, or files we can't read as gear): offer what Penumbra reports, unnamed.
        if (keyOfItem.Count == 0)
            return (penumbraChanged, new Dictionary<uint, string>());

        var oneModel = keyOfItem.Values.Distinct().Count() == 1;
        var names = new Dictionary<uint, string>();
        var namedAfterMod = new List<uint>();
        foreach (var (item, touched) in touching)
        {
            var pieceOptions = touched.Where(o => piecesGroups.Contains(o.GroupName)).ToList();
            var groups = touched.Select(o => o.GroupName).Distinct().ToList();
            string? name = pieceOptions.Count == 1 ? pieceOptions[0].OptionName
                : oneModel ? null
                : groups.Count == 1 && groupKeys[groups[0]].All(k => k == keyOfItem[item]) ? groups[0]
                : null;
            if (name is not null && Clean(name) is { Length: > 0 } clean)
                names[item] = clean;
            else
                namedAfterMod.Add(item);
        }

        // Several items named after the mod are different looks (e.g. one recolour per role's bracelet), so each keeps
        // its game item's name.
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        foreach (var item in namedAfterMod)
        {
            var game = namedAfterMod.Count > 1 ? sheet.GetRowOrDefault(item)?.Name.ExtractText().Trim() ?? "" : "";
            names[item] = game.Length == 0 ? Clean(modName) : WithSuffix(modName, game);
        }
        return (keyOfItem.Keys.ToHashSet(), names);
    }

    public readonly record struct Piece(uint ItemId, string Name, string GameName);

    /// The pieces to offer for a mod's items, by piece name when it has one. Items with the same name on the same
    /// model are one piece (lowest item id kept); a name shared by different models gets its game item's name added.
    public static List<Piece> ListPieces(IEnumerable<uint> itemIds, IReadOnlyDictionary<string, string>? names)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var pieces = new List<(Piece Piece, ApiEquipSlot Slot, ushort Set)>();
        var seen = new HashSet<(string, ApiEquipSlot, ushort)>();
        foreach (var id in itemIds.Distinct().Order())
        {
            if (sheet.GetRowOrDefault(id) is not { } item || GlamourerIpc.GetItemSlot(id) is not { } slot)
                continue;
            var gameName = item.Name.ExtractText();
            var name = names is not null && names.TryGetValue(id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var piece) ? piece : gameName;
            var set = (ushort)(item.ModelMain & 0xFFFF);
            if (string.IsNullOrWhiteSpace(name) || !seen.Add((name.ToLowerInvariant(), NormalizeSlot(slot), set)))
                continue;
            pieces.Add((new Piece(id, name, gameName), NormalizeSlot(slot), set));
        }
        var collisions = pieces.GroupBy(p => p.Piece.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return pieces
            .Select(p => collisions.Contains(p.Piece.Name) && p.Piece.Name != p.Piece.GameName
                ? p.Piece with { Name = $"{p.Piece.Name} ({p.Piece.GameName})" }
                : p.Piece)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string WithSuffix(string name, string suffix)
    {
        var room = Math.Max(8, MaxNameLength - suffix.Length - 3);
        var head = Clean(name);
        if (head.Length > room)
            head = head[..room].TrimEnd();
        return Clean($"{head} ({suffix})");
    }

    private static ApiEquipSlot NormalizeSlot(ApiEquipSlot slot) => slot == ApiEquipSlot.LFinger ? ApiEquipSlot.RFinger : slot;

    public static string Clean(string name)
    {
        var text = new string(name.Where(c => !char.IsControl(c)).ToArray()).Replace('"', '\'').Trim();
        return text.Length > MaxNameLength ? text[..MaxNameLength].TrimEnd() : text;
    }

    private static HashSet<(ApiEquipSlot, ushort)> ModelKeys(IEnumerable<string> paths)
    {
        var keys = new HashSet<(ApiEquipSlot, ushort)>();
        foreach (var raw in paths)
        {
            var match = GearPath.Match(raw.Replace('\\', '/'));
            if (match.Success && ushort.TryParse(match.Groups[1].Value, out var set) && SlotFor(match.Groups[2].Value) is { } slot)
                keys.Add((slot, set));
        }
        return keys;
    }

    private static ApiEquipSlot? SlotFor(string suffix) => suffix.ToLowerInvariant() switch
    {
        "met" => ApiEquipSlot.Head,
        "top" => ApiEquipSlot.Body,
        "glv" => ApiEquipSlot.Hands,
        "dwn" => ApiEquipSlot.Legs,
        "sho" => ApiEquipSlot.Feet,
        "ear" => ApiEquipSlot.Ears,
        "nek" => ApiEquipSlot.Neck,
        "wrs" => ApiEquipSlot.Wrists,
        "rir" or "ril" => ApiEquipSlot.RFinger,
        _ => null,
    };

    private static Dictionary<(ApiEquipSlot, ushort), List<uint>> BuildIndex()
    {
        var index = new Dictionary<(ApiEquipSlot, ushort), List<uint>>();
        foreach (var item in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>())
        {
            if (item.ModelMain == 0 || GlamourerIpc.GetItemSlot(item.RowId) is not { } slot)
                continue;
            var key = (NormalizeSlot(slot), (ushort)(item.ModelMain & 0xFFFF));
            if (!index.TryGetValue(key, out var list))
                index[key] = list = [];
            list.Add(item.RowId);
        }
        return index;
    }
}
