using System.Diagnostics;

namespace MNVTEngine.IO;

/// <summary>
/// Counters of a <see cref="VirtualFileSystem"/> and the packages it reads, updated lock-free from any thread. Read them
/// with <see cref="Snapshot"/>; rates (throughput) come from the difference of two snapshots.
/// </summary>
public sealed class IoStatistics
{
    private long _opens;
    private long _failedReads;
    private long _activeStreams;
    private long _pendingReads;
    private long _bytesRead;
    private long _storedBytesRead;
    private long _chunksDecoded;
    private long _chunkCacheHits;
    private long _corruptedChunks;
    private long _verifyTicks;
    private long _decryptTicks;
    private long _decompressTicks;
    private long _packageOpenTicks;

    public IoStatisticsSnapshot Snapshot() => new(
        Interlocked.Read(ref _opens),
        Interlocked.Read(ref _failedReads),
        Interlocked.Read(ref _activeStreams),
        Interlocked.Read(ref _pendingReads),
        Interlocked.Read(ref _bytesRead),
        Interlocked.Read(ref _storedBytesRead),
        Interlocked.Read(ref _chunksDecoded),
        Interlocked.Read(ref _chunkCacheHits),
        Interlocked.Read(ref _corruptedChunks),
        TimeSpan.FromSeconds(Interlocked.Read(ref _verifyTicks) / (double)Stopwatch.Frequency),
        TimeSpan.FromSeconds(Interlocked.Read(ref _decryptTicks) / (double)Stopwatch.Frequency),
        TimeSpan.FromSeconds(Interlocked.Read(ref _decompressTicks) / (double)Stopwatch.Frequency),
        TimeSpan.FromSeconds(Interlocked.Read(ref _packageOpenTicks) / (double)Stopwatch.Frequency));

    internal void Opened() => Interlocked.Increment(ref _opens);

    internal void ReadFailed() => Interlocked.Increment(ref _failedReads);

    internal void StreamOpened() => Interlocked.Increment(ref _activeStreams);

    internal void StreamClosed() => Interlocked.Decrement(ref _activeStreams);

    internal void ReadStarted() => Interlocked.Increment(ref _pendingReads);

    internal void ReadFinished() => Interlocked.Decrement(ref _pendingReads);

    internal void Read(long bytes) => Interlocked.Add(ref _bytesRead, bytes);

    internal void ChunkDecoded(long storedBytes)
    {
        Interlocked.Increment(ref _chunksDecoded);
        Interlocked.Add(ref _storedBytesRead, storedBytes);
    }

    internal void ChunkCacheHit() => Interlocked.Increment(ref _chunkCacheHits);

    internal void ChunkCorrupted() => Interlocked.Increment(ref _corruptedChunks);

    internal void AddVerify(long ticks) => Interlocked.Add(ref _verifyTicks, ticks);

    internal void AddDecrypt(long ticks) => Interlocked.Add(ref _decryptTicks, ticks);

    internal void AddDecompress(long ticks) => Interlocked.Add(ref _decompressTicks, ticks);

    internal void AddPackageOpen(long ticks) => Interlocked.Add(ref _packageOpenTicks, ticks);
}

/// <param name="Opens">Files opened.</param>
/// <param name="FailedReads">Opens or reads that failed (missing, damaged, tampered, wrong key).</param>
/// <param name="ActiveStreams">Streams open now.</param>
/// <param name="PendingReads">Asynchronous reads in flight now.</param>
/// <param name="BytesRead">Bytes handed to readers.</param>
/// <param name="StoredBytesRead">Package bytes read from disk (compressed, encrypted).</param>
/// <param name="ChunksDecoded">Package chunks verified and decoded.</param>
/// <param name="ChunkCacheHits">Reads served from a stream's decoded chunk without decoding again.</param>
/// <param name="CorruptedChunks">Chunks that failed their hash or authentication tag.</param>
/// <param name="VerifyTime">Time spent hashing chunks and verifying package metadata and signatures.</param>
/// <param name="DecryptTime">Time spent decrypting (AES-256-GCM).</param>
/// <param name="DecompressTime">Time spent decompressing.</param>
/// <param name="PackageOpenTime">Time spent opening packages (header, index, signature).</param>
public readonly record struct IoStatisticsSnapshot(
    long Opens, long FailedReads, long ActiveStreams, long PendingReads, long BytesRead, long StoredBytesRead, long ChunksDecoded,
    long ChunkCacheHits, long CorruptedChunks, TimeSpan VerifyTime, TimeSpan DecryptTime, TimeSpan DecompressTime, TimeSpan PackageOpenTime);
