using K4os.Compression.LZ4;
using ZstdSharp;

namespace MNVTEngine.IO.Packages;

/// <summary>Chunk codecs. Chunks are compressed independently, so reading one never decompresses another.</summary>
internal static class PackageCodecs
{
    /// <summary>A chunk is stored compressed only when that saves at least this fraction.</summary>
    public const double MinimumSaving = 0.05;

    [ThreadStatic]
    private static Decompressor? _zstd;

    /// <summary>File types whose content is compressed already: stored as is, without trying.</summary>
    public static readonly IReadOnlySet<string> PrecompressedExtensions = new HashSet<string>(
        [".png", ".jpg", ".jpeg", ".webp", ".ogg", ".mp3", ".flac", ".glb", ".zip", ".gz", ".br", ".zst", ".woff", ".woff2", ".mp4", ".webm"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>The compressed chunk, or null when the profile is <see cref="PackageCompression.None"/> or compression does not pay.</summary>
    public static (ChunkCodec Codec, byte[] Data)? Compress(ReadOnlySpan<byte> plain, PackageCompression compression)
    {
        (ChunkCodec, byte[])? result = compression switch
        {
            PackageCompression.None => null,
            PackageCompression.Fast => (ChunkCodec.Lz4, Lz4(plain)),
            PackageCompression.Balanced => (ChunkCodec.Zstd, Zstd(plain, 9)),
            PackageCompression.Small => (ChunkCodec.Zstd, Zstd(plain, 19)),
            _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, null),
        };
        return result is { } r && r.Item2.Length <= plain.Length * (1 - MinimumSaving) ? r : null;
    }

    /// <summary>Decompresses into exactly <paramref name="destination"/>.</summary>
    /// <exception cref="PackageException">The data does not decode to the expected size.</exception>
    public static void Decompress(ChunkCodec codec, ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int written;
        try
        {
            switch (codec)
            {
                case ChunkCodec.None:
                    if (source.Length != destination.Length)
                    {
                        throw new PackageException(PackageError.Malformed, "Stored chunk has the wrong size.");
                    }

                    source.CopyTo(destination);
                    return;
                case ChunkCodec.Lz4:
                    written = LZ4Codec.Decode(source, destination);
                    break;
                case ChunkCodec.Zstd:
                    written = (_zstd ??= new Decompressor()).Unwrap(source, destination);
                    break;
                default:
                    throw new PackageException(PackageError.Malformed, $"Unknown chunk codec {(byte)codec}.");
            }
        }
        catch (ZstdException ex)
        {
            throw new PackageException(PackageError.IntegrityFailure, $"Chunk does not decompress: {ex.Message}", ex);
        }

        if (written != destination.Length)
        {
            throw new PackageException(PackageError.IntegrityFailure, "Chunk decompresses to the wrong size.");
        }
    }

    [ThreadStatic]
    private static Dictionary<int, Compressor>? _zstdCompressors;

    private static byte[] Lz4(ReadOnlySpan<byte> plain)
    {
        var buffer = new byte[LZ4Codec.MaximumOutputSize(plain.Length)];
        var length = LZ4Codec.Encode(plain, buffer, LZ4Level.L09_HC);
        return length < 0 ? new byte[plain.Length] : buffer[..length]; // a failed encode is "does not pay": stored as is
    }

    private static byte[] Zstd(ReadOnlySpan<byte> plain, int level)
    {
        _zstdCompressors ??= [];
        if (!_zstdCompressors.TryGetValue(level, out var compressor))
        {
            _zstdCompressors[level] = compressor = new Compressor(level);
        }

        return compressor.Wrap(plain).ToArray();
    }
}
