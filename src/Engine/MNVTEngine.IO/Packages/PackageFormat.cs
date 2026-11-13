using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MNVTEngine.IO.Packages;

/// <summary>What went wrong with a package.</summary>
public enum PackageError
{
    /// <summary>Not a package, or the header, info or index is malformed or inconsistent.</summary>
    Malformed,

    /// <summary>A newer major format version.</summary>
    UnsupportedVersion,

    /// <summary>A hash or authentication tag does not match: the data was modified or is damaged.</summary>
    IntegrityFailure,

    /// <summary>The package is encrypted with a key the reader does not have.</summary>
    MissingKey,

    /// <summary>The key's id matches but decryption fails (a different key with a colliding id, or tampering).</summary>
    WrongKey,

    /// <summary>The signature does not verify.</summary>
    InvalidSignature,

    /// <summary>The policy requires a signature by a trusted key, and the package has none, or one by an unknown key.</summary>
    UntrustedSignature,
}

/// <summary>A package cannot be read safely. An <see cref="IOException"/>: damaged or foreign content is an input problem, not a bug.</summary>
public sealed class PackageException(PackageError error, string message, Exception? inner = null) : IOException(message, inner)
{
    public PackageError Error { get; } = error;
}

/// <summary>Chunk compression of a package (the codec is recorded per chunk).</summary>
public enum PackageCompression
{
    /// <summary>Stored as is.</summary>
    None,

    /// <summary>LZ4 HC: fastest decompression, larger packages.</summary>
    Fast,

    /// <summary>Zstandard level 9: the default.</summary>
    Balanced,

    /// <summary>Zstandard level 19: smallest packages, slow builds, decompression close to Balanced.</summary>
    Small,
}

/// <summary>Codec of one stored chunk (format value).</summary>
public enum ChunkCodec : byte
{
    None = 0,
    Lz4 = 1,
    Zstd = 2,
}

/// <summary>
/// Binary layout constants of the <c>.mnvtpack</c> format, version 1.0, little-endian throughout. The full specification
/// is in docs/architecture/virtual-filesystem.md. Layout:
/// <code>
/// [header 320 B] [chunk data ...] [info block] [index block] [signature block]
/// </code>
/// </summary>
internal static class PackageFormat
{
    public static ReadOnlySpan<byte> Magic => "MNVTPACK"u8;

    public const ushort MajorVersion = 1;
    public const ushort MinorVersion = 0;
    public const int HeaderSize = 320;

    /// <summary>Bytes of the header covered by the signature (everything before the header hash).</summary>
    public const int SignedHeaderSize = 288;

    public const int MinChunkSize = 4 * 1024;
    public const int MaxChunkSize = 16 * 1024 * 1024;
    public const int DefaultChunkSize = 64 * 1024;
    public const int MaxIndexSize = 256 * 1024 * 1024;
    public const int MaxInfoSize = 64 * 1024;
    public const int MaxSignatureSize = 1024;
    public const int EntryRecordSize = 88;
    public const int ChunkRecordSize = 80;
    public const int IndexHeaderSize = 16;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int HashSize = 32;

    // Header offsets.
    public const int OffMajor = 8;
    public const int OffMinor = 10;
    public const int OffHeaderSize = 12;
    public const int OffFlags = 16;
    public const int OffChunkSize = 20;
    public const int OffPackageGuid = 24;
    public const int OffBuildId = 40;
    public const int OffKeyId = 56;
    public const int OffKeySalt = 64;
    public const int OffInfoOffset = 96;
    public const int OffInfoLength = 104;
    public const int OffIndexPlainLength = 108;
    public const int OffIndexOffset = 112;
    public const int OffIndexStoredLength = 120;
    public const int OffIndexNonce = 124;
    public const int OffIndexTag = 136;
    public const int OffInfoHash = 152;
    public const int OffIndexHash = 184;
    public const int OffSignatureOffset = 216;
    public const int OffSignatureLength = 224;
    public const int OffHeaderHash = SignedHeaderSize;

    public const uint FlagEncrypted = 1;
    public const uint FlagSigned = 2;
    public const uint KnownFlags = FlagEncrypted | FlagSigned;

    /// <summary>Signature algorithm 1: ECDSA over NIST P-256 with SHA-256, IEEE P1363 (r || s, 64 bytes).</summary>
    public const ushort SignatureEcdsaP256 = 1;

    public static ulong ReadU64(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(b[offset..]);

    public static uint ReadU32(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b[offset..]);

    public static ushort ReadU16(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b[offset..]);

    public static void WriteU64(Span<byte> b, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(b[offset..], value);

    public static void WriteU32(Span<byte> b, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b[offset..], value);

    public static void WriteU16(Span<byte> b, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(b[offset..], value);

    public static Guid ReadGuid(ReadOnlySpan<byte> b, int offset) => new(b.Slice(offset, 16));

    public static void WriteGuid(Span<byte> b, int offset, Guid value)
    {
        if (!value.TryWriteBytes(b.Slice(offset, 16)))
        {
            throw new InvalidOperationException("GUID does not fit.");
        }
    }

    /// <summary>Associated data authenticated with each encrypted chunk: it cannot be moved to another index or package.</summary>
    public static byte[] ChunkAssociatedData(Guid package, uint chunk)
    {
        var data = new byte[20];
        WriteGuid(data, 0, package);
        WriteU32(data, 16, chunk);
        return data;
    }

    public static byte[] IndexAssociatedData(Guid package, Guid build)
    {
        var data = new byte[37];
        WriteGuid(data, 0, package);
        WriteGuid(data, 16, build);
        "index"u8.CopyTo(data.AsSpan(32));
        return data;
    }

    public static byte[] Sha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);
}
