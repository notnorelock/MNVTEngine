// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

namespace MNVTEngine.Audio.Voice.Tests;

/// <summary>Synthetic rooms: noise from the speakers comes back to the microphone delayed and filtered.</summary>
public sealed class EchoCancellerTests
{
    private const int Frame = VoiceFormat.FrameSamples;

    /// <summary>The speakers-to-microphone path: 30 ms of delay, then 10 ms of decaying reflections.</summary>
    private static float[] Room(Random random)
    {
        var path = new float[(VoiceFormat.SampleRate * 30 / 1000) + 480];
        for (var i = VoiceFormat.SampleRate * 30 / 1000; i < path.Length; i++)
        {
            path[i] = (float)((random.NextDouble() - 0.5) * 0.6 * Math.Exp(-(i - 1440) / 120.0));
        }

        return path;
    }

    private static float[] Noise(Random random, int samples, float level) =>
        [.. Enumerable.Range(0, samples).Select(_ => (float)(random.NextDouble() - 0.5) * 2f * level)];

    private static float[] Through(float[] signal, float[] path)
    {
        var result = new float[signal.Length];
        for (var t = 0; t < signal.Length; t++)
        {
            double sum = 0;
            for (var k = 0; k < path.Length && k <= t; k++)
            {
                sum += path[k] * signal[t - k];
            }

            result[t] = (float)sum;
        }

        return result;
    }

    /// <summary>Runs the canceller over whole frames; returns its output (aligned back by its latency) as floats.</summary>
    private static float[] Run(EchoCanceller canceller, float[] microphone, float[] playback)
    {
        var output = new float[microphone.Length];
        var (mic, outFrame) = (new short[Frame], new short[Frame]);
        for (var start = 0; start + Frame <= microphone.Length; start += Frame)
        {
            for (var i = 0; i < Frame; i++)
            {
                mic[i] = (short)Math.Clamp(microphone[start + i] * 32768f, short.MinValue, short.MaxValue);
            }

            canceller.Process(mic, playback.AsSpan(start, Frame), outFrame);
            for (var i = 0; i < Frame; i++)
            {
                var at = start + i - EchoCanceller.LatencySamples;
                if (at >= 0)
                {
                    output[at] = outFrame[i] / 32768f;
                }
            }
        }

        return output;
    }

    private static double Power(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return sum / samples.Length;
    }

    private static double Db(double ratio) => 10 * Math.Log10(ratio);

    [Fact]
    public void TheSpeakersEcho_IsLearned_AndRemoved()
    {
        var random = new Random(7);
        var far = Noise(random, VoiceFormat.SampleRate * 4, 0.3f);
        var mic = Through(far, Room(random));
        var canceller = new EchoCanceller { Suppression = false };

        var output = Run(canceller, mic, far);

        var lastTwoSeconds = (VoiceFormat.SampleRate * 2)..((VoiceFormat.SampleRate * 4) - Frame);
        var reduction = Db(Power(mic.AsSpan()[lastTwoSeconds]) / Power(output.AsSpan()[lastTwoSeconds]));
        Assert.True(reduction > 15, $"the filter alone took the echo down {reduction:F1} dB");
    }

    [Fact]
    public void Suppression_TakesTheRestFurtherDown()
    {
        var random = new Random(7);
        var far = Noise(random, VoiceFormat.SampleRate * 4, 0.3f);
        var mic = Through(far, Room(random));

        var output = Run(new EchoCanceller(), mic, far);

        var tail = (VoiceFormat.SampleRate * 2)..((VoiceFormat.SampleRate * 4) - Frame);
        Assert.True(Db(Power(mic.AsSpan()[tail]) / Power(output.AsSpan()[tail])) > 25);
    }

    [Fact]
    public void WithSilentSpeakers_TheMicrophonePassesUnchanged()
    {
        var random = new Random(3);
        var near = Noise(random, Frame * 20, 0.2f);
        var silence = new float[near.Length];

        var output = Run(new EchoCanceller(), near, silence);

        for (var i = 0; i < near.Length - Frame; i++)
        {
            Assert.Equal(near[i], output[i], 1.5f / 32768f);
        }
    }

    [Fact]
    public void ThePlayersVoice_SurvivesTalkingOverTheSpeakers()
    {
        var random = new Random(11);
        var seconds = 5;
        var far = Noise(random, VoiceFormat.SampleRate * seconds, 0.3f);
        var echo = Through(far, Room(random));
        var near = new float[far.Length];
        var talkFrom = VoiceFormat.SampleRate * 3; // after the filter learned the room
        Noise(random, far.Length - talkFrom, 0.2f).CopyTo(near, talkFrom);
        var mic = echo.Zip(near, (e, n) => e + n).ToArray();

        var output = Run(new EchoCanceller(), mic, far);

        var talk = (talkFrom + Frame)..(far.Length - Frame);
        var leftover = output.AsSpan()[talk].ToArray().Zip(near.AsSpan()[talk].ToArray(), (o, n) => o - n).ToArray();
        Assert.True(Db(Power(near.AsSpan()[talk]) / Power(leftover)) > 10, "the voice came through mostly as it was spoken");
    }

    [Fact]
    public void FramesOfTheWrongSize_AreRefused()
    {
        var canceller = new EchoCanceller();
        Assert.Throws<ArgumentOutOfRangeException>(() => canceller.Process(new short[10], new float[Frame], new short[Frame]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EchoCanceller(tailMilliseconds: 5));
    }
}
