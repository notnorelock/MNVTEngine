using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MNVTEngine.IO.Packages;

/// <param name="Keys">Encryption keys; an encrypted package needs the one its header names.</param>
/// <param name="TrustedSigners">Keys whose signatures are accepted. A signature by any other key cannot be verified.</param>
/// <param name="RequireSignature">Refuse packages that are not signed by a trusted key (release games).</param>
/// <param name="Statistics">Where reads are counted; the mounting file system passes its own.</param>
public sealed record PackageOpenOptions(
    PackageKeyRing? Keys = null,
    IReadOnlyList<PackageVerificationKey>? TrustedSigners = null,
    bool RequireSignature = false,
    IoStatistics? Statistics = null);

/// <summary>How a package's signature checked out. An invalid signature is never a status: opening fails.</summary>
public enum PackageSignatureStatus
{
    Unsigned,

    /// <summary>Signed by a trusted key, and the signature verifies.</summary>
    Trusted,

    /// <summary>Signed by a key the reader does not know, so it could not be verified.</summary>
    UnknownSigner,
}

/// <summary>A file in a package, as tools see it. Storage details beyond sizes (offsets, nonces) stay inside the package.</summary>
public sealed record PackageEntry(string Path, Guid AssetGuid, long Length, long StoredLength, int Chunks, IReadOnlyList<Guid> Dependencies, string ContentSha256, bool SharesChunks);

/// <summary>The public part of a package, readable without its key.</summary>
public sealed record PackageInfo(
    string FilePath, long FileLength, Version FormatVersion, Guid PackageGuid, Guid BuildId, bool IsEncrypted, bool IsSigned, ulong KeyId, string? SignerId,
    int ChunkSize, IReadOnlyDictionary<string, string> Properties, string HeaderSha256);

/// <summary>Result of checking every chunk of a package.</summary>
public sealed record PackageVerification(IReadOnlyList<string> Problems, int Chunks, long StoredBytes, TimeSpan Duration)
{
    public bool Succeeded => Problems.Count == 0;
}

/// <summary>
/// An open <c>.mnvtpack</c>: header, info, index and signature are validated when opening; file data is read chunk by
/// chunk on demand, each chunk's hash (and, when encrypted, its AES-GCM tag) checked before it is decrypted or
/// decompressed. Nothing else is loaded into memory. Thread-safe; disposal waits for open streams to close.
/// </summary>
public sealed class MnvtPackage : IDisposable
{
    private readonly SafeFileHandle _file;
    private readonly byte[]? _dataKey;
    private readonly Entry[] _entries;
    private readonly Chunk[] _chunks;
    private readonly Guid[] _dependencies;
    private readonly byte[] _index;
    private readonly Dictionary<string, int> _byPath;
    private readonly Dictionary<Guid, int> _byGuid;
    private readonly Lock _leaseLock = new();
    private int _leases;
    private bool _disposeRequested;
    private bool _closed;

    private MnvtPackage(PackageInfo info, PackageSignatureStatus signature, SafeFileHandle file, byte[]? dataKey, byte[] index, Entry[] entries, Chunk[] chunks, Guid[] dependencies, IoStatistics statistics)
    {
        Info = info;
        Signature = signature;
        _file = file;
        _dataKey = dataKey;
        _index = index;
        _entries = entries;
        _chunks = chunks;
        _dependencies = dependencies;
        Statistics = statistics;
        _byPath = new Dictionary<string, int>(entries.Length, VirtualPath.Comparer);
        _byGuid = [];
        for (var i = 0; i < entries.Length; i++)
        {
            if (!_byPath.TryAdd(entries[i].Path, i) || (entries[i].Guid != Guid.Empty && !_byGuid.TryAdd(entries[i].Guid, i)))
            {
                throw new PackageException(PackageError.Malformed, $"The package index lists '{entries[i].Path}' or its GUID twice.");
            }
        }
    }

    public PackageInfo Info { get; }

    public PackageSignatureStatus Signature { get; }

    public string FilePath => Info.FilePath;

    public Guid PackageGuid => Info.PackageGuid;

    public int Count => _entries.Length;

    internal IoStatistics Statistics { get; }

    /// <summary>Every file, in package order.</summary>
    public IEnumerable<PackageEntry> Entries => Enumerable.Range(0, _entries.Length).Select(Describe);

    /// <summary>Reads the public header and info without a key; nothing is verified beyond the header's own hash.</summary>
    /// <exception cref="PackageException">Not a package, or a damaged header.</exception>
    public static PackageInfo ReadInfo(string file)
    {
        using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadHeaderAndInfo(file, handle).Info;
    }

    /// <exception cref="PackageException">Malformed, unsupported, damaged, missing or wrong key, or a signature the policy rejects.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static MnvtPackage Open(string file, PackageOpenOptions? options = null)
    {
        options ??= new PackageOpenOptions();
        var statistics = options.Statistics ?? new IoStatistics();
        var started = Stopwatch.GetTimestamp();
        var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            var (header, info, layout) = ReadHeaderAndInfo(file, handle);
            var signature = CheckSignature(handle, header, info, layout, options, statistics);

            var storedIndex = ReadExact(handle, layout.IndexOffset, layout.IndexStoredLength, "index");
            if (!SHA256.HashData(storedIndex).AsSpan().SequenceEqual(header.AsSpan(PackageFormat.OffIndexHash, PackageFormat.HashSize)))
            {
                throw new PackageException(PackageError.IntegrityFailure, $"'{Path.GetFileName(file)}': the package index is damaged or was modified.");
            }

            byte[]? dataKey = null;
            var index = storedIndex;
            if (info.IsEncrypted)
            {
                if (options.Keys is null || !options.Keys.TryGet(info.KeyId, out var key))
                {
                    throw new PackageException(PackageError.MissingKey,
                        $"'{Path.GetFileName(file)}' is encrypted with key {PackageEncryptionKey.FormatId(info.KeyId)}, which this reader does not have.");
                }

                dataKey = key.DeriveDataKey(header.AsSpan(PackageFormat.OffKeySalt, 32), info.PackageGuid);
                index = new byte[storedIndex.Length];
                try
                {
                    using var aes = new AesGcm(dataKey, PackageFormat.TagSize);
                    var decryptStarted = Stopwatch.GetTimestamp();
                    aes.Decrypt(header.AsSpan(PackageFormat.OffIndexNonce, PackageFormat.NonceSize), storedIndex, header.AsSpan(PackageFormat.OffIndexTag, PackageFormat.TagSize), index,
                        PackageFormat.IndexAssociatedData(info.PackageGuid, info.BuildId));
                    statistics.AddDecrypt(Stopwatch.GetTimestamp() - decryptStarted);
                }
                catch (AuthenticationTagMismatchException ex)
                {
                    throw new PackageException(PackageError.WrongKey, $"'{Path.GetFileName(file)}' cannot be decrypted with key {PackageEncryptionKey.FormatId(info.KeyId)}: wrong key, or the package was modified.", ex);
                }
            }

            if (index.Length != layout.IndexPlainLength)
            {
                throw new PackageException(PackageError.Malformed, "The package index has the wrong size.");
            }

            var (entries, chunks, dependencies) = PackageIndexReader.Read(index, info.ChunkSize, layout.InfoOffset);
            statistics.AddPackageOpen(Stopwatch.GetTimestamp() - started);
            return new MnvtPackage(info, signature, handle, dataKey, index, entries, chunks, dependencies, statistics);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public bool Contains(string path) => VirtualPath.TryNormalize(path, out var canonical, out _) && _byPath.ContainsKey(canonical);

    public bool TryGetEntry(string path, out PackageEntry entry)
    {
        entry = null!;
        if (!VirtualPath.TryNormalize(path, out var canonical, out _) || !_byPath.TryGetValue(canonical, out var i))
        {
            return false;
        }

        entry = Describe(i);
        return true;
    }

    internal bool TryGetLength(string path, out long length, out Guid guid)
    {
        (length, guid) = (0, Guid.Empty);
        if (!_byPath.TryGetValue(path, out var i))
        {
            return false;
        }

        (length, guid) = (_entries[i].Length, _entries[i].Guid);
        return true;
    }

    /// <summary>Path of the file with this asset GUID; null when the package has none.</summary>
    public string? FindAsset(Guid assetGuid) => _byGuid.TryGetValue(assetGuid, out var i) ? _entries[i].Path : null;

    /// <summary>A read-only, seekable stream over one file. Reading verifies and decodes only the chunks it touches.</summary>
    /// <exception cref="FileNotFoundException">No such file.</exception>
    /// <exception cref="ObjectDisposedException">The package was disposed.</exception>
    public Stream OpenRead(string path)
    {
        if (!VirtualPath.TryNormalize(path, out var canonical, out _) || !_byPath.TryGetValue(canonical, out var i))
        {
            throw new FileNotFoundException($"'{path}' is not in {Path.GetFileName(FilePath)}.", path);
        }

        Acquire();
        try
        {
            return new PackageEntryStream(this, _entries[i]);
        }
        catch
        {
            Release();
            throw;
        }
    }

    /// <summary>Reads and checks every chunk (each shared chunk once): what <c>mnvt package verify</c> runs.</summary>
    public PackageVerification Verify()
    {
        var started = Stopwatch.GetTimestamp();
        var problems = new List<string>();
        var buffer = new byte[Info.ChunkSize];
        var stored = new byte[Info.ChunkSize];
        long bytes = 0;
        Acquire();
        try
        {
            using var aes = CreateAes();
            for (var c = 0; c < _chunks.Length; c++)
            {
                try
                {
                    ReadStored(c, stored);
                    DecodeStored(c, stored, buffer.AsSpan(0, _chunks[c].PlainLength), aes);
                    bytes += _chunks[c].StoredLength;
                }
                catch (PackageException ex)
                {
                    var users = _entries.Where(e => c >= e.FirstChunk && c < e.FirstChunk + e.ChunkCount).Select(e => e.Path);
                    problems.Add($"chunk {c} ({string.Join(", ", users)}): {ex.Message}");
                }
            }
        }
        finally
        {
            Release();
        }

        return new PackageVerification(problems, _chunks.Length, bytes, Stopwatch.GetElapsedTime(started));
    }

    public void Dispose()
    {
        lock (_leaseLock)
        {
            _disposeRequested = true;
            if (_leases > 0)
            {
                return; // closed by the last stream
            }
        }

        Close();
    }

    internal void Acquire()
    {
        lock (_leaseLock)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            _leases++;
        }
    }

    internal void Release()
    {
        lock (_leaseLock)
        {
            if (--_leases > 0 || !_disposeRequested)
            {
                return;
            }
        }

        Close();
    }

    internal int ChunkSize => Info.ChunkSize;

    internal ref readonly Chunk ChunkAt(int chunk) => ref _chunks[chunk];

    internal AesGcm? CreateAes() => _dataKey is null ? null : new AesGcm(_dataKey, PackageFormat.TagSize);

    internal void ReadStored(int chunk, byte[] stored)
    {
        ref readonly var c = ref _chunks[chunk];
        if (RandomAccess.Read(_file, stored.AsSpan(0, c.StoredLength), c.Offset) != c.StoredLength)
        {
            throw Corrupted(chunk, "it is truncated");
        }
    }

    internal async ValueTask ReadStoredAsync(int chunk, byte[] stored, CancellationToken cancellationToken)
    {
        var c = _chunks[chunk];
        if (await RandomAccess.ReadAsync(_file, stored.AsMemory(0, c.StoredLength), c.Offset, cancellationToken).ConfigureAwait(false) != c.StoredLength)
        {
            throw Corrupted(chunk, "it is truncated");
        }
    }

    /// <summary>Verifies (hash, then tag), decrypts and decompresses a chunk read by <see cref="ReadStored"/> into <paramref name="destination"/>.</summary>
    internal void DecodeStored(int chunk, byte[] stored, Span<byte> destination, AesGcm? aes)
    {
        ref readonly var c = ref _chunks[chunk];
        var data = stored.AsSpan(0, c.StoredLength);
        var started = Stopwatch.GetTimestamp();
        Span<byte> hash = stackalloc byte[PackageFormat.HashSize];
        SHA256.HashData(data, hash);
        var verified = Stopwatch.GetTimestamp();
        Statistics.AddVerify(verified - started);
        if (!hash.SequenceEqual(_index.AsSpan(c.HashOffset, PackageFormat.HashSize)))
        {
            throw Corrupted(chunk, "its hash does not match");
        }

        if (aes is not null)
        {
            try
            {
                aes.Decrypt(_index.AsSpan(c.NonceOffset, PackageFormat.NonceSize), data, _index.AsSpan(c.TagOffset, PackageFormat.TagSize), data, PackageFormat.ChunkAssociatedData(PackageGuid, (uint)chunk));
            }
            catch (AuthenticationTagMismatchException ex)
            {
                Statistics.ChunkCorrupted();
                throw new PackageException(PackageError.IntegrityFailure, $"Chunk {chunk} of '{Path.GetFileName(FilePath)}' fails authentication.", ex);
            }

            Statistics.AddDecrypt(Stopwatch.GetTimestamp() - verified);
        }

        var decompressStarted = Stopwatch.GetTimestamp();
        PackageCodecs.Decompress(c.Codec, data, destination);
        Statistics.AddDecompress(Stopwatch.GetTimestamp() - decompressStarted);
        Statistics.ChunkDecoded(c.StoredLength);
    }

    private PackageException Corrupted(int chunk, string reason)
    {
        Statistics.ChunkCorrupted();
        return new PackageException(PackageError.IntegrityFailure, $"Chunk {chunk} of '{Path.GetFileName(FilePath)}' is damaged or was modified: {reason}.");
    }

    private void Close()
    {
        lock (_leaseLock)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
        }

        _file.Dispose();
        if (_dataKey is not null)
        {
            CryptographicOperations.ZeroMemory(_dataKey);
        }
    }

    private PackageEntry Describe(int i)
    {
        var e = _entries[i];
        var stored = 0L;
        for (var c = 0; c < e.ChunkCount; c++)
        {
            stored += _chunks[e.FirstChunk + c].StoredLength;
        }

        var shared = _entries.Any(o => o.Path != e.Path && o.ChunkCount > 0 && o.FirstChunk == e.FirstChunk);
        return new PackageEntry(e.Path, e.Guid, e.Length, stored, e.ChunkCount, _dependencies.AsSpan(e.FirstDependency, e.DependencyCount).ToArray(), Convert.ToHexStringLower(e.ContentHash), shared);
    }

    private static (byte[] Header, PackageInfo Info, Layout Layout) ReadHeaderAndInfo(string file, SafeFileHandle handle)
    {
        var name = Path.GetFileName(file);
        var length = RandomAccess.GetLength(handle);
        var header = new byte[PackageFormat.HeaderSize];
        if (length < PackageFormat.HeaderSize || RandomAccess.Read(handle, header, 0) != header.Length || !header.AsSpan(0, 8).SequenceEqual(PackageFormat.Magic))
        {
            throw new PackageException(PackageError.Malformed, $"'{name}' is not an MNVTEngine package.");
        }

        var major = PackageFormat.ReadU16(header, PackageFormat.OffMajor);
        var minor = PackageFormat.ReadU16(header, PackageFormat.OffMinor);
        if (major != PackageFormat.MajorVersion)
        {
            throw new PackageException(PackageError.UnsupportedVersion, $"'{name}' uses package format {major}.{minor}; this engine reads {PackageFormat.MajorVersion}.x.");
        }

        if (!SHA256.HashData(header.AsSpan(0, PackageFormat.SignedHeaderSize)).AsSpan().SequenceEqual(header.AsSpan(PackageFormat.OffHeaderHash, PackageFormat.HashSize)))
        {
            throw new PackageException(PackageError.IntegrityFailure, $"'{name}': the package header is damaged or was modified.");
        }

        var flags = PackageFormat.ReadU32(header, PackageFormat.OffFlags);
        var chunkSize = PackageFormat.ReadU32(header, PackageFormat.OffChunkSize);
        if (PackageFormat.ReadU32(header, PackageFormat.OffHeaderSize) != PackageFormat.HeaderSize || (flags & ~PackageFormat.KnownFlags) != 0
            || chunkSize is < PackageFormat.MinChunkSize or > PackageFormat.MaxChunkSize)
        {
            throw new PackageException(PackageError.Malformed, $"'{name}' has an invalid header.");
        }

        var layout = new Layout(
            Bounded(PackageFormat.ReadU64(header, PackageFormat.OffInfoOffset)),
            (int)Math.Min(PackageFormat.ReadU32(header, PackageFormat.OffInfoLength), int.MaxValue),
            Bounded(PackageFormat.ReadU64(header, PackageFormat.OffIndexOffset)),
            (int)Math.Min(PackageFormat.ReadU32(header, PackageFormat.OffIndexStoredLength), int.MaxValue),
            (int)Math.Min(PackageFormat.ReadU32(header, PackageFormat.OffIndexPlainLength), int.MaxValue),
            Bounded(PackageFormat.ReadU64(header, PackageFormat.OffSignatureOffset)),
            (int)Math.Min(PackageFormat.ReadU32(header, PackageFormat.OffSignatureLength), int.MaxValue));
        var signed = (flags & PackageFormat.FlagSigned) != 0;
        if (layout.InfoOffset < PackageFormat.HeaderSize || layout.InfoLength > PackageFormat.MaxInfoSize
            || layout.IndexOffset != layout.InfoOffset + layout.InfoLength
            || layout.IndexStoredLength > PackageFormat.MaxIndexSize || layout.IndexPlainLength > PackageFormat.MaxIndexSize
            || layout.IndexOffset + layout.IndexStoredLength > length
            || (signed && (layout.SignatureOffset != layout.IndexOffset + layout.IndexStoredLength || layout.SignatureLength > PackageFormat.MaxSignatureSize || layout.SignatureOffset + layout.SignatureLength > length))
            || (!signed && (layout.SignatureOffset != 0 || layout.SignatureLength != 0)))
        {
            throw new PackageException(PackageError.Malformed, $"'{name}' has an invalid layout (offsets out of range).");
        }

        var infoBytes = ReadExact(handle, layout.InfoOffset, layout.InfoLength, "info");
        if (!SHA256.HashData(infoBytes).AsSpan().SequenceEqual(header.AsSpan(PackageFormat.OffInfoHash, PackageFormat.HashSize)))
        {
            throw new PackageException(PackageError.IntegrityFailure, $"'{name}': the package info is damaged or was modified.");
        }

        string? signer = null;
        if (signed)
        {
            var block = ReadExact(handle, layout.SignatureOffset, layout.SignatureLength, "signature");
            signer = block.Length >= 4 + PackageFormat.HashSize ? Convert.ToHexStringLower(block.AsSpan(4, 8)) : null;
        }

        var encrypted = (flags & PackageFormat.FlagEncrypted) != 0;
        var info = new PackageInfo(
            Path.GetFullPath(file), length, new Version(major, minor), PackageFormat.ReadGuid(header, PackageFormat.OffPackageGuid), PackageFormat.ReadGuid(header, PackageFormat.OffBuildId),
            encrypted, signed, encrypted ? PackageFormat.ReadU64(header, PackageFormat.OffKeyId) : 0, signer, (int)chunkSize, ReadProperties(infoBytes, name),
            Convert.ToHexStringLower(SHA256.HashData(header)));
        return (header, info, layout);

        static long Bounded(ulong value) => value > long.MaxValue / 2 ? long.MaxValue / 2 : (long)value;
    }

    private static PackageSignatureStatus CheckSignature(SafeFileHandle handle, byte[] header, PackageInfo info, Layout layout, PackageOpenOptions options, IoStatistics statistics)
    {
        var name = Path.GetFileName(info.FilePath);
        if (!info.IsSigned)
        {
            return options.RequireSignature
                ? throw new PackageException(PackageError.UntrustedSignature, $"'{name}' is not signed, and this game accepts only signed packages.")
                : PackageSignatureStatus.Unsigned;
        }

        var started = Stopwatch.GetTimestamp();
        var block = ReadExact(handle, layout.SignatureOffset, layout.SignatureLength, "signature");
        if (block.Length != 4 + PackageFormat.HashSize + 64 || PackageFormat.ReadU16(block, 0) != PackageFormat.SignatureEcdsaP256 || PackageFormat.ReadU16(block, 2) != 64)
        {
            throw new PackageException(PackageError.Malformed, $"'{name}' has a signature in an unknown format.");
        }

        var fingerprint = block[4..(4 + PackageFormat.HashSize)];
        var signer = options.TrustedSigners?.FirstOrDefault(k => k.Fingerprint.Span.SequenceEqual(fingerprint));
        if (signer is null)
        {
            return options.RequireSignature
                ? throw new PackageException(PackageError.UntrustedSignature, $"'{name}' is signed by key {info.SignerId}, which this game does not trust.")
                : PackageSignatureStatus.UnknownSigner;
        }

        var valid = signer.Verify(header.AsSpan(0, PackageFormat.SignedHeaderSize), block.AsSpan(4 + PackageFormat.HashSize, 64));
        statistics.AddVerify(Stopwatch.GetTimestamp() - started);
        return valid ? PackageSignatureStatus.Trusted : throw new PackageException(PackageError.InvalidSignature, $"'{name}': the signature does not match; the package was modified.");
    }

    private static SortedDictionary<string, string> ReadProperties(byte[] info, string name)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (info.Length == 0)
        {
            return result;
        }

        var offset = 0;
        var count = Take(2) is var c ? PackageFormat.ReadU16(c, 0) : 0;
        for (var i = 0; i < count; i++)
        {
            var key = Text();
            result[key] = Text();
        }

        return offset == info.Length ? result : throw new PackageException(PackageError.Malformed, $"'{name}' has malformed package info.");

        string Text()
        {
            var length = PackageFormat.ReadU16(Take(2), 0);
            return Encoding.UTF8.GetString(Take(length));
        }

        ReadOnlySpan<byte> Take(int bytes)
        {
            if (offset + bytes > info.Length)
            {
                throw new PackageException(PackageError.Malformed, $"'{name}' has malformed package info.");
            }

            offset += bytes;
            return info.AsSpan(offset - bytes, bytes);
        }
    }

    private static byte[] ReadExact(SafeFileHandle handle, long offset, int length, string what)
    {
        var data = new byte[length];
        return RandomAccess.Read(handle, data, offset) == length ? data : throw new PackageException(PackageError.Malformed, $"The package {what} is truncated.");
    }

    private sealed record Layout(long InfoOffset, int InfoLength, long IndexOffset, int IndexStoredLength, int IndexPlainLength, long SignatureOffset, int SignatureLength);

    internal readonly record struct Entry(string Path, Guid Guid, long Length, int FirstChunk, int ChunkCount, int FirstDependency, int DependencyCount, byte[] ContentHash);

    /// <summary>A stored chunk; nonce, tag and hash live in the index buffer at the given offsets.</summary>
    internal readonly record struct Chunk(long Offset, int StoredLength, int PlainLength, ChunkCodec Codec, int NonceOffset, int TagOffset, int HashOffset);
}
