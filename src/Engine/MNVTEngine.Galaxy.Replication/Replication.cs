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

    public static void WriteQuaternion(Message message, Quaternion value)
    {
        message.Write(value.X);
        message.Write(value.Y);
        message.Write(value.Z);
        message.Write(value.W);
    }

    public static Quaternion ReadQuaternion(Message message) => new(message.ReadFloat(), message.ReadFloat(), message.ReadFloat(), message.ReadFloat());

    public static void WriteState(Message message, byte flags, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        message.Write(flags);
        if ((flags & TransformState.PositionBit) != 0)
        {
            WriteVector(message, position);
        }

        if ((flags & TransformState.RotationBit) != 0)
        {
            WriteQuaternion(message, rotation);
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
        Quaternion rotation = (flags & TransformState.RotationBit) != 0 ? ReadQuaternion(message) : Quaternion.Identity;
        Vector3 scale = (flags & TransformState.ScaleBit) != 0 ? ReadVector(message) : Vector3.One;
        return new TransformState(flags, position, rotation, scale);
    }
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
