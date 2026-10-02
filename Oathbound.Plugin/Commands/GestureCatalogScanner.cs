using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.Ipc;
using Lumina.Excel.Sheets;

namespace Oathbound.Plugin.Commands;

public readonly record struct GestureScanResult(int TotalMods, IReadOnlyList<GestureCatalogEntry> Entries, string? Error = null);
public readonly record struct PenumbraOptionScanResult(int TotalMods, IReadOnlyList<PenumbraOptionScanEntry> Entries, string? Error = null);
public sealed record PenumbraOptionScanEntry(string Id, string ModDirectory, string ModName, string GroupName,
    string OptionName, int GroupOrder, int OptionOrder, Dictionary<string, List<string>> GroupSelections,
    bool ModEnabled, IReadOnlyList<string> Paths);
public sealed record PenumbraModScanEntry(string Id, string ModDirectory, string ModName,
    Dictionary<string, List<string>> SavedSelections, bool ModEnabled, IReadOnlySet<uint> ChangedItemIds);

/// Reads Penumbra's option manifests directly, keeping author-facing option names.
public sealed class GestureCatalogScanner(PenumbraIpc ipc, PluginConfig config)
{
    private sealed record ModMetaDto(DefaultDataDto? DefaultData, List<GroupDto>? Groups);
    private sealed record DefaultDataDto(Dictionary<string, string>? Files);
    private sealed record GroupDto(string Type, string Name, List<OptionDto> Options);
    private sealed record OptionDto(string Name, Dictionary<string, string>? Files);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };

    public GestureScanResult Scan()
    {
        var raw = ScanOptions(ResolveGestureScope());
        if (raw.Error is not null) return new GestureScanResult(raw.TotalMods, [], raw.Error);
        var entries = new List<GestureCatalogEntry>();
        foreach (var option in raw.Entries)
        {
            var triggers = GestureTriggerResolver.Detect(option.GroupName, option.OptionName, option.Paths);
            var standingIdle = GestureTriggerResolver.DetectStandingIdle(option.Paths);
            var hasIdle = standingIdle is not null || HasIdleHint(option.GroupName) || HasIdleHint(option.OptionName) || option.Paths.Any(HasIdleHint);
            if (triggers.Count == 0) triggers.Add(null);
            else if (hasIdle) triggers.Insert(0, null);
            for (var triggerOrder = 0; triggerOrder < triggers.Count; triggerOrder++)
            {
                var entry = new GestureCatalogEntry
                {
                    ModDirectory = option.ModDirectory, ModName = option.ModName, GroupName = option.GroupName,
                    AnimationName = option.OptionName, GroupSelections = option.GroupSelections, Trigger = triggers[triggerOrder],
                    ModEnabled = option.ModEnabled, GroupOrder = option.GroupOrder, OptionOrder = option.OptionOrder,
                    TriggerOrder = triggerOrder,
                    IdlePose = triggers[triggerOrder] is null ? standingIdle : null,
                };
                entry.Id = StableId(entry);
                entries.Add(entry);
            }
        }
        return new GestureScanResult(raw.TotalMods, entries);
    }

    public PenumbraOptionScanResult ScanOptions(IEnumerable<string> directories)
    {
        var mods = ipc.TryGetModList();
        var root = ipc.TryGetModDirectory();
        var collection = ipc.TryGetLocalPlayerCollectionId();
        if (mods is null || root is null || collection is null)
            return new PenumbraOptionScanResult(mods?.Count ?? 0, [], "Penumbra or the local-player collection is not ready.");

        var entries = new List<PenumbraOptionScanEntry>();
        foreach (var directory in directories)
        {
            if (!mods.TryGetValue(directory, out var modName)) continue;
            var (enabled, current) = ipc.TryGetCurrentSettings(collection.Value, directory);
            var modPath = Path.Combine(root, directory);
            if (!Directory.Exists(modPath)) continue;

            entries.AddRange(ScanManifest(modPath, directory, modName, enabled, current));
        }
        return new PenumbraOptionScanResult(mods.Count, entries);
    }

    public (int TotalMods, IReadOnlyList<PenumbraModScanEntry> Entries, string? Error) ScanMods(IEnumerable<string> directories)
    {
        var mods = ipc.TryGetModList();
        var collection = ipc.TryGetLocalPlayerCollectionId();
        if (mods is null || collection is null)
            return (mods?.Count ?? 0, [], "Penumbra or the local-player collection is not ready.");
        var entries = new List<PenumbraModScanEntry>();
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!mods.TryGetValue(directory, out var name)) continue;
            var (enabled, selections) = ipc.TryGetCurrentSettings(collection.Value, directory);
            entries.Add(new PenumbraModScanEntry(StableModId(directory), directory, name,
                selections ?? new Dictionary<string, List<string>>(), enabled,
                ipc.TryGetChangedItemIds(directory, name)));
        }
        return (mods.Count, entries, null);
    }

    public static IReadOnlyList<PenumbraOptionScanEntry> ScanManifest(string modPath, string directory,
        string modName, bool enabled, Dictionary<string, List<string>>? current)
    {
        var entries = new List<PenumbraOptionScanEntry>();
        var groups = ReadGroups(modPath, modName, current);
            for (var groupOrder = 0; groupOrder < groups.Count; groupOrder++)
            {
                var group = groups[groupOrder];
                for (var optionOrder = 0; optionOrder < group.Options.Count; optionOrder++)
                {
                    var option = group.Options[optionOrder];
                    // Not ToDictionary: groups can share a display name. Last one wins instead of crashing the scan.
                    var selections = new Dictionary<string, List<string>>();
                    foreach (var g in groups.Where(g => !g.Implicit))
                        selections[g.Name] = g == group ? SelectionFor(g, option.Name) : g.Selected.ToList();
                    entries.Add(new PenumbraOptionScanEntry(StableOptionId(directory, group.Name, option.Name),
                        directory, modName, group.Name, option.Name, groupOrder, optionOrder, selections, enabled,
                        option.Paths.ToList()));
                }
            }
        return entries;
    }

    public IReadOnlyList<string> ResolveGestureScope()
    {
        var mods = ipc.TryGetModList();
        if (mods is null) return [];
        MigrateFolderSelection(mods);
        return SelectScope(mods.Select(x => (x.Key, ipc.TryGetModPath(x.Key, x.Value) ?? "")),
            config.SelectedGestureFolders, config.SelectedGestureMods, emptyFoldersMeanAll: true);
    }

    public IReadOnlyList<string> ResolveRestraintScope()
    {
        var mods = ipc.TryGetModList();
        if (mods is null) return [];
        return SelectScope(mods.Select(x => (x.Key, ipc.TryGetModPath(x.Key, x.Value) ?? "")),
            config.SelectedRestraintFolders, new HashSet<string>(), emptyFoldersMeanAll: false);
    }

    public static IReadOnlyList<string> SelectScope(IEnumerable<(string Directory, string SortPath)> mods,
        IReadOnlyCollection<string> folders, IReadOnlySet<string> explicitMods, bool emptyFoldersMeanAll)
    {
        if (folders.Count == 0 && !emptyFoldersMeanAll) return [];
        return mods.Where(x => (folders.Count == 0 || folders.Any(folder => IsUnder(x.SortPath, folder))) &&
                (explicitMods.Count == 0 || explicitMods.Contains(x.Directory)))
            .Select(x => x.Directory).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void MigrateFolderSelection(Dictionary<string, string> mods)
    {
        if (config.SelectedGestureMods.Count != 0 || config.GestureFolderAllowlist.Count == 0) return;
        foreach (var (directory, name) in mods)
        {
            var path = ipc.TryGetModPath(directory, name);
            if (path != null && config.GestureFolderAllowlist.Any(f => IsUnder(path, f))) config.SelectedGestureMods.Add(directory);
        }
        config.Save();
    }

    public static bool IsUnder(string path, string folder)
    {
        path = path.Replace('\\', '/').TrimEnd('/');
        folder = folder.Replace('\\', '/').TrimEnd('/');
        return folder.Length > 0 && (path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase));
    }
    private static bool HasIdleHint(string text) => Regex.IsMatch(text, @"(^|[^a-z])idle([^a-z]|$)", RegexOptions.IgnoreCase);
    private static List<string> SelectionFor(Group group, string option) => group.Multi ? group.Selected.Append(option).Distinct().ToList() : [option];

    private static string StableId(GestureCatalogEntry e)
    {
        var raw = $"{e.ModDirectory}\n{e.GroupName}\n{e.AnimationName}\n{e.Trigger?.Kind}\n{e.Trigger?.SlashCommand}\n{e.Trigger?.EmoteModeId}\n{e.Trigger?.CPoseState}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
    }

    public static string StableOptionId(string directory, string group, string option)
    {
        var raw = $"{directory}\n{group}\n{option}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
    }

    public static string StableModId(string directory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.Trim().Replace('\\', '/').ToLowerInvariant())))[..16].ToLowerInvariant();

    private sealed record Option(string Name, IEnumerable<string> Paths);
    private sealed record Group(string Name, bool Multi, bool Implicit, List<Option> Options, HashSet<string> Selected);

    /// Reads the mod's meta.json (schema 4): DefaultData plus Groups in manifest order. Penumbra migrates older
    /// split-file mods to this format in place, so only it needs reading.
    private static List<Group> ReadGroups(string modPath, string modName, Dictionary<string, List<string>>? current)
    {
        var groups = new List<Group>();
        var metaPath = Path.Combine(modPath, "meta.json");
        ModMetaDto? meta = null;
        if (File.Exists(metaPath))
        {
            try { meta = JsonSerializer.Deserialize<ModMetaDto>(File.ReadAllText(metaPath), JsonOptions); }
            catch (Exception ex) { Plugin.Log.Warning(ex, $"Failed to parse {metaPath}."); }
        }

        var defaultPaths = (meta?.DefaultData?.Files?.Keys ?? Enumerable.Empty<string>())
            .Where(p => p.StartsWith("chara/", StringComparison.OrdinalIgnoreCase)).ToList();
        if (defaultPaths.Count > 0) groups.Add(new Group("Default", false, true, [new Option(modName, defaultPaths)], [modName]));

        foreach (var dto in meta?.Groups ?? [])
        {
            if (dto.Options is null) continue;
            groups.Add(new Group(dto.Name, dto.Type.Equals("Multi", StringComparison.OrdinalIgnoreCase), false,
                dto.Options.Select(o => new Option(o.Name, o.Files?.Keys ?? Enumerable.Empty<string>())).ToList(),
                current != null && current.TryGetValue(dto.Name, out var selected) ? [.. selected] : []));
        }
        return groups;
    }
}

internal static partial class GestureTriggerResolver
{
    private static readonly Regex CommandHint = new(@"\(/([a-zA-Z]+)\)", RegexOptions.Compiled);
    private static readonly (uint Mode, Regex Pattern)[] PosePatterns = [(1, new Regex(@"j_pose(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)), (2, new Regex(@"s_pose(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)), (3, new Regex(@"l_pose(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled))];
    private static Dictionary<string, List<(string Key, string Command)>>? emotes;

    public static List<GestureTrigger?> Detect(string group, string option, IEnumerable<string> paths)
    {
        var result = new List<GestureTrigger?>();
        var seen = new HashSet<string>();
        void Add(GestureTrigger t) { if (seen.Add($"{t.Kind}:{t.SlashCommand}:{t.EmoteModeId}:{t.CPoseState}")) result.Add(t); }
        var hint = CommandHint.Match(option); if (!hint.Success) hint = CommandHint.Match(group);
        if (hint.Success) Add(new GestureTrigger { Kind = GestureTriggerKind.SlashCommand, SlashCommand = hint.Groups[1].Value });
        foreach (var raw in paths)
        {
            var path = raw.Replace('\\', '/');
            foreach (var (mode, regex) in PosePatterns)
            {
                var match = regex.Match(path);
                if (match.Success && byte.TryParse(match.Groups[1].Value, out var state) && state <= 6)
                    Add(new GestureTrigger { Kind = GestureTriggerKind.Pose, EmoteModeId = mode, CPoseState = state });
            }
            var baseGroundSit = path.EndsWith("/jmn.pap", StringComparison.OrdinalIgnoreCase);
            if (baseGroundSit) Add(new GestureTrigger { Kind = GestureTriggerKind.Pose, EmoteModeId = 1, CPoseState = 0 });
            // `Length: > 0`: some emotes resolve with blank command text, which would catalog an unplayable trigger.
            else if (path.EndsWith(".pap", StringComparison.OrdinalIgnoreCase) && Lookup(path[..^4]) is { Length: > 0 } cmd)
                Add(new GestureTrigger { Kind = GestureTriggerKind.SlashCommand, SlashCommand = cmd });
        }
        return result;
    }

    // Standing idles live under bt_common; weapon-stance folders are battle idles, not /cpose ones.
    private static readonly Regex StandingIdlePose = new(@"/bt_common/emote/pose(\d+)_(loop|start)\.pap$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// Null if none. When it replaces several, the lowest wins.
    public static byte? DetectStandingIdle(IEnumerable<string> paths)
    {
        byte? result = null;
        foreach (var raw in paths)
        {
            var path = raw.Replace('\\', '/');
            byte? pose = path.EndsWith("/bt_common/resident/idle.pap", StringComparison.OrdinalIgnoreCase) ? 0
                : StandingIdlePose.Match(path) is { Success: true } m && byte.TryParse(m.Groups[1].Value, out var n) && n <= 6 ? n
                : null;
            if (pose is { } p && (result is null || p < result))
                result = p;
        }
        return result;
    }

    private static string? Lookup(string path)
    {
        emotes ??= BuildIndex();
        var basename = path[(path.LastIndexOf('/') + 1)..];
        return emotes.TryGetValue(basename, out var hits) ? hits.FirstOrDefault(x => path.EndsWith(x.Key, StringComparison.OrdinalIgnoreCase)).Command : null;
    }

    private static Dictionary<string, (ushort EmoteId, bool Looping)>? emoteModes;

    /// The game loops an emote by its row, whatever animation a mod puts in its place. Null if the command isn't
    /// an emote.
    public static (ushort EmoteId, bool Looping)? LookupEmoteMode(string command)
    {
        emoteModes ??= BuildEmoteModeIndex();
        return emoteModes.TryGetValue(command.TrimStart('/'), out var hit) ? hit : null;
    }

    private static Dictionary<string, (ushort, bool)> BuildEmoteModeIndex()
    {
        var result = new Dictionary<string, (ushort, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (var emote in Plugin.DataManager.GetExcelSheet<Emote>())
        {
            if (!emote.TextCommand.IsValid) continue;
            var text = emote.TextCommand.Value;
            var mode = ((ushort)emote.RowId, emote.EmoteMode.RowId != 0);
            foreach (var command in new[] { text.Command, text.ShortCommand, text.Alias, text.ShortAlias })
            {
                var key = command.ExtractText().TrimStart('/');
                if (key.Length > 0) result.TryAdd(key, mode);
            }
        }
        return result;
    }

    private static Dictionary<string, List<(string, string)>> BuildIndex()
    {
        var result = new Dictionary<string, List<(string, string)>>();
        foreach (var emote in Plugin.DataManager.GetExcelSheet<Emote>())
        {
            if (!emote.TextCommand.IsValid) continue;
            var command = emote.TextCommand.Value.Command.ExtractText().TrimStart('/');
            foreach (var timeline in emote.ActionTimeline)
            {
                if (!timeline.IsValid) continue;
                var key = timeline.Value.Key.ExtractText();
                var basename = key[(key.LastIndexOf('/') + 1)..];
                if (!result.TryGetValue(basename, out var list)) result[basename] = list = [];
                list.Add((key, command));
            }
        }
        return result;
    }
}
