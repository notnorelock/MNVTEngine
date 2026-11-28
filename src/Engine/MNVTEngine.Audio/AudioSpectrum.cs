// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

namespace MNVTEngine.Audio;

/// <summary>
/// The frequency content of a stretch of audio: <see cref="Analyze"/> windows <see cref="Size"/> mono samples (Hann),
/// transforms them (<see cref="Fft"/>) and keeps each bin's amplitude; <see cref="Band"/> sums a frequency range, e.g.
/// the bass for a beat. With <see cref="IAudioDevice.ReadBus"/> it shows what a mixer bus plays now:
/// <code>
/// var read = device.ReadBus("Music", samples);
/// spectrum.Analyze(samples[..read], device.Playback.SampleRate);
/// var bass = spectrum.Band(40, 150);
/// </code>
/// Not thread-safe; it allocates nothing after construction.
/// </summary>
public sealed class AudioSpectrum
{
    private readonly Fft _fft;
    private readonly float[] _window, _re, _im, _amplitudes;
    private int _sampleRate = 1;

    /// <param name="size">Samples per analysis, a power of two: 2048 at 48 kHz is 43 ms and 23 Hz per bin.</param>
    public AudioSpectrum(int size = 2048)
    {
        _fft = new Fft(size);
        (_window, _re, _im, _amplitudes) = (new float[size], new float[size], new float[size], new float[size / 2]);
        for (var i = 0; i < size; i++)
        {
            _window[i] = 0.5f - (0.5f * MathF.Cos(2f * MathF.PI * i / (size - 1)));
        }
    }

    public int Size => _fft.Size;

    /// <summary>Each bin's amplitude after the last <see cref="Analyze"/> (a full-scale sine reads about 1 in its bin); bin i is i × rate / <see cref="Size"/> Hz.</summary>
    public ReadOnlySpan<float> Amplitudes => _amplitudes;

    /// <summary>Hz per bin at the last analysed rate.</summary>
    public float BinHz => _sampleRate / (float)Size;

    /// <summary>Analyses the newest <see cref="Size"/> of <paramref name="mono"/>; fewer are padded with silence in front.</summary>
    public void Analyze(ReadOnlySpan<float> mono, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        _sampleRate = sampleRate;
        var take = Math.Min(mono.Length, Size);
        var offset = Size - take;
        _re.AsSpan(0, offset).Clear();
        for (var i = 0; i < take; i++)
        {
            _re[offset + i] = mono[mono.Length - take + i] * _window[offset + i];
        }

        _im.AsSpan().Clear();
        _fft.Forward(_re, _im);
        var scale = 4f / Size; // the Hann window halves the amplitude, one side of the spectrum halves it again
        for (var i = 0; i < _amplitudes.Length; i++)
        {
            _amplitudes[i] = MathF.Sqrt((_re[i] * _re[i]) + (_im[i] * _im[i])) * scale;
        }
    }

    /// <summary>
    /// The summed amplitude of the bins from <paramref name="lowHz"/> up to <paramref name="highHz"/>: a sine inside
    /// the range reads about twice its amplitude (the window spreads it over neighbouring bins).
    /// </summary>
    public float Band(float lowHz, float highHz)
    {
        var from = Math.Clamp((int)MathF.Floor(lowHz / BinHz), 0, _amplitudes.Length - 1);
        var to = Math.Clamp((int)MathF.Ceiling(highHz / BinHz), from, _amplitudes.Length - 1);
        var sum = 0f;
        for (var i = from; i <= to; i++)
        {
            sum += _amplitudes[i];
        }

        return sum;
    }
}
