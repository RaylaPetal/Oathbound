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
        {
            MigrateLegacy(config);
            return;
        }

        LoadInto(config);
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
        TryAssign<GestureCatalogEntry>(config.GestureMapping.LegacyExtensionData, "LocalCatalog", v => config.GestureMapping.LocalCatalog = v);
        TryAssign<GestureExportEntry>(config.GestureMapping.LegacyExtensionData, "ImportedPeerCatalog", v => config.GestureMapping.ImportedPeerCatalog = v);
        TryAssign<RestraintCatalogEntry>(config.RestraintMapping.LegacyExtensionData, "LocalCatalog", v => config.RestraintMapping.LocalCatalog = v);
        TryAssign<RestraintCatalogExportEntry>(config.RestraintMapping.LegacyExtensionData, "ImportedPeerCatalog", v => config.RestraintMapping.ImportedPeerCatalog = v);
        TryAssign<MoodlesStatusEntry>(config.MoodlesMapping.LegacyExtensionData, "LocalCatalog", v => config.MoodlesMapping.LocalCatalog = v);

        // Never let the legacy inline data round-trip back into the main config.
        config.GestureMapping.LegacyExtensionData = null;
        config.RestraintMapping.LegacyExtensionData = null;
        config.MoodlesMapping.LegacyExtensionData = null;

        Save(config);
    }

    private static void TryAssign<T>(Dictionary<string, JsonElement>? extensionData, string propertyName, Action<Dictionary<string, T>> assign)
    {
        if (extensionData is null) return;
        var key = extensionData.Keys.FirstOrDefault(k => string.Equals(k, propertyName, StringComparison.OrdinalIgnoreCase));
        if (key is null) return;

        try
        {
            var value = JsonSerializer.Deserialize<Dictionary<string, T>>(extensionData[key]);
            if (value is not null) assign(value);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Failed to migrate legacy '{propertyName}' catalog; it will need a rescan.");
        }
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
