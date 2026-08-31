using System.IO.Compression;

namespace Mail.Storage.Database;

/// <summary>Brotli-or-nothing codec for the blobs table; keeps whichever is smaller.</summary>
public static class BlobCodec
{
    const int MinCompressSize = 128;

    public static (string Compression, byte[] Stored) Encode(ReadOnlySpan<byte> content)
    {
        if (content.Length >= MinCompressSize)
        {
            using var buffer = new MemoryStream();
            using (var brotli = new BrotliStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                brotli.Write(content);
            if (buffer.Length < content.Length)
                return ("brotli", buffer.ToArray());
        }
        return ("none", content.ToArray());
    }

    public static byte[] Decode(string compression, byte[] stored, int size)
    {
        switch (compression)
        {
            case "none":
                return stored;
            case "brotli":
                var result = new byte[size];
                using (var brotli = new BrotliStream(new MemoryStream(stored), CompressionMode.Decompress))
                    brotli.ReadExactly(result);
                return result;
            default:
                throw new InvalidDataException($"Unknown blob compression '{compression}'.");
        }
    }
}
