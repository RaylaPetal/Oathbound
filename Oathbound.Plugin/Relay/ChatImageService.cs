using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures;

namespace Oathbound.Plugin.Relay;

/// Chat pictures on disk and over the relay: preparing one to send, encrypting and uploading it, and fetching and
/// opening one received. Everything here runs off the main thread; callers marshal results back.
public sealed class ChatImageService
{
    private readonly RelayClient relay;

    public ChatImageService(RelayClient relay) => this.relay = relay;

    public static string Folder => Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "chat", "images");

    public static string? PathOf(string? fileName) =>
        fileName is { Length: > 0 } && fileName == Path.GetFileName(fileName) ? Path.Combine(Folder, fileName) : null;

    public static void Delete(string? fileName)
    {
        try
        {
            if (PathOf(fileName) is { } path && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "A chat picture couldn't be deleted.");
        }
    }

    public sealed record Prepared(byte[] Bytes, string Extension, int Width, int Height);

    /// A PNG or JPEG fit to send: the original when it's already small enough, else scaled down and re-encoded as JPEG.
    /// Throws InvalidOperationException with a message for the user when it can't be.
    public static Task<Prepared> PrepareAsync(string sourcePath) =>
        PrepareAsync(sourcePath, ChatImageCrypto.MaxSidePixels, ChatImageCrypto.MaxPlaintextBytes, "send");

    /// `verb` finishes the user-facing messages ("too big to send").
    public static async Task<Prepared> PrepareAsync(string sourcePath, int maxSide, int maxBytes, string verb)
    {
        byte[] original;
        try
        {
            original = await File.ReadAllBytesAsync(sourcePath).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("That picture couldn't be read.");
        }
        var extension = ExtensionFor(original) ?? throw new InvalidOperationException($"Only PNG and JPEG pictures can be {(verb == "send" ? "sent" : "used")}.");

        using var source = await Plugin.TextureProvider.CreateFromImageAsync(original).ConfigureAwait(false);
        if (source.Width <= maxSide && source.Height <= maxSide && original.Length <= maxBytes)
            return new Prepared(original, extension, source.Width, source.Height);

        var jpeg = Plugin.TextureReadback.GetSupportedImageEncoderInfos()
            .FirstOrDefault(c => c.Extensions.Any(e => e.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || e.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)));
        if (jpeg is null)
            throw new InvalidOperationException($"That picture is too big to {verb} (over {maxBytes / 1024} KB or {maxSide} px), and this system can't shrink pictures. Try a smaller one.");

        for (var frame = 1f; frame >= 0.25f; frame *= 0.8f)
        {
            var scale = MathF.Min(1f, MathF.Min(maxSide * frame / source.Width, maxSide * frame / source.Height));
            using var scaled = await Plugin.TextureProvider.CreateFromExistingTextureAsync(source, new TextureModificationArgs
            {
                NewWidth = Math.Max(1, (int)(source.Width * scale)),
                NewHeight = Math.Max(1, (int)(source.Height * scale)),
                MakeOpaque = true,
            }, leaveWrapOpen: true).ConfigureAwait(false);

            foreach (var quality in new[] { 0.88f, 0.75f, 0.6f })
            {
                using var stream = new MemoryStream();
                await Plugin.TextureReadback.SaveToStreamAsync(scaled, jpeg.ContainerGuid, stream,
                    new Dictionary<string, object> { ["ImageQuality"] = quality }, leaveWrapOpen: true, leaveStreamOpen: true).ConfigureAwait(false);
                if (stream.Length <= maxBytes)
                    return new Prepared(stream.ToArray(), ".jpg", scaled.Width, scaled.Height);
            }
        }
        throw new InvalidOperationException($"That picture couldn't be made small enough to {verb}.");
    }

    public static string FileNameFor(string imageId, Prepared image) => imageId + image.Extension;

    /// Keeps the sender's own copy, encrypts it and uploads the ciphertext. Returns the local file name and the key.
    /// The copy stays on a failed upload too, so the message can be retried; the caller records it.
    public async Task<(string FileName, string Key)> UploadAsync(string pairIdHash, int pairEpoch, string imageId, Prepared image, CancellationToken ct)
    {
        var fileName = FileNameFor(imageId, image);
        Directory.CreateDirectory(Folder);
        await File.WriteAllBytesAsync(Path.Combine(Folder, fileName), image.Bytes, ct).ConfigureAwait(false);
        var (key, blob) = ChatImageCrypto.Encrypt(image.Bytes, imageId);
        try
        {
            await relay.UploadChatImageAsync(pairIdHash, pairEpoch, imageId, blob, ct).ConfigureAwait(false);
        }
        catch (RelayException ex)
        {
            Plugin.Log.Warning($"Chat picture upload refused by the relay: {ex.Message}");
            throw new InvalidOperationException(ex.Code switch
            {
                "payload_too_large" => "too many pictures are waiting for your partner right now - try again later",
                "rate_limited" => "you've sent a lot of pictures this hour - try again later",
                "unauthorized" => "the relay doesn't see this pairing as active",
                "not_found" => "this relay doesn't support pictures yet",
                "service_unavailable" => "the relay is busy - try again later",
                _ => $"the relay couldn't store it ({ex.Code})",
            });
        }
        return (fileName, key);
    }

    public enum FetchOutcome { Saved, Gone, Failed }

    /// Fetches, opens and saves a received picture. Gone when the relay no longer has it (expired, or the pairing
    /// ended); Failed when it may work later.
    public async Task<(FetchOutcome Outcome, string? FileName)> DownloadAsync(string pairIdHash, int pairEpoch, ChatImageRef image, CancellationToken ct)
    {
        byte[] blob;
        try
        {
            blob = await relay.FetchChatImageAsync(pairIdHash, pairEpoch, image.Id, ct).ConfigureAwait(false);
        }
        catch (RelayException ex) when (ex.Code is "not_found" or "unauthorized")
        {
            return (FetchOutcome.Gone, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Plugin.Log.Information($"Chat picture fetch failed, will retry: {ex.Message}");
            return (FetchOutcome.Failed, null);
        }

        var bytes = ChatImageCrypto.Decrypt(blob, image.Key, image.Id);
        if (bytes is null || ExtensionFor(bytes) is not { } extension)
        {
            Plugin.Log.Warning("Chat picture ignored: it didn't open with its key or isn't a PNG/JPEG.");
            return (FetchOutcome.Gone, null);
        }
        var fileName = image.Id + extension;
        Directory.CreateDirectory(Folder);
        await File.WriteAllBytesAsync(Path.Combine(Folder, fileName), bytes, ct).ConfigureAwait(false);
        return (FetchOutcome.Saved, fileName);
    }

    private static string? ExtensionFor(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        return null;
    }
}
