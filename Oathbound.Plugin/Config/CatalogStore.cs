using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Oathbound.Plugin.Config;

/// Scanned and imported catalogs live in their own file, so an unrelated config.Save() doesn't rewrite them.
public sealed class CatalogStore
{
    private const string FileName = "catalogs.json";

    private static string FilePath => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, FileName);

    /// Migrates an older install's inline catalogs on first run. Never throws - worst case is empty catalogs and a rescan.
    public void LoadOrMigrate(PluginConfig config)
    {
        if (!File.Exists(FilePath))
            MigrateLegacy(config);
        else
            LoadInto(config);
        // Rewrites a config that still carries inline catalogs, so it shrinks right away.
        if (ClearLegacy(config))
            config.Save();
    }

    /// Temp-file-then-replace, so a crash mid-write can't leave a corrupt file.
    public void Save(PluginConfig config)
    {
        var data = new CatalogStoreData
        {
            GestureLocal = config.GestureMapping.LocalCatalog,
            GesturePeer = config.GestureMapping.ImportedPeerCatalog,
            RestraintLocal = config.RestraintMapping.LocalCatalog,
            RestraintPeer = config.RestraintMapping.ImportedPeerCatalog,
            MoodlesLocal = config.MoodlesMapping.LocalCatalog,
        };

        var directory = Plugin.PluginInterface.ConfigDirectory;
        if (!directory.Exists) directory.Create();

        var tempPath = FilePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(data));
        if (File.Exists(FilePath))
            File.Replace(tempPath, FilePath, null);
        else
            File.Move(tempPath, FilePath);
        config.NotifyChanged();
    }

    private void LoadInto(PluginConfig config)
    {
        CatalogStoreData data;
        try
        {
            var json = File.ReadAllText(FilePath);
            data = JsonSerializer.Deserialize<CatalogStoreData>(json) ?? new CatalogStoreData();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to read catalogs.json; starting with empty catalogs (a rescan will repopulate them).");
            data = new CatalogStoreData();
        }

        config.GestureMapping.LocalCatalog = data.GestureLocal;
        config.GestureMapping.ImportedPeerCatalog = data.GesturePeer;
        config.RestraintMapping.LocalCatalog = data.RestraintLocal;
        config.RestraintMapping.ImportedPeerCatalog = data.RestraintPeer;
        config.MoodlesMapping.LocalCatalog = data.MoodlesLocal;
    }

    private void MigrateLegacy(PluginConfig config)
    {
        var gesture = config.GestureMapping;
        var restraint = config.RestraintMapping;
        var moodles = config.MoodlesMapping;
        if (gesture.LegacyLocalCatalog is { } gl) gesture.LocalCatalog = gl;
        if (gesture.LegacyImportedPeerCatalog is { } gp) gesture.ImportedPeerCatalog = gp;
        if (restraint.LegacyLocalCatalog is { } rl) restraint.LocalCatalog = rl;
        if (restraint.LegacyImportedPeerCatalog is { } rp) restraint.ImportedPeerCatalog = rp;
        if (moodles.LegacyLocalCatalog is { } ml) moodles.LocalCatalog = ml;

        Save(config);
    }

    /// The inline copies are only a migration source; with catalogs.json present they're stale and just held memory.
    private static bool ClearLegacy(PluginConfig config)
    {
        var had = config.GestureMapping.LegacyLocalCatalog is not null || config.GestureMapping.LegacyImportedPeerCatalog is not null
            || config.RestraintMapping.LegacyLocalCatalog is not null || config.RestraintMapping.LegacyImportedPeerCatalog is not null
            || config.MoodlesMapping.LegacyLocalCatalog is not null;
        config.GestureMapping.LegacyLocalCatalog = null;
        config.GestureMapping.LegacyImportedPeerCatalog = null;
        config.RestraintMapping.LegacyLocalCatalog = null;
        config.RestraintMapping.LegacyImportedPeerCatalog = null;
        config.MoodlesMapping.LegacyLocalCatalog = null;
        return had;
    }

    private sealed class CatalogStoreData
    {
        public Dictionary<string, GestureCatalogEntry> GestureLocal { get; set; } = new();
        public Dictionary<string, GestureExportEntry> GesturePeer { get; set; } = new();
        public Dictionary<string, RestraintCatalogEntry> RestraintLocal { get; set; } = new();
        public Dictionary<string, RestraintCatalogExportEntry> RestraintPeer { get; set; } = new();
        public Dictionary<string, MoodlesStatusEntry> MoodlesLocal { get; set; } = new();
    }
}
