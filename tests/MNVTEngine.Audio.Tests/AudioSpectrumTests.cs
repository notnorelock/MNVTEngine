// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Audio;

namespace MNVTEngine.Audio.Tests;

public sealed class AudioSpectrumTests
{
    private static float[] Sine(float hz, float amplitude, int count = 2048, int rate = 48000) =>
        [.. Enumerable.Range(0, count).Select(i => amplitude * MathF.Sin(2 * MathF.PI * hz * i / rate))];

    [Fact]
    public void ASine_ShowsInItsBin_AtItsAmplitude()
    {
        var spectrum = new AudioSpectrum();
        spectrum.Analyze(Sine(1000f, 0.5f), 48000);
        var peak = spectrum.Amplitudes.ToArray().Select((a, i) => (a, i)).Max();
        Assert.Equal(1000f, peak.i * spectrum.BinHz, 12f); // bins are 23.4 Hz wide
        Assert.InRange(peak.a, 0.4f, 0.55f);
        Assert.InRange(spectrum.Band(950f, 1050f), 0.9f, 1.2f); // the window spreads it over the neighbours too
        Assert.True(spectrum.Band(40f, 150f) < 0.01f, "no bass in a 1 kHz tone");
    }

    [Fact]
    public void Bass_AndTreble_AreTold_Apart()
    {
        var spectrum = new AudioSpectrum();
        var mix = Sine(80f, 0.3f).Zip(Sine(6000f, 0.3f), (a, b) => a + b).ToArray();
        spectrum.Analyze(mix, 48000);
        Assert.InRange(spectrum.Band(40f, 150f), 0.5f, 0.75f);
        Assert.InRange(spectrum.Band(5000f, 7000f), 0.5f, 0.75f);
        spectrum.Analyze(mix.AsSpan(0, 10), 48000); // fewer samples than the size: padded, no throw
        Assert.True(spectrum.Band(0f, 24000f) < 0.05f);
    }

    [Fact]
    public void Fft_InverseUndoesForward_AndRefusesOtherSizes()
    {
        var fft = new Fft(64);
        float[] re = [.. Enumerable.Range(0, 64).Select(i => MathF.Cos(i * 0.3f))], im = new float[64];
        var original = re.ToArray();
        fft.Forward(re, im);
        fft.Inverse(re, im);
        Assert.All(re.Zip(original), p => Assert.Equal(p.Second, p.First, 1e-4f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fft(100));
        Assert.Throws<ArgumentException>(() => fft.Forward(new float[32], new float[32]));
    }

    [Fact]
    public void ReadBus_GivesWhatTheBusPlayed()
    {
        using var device = new NullAudioDevice();
        device.SetBuses([new("Master", "", 1f, false, []), new("Music", "Master", 1f, false, [])]);
        using var buffer = device.CreateBuffer(new AudioClipData([.. Sine(80f, 0.5f, 48000).Select(s => (short)(s * 32767))], 1, 48000));
        var voice = device.CreateVoice();
        (voice.Bus, voice.Loop) = ("Music", true);
        voice.Play(buffer);
        device.Update(0.2f);

        var samples = new float[2048];
        Assert.Equal(2048, device.ReadBus("Music", samples));
        var spectrum = new AudioSpectrum();
        spectrum.Analyze(samples, device.Playback.SampleRate);
        Assert.True(spectrum.Band(40f, 150f) > 0.2f, $"{spectrum.Band(40f, 150f)}");
        Assert.Equal(0, device.ReadBus("Nope", samples));
    }
}
