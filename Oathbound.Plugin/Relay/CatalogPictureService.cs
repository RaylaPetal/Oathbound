using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Oathbound.Plugin.Commands;
using Oathbound.Plugin.Config;
using Oathbound.Plugin.UI;

namespace Oathbound.Plugin.Relay;

/// Restraint pictures through the relay's per-pair picture store. The catalog only carries each picture's reference
/// (id, content hash, key); this moves the encrypted bytes: the Sub uploads what the relay lacks before a catalog goes
/// out, and the Owner downloads what it doesn't have after importing one. Neither side ever fails a sync over a picture.
public sealed class CatalogPictureService
{
    private readonly PluginConfig config;
    private readonly RelayClient relay;
    private readonly RestraintCommand restraints;

    private readonly object gate = new();
    private readonly HashSet<Guid> fetchesInFlight = new();

    public CatalogPictureService(PluginConfig config, RelayClient relay, RestraintCommand restraints)
    {
        this.config = config;
        this.relay = relay;
        this.restraints = restraints;
    }

    // ---- Sub side ----

    /// Uploads the pictures of the export just built that the relay doesn't hold for this pair. A picture that doesn't
    /// go up now is tried again on the next publish, since the relay keeps reporting it missing.
    public async Task SyncBeforePublishAsync(PairingState pairing, CancellationToken ct)
    {
        if (pairing.PairIdHash is not { Length: > 0 } pairIdHash)
            return;
        var pictures = restraints.LastSharedPictures;
        try
        {
            var missing = await relay.SyncPicturesAsync(pairIdHash, pairing.PairEpoch, pictures.Select(p => p.Ref.Id), ct).ConfigureAwait(false);
            var uploaded = 0;
            foreach (var id in missing)
            {
                var (reference, thumbnailFile) = pictures.FirstOrDefault(p => p.Ref.Id == id);
                if (reference is null || ImageTile.ReadThumbnail(thumbnailFile) is not { } bytes)
                    continue;
                try
                {
                    var blob = RelayCrypto.EncryptPicture(RelayCrypto.Base64UrlDecode(reference.Key), reference.Id, bytes);
                    await relay.UploadPictureAsync(pairIdHash, pairing.PairEpoch, reference.Id, blob, ct).ConfigureAwait(false);
                    uploaded++;
                }
                catch (RelayException ex) when (ex.Code is "rate_limited" or "service_unavailable")
                {
                    Plugin.Log.Warning($"Restraint pictures for {pairing.PeerName}: the relay asked to slow down ({ex.Code}); the rest go with the next publish.");
                    break;
                }
                catch (RelayException ex)
                {
                    Plugin.Log.Warning($"Restraint pictures for {pairing.PeerName}: a picture was rejected ({ex.Code}).");
                }
            }
            if (missing.Count > 0)
                Plugin.Log.Debug($"Restraint pictures for {pairing.PeerName}: uploaded {uploaded} of {missing.Count} missing ({pictures.Count} shared).");
        }
        catch (RelayException ex)
        {
            Plugin.Log.Warning($"Restraint pictures for {pairing.PeerName} not synced ({ex.Code}); the catalog still goes out.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- Owner side ----

    /// Downloads the Sub's pictures this pairing's imported restraints point at but that aren't on disk yet.
    public async Task FetchMissingAsync(PairingState pairing, CancellationToken ct)
    {
        if (pairing is not { Direction: PairingDirection.OwnerSide, IsPaired: true, PairIdHash: { Length: > 0 } pairIdHash })
            return;
        lock (gate)
            if (!fetchesInFlight.Add(pairing.Id))
                return;
        try
        {
            // The lists are the UI's; read them on the framework thread.
            var wanted = await Plugin.Framework.RunOnFrameworkThread(() => config.QuickCommands.Restraints
                .Where(c => c.SourcePairIdHash == pairIdHash && c.SharedPictureRef is not null && c.SharedImageFile is not null && !ImageTile.SharedExists(c.SharedImageFile))
                .GroupBy(c => c.SharedPictureRef!.Id)
                .Select(g => (Ref: g.First().SharedPictureRef!, Files: g.Select(c => c.SharedImageFile!).Distinct().ToList()))
                .ToList()).ConfigureAwait(false);
            if (wanted.Count == 0)
                return;

            var saved = 0;
            foreach (var batch in wanted.Chunk(RelayProtocolConstants.PictureFetchBatchMax))
            {
                var blobs = await relay.FetchPicturesAsync(pairIdHash, pairing.PairEpoch, batch.Select(w => w.Ref.Id), ct).ConfigureAwait(false);
                foreach (var (pictureId, blob) in blobs)
                {
                    var (reference, files) = batch.First(w => w.Ref.Id == pictureId);
                    if (TryOpen(reference, blob) is not { } jpeg)
                        continue;
                    foreach (var file in files)
                        if (ImageTile.WriteSharedBytes(file, jpeg))
                            saved++;
                }
            }
            Plugin.Log.Debug($"Restraint pictures from {pairing.PeerName}: saved {saved} of {wanted.Count} missing.");
        }
        catch (RelayException ex)
        {
            Plugin.Log.Information($"Restraint pictures from {pairing.PeerName} not fetched ({ex.Code}); tried again on the next import or Check now.");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (gate)
                fetchesInFlight.Remove(pairing.Id);
        }
    }

    /// Null unless the blob decrypts with the catalog's key and is exactly the content the catalog named.
    private static byte[]? TryOpen(PictureReference reference, byte[] blob)
    {
        try
        {
            var jpeg = RelayCrypto.DecryptPicture(RelayCrypto.Base64UrlDecode(reference.Key), reference.Id, blob);
            if (Convert.ToHexStringLower(SHA256.HashData(jpeg)) == reference.Sha256.ToLowerInvariant())
                return jpeg;
            Plugin.Log.Warning("A restraint picture from the relay didn't match its catalog entry - discarded.");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Plugin.Log.Warning("A restraint picture from the relay couldn't be decrypted - discarded.");
        }
        return null;
    }
}
