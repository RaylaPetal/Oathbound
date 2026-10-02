using System;
using System.IO;
using System.IO.Compression;

namespace Oathbound.Plugin.Relay;

/// Both ends are this same plugin, so cross-runtime gzip compatibility isn't a concern.
public static class RelayCompression
{
    public static byte[] Compress(byte[] plaintext)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(plaintext, 0, plaintext.Length);
        return output.ToArray();
    }

    /// Refuses to write past `maxDecompressedBytes`, so a decompression bomb can't exhaust memory.
    public static byte[] Decompress(byte[] compressed, int maxDecompressedBytes)
    {
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        var buffer = new byte[81920];
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maxDecompressedBytes)
                throw new InvalidDataException($"Decompressed size exceeds the {maxDecompressedBytes}-byte limit; refusing to continue (possible decompression bomb).");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
