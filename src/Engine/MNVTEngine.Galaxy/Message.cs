// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace MNVTEngine.Galaxy;

/// <summary>
/// A growable, little-endian buffer for building and reading network messages. Writes append; reads advance a
/// separate cursor. Reads are bounds-checked and never throw: a read past the end leaves <see cref="Failed"/> set and
/// returns a default, so a malformed message from the network is rejected, not a crash. Instances are pooled; create
/// one with <see cref="Create"/> and, after sending, the transport returns it to the pool.
/// </summary>
public sealed class Message
{
    private static readonly ConcurrentQueue<Message> Pool = new();

    private byte[] _buffer;
    private int _writePosition;
    private int _readPosition;

    private Message() => _buffer = new byte[64];

    /// <summary>The number IDs the message so a handler can be chosen without relying on a type name (which
    /// obfuscation would rename). Application IDs are any non-negative value; negatives are reserved.</summary>
    public int Id { get; private set; }

    /// <summary>True once a read ran past the written data or a length prefix was out of range. A failed message must
    /// not be trusted.</summary>
    public bool Failed { get; private set; }

    /// <summary>Bytes written so far.</summary>
    public int Length => _writePosition;

    /// <summary>Bytes not yet read.</summary>
    public int Unread => _writePosition - _readPosition;

    /// <summary>Rents a message and sets its <see cref="Id"/>. Reuse keeps networking off the garbage collector's
    /// hot path.</summary>
    public static Message Create(int id = 0)
    {
        if (!Pool.TryDequeue(out var message))
        {
            message = new Message();
        }

        message.Reset();
        message.Id = id;
        return message;
    }

    /// <summary>Returns a message to the pool. The caller must not use it afterwards.</summary>
    public void Release()
    {
        Reset();
        if (Pool.Count < 1024)
        {
            Pool.Enqueue(this);
        }
    }

    internal void Reset()
    {
        _writePosition = 0;
        _readPosition = 0;
        Id = 0;
        Failed = false;
    }

    internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _writePosition);

    internal void LoadFrom(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(data.Length);
        data.CopyTo(_buffer);
        _writePosition = data.Length;
        _readPosition = 0;
        Failed = false;
    }

    private void EnsureCapacity(int additional)
    {
        int required = _writePosition + additional;
        if (required <= _buffer.Length)
        {
            return;
        }

        int size = _buffer.Length * 2;
        while (size < required)
        {
            size *= 2;
        }

        Array.Resize(ref _buffer, size);
    }

    private Span<byte> Reserve(int count)
    {
        EnsureCapacity(count);
        var span = _buffer.AsSpan(_writePosition, count);
        _writePosition += count;
        return span;
    }

    private bool TryRead(int count, out ReadOnlySpan<byte> span)
    {
        if (Failed || _readPosition + count > _writePosition)
        {
            Failed = true;
            span = default;
            return false;
        }

        span = _buffer.AsSpan(_readPosition, count);
        _readPosition += count;
        return true;
    }

    public Message Write(byte value)
    {
        Reserve(1)[0] = value;
        return this;
    }

    public Message Write(bool value) => Write((byte)(value ? 1 : 0));

    public Message Write(short value)
    {
        BinaryPrimitives.WriteInt16LittleEndian(Reserve(2), value);
        return this;
    }

    public Message Write(ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(Reserve(2), value);
        return this;
    }

    public Message Write(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);
        return this;
    }

    public Message Write(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Reserve(4), value);
        return this;
    }

    public Message Write(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);
        return this;
    }

    public Message Write(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);
        return this;
    }

    public Message Write(float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(Reserve(4), value);
        return this;
    }

    public Message Write(double value)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(Reserve(8), value);
        return this;
    }

    /// <summary>Writes an unsigned integer as a variable-length (LEB128) value: 1 byte for values under 128, growing
    /// to 5. Small ids and counts cost far less than a fixed 4 bytes.</summary>
    public Message WriteVarUInt(uint value)
    {
        while (value >= 0x80)
        {
            Write((byte)(value | 0x80));
            value >>= 7;
        }

        return Write((byte)value);
    }

    /// <summary>Reads a variable-length unsigned integer; sets <see cref="Failed"/> on a truncated or overlong value.</summary>
    public uint ReadVarUInt()
    {
        uint result = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = ReadByte();
            if (Failed)
            {
                return 0;
            }

            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        Failed = true;
        return 0;
    }

    /// <summary>Writes a length-prefixed byte block (prefix is a 2-byte count, so at most 65535 bytes).</summary>
    public Message Write(ReadOnlySpan<byte> value)
    {
        if (value.Length > ushort.MaxValue)
        {
            throw new ArgumentException($"A byte block in a message is limited to {ushort.MaxValue} bytes.", nameof(value));
        }

        Write((ushort)value.Length);
        value.CopyTo(Reserve(value.Length));
        return this;
    }

    /// <summary>Writes a length-prefixed UTF-8 string.</summary>
    public Message Write(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > ushort.MaxValue)
        {
            throw new ArgumentException($"A string in a message is limited to {ushort.MaxValue} UTF-8 bytes.", nameof(value));
        }

        Write((ushort)byteCount);
        Encoding.UTF8.GetBytes(value, Reserve(byteCount));
        return this;
    }

    public byte ReadByte() => TryRead(1, out var span) ? span[0] : (byte)0;

    public bool ReadBool() => ReadByte() != 0;

    public short ReadShort() => TryRead(2, out var span) ? BinaryPrimitives.ReadInt16LittleEndian(span) : (short)0;

    public ushort ReadUShort() => TryRead(2, out var span) ? BinaryPrimitives.ReadUInt16LittleEndian(span) : (ushort)0;

    public int ReadInt() => TryRead(4, out var span) ? BinaryPrimitives.ReadInt32LittleEndian(span) : 0;

    public uint ReadUInt() => TryRead(4, out var span) ? BinaryPrimitives.ReadUInt32LittleEndian(span) : 0;

    public long ReadLong() => TryRead(8, out var span) ? BinaryPrimitives.ReadInt64LittleEndian(span) : 0;

    public ulong ReadULong() => TryRead(8, out var span) ? BinaryPrimitives.ReadUInt64LittleEndian(span) : 0;

    public float ReadFloat() => TryRead(4, out var span) ? BinaryPrimitives.ReadSingleLittleEndian(span) : 0f;

    public double ReadDouble() => TryRead(8, out var span) ? BinaryPrimitives.ReadDoubleLittleEndian(span) : 0d;

    /// <summary>Reads a length-prefixed byte block, or an empty array if the message is malformed.</summary>
    public byte[] ReadBytes()
    {
        ushort count = ReadUShort();
        return TryRead(count, out var span) ? span.ToArray() : [];
    }

    /// <summary>Reads a length-prefixed UTF-8 string, or an empty string if the message is malformed.</summary>
    public string ReadString()
    {
        ushort count = ReadUShort();
        return TryRead(count, out var span) ? Encoding.UTF8.GetString(span) : string.Empty;
    }
}
