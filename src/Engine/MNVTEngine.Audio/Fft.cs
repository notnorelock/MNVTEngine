// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

namespace MNVTEngine.Audio;

/// <summary>
/// An in-place radix-2 complex FFT of a fixed power-of-two size, its twiddles and bit reversal computed once. (Voice
/// keeps its own copy: it may not depend on this module.)
/// </summary>
public sealed class Fft
{
    private readonly float[] _cos, _sin;
    private readonly int[] _reverse;

    /// <exception cref="ArgumentOutOfRangeException">Not a power of two of at least 2.</exception>
    public Fft(int size)
    {
        if (size < 2 || !int.IsPow2(size))
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "The FFT size must be a power of two.");
        }

        Size = size;
        (_cos, _sin) = (new float[size / 2], new float[size / 2]);
        for (var i = 0; i < size / 2; i++)
        {
            (_sin[i], _cos[i]) = MathF.SinCos(-2f * MathF.PI * i / size);
        }

        var bits = int.Log2(size);
        _reverse = new int[size];
        for (var i = 0; i < size; i++)
        {
            var r = 0;
            for (var bit = 0; bit < bits; bit++)
            {
                r |= ((i >> bit) & 1) << (bits - 1 - bit);
            }

            _reverse[i] = r;
        }
    }

    public int Size { get; }

    /// <summary>Transforms <paramref name="re"/> and <paramref name="im"/> (each <see cref="Size"/> long) in place.</summary>
    public void Forward(Span<float> re, Span<float> im) => Run(re, im, 1f);

    /// <summary>The inverse, scaled by 1/<see cref="Size"/>.</summary>
    public void Inverse(Span<float> re, Span<float> im)
    {
        Run(re, im, -1f);
        var scale = 1f / Size;
        for (var i = 0; i < Size; i++)
        {
            (re[i], im[i]) = (re[i] * scale, im[i] * scale);
        }
    }

    private void Run(Span<float> re, Span<float> im, float direction)
    {
        if (re.Length != Size || im.Length != Size)
        {
            throw new ArgumentException($"The FFT takes {Size} values in each part.");
        }

        for (var i = 0; i < Size; i++)
        {
            var j = _reverse[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var length = 2; length <= Size; length <<= 1)
        {
            var (half, stride) = (length / 2, Size / length);
            for (var start = 0; start < Size; start += length)
            {
                for (var k = 0; k < half; k++)
                {
                    var (c, s) = (_cos[k * stride], _sin[k * stride] * direction);
                    var (a, b) = (start + k, start + k + half);
                    var tr = (re[b] * c) - (im[b] * s);
                    var ti = (re[b] * s) + (im[b] * c);
                    (re[b], im[b]) = (re[a] - tr, im[a] - ti);
                    (re[a], im[a]) = (re[a] + tr, im[a] + ti);
                }
            }
        }
    }
}
