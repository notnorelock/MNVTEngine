using System.Buffers;
using System.Security.Cryptography;

namespace MNVTEngine.IO.Packages;

/// <summary>
/// Seekable read-only stream over one packaged file. Keeps one decoded chunk (pooled buffers, no per-read allocation);
/// reads that cover a whole chunk decode straight into the caller's buffer. Not thread-safe, like any stream; open one per reader.
/// </summary>
internal sealed class PackageEntryStream : Stream
{
    private readonly MnvtPackage _package;
    private readonly MnvtPackage.Entry _entry;
    private readonly AesGcm? _aes;
    private readonly byte[] _stored;
    private readonly byte[] _decoded;
    private int _cachedChunk = -1;
    private long _position;
    private bool _disposed;

    public PackageEntryStream(MnvtPackage package, MnvtPackage.Entry entry)
    {
        _package = package;
        _entry = entry;
        _aes = package.CreateAes();
        _stored = ArrayPool<byte>.Shared.Rent(package.ChunkSize);
        _decoded = ArrayPool<byte>.Shared.Rent(package.ChunkSize);
        package.Statistics.StreamOpened();
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => !_disposed;

    public override bool CanWrite => false;

    public override long Length => _entry.Length;

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var total = 0;
        while (buffer.Length > 0 && _position < _entry.Length)
        {
            var (chunk, inChunk, plain) = Locate();
            var count = (int)Math.Min(buffer.Length, plain - inChunk);
            if (inChunk == 0 && count == plain && chunk != _cachedChunk)
            {
                _package.ReadStored(chunk, _stored);
                _package.DecodeStored(chunk, _stored, buffer[..count], _aes);
            }
            else
            {
                EnsureCached(chunk);
                _decoded.AsSpan(inChunk, count).CopyTo(buffer);
            }

            buffer = buffer[count..];
            _position += count;
            total += count;
        }

        _package.Statistics.Read(total);
        return total;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var total = 0;
        while (buffer.Length > 0 && _position < _entry.Length)
        {
            var (chunk, inChunk, plain) = Locate();
            var count = (int)Math.Min(buffer.Length, plain - inChunk);
            if (inChunk == 0 && count == plain && chunk != _cachedChunk)
            {
                await _package.ReadStoredAsync(chunk, _stored, cancellationToken).ConfigureAwait(false);
                _package.DecodeStored(chunk, _stored, buffer.Span[..count], _aes);
            }
            else
            {
                await EnsureCachedAsync(chunk, cancellationToken).ConfigureAwait(false);
                _decoded.AsMemory(inChunk, count).CopyTo(buffer);
            }

            buffer = buffer[count..];
            _position += count;
            total += count;
        }

        _package.Statistics.Read(total);
        return total;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _entry.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException("Package files are read-only.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Package files are read-only.");

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            _aes?.Dispose();
            ArrayPool<byte>.Shared.Return(_stored, clearArray: _aes is not null);
            ArrayPool<byte>.Shared.Return(_decoded, clearArray: _aes is not null);
            _package.Statistics.StreamClosed();
            _package.Release();
        }

        base.Dispose(disposing);
    }

    /// <summary>Package chunk index, offset inside it, and its plain size, at the current position.</summary>
    private (int Chunk, int InChunk, int Plain) Locate()
    {
        var local = (int)(_position / _package.ChunkSize);
        var chunk = _entry.FirstChunk + local;
        return (chunk, (int)(_position - ((long)local * _package.ChunkSize)), _package.ChunkAt(chunk).PlainLength);
    }

    private void EnsureCached(int chunk)
    {
        if (chunk == _cachedChunk)
        {
            _package.Statistics.ChunkCacheHit();
            return;
        }

        _cachedChunk = -1;
        _package.ReadStored(chunk, _stored);
        _package.DecodeStored(chunk, _stored, _decoded.AsSpan(0, _package.ChunkAt(chunk).PlainLength), _aes);
        _cachedChunk = chunk;
    }

    private async ValueTask EnsureCachedAsync(int chunk, CancellationToken cancellationToken)
    {
        if (chunk == _cachedChunk)
        {
            _package.Statistics.ChunkCacheHit();
            return;
        }

        _cachedChunk = -1;
        await _package.ReadStoredAsync(chunk, _stored, cancellationToken).ConfigureAwait(false);
        _package.DecodeStored(chunk, _stored, _decoded.AsSpan(0, _package.ChunkAt(chunk).PlainLength), _aes);
        _cachedChunk = chunk;
    }
}
