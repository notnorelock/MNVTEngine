using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MNVTEngine.IO.Packages;

/// <param name="PackageGuid">Identity of the package; a build passes a reproducible one.</param>
/// <param name="BuildId">The build the package belongs to; the runtime checks all packages it mounts share the manifest's.</param>
/// <param name="CompressionFor">Per-file compression by virtual path; null applies <see cref="Compression"/>, except to
/// file types compressed already (PNG, Ogg, ...), which are stored as is.</param>
/// <param name="EncryptionKey">Encrypts the index and every chunk (AES-256-GCM); null writes a plain package.</param>
/// <param name="SigningKey">Signs the header, which covers the info, index and every chunk hash; null leaves the package unsigned.</param>
/// <param name="Properties">Public, unencrypted properties (name, target, profile, ...), readable without a key.</param>
public sealed record PackageWriterOptions(
    Guid PackageGuid,
    Guid BuildId,
    PackageCompression Compression = PackageCompression.Balanced,
    int ChunkSize = PackageFormat.DefaultChunkSize,
    Func<string, PackageCompression?>? CompressionFor = null,
    PackageEncryptionKey? EncryptionKey = null,
    PackageSigningKey? SigningKey = null,
    IReadOnlyDictionary<string, string>? Properties = null);

/// <summary>What <see cref="PackageWriter.Complete"/> wrote.</summary>
/// <param name="HeaderSha256">SHA-256 of the complete header (hex): identifies this exact package in a build manifest.</param>
/// <param name="DuplicateFiles">Files whose content equals an earlier file's and share its chunks.</param>
public sealed record PackageWriteResult(
    Guid PackageGuid, Guid BuildId, string HeaderSha256, int Files, int Chunks, long OriginalBytes, long StoredBytes, long FileLength,
    int DuplicateFiles, TimeSpan CompressTime, TimeSpan EncryptTime);

/// <summary>
/// Writes a <c>.mnvtpack</c>. Files are split into chunks of <see cref="PackageWriterOptions.ChunkSize"/>, each compressed,
/// encrypted and hashed independently and streamed to disk, so writing never holds a whole file. Identical files share
/// chunks. Output is deterministic for identical input and options: chunk nonces are synthetic (derived from the data
/// key, the chunk index and the chunk's content), so a nonce never repeats for different data under one key.
/// <para>Call <see cref="Complete"/>; a writer disposed without it leaves a file with an empty header, which readers reject.</para>
/// </summary>
public sealed class PackageWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly PackageWriterOptions _options;
    private readonly byte[]? _dataKey;
    private readonly byte[]? _salt;
    private readonly AesGcm? _aes;
    private readonly List<EntryRecord> _entries = [];
    private readonly List<ChunkRecord> _chunks = [];
    private readonly HashSet<string> _paths = new(VirtualPath.Comparer);
    private readonly HashSet<Guid> _guids = [];
    private readonly Dictionary<string, (uint First, uint Count)> _byContent = new(StringComparer.Ordinal);
    private long _compressTicks;
    private long _encryptTicks;
    private long _originalBytes;
    private int _duplicates;
    private bool _completed;

    /// <exception cref="ArgumentException">Invalid chunk size.</exception>
    public PackageWriter(string file, PackageWriterOptions options)
        : this(new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None), options, ownsStream: true)
    {
    }

    /// <param name="stream">Seekable, writable and empty.</param>
    public PackageWriter(Stream stream, PackageWriterOptions options, bool ownsStream = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ChunkSize is < PackageFormat.MinChunkSize or > PackageFormat.MaxChunkSize)
        {
            throw new ArgumentException($"Chunk size must be {PackageFormat.MinChunkSize}..{PackageFormat.MaxChunkSize} bytes.", nameof(options));
        }

        _stream = stream;
        _ownsStream = ownsStream;
        _options = options;
        if (options.EncryptionKey is { } key)
        {
            _salt = key.DeriveSalt(options.PackageGuid, options.BuildId);
            _dataKey = key.DeriveDataKey(_salt, options.PackageGuid);
            _aes = new AesGcm(_dataKey, PackageFormat.TagSize);
        }

        _stream.SetLength(0);
        _stream.Write(new byte[PackageFormat.HeaderSize]);
    }

    public int Count => _entries.Count;

    /// <summary>Adds a file.</summary>
    /// <param name="dependencies">Assets this one references (GUIDs; they may live in other packages).</param>
    /// <exception cref="ArgumentException">Invalid or duplicate path, or a GUID used twice.</exception>
    public void Add(string path, byte[] data, Guid assetGuid = default, IEnumerable<Guid>? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var stream = new MemoryStream(data, writable: false);
        Add(path, stream, assetGuid, dependencies);
    }

    /// <summary>Adds a file read from <paramref name="data"/> to its end, chunk by chunk.</summary>
    /// <inheritdoc cref="Add(string, byte[], Guid, IEnumerable{Guid})"/>
    public void Add(string path, Stream data, Guid assetGuid = default, IEnumerable<Guid>? dependencies = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ObjectDisposedException.ThrowIf(_completed, this);
        var canonical = VirtualPath.Normalize(path);
        if (canonical.Length == 0 || !_paths.Add(canonical))
        {
            throw new ArgumentException($"Duplicate or empty package path '{path}'.", nameof(path));
        }

        if (assetGuid != Guid.Empty && !_guids.Add(assetGuid))
        {
            _paths.Remove(canonical);
            throw new ArgumentException($"Asset GUID {assetGuid} is used by two files.", nameof(assetGuid));
        }

        var compression = _options.CompressionFor?.Invoke(canonical)
            ?? (PackageCodecs.PrecompressedExtensions.Contains(Path.GetExtension(canonical)) ? PackageCompression.None : _options.Compression);
        var start = _stream.Position;
        var firstChunk = (uint)_chunks.Count;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[_options.ChunkSize];
        long length = 0;
        int read;
        while ((read = data.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            WriteChunk(buffer.AsSpan(0, read), compression);
            length += read;
        }

        var content = hash.GetHashAndReset();
        var key = Convert.ToHexString(content) + ":" + length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var chunks = (First: firstChunk, Count: (uint)_chunks.Count - firstChunk);
        if (_byContent.TryGetValue(key, out var existing))
        {
            // Same content as an earlier file: drop the chunks just written and share the earlier ones.
            _chunks.RemoveRange((int)firstChunk, _chunks.Count - (int)firstChunk);
            _stream.SetLength(start);
            _stream.Position = start;
            chunks = existing;
            _duplicates++;
        }
        else
        {
            _byContent[key] = chunks;
        }

        _originalBytes += length;
        _entries.Add(new EntryRecord(canonical, assetGuid, length, chunks.First, chunks.Count, [.. (dependencies ?? []).Where(d => d != Guid.Empty).Distinct().Order()], content));
    }

    /// <summary>Writes the info, index, signature and header. The package is valid only after this.</summary>
    public PackageWriteResult Complete()
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        _completed = true;
        var header = new byte[PackageFormat.HeaderSize];

        var info = WriteInfo(_options.Properties ?? new Dictionary<string, string>());
        var infoOffset = _stream.Position;
        _stream.Write(info);

        var index = BuildIndex();
        byte[] storedIndex = index;
        if (_aes is not null)
        {
            var nonce = Nonce("index"u8, 0, SHA256.HashData(index));
            storedIndex = new byte[index.Length];
            _aes.Encrypt(nonce, index, storedIndex, header.AsSpan(PackageFormat.OffIndexTag, PackageFormat.TagSize), PackageFormat.IndexAssociatedData(_options.PackageGuid, _options.BuildId));
            nonce.CopyTo(header, PackageFormat.OffIndexNonce);
        }

        var indexOffset = _stream.Position;
        _stream.Write(storedIndex);

        PackageFormat.Magic.CopyTo(header);
        PackageFormat.WriteU16(header, PackageFormat.OffMajor, PackageFormat.MajorVersion);
        PackageFormat.WriteU16(header, PackageFormat.OffMinor, PackageFormat.MinorVersion);
        PackageFormat.WriteU32(header, PackageFormat.OffHeaderSize, PackageFormat.HeaderSize);
        var flags = (_aes is not null ? PackageFormat.FlagEncrypted : 0) | (_options.SigningKey is not null ? PackageFormat.FlagSigned : 0);
        PackageFormat.WriteU32(header, PackageFormat.OffFlags, flags);
        PackageFormat.WriteU32(header, PackageFormat.OffChunkSize, (uint)_options.ChunkSize);
        PackageFormat.WriteGuid(header, PackageFormat.OffPackageGuid, _options.PackageGuid);
        PackageFormat.WriteGuid(header, PackageFormat.OffBuildId, _options.BuildId);
        PackageFormat.WriteU64(header, PackageFormat.OffKeyId, _options.EncryptionKey?.Id ?? 0);
        _salt?.CopyTo(header, PackageFormat.OffKeySalt);
        PackageFormat.WriteU64(header, PackageFormat.OffInfoOffset, (ulong)infoOffset);
        PackageFormat.WriteU32(header, PackageFormat.OffInfoLength, (uint)info.Length);
        PackageFormat.WriteU32(header, PackageFormat.OffIndexPlainLength, (uint)index.Length);
        PackageFormat.WriteU64(header, PackageFormat.OffIndexOffset, (ulong)indexOffset);
        PackageFormat.WriteU32(header, PackageFormat.OffIndexStoredLength, (uint)storedIndex.Length);
        SHA256.HashData(info).CopyTo(header, PackageFormat.OffInfoHash);
        SHA256.HashData(storedIndex).CopyTo(header, PackageFormat.OffIndexHash);

        if (_options.SigningKey is { } signer)
        {
            var block = new byte[4 + PackageFormat.HashSize + 64];
            PackageFormat.WriteU16(block, 0, PackageFormat.SignatureEcdsaP256);
            PackageFormat.WriteU16(block, 2, 64);
            signer.PublicKey.Fingerprint.Span.CopyTo(block.AsSpan(4));
            PackageFormat.WriteU64(header, PackageFormat.OffSignatureOffset, (ulong)_stream.Position);
            PackageFormat.WriteU32(header, PackageFormat.OffSignatureLength, (uint)block.Length);
            signer.Sign(header.AsSpan(0, PackageFormat.SignedHeaderSize)).CopyTo(block, 4 + PackageFormat.HashSize);
            _stream.Write(block);
        }

        SHA256.HashData(header.AsSpan(0, PackageFormat.SignedHeaderSize)).CopyTo(header, PackageFormat.OffHeaderHash);
        var fileLength = _stream.Position;
        _stream.Position = 0;
        _stream.Write(header);
        _stream.Flush();

        var stored = _chunks.Sum(c => (long)c.StoredLength);
        return new PackageWriteResult(
            _options.PackageGuid, _options.BuildId, Convert.ToHexStringLower(SHA256.HashData(header)), _entries.Count, _chunks.Count, _originalBytes, stored, fileLength,
            _duplicates, Stopwatch.GetElapsedTime(0, _compressTicks), Stopwatch.GetElapsedTime(0, _encryptTicks));
    }

    public void Dispose()
    {
        _completed = true;
        _aes?.Dispose();
        if (_dataKey is not null)
        {
            CryptographicOperations.ZeroMemory(_dataKey);
        }

        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }

    private void WriteChunk(ReadOnlySpan<byte> plain, PackageCompression compression)
    {
        var index = (uint)_chunks.Count;
        var started = Stopwatch.GetTimestamp();
        var compressed = PackageCodecs.Compress(plain, compression);
        _compressTicks += Stopwatch.GetTimestamp() - started;
        var codec = compressed?.Codec ?? ChunkCodec.None;
        var stored = compressed?.Data ?? plain.ToArray();
        var nonce = new byte[PackageFormat.NonceSize];
        var tag = new byte[PackageFormat.TagSize];
        if (_aes is not null)
        {
            started = Stopwatch.GetTimestamp();
            nonce = Nonce("chunk"u8, index, SHA256.HashData(stored));
            var cipher = new byte[stored.Length];
            _aes.Encrypt(nonce, stored, cipher, tag, PackageFormat.ChunkAssociatedData(_options.PackageGuid, index));
            stored = cipher;
            _encryptTicks += Stopwatch.GetTimestamp() - started;
        }

        _chunks.Add(new ChunkRecord((ulong)_stream.Position, (uint)stored.Length, (uint)plain.Length, codec, nonce, tag, SHA256.HashData(stored)));
        _stream.Write(stored);
    }

    /// <summary>Synthetic nonce: HMAC-SHA256(data key, purpose || index || SHA-256(content)), first 12 bytes.</summary>
    private byte[] Nonce(ReadOnlySpan<byte> purpose, uint index, byte[] contentHash)
    {
        var input = new byte[purpose.Length + 4 + contentHash.Length];
        purpose.CopyTo(input);
        PackageFormat.WriteU32(input, purpose.Length, index);
        contentHash.CopyTo(input, purpose.Length + 4);
        return HMACSHA256.HashData(_dataKey!, input)[..PackageFormat.NonceSize];
    }

    private static byte[] WriteInfo(IReadOnlyDictionary<string, string> properties)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8);
        writer.Write((ushort)properties.Count);
        foreach (var (key, value) in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            WriteString(writer, key);
            WriteString(writer, value);
        }

        writer.Flush();
        var info = buffer.ToArray();
        return info.Length <= PackageFormat.MaxInfoSize ? info : throw new ArgumentException("Package properties are too large.");

        static void WriteString(BinaryWriter writer, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > ushort.MaxValue)
            {
                throw new ArgumentException("Package property is too long.");
            }

            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }
    }

    private byte[] BuildIndex()
    {
        var pool = new MemoryStream();
        var dependencies = new List<Guid>();
        var entries = new byte[_entries.Count * PackageFormat.EntryRecordSize];
        for (var i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            var record = entries.AsSpan(i * PackageFormat.EntryRecordSize, PackageFormat.EntryRecordSize);
            var path = Encoding.UTF8.GetBytes(e.Path);
            PackageFormat.WriteU32(record, 0, (uint)pool.Length);
            PackageFormat.WriteU32(record, 4, (uint)path.Length);
            pool.Write(path);
            PackageFormat.WriteGuid(record, 16, e.Guid);
            PackageFormat.WriteU64(record, 32, (ulong)e.Length);
            PackageFormat.WriteU32(record, 40, e.FirstChunk);
            PackageFormat.WriteU32(record, 44, e.ChunkCount);
            PackageFormat.WriteU32(record, 48, (uint)dependencies.Count);
            PackageFormat.WriteU32(record, 52, (uint)e.Dependencies.Length);
            e.ContentHash.CopyTo(record[56..]);
            dependencies.AddRange(e.Dependencies);
        }

        var chunks = new byte[_chunks.Count * PackageFormat.ChunkRecordSize];
        for (var i = 0; i < _chunks.Count; i++)
        {
            var c = _chunks[i];
            var record = chunks.AsSpan(i * PackageFormat.ChunkRecordSize, PackageFormat.ChunkRecordSize);
            PackageFormat.WriteU64(record, 0, c.Offset);
            PackageFormat.WriteU32(record, 8, c.StoredLength);
            PackageFormat.WriteU32(record, 12, c.PlainLength);
            record[16] = (byte)c.Codec;
            c.Nonce.CopyTo(record[20..]);
            c.Tag.CopyTo(record[32..]);
            c.Hash.CopyTo(record[48..]);
        }

        var index = new byte[PackageFormat.IndexHeaderSize + entries.Length + chunks.Length + (dependencies.Count * 16) + pool.Length];
        PackageFormat.WriteU32(index, 0, (uint)_entries.Count);
        PackageFormat.WriteU32(index, 4, (uint)_chunks.Count);
        PackageFormat.WriteU32(index, 8, (uint)dependencies.Count);
        PackageFormat.WriteU32(index, 12, (uint)pool.Length);
        var offset = PackageFormat.IndexHeaderSize;
        entries.CopyTo(index, offset);
        offset += entries.Length;
        chunks.CopyTo(index, offset);
        offset += chunks.Length;
        foreach (var dependency in dependencies)
        {
            PackageFormat.WriteGuid(index, offset, dependency);
            offset += 16;
        }

        pool.ToArray().CopyTo(index, offset);
        return index.Length <= PackageFormat.MaxIndexSize ? index : throw new InvalidOperationException("The package index is too large; split the content over more packages.");
    }

    private sealed record EntryRecord(string Path, Guid Guid, long Length, uint FirstChunk, uint ChunkCount, Guid[] Dependencies, byte[] ContentHash);

    private sealed record ChunkRecord(ulong Offset, uint StoredLength, uint PlainLength, ChunkCodec Codec, byte[] Nonce, byte[] Tag, byte[] Hash);
}
