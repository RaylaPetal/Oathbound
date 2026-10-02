using System;
using System.Collections.Generic;

namespace Oathbound.Plugin.Config;

[Serializable]
public class WardrobeDesignEntry
{
    public Guid DesignId { get; set; }
    public string Name { get; set; } = "";
}

[Serializable]
public class WardrobeMapping
{
    /// The Sub's designs within the folder allowlist.
    public Dictionary<Guid, WardrobeDesignEntry> LocalDesigns { get; set; } = new();
}
