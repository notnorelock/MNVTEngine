using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MNVTEngine.Assets;

/// <summary>
/// Read-only, encrypted content archive of an exported game (<c>.mnvtpak</c>). Every entry and the index are encrypted
/// and authenticated with AES-256-GCM, so modified or swapped data is rejected rather than loaded. Entries are deflated
/// when that makes them smaller. Reads are random-access and thread-safe; nothing is ever extracted to disk.
/// <para>
/// Format v1: header <c>"MNVTPAK\0"</c>, int32 version, int64 index offset, int32 index length, index nonce (12),
/// index tag (16); then entry ciphertexts; then the encrypted index. Nonces are derived from the key, the entry path
/// and a hash of its content, so identical input gives an identical archive (reproducible builds) while a nonce is never
/// reused for different data. The entry path is authenticated with each entry, the header with the index.
/// </para>
/// <para>
/// The key ships with the game (it has to: the game must decrypt its content). This keeps assets and code out of reach
/// of casual extraction and detects tampering; it cannot stop someone determined to reverse-engineer the executable.
/// </para>
/// </summary>
public sealed class ContentArchive : IDisposable
{
    public const string Extension = ".mnvtpak";
    public const int KeySize = 32;
    private const int Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 8 + 4 + 8 + 4 + NonceSize + TagSize;
    private static readonly byte[] Magic = "MNVTPAK\0"u8.ToArray();

    private readonly SafeFileHandle _file;
    private readonly AesGcm _aes;
    private readonly Dictionary<string, Entry> _entries;

    private ContentArchive(string path, SafeFileHandle file, AesGcm aes, Dictionary<string, Entry> entries)
    {
        FilePath = path;
        _file = file;
        _aes = aes;
        _entries = entries;
    }

    public string FilePath { get; }

    /// <summary>Entry paths, '/'-separated, in archive order.</summary>
    public IEnumerable<string> Entries => _entries.Values.OrderBy(e => e.Offset).Select(e => e.Path);

    public static byte[] CreateKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <exception cref="InvalidDataException">Not an archive, unsupported version, wrong key, or damaged index.</exception>
    public static ContentArchive Open(string path, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        CheckKey(key);
        var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        var aes = new AesGcm(key, TagSize);
        try
        {
            var header = new byte[HeaderSize];
            if (RandomAccess.Read(file, header, 0) != HeaderSize || !header.AsSpan(0, 8).SequenceEqual(Magic))
            {
                throw new InvalidDataException($"'{path}' is not an MNVTEngine content archive.");
            }

            var version = BitConverter.ToInt32(header, 8);
            if (version != Version)
            {
                throw new InvalidDataException($"Content archive version {version} is not supported (expected {Version}).");
            }

            var indexOffset = BitConverter.ToInt64(header, 12);
            var indexLength = BitConverter.ToInt32(header, 20);
            if (indexOffset < HeaderSize || indexLength < 0 || indexOffset + indexLength > RandomAccess.GetLength(file))
            {
                throw new InvalidDataException("Content archive index is out of range.");
            }

            var cipher = new byte[indexLength];
            RandomAccess.Read(file, cipher, indexOffset);
            var plain = new byte[indexLength];
            Decrypt(aes, header.AsSpan(24, NonceSize), cipher, header.AsSpan(24 + NonceSize, TagSize), plain, header.AsSpan(0, 24), "index");
            return new ContentArchive(path, file, aes, ReadIndex(plain, indexOffset));
        }
        catch
        {
            aes.Dispose();
            file.Dispose();
            throw;
        }
    }

    public bool Contains(string path) => _entries.ContainsKey(Normalize(path));

    /// <exception cref="FileNotFoundException">No such entry.</exception>
    /// <exception cref="InvalidDataException">The entry was modified or damaged.</exception>
    public byte[] Read(string path) =>
        TryRead(path) ?? throw new FileNotFoundException($"'{path}' is not in the game content.", path);

    /// <summary>The entry's content, or null if there is no such entry.</summary>
    /// <exception cref="InvalidDataException">The entry was modified or damaged.</exception>
    public byte[]? TryRead(string path)
    {
        if (!_entries.TryGetValue(Normalize(path), out var entry))
        {
            return null;
        }

        var cipher = new byte[entry.StoredLength];
        if (RandomAccess.Read(_file, cipher, entry.Offset) != cipher.Length)
        {
            throw new InvalidDataException($"Content entry '{entry.Path}' is truncated.");
        }

        var plain = new byte[entry.StoredLength];
        Decrypt(_aes, entry.Nonce, cipher, entry.Tag, plain, Encoding.UTF8.GetBytes(entry.Path), entry.Path);
        return entry.Compressed ? Inflate(plain, entry.Length, entry.Path) : plain;
    }

    public void Dispose()
    {
        _aes.Dispose();
        _file.Dispose();
    }

    internal static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void CheckKey(byte[] key)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"Content keys are {KeySize} bytes.", nameof(key));
        }
    }

    private static void Decrypt(AesGcm aes, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> tag, Span<byte> plain, ReadOnlySpan<byte> associated, string what)
    {
        try
        {
            aes.Decrypt(nonce, cipher, tag, plain, associated);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new InvalidDataException(what == "index"
                ? "Game content cannot be decrypted: wrong key, or the archive is damaged."
                : $"Content entry '{what}' was modified or is damaged.", ex);
        }
    }

    private static byte[] Inflate(byte[] data, int length, string path)
    {
        var result = new byte[length];
        try
        {
            using var stream = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
            stream.ReadExactly(result);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
            throw new InvalidDataException($"Content entry '{path}' is damaged.", ex);
        }

        return result;
    }

    private static Dictionary<string, Entry> ReadIndex(byte[] index, long dataEnd)
    {
        using var reader = new BinaryReader(new MemoryStream(index), Encoding.UTF8);
        var count = reader.ReadInt32();
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            var entry = new Entry(reader.ReadString(), reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadBoolean(), reader.ReadBytes(NonceSize), reader.ReadBytes(TagSize));
            if (entry.Offset < HeaderSize || entry.StoredLength < 0 || entry.Length < 0 || entry.Offset + entry.StoredLength > dataEnd || !entries.TryAdd(entry.Path, entry))
            {
                throw new InvalidDataException($"Content archive index is invalid at '{entry.Path}'.");
            }
        }

        return entries;
    }

    private sealed record Entry(string Path, long Offset, int StoredLength, int Length, bool Compressed, byte[] Nonce, byte[] Tag);

    /// <summary>Streams entries into a new archive; the index is written by <see cref="Dispose"/>.</summary>
    public sealed class Writer : IDisposable
    {
        private readonly FileStream _stream;
        private readonly byte[] _key;
        private readonly AesGcm _aes;
        private readonly List<Entry> _entries = [];
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
        private bool _completed;

        public Writer(string path, byte[] key)
        {
            ArgumentNullException.ThrowIfNull(key);
            CheckKey(key);
            _key = [.. key];
            _aes = new AesGcm(_key, TagSize);
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            _stream.Write(new byte[HeaderSize]);
        }

        public int Count => _entries.Count;

        /// <exception cref="ArgumentException">Duplicate or empty path.</exception>
        public void Add(string path, byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            path = Normalize(path);
            if (path.Length == 0 || !_paths.Add(path))
            {
                throw new ArgumentException($"Duplicate or empty archive path '{path}'.", nameof(path));
            }

            var deflated = Deflate(data);
            var compressed = deflated.Length < data.Length;
            var stored = compressed ? deflated : data;
            var nonce = DeriveNonce("entry", path, data);
            var cipher = new byte[stored.Length];
            var tag = new byte[TagSize];
            _aes.Encrypt(nonce, stored, cipher, tag, Encoding.UTF8.GetBytes(path));
            _entries.Add(new Entry(path, _stream.Position, cipher.Length, data.Length, compressed, nonce, tag));
            _stream.Write(cipher);
        }

        public void Dispose()
        {
            if (!_completed)
            {
                _completed = true;
                WriteIndex();
            }

            _aes.Dispose();
            _stream.Dispose();
            CryptographicOperations.ZeroMemory(_key);
        }

        private void WriteIndex()
        {
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(_entries.Count);
                foreach (var e in _entries)
                {
                    writer.Write(e.Path);
                    writer.Write(e.Offset);
                    writer.Write(e.StoredLength);
                    writer.Write(e.Length);
                    writer.Write(e.Compressed);
                    writer.Write(e.Nonce);
                    writer.Write(e.Tag);
                }
            }

            var index = buffer.ToArray();
            var header = new byte[HeaderSize];
            Magic.CopyTo(header, 0);
            BitConverter.TryWriteBytes(header.AsSpan(8), Version);
            BitConverter.TryWriteBytes(header.AsSpan(12), _stream.Position);
            BitConverter.TryWriteBytes(header.AsSpan(20), index.Length);
            DeriveNonce("index", "", index).CopyTo(header, 24);
            var cipher = new byte[index.Length];
            _aes.Encrypt(header.AsSpan(24, NonceSize), index, cipher, header.AsSpan(24 + NonceSize, TagSize), header.AsSpan(0, 24));
            _stream.Write(cipher);
            _stream.Position = 0;
            _stream.Write(header);
        }

        private byte[] DeriveNonce(string purpose, string path, byte[] data)
        {
            var input = Encoding.UTF8.GetBytes($"{purpose}\0{path}\0{Convert.ToHexString(SHA256.HashData(data))}");
            return HMACSHA256.HashData(_key, input)[..NonceSize];
        }

        private static byte[] Deflate(byte[] data)
        {
            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(data);
            }

            return output.ToArray();
        }
    }
}
