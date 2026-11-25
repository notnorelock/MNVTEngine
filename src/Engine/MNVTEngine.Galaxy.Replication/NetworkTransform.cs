// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using MNVTEngine.Galaxy.Replication;

namespace MNVTEngine;

/// <summary>
/// Replicates its GameObject's transform. On the authority (server) side it does nothing itself — the
/// <see cref="NetworkServer"/> reads the transform into each snapshot. On a remote (client) side it feeds incoming
/// snapshots into a buffer and plays them back a short delay behind, interpolating between them so motion is smooth
/// despite jitter and the loss of the unreliable snapshot stream.
/// </summary>
[ComponentId("MNVTEngine.NetworkTransform")]
public sealed class NetworkTransform : Component
{
    private readonly SnapshotInterpolator _interpolator = new();
    private NetworkObject? _networkObject;
    private TransformState _full;
    private bool _hasFull;

    /// <summary>Replicate world position.</summary>
    [SerializeField]
    public bool SyncPosition { get; set; } = true;

    /// <summary>Replicate world rotation.</summary>
    [SerializeField]
    public bool SyncRotation { get; set; } = true;

    /// <summary>Replicate local scale.</summary>
    [SerializeField]
    public bool SyncScale { get; set; }

    /// <summary>How far behind the newest snapshot the client plays back, in seconds, to absorb jitter and loss.</summary>
    [SerializeField]
    public float InterpolationDelay { get; set; } = 0.1f;

    /// <summary>Which transform parts this replicates, as snapshot flags.</summary>
    public byte Flags => (byte)(
        (SyncPosition ? TransformState.PositionBit : 0) |
        (SyncRotation ? TransformState.RotationBit : 0) |
        (SyncScale ? TransformState.ScaleBit : 0));

    public override void Awake() => _networkObject = GetComponent<NetworkObject>();

    /// <summary>Seeds the full transform from a spawn, so later per-field deltas have a complete base to merge onto.</summary>
    internal void SetInitialState(TransformState full)
    {
        _full = full;
        _hasFull = true;
    }

    /// <summary>Called by the client with each received snapshot. A snapshot carries only the fields that changed, so
    /// they are merged onto the last full state before interpolating (interpolation needs complete states).</summary>
    internal void ReceiveState(float time, TransformState delta)
    {
        _full = _hasFull ? Merge(_full, delta) : delta;
        _hasFull = true;
        _interpolator.Push(time, _full with { Flags = Flags });
    }

    private static TransformState Merge(TransformState full, TransformState delta) => new(
        full.Flags,
        (delta.Flags & TransformState.PositionBit) != 0 ? delta.Position : full.Position,
        (delta.Flags & TransformState.RotationBit) != 0 ? delta.Rotation : full.Rotation,
        (delta.Flags & TransformState.ScaleBit) != 0 ? delta.Scale : full.Scale);

    internal void ClearBuffer()
    {
        _interpolator.Clear();
        _hasFull = false;
    }

    public override void Update()
    {
        if ((_networkObject ??= GetComponent<NetworkObject>())?.Role != NetworkRole.Remote)
        {
            return;
        }

        if (_interpolator.Sample(Time.TimeSinceStart - InterpolationDelay, out TransformState state))
        {
            Apply(state);
        }
    }

    private void Apply(TransformState state)
    {
        if ((state.Flags & TransformState.PositionBit) != 0)
        {
            Transform.Position = state.Position;
        }

        if ((state.Flags & TransformState.RotationBit) != 0)
        {
            Transform.Rotation = state.Rotation;
        }

        if ((state.Flags & TransformState.ScaleBit) != 0)
        {
            Transform.LocalScale = state.Scale;
        }
    }
}
