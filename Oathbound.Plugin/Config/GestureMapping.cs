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

    /// Display only ("Pose 1" like the file's numbering). DisplayName keeps the old +1 numbering because saved commands
    /// resolve by it.
    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
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
    /// Set only on the trigger-less "idle/walk" entry, outside Trigger/Id/Label so existing references keep resolving.
    /// Never exported.
    public byte? IdlePose { get; set; }
    public bool ModEnabled { get; set; }

    /// The entry's own trigger, else a rotation to its standing idle.
    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public GestureTrigger? PlayableTrigger => Trigger
        ?? (IdlePose is { } idle ? new GestureTrigger { Kind = GestureTriggerKind.Pose, EmoteModeId = 0, CPoseState = idle } : null);
    public string Label => $"{ModName} — {AnimationName}" + (Trigger is null ? " — no playable trigger" : $" — {Trigger.DisplayName}");

    /// Display counterpart of Label, which stays the matching text.
    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public string DisplayLabel => $"{ModName} — {AnimationName}" + (Trigger is null ? " — no playable trigger" : $" — {Trigger.Label}");
}

/// Catalogs are persisted by CatalogStore so a config save doesn't re-serialize them. Dalamud saves the config with
/// Newtonsoft, so its JsonIgnore is the one that keeps them out.
[Serializable]
public class GestureMapping
{
    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public Dictionary<string, GestureCatalogEntry> LocalCatalog { get; set; } = new();

    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public Dictionary<string, GestureExportEntry> ImportedPeerCatalog { get; set; } = new();

    // Set-only, so an older config's inline catalogs are read for CatalogStore's migration but never written back.
    [Newtonsoft.Json.JsonProperty("LocalCatalog")]
    private Dictionary<string, GestureCatalogEntry>? InlineLocalCatalog { set => LegacyLocalCatalog = value; }

    [Newtonsoft.Json.JsonProperty("ImportedPeerCatalog")]
    private Dictionary<string, GestureExportEntry>? InlineImportedPeerCatalog { set => LegacyImportedPeerCatalog = value; }

    internal Dictionary<string, GestureCatalogEntry>? LegacyLocalCatalog;
    internal Dictionary<string, GestureExportEntry>? LegacyImportedPeerCatalog;
}

/// The slim export shape. Excludes the playback-only fields; GroupSelections made exports scale combinatorially.
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

    /// Display counterpart of Label, which stays the matching text.
    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
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
