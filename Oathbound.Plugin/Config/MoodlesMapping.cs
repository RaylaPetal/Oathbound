using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oathbound.Plugin.Config;

/// Individual statuses rather than presets, so the Owner can apply a single one. No folder scope - Moodles has none.
[Serializable]
public class MoodlesStatusEntry
{
    public string StatusId { get; set; } = "";
    public string Name { get; set; } = "";
}

/// StatusName is display only, so the pick still reads sensibly if the status is renamed or deleted.
[Serializable]
public class AttachedMoodleRef
{
    public Guid StatusId { get; set; }
    public string StatusName { get; set; } = "";
}

[Serializable]
public class MoodlesMapping
{
    /// Local only. Persisted separately by CatalogStore.
    [JsonIgnore, Newtonsoft.Json.JsonIgnore]
    public Dictionary<string, MoodlesStatusEntry> LocalCatalog { get; set; } = new();

    // Set-only: an older config's inline catalog is read for CatalogStore's migration, never written back.
    [Newtonsoft.Json.JsonProperty("LocalCatalog")]
    private Dictionary<string, MoodlesStatusEntry>? InlineLocalCatalog { set => LegacyLocalCatalog = value; }

    internal Dictionary<string, MoodlesStatusEntry>? LegacyLocalCatalog;
}
