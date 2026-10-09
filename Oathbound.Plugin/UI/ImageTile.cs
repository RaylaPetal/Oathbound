using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;

namespace Oathbound.Plugin.UI;

/// Restraint pictures: snapshots or copies kept in the plugin's own folder so the original can move, and never sent anywhere.
public static class ImageTile
{
    /// Width / height of the portrait frame every picture is fitted into.
    public const float Aspect = 5f / 6f;
    private const long MaxBytes = 5L * 1024 * 1024;

    public static string Folder => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "images");

    /// The small copy shared with the Owner: through the relay's picture store, or inside a catalog file.
    public const int ThumbWidth = 240;
    public const int ThumbHeight = 288;
    /// Below this width a thumbnail is no longer worth sharing; the picture is left out instead.
    private const int MinThumbWidth = 96;
    public const int MaxThumbBytes = 40 * 1024;
    private const string SharedPrefix = "shared-";

    /// Only bare file names inside the images folder, so a tampered config can't point anywhere else.
    private static string? PathOf(string? fileName) =>
        fileName is { Length: > 0 } && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? Path.Combine(Folder, fileName) : null;

    /// Letterboxed into a 5:6 frame. An unloadable or missing picture shows the empty frame (or `icon`), never an error.
    public static void Draw(string? fileName, float width, FontAwesomeIcon? icon = null, string? emptyHint = null)
    {
        var size = new Vector2(width, width / Aspect);
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var rounding = Layout.Scaled(4);
        draw.AddRectFilled(pos, pos + size, ImGui.GetColorU32(Theme.TileBg), rounding);
        ImGui.Dummy(size);

        if (PathOf(fileName) is { } path && File.Exists(path)
            && Plugin.TextureProvider.GetFromFile(path).TryGetWrap(out var wrap, out _) && wrap.Width > 0 && wrap.Height > 0)
        {
            var scale = MathF.Min(size.X / wrap.Width, size.Y / wrap.Height);
            var drawn = new Vector2(wrap.Width * scale, wrap.Height * scale);
            var offset = pos + (size - drawn) / 2f;
            draw.AddImageRounded(wrap.Handle, offset, offset + drawn, Vector2.Zero, Vector2.One, 0xFFFFFFFF, rounding);
            return;
        }

        if (icon is { } glyph)
        {
            using var font = ImRaii.PushFont(Plugin.PluginInterface.UiBuilder.FontIcon);
            var text = glyph.ToIconString();
            var textSize = ImGui.CalcTextSize(text);
            draw.AddText(pos + (size - textSize) / 2f, ImGui.GetColorU32(Theme.TextMuted), text);
        }
        else if (emptyHint is not null && width >= Layout.Scaled(80))
        {
            var textSize = ImGui.CalcTextSize(emptyHint);
            draw.AddText(pos + (size - textSize) / 2f, ImGui.GetColorU32(Theme.TextMuted), emptyHint);
        }
    }

    /// "Take snapshot" / "Choose file..." / "Remove" under a tile. `onChanged` gets the new file name, or null when removed.
    public static void DrawPicker(string id, string? current, FileDialogManager dialogs, SnapshotOverlay snapshot, Action<string?> onChanged)
    {
        if (ImGui.Button($"Take snapshot##{id}"))
            snapshot.Open(file =>
            {
                Delete(current);
                onChanged(file);
            });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Frame part of the game screen and capture it. Oathbound's windows step aside while you frame.\nTip: hide the game's own UI first (Scroll Lock by default) or use group pose.");
        Layout.ContinueRowOrWrap(Layout.ButtonWidth("Choose file..."));
        if (ImGui.Button($"Choose file...##{id}"))
        {
            dialogs.OpenFileDialog("Choose a picture", "Pictures{.png,.jpg,.jpeg}", (ok, path) =>
            {
                if (!ok)
                    return;
                if (TryImport(path, out var copied, out var error))
                {
                    Delete(current);
                    onChanged(copied);
                }
                else
                    Plugin.NotificationManager.AddNotification(new Dalamud.Interface.ImGuiNotification.Notification
                    {
                        Title = "Picture not used",
                        Content = error ?? "The picture couldn't be used.",
                        Type = Dalamud.Interface.ImGuiNotification.NotificationType.Warning,
                    });
            });
        }
        if (current is null)
            return;
        Layout.ContinueRowOrWrap(Layout.ButtonWidth("Remove"));
        if (ImGui.Button($"Remove##{id}"))
        {
            Delete(current);
            onChanged(null);
        }
    }

    public static bool TryImport(string source, out string? fileName, out string? error)
    {
        fileName = null;
        error = null;
        try
        {
            var info = new FileInfo(source);
            if (info.Extension.ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg"))
            {
                error = "Only PNG and JPEG pictures can be used.";
                return false;
            }
            if (info.Length > MaxBytes)
            {
                error = "That picture is larger than 5 MB.";
                return false;
            }

            var head = new byte[4];
            using (var stream = info.OpenRead())
                if (stream.Read(head, 0, head.Length) < head.Length)
                {
                    error = "That file isn't a PNG or JPEG picture.";
                    return false;
                }
            var isPng = head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47;
            var isJpeg = head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF;
            if (!isPng && !isJpeg)
            {
                error = "That file isn't a PNG or JPEG picture.";
                return false;
            }

            Directory.CreateDirectory(Folder);
            fileName = $"{Guid.NewGuid():N}{(isPng ? ".png" : ".jpg")}";
            File.Copy(source, Path.Combine(Folder, fileName));
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't copy a restraint picture.");
            error = "The picture couldn't be copied.";
            fileName = null;
            return false;
        }
    }

    /// A snapshot taken in game, scaled down to at most 600 x 720 and saved as JPEG (PNG when there's no JPEG encoder).
    /// Null when no encoder is available at all.
    public static async Task<string?> SaveCaptureAsync(IDalamudTextureWrap shot)
    {
        var encoders = Plugin.TextureReadback.GetSupportedImageEncoderInfos().ToList();
        var jpeg = encoders.FirstOrDefault(c => c.Extensions.Any(e => e.Equals(".jpg", StringComparison.OrdinalIgnoreCase)));
        var png = encoders.FirstOrDefault(c => c.Extensions.Any(e => e.Equals(".png", StringComparison.OrdinalIgnoreCase)));
        if ((jpeg ?? png) is not { } encoder)
            return null;

        var scale = MathF.Min(1f, MathF.Min(600f / shot.Width, 720f / shot.Height));
        using var scaled = await Plugin.TextureProvider.CreateFromExistingTextureAsync(shot, new TextureModificationArgs
        {
            NewWidth = Math.Max(1, (int)(shot.Width * scale)),
            NewHeight = Math.Max(1, (int)(shot.Height * scale)),
            MakeOpaque = true,
        }, leaveWrapOpen: true);

        using var stream = new MemoryStream();
        var props = jpeg is not null ? new Dictionary<string, object> { ["ImageQuality"] = 0.9f } : null;
        await Plugin.TextureReadback.SaveToStreamAsync(scaled, encoder.ContainerGuid, stream, props, leaveWrapOpen: true, leaveStreamOpen: true);
        Directory.CreateDirectory(Folder);
        var name = $"{Guid.NewGuid():N}{(jpeg is not null ? ".jpg" : ".png")}";
        await File.WriteAllBytesAsync(Path.Combine(Folder, name), stream.ToArray());
        return name;
    }

    /// Fits the picture into `maxWidth` x `maxHeight` and encodes JPEG, lowering quality and then the frame until it
    /// is at most `targetBytes`. Null when it can't (no JPEG encoder, e.g. under Wine, or still too big at the smallest
    /// frame); the restraint is then shared without a picture.
    public static async Task<string?> MakeThumbnailAsync(string imageFile, int maxWidth, int maxHeight, int targetBytes)
    {
        if (PathOf(imageFile) is not { } path || !File.Exists(path))
            return null;
        try
        {
            var jpeg = Plugin.TextureReadback.GetSupportedImageEncoderInfos()
                .FirstOrDefault(c => c.Extensions.Any(e => e.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || e.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)));
            if (jpeg is null)
            {
                Plugin.Log.Warning("No JPEG encoder available - restraint pictures won't be shared.");
                return null;
            }

            using var source = await Plugin.TextureProvider.CreateFromImageAsync(await File.ReadAllBytesAsync(path));
            targetBytes = Math.Min(targetBytes, MaxThumbBytes);
            for (float frame = 1f; maxWidth * frame >= MinThumbWidth; frame *= 0.8f)
            {
                var scale = MathF.Min(1f, MathF.Min(maxWidth * frame / source.Width, maxHeight * frame / source.Height));
                using var scaled = await Plugin.TextureProvider.CreateFromExistingTextureAsync(source, new TextureModificationArgs
                {
                    NewWidth = Math.Max(1, (int)(source.Width * scale)),
                    NewHeight = Math.Max(1, (int)(source.Height * scale)),
                    MakeOpaque = true,
                }, leaveWrapOpen: true);

                foreach (var quality in new[] { 0.85f, 0.7f, 0.55f, 0.4f, 0.3f })
                {
                    using var stream = new MemoryStream();
                    await Plugin.TextureReadback.SaveToStreamAsync(scaled, jpeg.ContainerGuid, stream,
                        new Dictionary<string, object> { ["ImageQuality"] = quality }, leaveWrapOpen: true, leaveStreamOpen: true);
                    if (stream.Length > targetBytes)
                        continue;
                    var name = $"{Path.GetFileNameWithoutExtension(imageFile)}.thumb.jpg";
                    await File.WriteAllBytesAsync(Path.Combine(Folder, name), stream.ToArray());
                    Plugin.Log.Debug($"Restraint picture small copy: {scaled.Width}x{scaled.Height} at quality {quality}, {stream.Length} bytes (target {targetBytes}).");
                    return name;
                }
            }
            Plugin.Log.Warning($"A restraint picture's small copy stayed over {targetBytes / 1024} KB even at its smallest - it won't be shared.");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't make a small copy of a restraint picture - it won't be shared.");
        }
        return null;
    }

    public static byte[]? ReadThumbnail(string? fileName)
    {
        if (PathOf(fileName) is not { } path || !File.Exists(path))
            return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            return bytes.Length <= MaxThumbBytes ? bytes : null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't read a restraint picture's small copy.");
            return null;
        }
    }

    /// The Owner's copy of a picture the Sub shared. Named by content, so an unchanged picture isn't rewritten.
    public static string? WriteShared(string restraintId, string? base64)
    {
        if (base64 is null)
            return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return SharedNameFor(restraintId, Convert.ToHexStringLower(SHA256.HashData(bytes))) is { } name && WriteSharedBytes(name, bytes) ? name : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// The file name a shared picture with this content hash is saved under, or null for an unusable restraint id.
    public static string? SharedNameFor(string restraintId, string sha256Hex) =>
        restraintId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && sha256Hex.Length >= 12 && sha256Hex.All(Uri.IsHexDigit)
            ? $"{SharedPrefix}{restraintId}-{sha256Hex[..12].ToLowerInvariant()}.jpg"
            : null;

    public static bool SharedExists(string? fileName) => PathOf(fileName) is { } path && File.Exists(path);

    /// Writes a shared picture once it checks out as a small JPEG. False when it doesn't, or couldn't be written.
    public static bool WriteSharedBytes(string fileName, byte[] bytes)
    {
        if (bytes.Length > MaxThumbBytes || bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
            return false;
        if (PathOf(fileName) is not { } path)
            return false;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllBytes(path, bytes);
            }
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't save a picture shared by the Sub.");
            return false;
        }
    }

    /// Shared pictures no saved command points at any more: the Sub removed or changed them, or the command is gone.
    public static void DeleteUnusedShared(IEnumerable<string?> stillUsed)
    {
        try
        {
            if (!Directory.Exists(Folder))
                return;
            var keep = stillUsed.Where(f => f is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(Folder, SharedPrefix + "*.jpg"))
                if (!keep.Contains(Path.GetFileName(file)))
                    File.Delete(file);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't clean up shared restraint pictures.");
        }
    }

    public static void Delete(string? fileName)
    {
        if (PathOf(fileName) is not { } path || !File.Exists(path))
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't delete a restraint picture.");
        }
    }
}
