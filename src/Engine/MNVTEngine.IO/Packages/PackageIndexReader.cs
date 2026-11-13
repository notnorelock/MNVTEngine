using System.Text;

namespace MNVTEngine.IO.Packages;

/// <summary>
/// Parses a decrypted package index, trusting nothing: every count, offset and length is checked against the index size,
/// the data region and the chunk size before it is used, so a malformed index cannot cause an out-of-range read.
/// </summary>
internal static class PackageIndexReader
{
    /// <param name="dataEnd">End of the chunk data region (where the info block starts).</param>
    public static (MnvtPackage.Entry[] Entries, MnvtPackage.Chunk[] Chunks, Guid[] Dependencies) Read(byte[] index, int chunkSize, long dataEnd)
    {
        if (index.Length < PackageFormat.IndexHeaderSize)
        {
            throw Malformed("too short");
        }

        long entryCount = PackageFormat.ReadU32(index, 0);
        long chunkCount = PackageFormat.ReadU32(index, 4);
        long dependencyCount = PackageFormat.ReadU32(index, 8);
        long poolLength = PackageFormat.ReadU32(index, 12);
        var entriesStart = (long)PackageFormat.IndexHeaderSize;
        var chunksStart = entriesStart + (entryCount * PackageFormat.EntryRecordSize);
        var dependenciesStart = chunksStart + (chunkCount * PackageFormat.ChunkRecordSize);
        var poolStart = dependenciesStart + (dependencyCount * 16);
        if (poolStart + poolLength != index.Length)
        {
            throw Malformed("its sections do not add up to its size");
        }

        var chunks = new MnvtPackage.Chunk[chunkCount];
        for (var i = 0; i < chunkCount; i++)
        {
            var at = (int)(chunksStart + (i * PackageFormat.ChunkRecordSize));
            var record = index.AsSpan(at, PackageFormat.ChunkRecordSize);
            var offset = PackageFormat.ReadU64(record, 0);
            var stored = PackageFormat.ReadU32(record, 8);
            var plain = PackageFormat.ReadU32(record, 12);
            var codec = (ChunkCodec)record[16];
            if (offset < PackageFormat.HeaderSize || offset > (ulong)dataEnd || stored > (ulong)dataEnd - offset
                || plain == 0 || plain > chunkSize || !Enum.IsDefined(codec)
                || (codec == ChunkCodec.None ? stored != plain : stored >= plain || stored == 0))
            {
                throw Malformed($"chunk {i} is out of range");
            }

            chunks[i] = new MnvtPackage.Chunk((long)offset, (int)stored, (int)plain, codec, at + 20, at + 32, at + 48);
        }

        var dependencies = new Guid[dependencyCount];
        for (var i = 0; i < dependencyCount; i++)
        {
            dependencies[i] = PackageFormat.ReadGuid(index, (int)(dependenciesStart + (i * 16)));
        }

        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        var entries = new MnvtPackage.Entry[entryCount];
        for (var i = 0; i < entryCount; i++)
        {
            var record = index.AsSpan((int)(entriesStart + (i * PackageFormat.EntryRecordSize)), PackageFormat.EntryRecordSize);
            long pathOffset = PackageFormat.ReadU32(record, 0);
            long pathLength = PackageFormat.ReadU32(record, 4);
            var length = PackageFormat.ReadU64(record, 32);
            long firstChunk = PackageFormat.ReadU32(record, 40);
            long count = PackageFormat.ReadU32(record, 44);
            long firstDependency = PackageFormat.ReadU32(record, 48);
            long dependencyCountOfEntry = PackageFormat.ReadU32(record, 52);
            if (pathLength == 0 || pathOffset + pathLength > poolLength || firstChunk + count > chunkCount || firstDependency + dependencyCountOfEntry > dependencyCount
                || length > long.MaxValue || (ulong)count != (length + (ulong)chunkSize - 1) / (ulong)chunkSize)
            {
                throw Malformed($"entry {i} is out of range");
            }

            string path;
            try
            {
                path = strict.GetString(index, (int)(poolStart + pathOffset), (int)pathLength);
            }
            catch (DecoderFallbackException ex)
            {
                throw new PackageException(PackageError.Malformed, $"The package index is malformed: entry {i} has an invalid path.", ex);
            }

            if (!VirtualPath.TryNormalize(path, out var canonical, out _) || canonical != path || canonical.Length == 0)
            {
                throw Malformed($"entry {i} has an invalid path");
            }

            // Every chunk but the last is full; together they hold exactly the file.
            for (var c = 0; c < count; c++)
            {
                var expected = c < count - 1 ? chunkSize : (long)length - ((count - 1) * chunkSize);
                if (chunks[firstChunk + c].PlainLength != expected)
                {
                    throw Malformed($"entry '{path}' has chunks of the wrong size");
                }
            }

            entries[i] = new MnvtPackage.Entry(path, PackageFormat.ReadGuid(record, 16), (long)length, (int)firstChunk, (int)count, (int)firstDependency, (int)dependencyCountOfEntry, record.Slice(56, 32).ToArray());
        }

        return (entries, chunks, dependencies);
    }

    private static PackageException Malformed(string reason) => new(PackageError.Malformed, $"The package index is malformed: {reason}.");
}
