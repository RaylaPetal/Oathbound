namespace Oathbound.Plugin.Config;

/// Which scannable categories have completed a scan on this install, so an export can leave out a never-scanned one
/// instead of sending it as empty (which would clear the Owner's copy).
public class CatalogScanState
{
    public bool Wardrobe { get; set; }
    public bool Gestures { get; set; }
    public bool Restraints { get; set; }
    public bool Moodles { get; set; }

    public bool Any => Wardrobe || Gestures || Restraints || Moodles;

    /// Installs from before these flags existed: a stored catalog means that category was scanned.
    public void AdoptStoredCatalogs(PluginConfig config)
    {
        Wardrobe |= config.WardrobeMapping.LocalDesigns.Count > 0;
        Gestures |= config.GestureMapping.LocalCatalog.Count > 0;
        Restraints |= config.RestraintMapping.LocalCatalog.Count > 0;
        Moodles |= config.MoodlesMapping.LocalCatalog.Count > 0;
    }
}
