// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

namespace MNVTEngine.Audio.Voice;

/// <summary>
/// Removes the speakers' sound from the microphone (docs/engine/voice.md, "Echo cancellation"). Feed it each recorded
/// frame with the frame of <see cref="PlaybackTap"/> output read at the same time; it learns the path from the
/// speakers to the microphone (delay and room, up to <see cref="TailMilliseconds"/>) with a partitioned-block
/// frequency-domain NLMS filter, subtracts the echo it predicts and, with <see cref="Suppression"/>, turns down what
/// is left while only the speakers play. Adaptation slows down while the player talks over the speakers, so their
/// voice does not teach the filter. Output lags the input by <see cref="LatencySamples"/>. One microphone, one thread.
/// </summary>
public sealed class EchoCanceller
{
    /// <summary>Samples per filter block; the FFT is twice that.</summary>
    public const int BlockSamples = 256;

    /// <summary>How far the output lags the microphone (one block, 5.3 ms at 48 kHz).</summary>
    public const int LatencySamples = BlockSamples;

    private const int Size = BlockSamples * 2;
    private const float MaxStep = 0.5f;
    private const float StartStep = 0.3f;
    private const float MinStep = 0.02f;
    private const float FarActive = 1e-8f; // mean square of a far-end block (-80 dBFS) below which nothing is learned
    private const float MinGain = 0.1f; // the suppressor turns residual echo down by at most 20 dB
    private const float ResidualFloor = 0.01f; // the echo assumed left over at best (-20 dB of the estimate)

    private readonly int _partitions;
    private readonly Fft _fft = new(Size);
    private readonly float[][] _wRe, _wIm, _xRe, _xIm;
    private readonly float[] _power = new float[Size];
    private readonly float[] _farTime = new float[Size];
    private readonly float[] _re = new float[Size], _im = new float[Size];
    private readonly float[] _farIn = new float[BlockSamples], _nearIn = new float[BlockSamples];
    private readonly float[] _out = new float[VoiceFormat.FrameSamples + (BlockSamples * 2)]; // holds LatencySamples - pending input, plus a frame
    private readonly float[] _echo = new float[BlockSamples], _error = new float[BlockSamples];
    private int _inCount, _outCount;
    private int _head, _constrain, _adaptedBlocks;
    private float _near, _estimate, _residual, _correlation, _estimatePower, _gain = 1f, _outPower;

    /// <param name="tailMilliseconds">The longest delay plus room echo it can learn (at <see cref="VoiceFormat.SampleRate"/>).</param>
    public EchoCanceller(int tailMilliseconds = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tailMilliseconds, 10);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tailMilliseconds, 1000);
        TailMilliseconds = tailMilliseconds;
        _partitions = (int)Math.Ceiling(tailMilliseconds * (VoiceFormat.SampleRate / 1000.0) / BlockSamples);
        (_wRe, _wIm, _xRe, _xIm) = (Matrix(), Matrix(), Matrix(), Matrix());
        _outCount = LatencySamples;
    }

    public int TailMilliseconds { get; }

    /// <summary>Turns down the echo the filter leaves while the speakers play and the player does not talk.</summary>
    public bool Suppression { get; set; } = true;

    /// <summary>How much quieter the output is than the microphone, smoothed (echo return loss enhancement); 0 before any echo.</summary>
    public float EchoReductionDb => _near > 1e-12f && _outPower > 1e-12f ? 10f * MathF.Log10(_near / _outPower) : 0f;

    /// <summary>Cleans one frame (<see cref="VoiceFormat.FrameSamples"/> each).</summary>
    /// <param name="microphone">What was recorded.</param>
    /// <param name="playback">The speakers' output read alongside it (<see cref="PlaybackTap"/>, -1 to 1).</param>
    /// <param name="output">The microphone without the echo, <see cref="LatencySamples"/> later.</param>
    public void Process(ReadOnlySpan<short> microphone, ReadOnlySpan<float> playback, Span<short> output)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(microphone.Length, VoiceFormat.FrameSamples);
        ArgumentOutOfRangeException.ThrowIfNotEqual(playback.Length, VoiceFormat.FrameSamples);
        ArgumentOutOfRangeException.ThrowIfNotEqual(output.Length, VoiceFormat.FrameSamples);
        for (var i = 0; i < microphone.Length; i++)
        {
            _nearIn[_inCount] = microphone[i] / 32768f;
            _farIn[_inCount] = float.IsFinite(playback[i]) ? Math.Clamp(playback[i], -1f, 1f) : 0f;
            if (++_inCount == BlockSamples)
            {
                Block();
                _inCount = 0;
            }
        }

        for (var i = 0; i < output.Length; i++)
        {
            output[i] = (short)Math.Clamp(MathF.Round(_out[i] * 32768f), short.MinValue, short.MaxValue);
        }

        _outCount -= output.Length;
        Array.Copy(_out, output.Length, _out, 0, _outCount);
    }

    /// <summary>Forgets the learned echo path (after the output or microphone device changes).</summary>
    public void Reset()
    {
        foreach (var row in _wRe.Concat(_wIm).Concat(_xRe).Concat(_xIm))
        {
            Array.Clear(row);
        }

        Array.Clear(_power);
        Array.Clear(_farTime);
        (_adaptedBlocks, _near, _estimate, _residual, _correlation, _estimatePower, _gain, _outPower) = (0, 0, 0, 0, 0, 0, 1, 0);
    }

    private float[][] Matrix() => [.. Enumerable.Range(0, _partitions).Select(_ => new float[Size])];

    private void Block()
    {
        const int b = BlockSamples;

        // The far end's last two blocks, in the frequency domain, become the newest partition's input.
        Array.Copy(_farTime, b, _farTime, 0, b);
        Array.Copy(_farIn, 0, _farTime, b, b);
        _head = (_head + _partitions - 1) % _partitions;
        var (xr, xi) = (_xRe[_head], _xIm[_head]);
        Array.Copy(_farTime, xr, Size);
        Array.Clear(xi);
        _fft.Forward(xr, xi);

        // The predicted echo: every partition's filter on its block of the past, overlap-save (the last half is valid).
        Array.Clear(_re);
        Array.Clear(_im);
        for (var p = 0; p < _partitions; p++)
        {
            var (wr, wi, pr, pi) = (_wRe[p], _wIm[p], _xRe[(_head + p) % _partitions], _xIm[(_head + p) % _partitions]);
            for (var k = 0; k < Size; k++)
            {
                _re[k] += (wr[k] * pr[k]) - (wi[k] * pi[k]);
                _im[k] += (wr[k] * pi[k]) + (wi[k] * pr[k]);
            }
        }

        _fft.Inverse(_re, _im);
        float near = 0, echo = 0, error = 0, far = 0, cross = 0;
        for (var i = 0; i < b; i++)
        {
            _echo[i] = _re[b + i];
            _error[i] = _nearIn[i] - _echo[i];
            (near, echo, error, far, cross) = (near + (_nearIn[i] * _nearIn[i]), echo + (_echo[i] * _echo[i]), error + (_error[i] * _error[i]), far + (_farIn[i] * _farIn[i]), cross + (_error[i] * _echo[i]));
        }

        (near, echo, error, far, cross) = (near / b, echo / b, error / b, far / b, cross / b);
        const float s = 0.1f;
        (_near, _estimate, _residual) = (Smooth(_near, near, s), Smooth(_estimate, echo, s), Smooth(_residual, error, s));
        (_correlation, _estimatePower) = (Smooth(_correlation, cross, 0.05f), Smooth(_estimatePower, echo, 0.05f));

        var farActive = far > FarActive;
        if (_residual > 4f * _near && _near > 1e-10f)
        {
            Reset(); // diverged: the prediction makes the microphone louder
        }
        else if (farActive)
        {
            Adapt();
        }

        // Never louder than the microphone: a block the filter got wrong passes as it was.
        var useError = error <= near;
        var target = 1f;
        if (Suppression && farActive && error > 1e-12f)
        {
            var leak = _estimatePower > 1e-12f ? Math.Clamp(_correlation / _estimatePower, 0f, 1f) : 0f;
            target = Math.Clamp(1f - ((leak * leak) + ResidualFloor) * echo / error, MinGain, 1f);
        }

        var from = _gain;
        _gain = target < _gain ? Smooth(_gain, target, 0.5f) : Smooth(_gain, target, 0.3f);
        float outPower = 0;
        for (var i = 0; i < b; i++)
        {
            var sample = (useError ? _error[i] : _nearIn[i]) * (from + ((_gain - from) * (i + 1) / b)); // ramped: no clicks
            _out[_outCount++] = sample;
            outPower += sample * sample;
        }

        _outPower = Smooth(_outPower, outPower / b, s);
    }

    private void Adapt()
    {
        // The step: fixed while learning first, then larger the more of the microphone the prediction explains, so
        // the player talking over the speakers (a large error the prediction does not explain) barely moves it.
        var step = _adaptedBlocks < VoiceFormat.SampleRate / BlockSamples ? StartStep : Math.Clamp(MaxStep * _estimate / (_residual + 1e-12f), MinStep, MaxStep);
        if (_residual > _near)
        {
            return; // the prediction hurts: wait for it to settle or for the reset
        }

        _adaptedBlocks++;
        Array.Clear(_re, 0, BlockSamples);
        Array.Copy(_error, 0, _re, BlockSamples, BlockSamples);
        Array.Clear(_im);
        _fft.Forward(_re, _im);

        var (nr, ni) = (_xRe[_head], _xIm[_head]);
        float mean = 0;
        for (var k = 0; k < Size; k++)
        {
            _power[k] = Smooth(_power[k], (nr[k] * nr[k]) + (ni[k] * ni[k]), 0.2f);
            mean += _power[k];
        }

        var regular = (mean / Size * 0.01f) + 1e-10f;
        for (var p = 0; p < _partitions; p++)
        {
            var (wr, wi, pr, pi) = (_wRe[p], _wIm[p], _xRe[(_head + p) % _partitions], _xIm[(_head + p) % _partitions]);
            for (var k = 0; k < Size; k++)
            {
                var mu = step / (_power[k] + regular);
                wr[k] += mu * ((pr[k] * _re[k]) + (pi[k] * _im[k])); // conj(X) * E
                wi[k] += mu * ((pr[k] * _im[k]) - (pi[k] * _re[k]));
            }
        }

        // Keep one partition (in turn) a true block-long filter: its second half in time must stay zero.
        var (cr, ci) = (_wRe[_constrain], _wIm[_constrain]);
        _fft.Inverse(cr, ci);
        Array.Clear(cr, BlockSamples, BlockSamples);
        Array.Clear(ci);
        _fft.Forward(cr, ci);
        _constrain = (_constrain + 1) % _partitions;
    }

    private static float Smooth(float current, float value, float rate) => current + ((value - current) * rate);

    /// <summary>In-place radix-2 complex FFT of a fixed power-of-two size.</summary>
    private sealed class Fft
    {
        private readonly int _size;
        private readonly float[] _cos, _sin;
        private readonly int[] _reverse;

        public Fft(int size)
        {
            _size = size;
            (_cos, _sin) = (new float[size / 2], new float[size / 2]);
            for (var i = 0; i < size / 2; i++)
            {
                (_sin[i], _cos[i]) = MathF.SinCos(-2f * MathF.PI * i / size);
            }

            var bits = (int)Math.Log2(size);
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

        public void Forward(float[] re, float[] im) => Run(re, im, 1f);

        /// <summary>The inverse, scaled by 1/size.</summary>
        public void Inverse(float[] re, float[] im)
        {
            Run(re, im, -1f);
            var scale = 1f / _size;
            for (var i = 0; i < _size; i++)
            {
                (re[i], im[i]) = (re[i] * scale, im[i] * scale);
            }
        }

        private void Run(float[] re, float[] im, float direction)
        {
            for (var i = 0; i < _size; i++)
            {
                var j = _reverse[i];
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (var length = 2; length <= _size; length <<= 1)
            {
                var half = length / 2;
                var stride = _size / length;
                for (var start = 0; start < _size; start += length)
                {
                    for (var k = 0; k < half; k++)
                    {
                        var (c, s) = (_cos[k * stride], _sin[k * stride] * direction);
                        var (a, bIndex) = (start + k, start + k + half);
                        var tr = (re[bIndex] * c) - (im[bIndex] * s);
                        var ti = (re[bIndex] * s) + (im[bIndex] * c);
                        (re[bIndex], im[bIndex]) = (re[a] - tr, im[a] - ti);
                        (re[a], im[a]) = (re[a] + tr, im[a] + ti);
                    }
                }
            }
        }
    }
}
