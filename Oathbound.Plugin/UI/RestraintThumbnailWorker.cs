using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;

namespace Oathbound.Plugin.UI;

/// Makes the shared small copy of every configured restraint's picture, one at a time from the framework tick, so a
/// picture reaches the Owner without its restraint ever being opened. Its save marks the catalog changed, and the
/// publish debounce batches a burst of these into one snapshot.
public sealed class RestraintThumbnailWorker
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    private readonly PluginConfig config;
    private readonly RestraintCommand restraints;

    /// (restraint, picture, target) attempts that failed this session; retried only when one of them changes.
    private readonly HashSet<(string, string, int)> failed = new();
    private bool encoding;
    private DateTime nextScanUtc;

    public RestraintThumbnailWorker(PluginConfig config, RestraintCommand restraints)
    {
        this.config = config;
        this.restraints = restraints;
    }

    /// Restraints whose picture couldn't be made small enough (or no JPEG encoder exists) at the current target.
    public IReadOnlyList<string> Failed(int target) =>
        config.RestraintMapping.ConfiguredMods
            .Where(m => m.ImageFile is { } image && failed.Contains((m.Id, image, target)))
            .Select(m => m.Name)
            .ToList();

    public void OnFrameworkUpdate()
    {
        var now = DateTime.UtcNow;
        if (encoding || now < nextScanUtc)
            return;
        nextScanUtc = now + ScanInterval;

        var target = restraints.ThumbnailTargetBytes();
        var mod = config.RestraintMapping.ConfiguredMods.FirstOrDefault(m =>
            m.ImageFile is { } image && NeedsThumbnail(m, target) && !failed.Contains((m.Id, image, target)));
        if (mod is null)
            return;

        var picture = mod.ImageFile!;
        encoding = true;
        _ = ImageTile.MakeThumbnailAsync(picture, ImageTile.ThumbWidth, ImageTile.ThumbHeight, target).ContinueWith(task =>
            Plugin.Framework.RunOnFrameworkThread(() =>
            {
                encoding = false;
                var thumbnail = task.IsCompletedSuccessfully ? task.Result : null;
                if (thumbnail is null)
                {
                    failed.Add((mod.Id, picture, target));
                    return;
                }
                // The picture changed or the restraint was deleted while encoding.
                if (mod.ImageFile != picture || !config.RestraintMapping.ConfiguredMods.Contains(mod))
                {
                    if (thumbnail != mod.ThumbnailFile)
                        ImageTile.Delete(thumbnail);
                    return;
                }
                mod.ThumbnailFile = thumbnail;
                mod.ThumbnailTargetBytes = target;
                config.Save();
            }));
    }

    private static bool NeedsThumbnail(ConfiguredModRestraint mod, int target)
    {
        if (mod.ThumbnailFile is null)
            return true;
        if (mod.ThumbnailTargetBytes > 0)
            return mod.ThumbnailTargetBytes > target;
        // Made before targets were recorded: redo it only if it doesn't fit.
        var path = Path.Combine(ImageTile.Folder, mod.ThumbnailFile);
        return !File.Exists(path) || new FileInfo(path).Length > target;
    }
}
