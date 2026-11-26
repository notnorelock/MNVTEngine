// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

namespace MNVTEngine.Audio;

/// <summary>
/// A mono copy of what the mixer sends to the speakers, for echo cancellation (docs/engine/voice.md): what a
/// microphone hears back from the speakers is this, delayed and filtered by the room. Costs nothing until
/// <see cref="Enabled"/>; then it keeps the newest <see cref="Capacity"/> samples until they are read.
/// The mixer writes on its rendering thread, the game reads on its own.
/// </summary>
public sealed class PlaybackTap(int sampleRate)
{
    private readonly Lock _lock = new();
    private readonly float[] _ring = new float[sampleRate]; // a second
    private int _start;
    private int _count;
    private volatile bool _enabled;

    public int SampleRate { get; } = sampleRate;

    public int Capacity => _ring.Length;

    /// <summary>Whether the mixer copies its output; turning it on or off empties the tap.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            Clear();
            _enabled = value;
        }
    }

    /// <summary>Samples waiting to be read.</summary>
    public int Available
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>Reads the oldest samples into <paramref name="mono"/>; returns how many there were.</summary>
    public int Read(Span<float> mono)
    {
        lock (_lock)
        {
            var n = Math.Min(mono.Length, _count);
            for (var i = 0; i < n; i++)
            {
                mono[i] = _ring[(_start + i) % _ring.Length];
            }

            (_start, _count) = ((_start + n) % _ring.Length, _count - n);
            return n;
        }
    }

    /// <summary>Drops the oldest samples so that at most <paramref name="keep"/> wait.</summary>
    public void Trim(int keep)
    {
        lock (_lock)
        {
            var drop = Math.Max(0, _count - Math.Max(0, keep));
            (_start, _count) = ((_start + drop) % _ring.Length, _count - drop);
        }
    }

    public void Clear() => Trim(0);

    /// <summary>Adds interleaved stereo output, mixed down to mono; past the capacity the oldest samples go.</summary>
    public void Write(ReadOnlySpan<float> stereo)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_lock)
        {
            for (var i = 0; i + 1 < stereo.Length; i += 2)
            {
                _ring[(_start + _count) % _ring.Length] = (stereo[i] + stereo[i + 1]) * 0.5f;
                if (_count < _ring.Length)
                {
                    _count++;
                }
                else
                {
                    _start = (_start + 1) % _ring.Length;
                }
            }
        }
    }
}
