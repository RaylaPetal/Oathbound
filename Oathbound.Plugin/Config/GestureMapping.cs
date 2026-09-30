using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oathbound.Plugin.Config;

public enum GestureTriggerKind { SlashCommand, Pose }

[Serializable]
public class GestureTrigger
{
    public GestureTriggerKind Kind { get; set; }
    public string SlashCommand { get; set; } = "";
    public uint EmoteModeId { get; set; }
    public byte CPoseState { get; set; }
    public string DisplayName => Kind == GestureTriggerKind.SlashCommand
        ? $"/{SlashCommand.TrimStart('/')}"
        : EmoteModeId switch { 1 => $"Ground Sit Pose {CPoseState + 1}", 2 => $"Sit Pose {CPoseState + 1}", 3 => $"Doze Pose {CPoseState + 1}", _ => $"Pose {CPoseState + 1}" };

    /// collar/animation-labels "Pose labels match the game's own numbering": what a person reads - a pose
    /// numbered like its own animation file (`j_pose01` -> "Pose 1"), the base ground sit as "Ground Sit
    /// (default)". Display only: DisplayName above keeps the old +1 numbering because it is also the text saved
    /// commands and aliases resolve by ("Display labels never change which animation a saved command plays"),
    /// and [JsonIgnore] keeps it out of exports and the catalog store.
    [JsonIgnore]
    public string Label => Kind == GestureTriggerKind.SlashCommand
        ? DisplayName
        : (EmoteModeId, CPoseState) switch
        {
            (1, 0) => "Ground Sit (default)",
            (1, _) => $"Ground Sit Pose {CPoseState}",
            (2, _) => $"Sit Pose {CPoseState}",
            (3, _) => $"Doze Pose {CPoseState}",
            _ => $"Pose {CPoseState}",
        };
}

[Serializable]
public class GestureCatalogEntry
{
    public string Id { get; set; } = "";
    public string ModDirectory { get; set; } = "";
    public string ModName { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string AnimationName { get; set; } = "";
    public int GroupOrder { get; set; }
    public int OptionOrder { get; set; }
    public int TriggerOrder { get; set; }
    public Dictionary<string, List<string>> GroupSelections { get; set; } = new();
    public GestureTrigger? Trigger { get; set; }
    /// The standing idle (/cpose number) this option replaces, when it replaces one - set only on its
    /// trigger-less "idle/walk" entry. Kept out of Trigger/Id/Label on purpose so saved commands and restraint
    /// rules that already reference the entry keep resolving. Local-only: never part of the slim export.
    public byte? IdlePose { get; set; }
    public bool ModEnabled { get; set; }

    /// What a restraint's bound animation plays: the entry's own trigger, else a rotation to its standing idle.
    [JsonIgnore]
    public GestureTrigger? PlayableTrigger => Trigger
        ?? (IdlePose is { } idle ? new GestureTrigger { Kind = GestureTriggerKind.Pose, EmoteModeId = 0, CPoseState = idle } : null);
    public string Label => $"{ModName} — {AnimationName}" + (Trigger is null ? " — no playable trigger" : $" — {Trigger.DisplayName}");

    /// Display counterpart of Label (which stays the matching text) - see GestureTrigger.Label.
    [JsonIgnore]
    public string DisplayLabel => $"{ModName} — {AnimationName}" + (Trigger is null ? " — no playable trigger" : $" — {Trigger.Label}");
}

/// collar/config-performance "Catalogs live outside the hot-saved config file" (split-catalog-storage-from-
/// config): LocalCatalog/ImportedPeerCatalog are [JsonIgnore]d and persisted separately by CatalogStore
/// instead, so an unrelated config.Save() elsewhere in the plugin no longer re-serializes every scanned mod.
/// LegacyExtensionData exists only so CatalogStore can migrate a pre-upgrade install's inline catalog data
/// out of GetPluginConfig()'s normal deserialization the first time this loads - see CatalogStore.
[Serializable]
public class GestureMapping
{
    [JsonIgnore]
    public Dictionary<string, GestureCatalogEntry> LocalCatalog { get; set; } = new();

    [JsonIgnore]
    public Dictionary<string, GestureExportEntry> ImportedPeerCatalog { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? LegacyExtensionData { get; set; }
}

/// collar/catalog-sync "Exporting every catalog to one file": the slim shape actually serialized into a
/// Gesture export line - only the fields `CatalogSyncService.ImportGestureLines` (the sole reader) ever
/// consumes. Deliberately excludes `GroupSelections`, `ModDirectory`, `TriggerOrder`, and `ModEnabled` -
/// those are meaningful only to the Sub's own local playback (`GestureCommand.Execute`, which reads them
/// straight off `GestureMapping.LocalCatalog` by `Id`, never from anything re-imported) and, in
/// `GroupSelections`' case, redundantly repeat every other option group's selection state on every single
/// entry - the one field that made a large mod collection's export scale combinatorially instead of
/// linearly with catalog size.
[Serializable]
public class GestureExportEntry
{
    public string Id { get; set; } = "";
    public string ModName { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string AnimationName { get; set; } = "";
    public int GroupOrder { get; set; }
    public int OptionOrder { get; set; }
    public GestureTrigger? Trigger { get; set; }
    public string Label => $"{ModName} — {AnimationName}" + (Trigger is null ? " — no playable trigger" : $" — {Trigger.DisplayName}");

    /// Display counterpart of Label (which stays the matching text) - see GestureTrigger.Label.
    [JsonIgnore]
    public string DisplayLabel => $"{ModName} — {AnimationName}" + (Trigger is null ? " — no playable trigger" : $" — {Trigger.Label}");

    public static GestureExportEntry From(GestureCatalogEntry entry) => new()
    {
        Id = entry.Id,
        ModName = entry.ModName,
        GroupName = entry.GroupName,
        AnimationName = entry.AnimationName,
        GroupOrder = entry.GroupOrder,
        OptionOrder = entry.OptionOrder,
        Trigger = entry.Trigger,
    };
}
