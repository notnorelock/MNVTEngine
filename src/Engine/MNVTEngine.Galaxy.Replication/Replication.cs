// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using MNVTEngine.Galaxy;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Galaxy.Replication;

/// <summary>Which parts of a transform a snapshot carries, and their values.</summary>
public readonly record struct TransformState(byte Flags, Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    public const byte PositionBit = 1;
    public const byte RotationBit = 2;
    public const byte ScaleBit = 4;
}

/// <summary>Wire format of the replication messages: object spawn, despawn and the per-tick transform snapshot. The
/// message id selects the kind; serialization is plain and self-describing. Replication uses its own message ids, so a
/// game is free to use any other ids for its own messages.</summary>
internal static class ReplicationProtocol
{
    public const int Spawn = 60_001;
    public const int Despawn = 60_002;
    public const int Snapshot = 60_003;
    public const int OwnerAssign = 60_004; // server -> owning client: you now own (or no longer own) an object
    public const int OwnerState = 60_005;  // owning client -> server: the transform of an object it owns

    public static void WriteVector(Message message, Vector3 value)
    {
        message.Write(value.X);
        message.Write(value.Y);
        message.Write(value.Z);
    }

    public static Vector3 ReadVector(Message message) => new(message.ReadFloat(), message.ReadFloat(), message.ReadFloat());

    public static void WriteState(Message message, byte flags, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        message.Write(flags);
        if ((flags & TransformState.PositionBit) != 0)
        {
            WriteVector(message, position);
        }

        if ((flags & TransformState.RotationBit) != 0)
        {
            message.Write(QuaternionCodec.Compress(rotation)); // 4 bytes instead of 16
        }

        if ((flags & TransformState.ScaleBit) != 0)
        {
            WriteVector(message, scale);
        }
    }

    public static TransformState ReadState(Message message)
    {
        byte flags = message.ReadByte();
        Vector3 position = (flags & TransformState.PositionBit) != 0 ? ReadVector(message) : default;
        Quaternion rotation = (flags & TransformState.RotationBit) != 0 ? QuaternionCodec.Decompress(message.ReadUInt()) : Quaternion.Identity;
        Vector3 scale = (flags & TransformState.ScaleBit) != 0 ? ReadVector(message) : Vector3.One;
        return new TransformState(flags, position, rotation, scale);
    }
}

/// <summary>
/// Compresses a unit quaternion to four bytes with the "smallest three" scheme: the largest-magnitude component is
/// dropped (reconstructed from the other three, since the quaternion is unit length) and its index stored in two bits,
/// and the remaining three are each quantized to ten bits over their range. A rotation costs 4 bytes instead of 16,
/// with error far below what gameplay notices. q and -q are the same rotation, so the dropped component is taken
/// positive.
/// </summary>
internal static class QuaternionCodec
{
    private const float Range = 0.70710677f; // 1 / sqrt(2): the largest the smaller components can be.

    public static uint Compress(Quaternion value)
    {
        float length = MathF.Sqrt((value.X * value.X) + (value.Y * value.Y) + (value.Z * value.Z) + (value.W * value.W));
        if (length < 1e-8f)
        {
            return Pack(3, Quantize(0), Quantize(0), Quantize(0)); // Degenerate: treat as identity.
        }

        Span<float> c = [value.X / length, value.Y / length, value.Z / length, value.W / length];
        int largest = 0;
        for (int i = 1; i < 4; i++)
        {
            if (MathF.Abs(c[i]) > MathF.Abs(c[largest]))
            {
                largest = i;
            }
        }

        float sign = c[largest] < 0f ? -1f : 1f;
        Span<int> quantized = stackalloc int[3];
        int next = 0;
        for (int i = 0; i < 4; i++)
        {
            if (i != largest)
            {
                quantized[next++] = Quantize(c[i] * sign);
            }
        }

        return Pack(largest, quantized[0], quantized[1], quantized[2]);
    }

    public static Quaternion Decompress(uint packed)
    {
        int largest = (int)((packed >> 30) & 0x3);
        float a = Dequantize((int)(packed & 0x3FF));
        float b = Dequantize((int)((packed >> 10) & 0x3FF));
        float cc = Dequantize((int)((packed >> 20) & 0x3FF));
        float reconstructed = MathF.Sqrt(MathF.Max(0f, 1f - ((a * a) + (b * b) + (cc * cc))));

        Span<float> smalls = [a, b, cc];
        Span<float> components = stackalloc float[4];
        int next = 0;
        for (int i = 0; i < 4; i++)
        {
            components[i] = i == largest ? reconstructed : smalls[next++];
        }

        return new Quaternion(components[0], components[1], components[2], components[3]);
    }

    private static uint Pack(int index, int a, int b, int c) =>
        (uint)((a & 0x3FF) | ((b & 0x3FF) << 10) | ((c & 0x3FF) << 20) | ((index & 0x3) << 30));

    private static int Quantize(float value)
    {
        float normalized = Math.Clamp(value / Range, -1f, 1f);
        return (int)MathF.Round(((normalized * 0.5f) + 0.5f) * 1023f);
    }

    private static float Dequantize(int quantized) => (((quantized / 1023f) * 2f) - 1f) * Range;
}

/// <summary>
/// A small buffer of received transform states that the client plays back slightly delayed, interpolating between the
/// two states that bracket the render time. This hides jitter and loss in the unreliable snapshot stream: a late or
/// missing snapshot just means interpolating across a wider gap, not a stutter.
/// </summary>
internal sealed class SnapshotInterpolator
{
    private const int Capacity = 32;
    private readonly List<(float Time, TransformState State)> _samples = [];

    public void Push(float time, TransformState state)
    {
        _samples.Add((time, state));
        if (_samples.Count > Capacity)
        {
            _samples.RemoveAt(0);
        }
    }

    public bool Sample(float renderTime, out TransformState state)
    {
        state = default;
        if (_samples.Count == 0)
        {
            return false;
        }

        if (_samples.Count == 1 || renderTime <= _samples[0].Time)
        {
            state = _samples[0].State;
            return true;
        }

        for (int i = 0; i < _samples.Count - 1; i++)
        {
            (float aTime, TransformState aState) = _samples[i];
            (float bTime, TransformState bState) = _samples[i + 1];
            if (renderTime >= aTime && renderTime <= bTime)
            {
                float t = bTime > aTime ? (renderTime - aTime) / (bTime - aTime) : 0f;
                state = new TransformState(
                    bState.Flags,
                    Vector3.Lerp(aState.Position, bState.Position, t),
                    Quaternion.Slerp(aState.Rotation, bState.Rotation, t),
                    Vector3.Lerp(aState.Scale, bState.Scale, t));
                return true;
            }
        }

        state = _samples[^1].State; // Ahead of the newest sample: hold it until a newer one arrives.
        return true;
    }

    public void Clear() => _samples.Clear();
}
