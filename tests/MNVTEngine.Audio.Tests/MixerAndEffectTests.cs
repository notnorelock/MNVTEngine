using MNVTEngine.Audio;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;

namespace MNVTEngine.Audio.Tests;

public sealed class AudioEffectTests
{
    private const int Rate = 48000;

    private static float[] Sine(float hz, float seconds = 0.5f, float amplitude = 0.5f)
    {
        var frames = (int)(Rate * seconds);
        var stereo = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            stereo[i * 2] = stereo[(i * 2) + 1] = amplitude * MathF.Sin(2 * MathF.PI * hz * i / Rate);
        }

        return stereo;
    }

    /// <summary>Runs an effect over a signal in 256-frame blocks, as a bus would.</summary>
    private static float[] Run(string type, float[] input, params (string Name, float Value)[] parameters)
    {
        var definition = AudioEffects.Find(type)!;
        var settings = definition.CreateSettings();
        foreach (var (name, value) in parameters)
        {
            settings.Parameters[name] = value;
        }

        var effect = definition.Create(Rate);
        effect.Configure(settings, definition);
        var output = (float[])input.Clone();
        for (var offset = 0; offset < output.Length; offset += 512)
        {
            effect.Process(output.AsSpan(offset, Math.Min(512, output.Length - offset)));
        }

        return output;
    }

    /// <summary>Peak over the second half (filters and envelopes have settled).</summary>
    private static float SettledPeak(float[] stereo) => stereo.Skip(stereo.Length / 2).Max(Math.Abs);

    [Fact]
    public void Filters_PassTheirBand_AndCutTheOther()
    {
        Assert.True(SettledPeak(Run(AudioEffects.LowPass, Sine(100f), ("cutoff", 1000f))) > 0.45f);
        Assert.True(SettledPeak(Run(AudioEffects.LowPass, Sine(8000f), ("cutoff", 200f))) < 0.01f);
        Assert.True(SettledPeak(Run(AudioEffects.HighPass, Sine(8000f), ("cutoff", 500f))) > 0.45f);
        Assert.True(SettledPeak(Run(AudioEffects.HighPass, Sine(50f), ("cutoff", 2000f))) < 0.01f);
    }

    [Fact]
    public void Eq_BoostsAndCutsItsBands()
    {
        Assert.Equal(0.5f * 2f, SettledPeak(Run(AudioEffects.Eq3, Sine(40f), ("low", 6f))), 1); // +6 dB is about x2
        Assert.True(SettledPeak(Run(AudioEffects.Eq3, Sine(12000f), ("high", -24f))) < 0.05f);
        Assert.Equal(0.5f, SettledPeak(Run(AudioEffects.Eq3, Sine(1000f))), 2); // flat when every band is 0 dB
    }

    [Fact]
    public void Compressor_ReducesLoudSignals_AndLeavesQuietOnes()
    {
        Assert.True(SettledPeak(Run(AudioEffects.Compressor, Sine(200f, amplitude: 0.9f), ("threshold", -24f), ("ratio", 10f))) < 0.3f);
        Assert.Equal(0.02f, SettledPeak(Run(AudioEffects.Compressor, Sine(200f, amplitude: 0.02f), ("threshold", -24f), ("ratio", 10f))), 3);
    }

    [Fact]
    public void Delay_RepeatsAnImpulse_AfterItsTime()
    {
        var impulse = new float[Rate * 2];
        impulse[0] = impulse[1] = 1f;
        var output = Run(AudioEffects.Delay, impulse, ("time", 100f), ("feedback", 0.5f), ("mix", 1f));
        var echo = Rate / 10;
        Assert.Equal(1f, output[echo * 2], 3);            // the first repeat, at 100 ms
        Assert.Equal(0.5f, output[echo * 2 * 2], 3);      // the second, fed back at half
        Assert.Equal(0f, output[(echo / 2) * 2]);         // nothing in between
    }

    [Fact]
    public void Reverb_LeavesATail()
    {
        var impulse = new float[Rate * 2];
        impulse[0] = impulse[1] = 1f;
        var output = Run(AudioEffects.Reverb, impulse, ("mix", 1f), ("size", 0.9f));
        var tail = output.Skip(Rate / 5 * 2).Take(Rate / 5 * 2).Max(Math.Abs); // 200 to 400 ms later
        Assert.True(tail > 0.001f, $"tail {tail}");
        Assert.All(output, s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void Saturator_StaysBounded_AndChorusChangesTheSignal()
    {
        Assert.True(SettledPeak(Run(AudioEffects.Distortion, Sine(200f, amplitude: 1f), ("drive", 36f), ("output", -6f))) <= 0.502f);
        var dry = Sine(300f);
        var chorus = Run(AudioEffects.Chorus, dry, ("mix", 1f), ("depth", 5f));
        Assert.True(dry.Zip(chorus).Max(p => Math.Abs(p.First - p.Second)) > 0.05f);
    }

    [Fact]
    public void Parameters_AreClamped_AndMissingOnesUseDefaults()
    {
        var definition = AudioEffects.Find("LOWPASS")!; // ids are case-insensitive
        var settings = new AudioEffectSettings { Type = definition.Id, Parameters = { ["cutoff"] = 1e9f, ["resonance"] = float.NaN } };
        Assert.Equal(20000f, settings.Get(definition.Parameter("cutoff")!));
        Assert.Equal(0.707f, settings.Get(definition.Parameter("resonance")!));
        Assert.All(AudioEffects.All, d => Assert.All(d.Parameters, p => Assert.InRange(p.Default, p.Min, p.Max)));
    }
}

public sealed class MixingDeviceTests
{
    private static AudioClipData Tone(int channels = 1, float seconds = 1f) =>
        new([.. Enumerable.Range(0, (int)(48000 * seconds) * channels).Select(i => (short)(MathF.Sin(i / channels * 2 * MathF.PI * 440 / 48000) * 16384))], channels, 48000);

    private static float Peak(MixingAudioDevice device, string bus, out AudioMeter meter)
    {
        Assert.True(device.TryGetMeter(bus, out meter));
        return Math.Max(meter.PeakLeft, meter.PeakRight);
    }

    [Fact]
    public void SpatialVoices_PanTowardsTheirSide_AndFadeWithDistance()
    {
        using var device = new NullAudioDevice();
        using var buffer = device.CreateBuffer(Tone());
        var voice = device.CreateVoice();
        (voice.Spatial, voice.ReferenceDistance, voice.MaxDistance, voice.Loop) = (true, 1f, 100f, true);
        voice.Position = new Vector2(5f, 0f);
        voice.Play(buffer);
        device.Update(0.1f);
        Peak(device, "Master", out var right);
        Assert.True(right.PeakRight > right.PeakLeft * 10f, $"{right}");

        voice.Position = new Vector2(-50f, 0f);
        device.Update(0.1f);
        Peak(device, "Master", out var far);
        Assert.True(far.PeakLeft > far.PeakRight * 10f, $"{far}");
        Assert.True(far.PeakLeft < right.PeakRight / 5f); // 50 units away against 5
    }

    [Fact]
    public void Buses_ChainToMaster_WithInsertsBeforeTheFader_AndCycleSafeParents()
    {
        using var device = new NullAudioDevice();
        using var buffer = device.CreateBuffer(Tone());
        device.SetBuses(
        [
            new("Master", "", 1f, false, []),
            new("Music", "Group", 0.5f, false, [new AudioEffectSettings { Type = AudioEffects.LowPass, Parameters = { ["cutoff"] = 100f } }]),
            new("Group", "Music", 1f, false, [new AudioEffectSettings { Type = "unknown-effect" }]), // a cycle, and an effect type nobody knows
        ]);
        var voice = device.CreateVoice();
        (voice.Bus, voice.Loop) = ("Music", true);
        voice.Play(buffer);
        device.Update(0.2f);
        var music = Peak(device, "Music", out _);
        Assert.True(music < 0.05f, $"440 Hz through a 100 Hz low-pass then a 0.5 fader: {music}");
        Assert.True(Peak(device, "Master", out _) > 0f); // the cycle was broken: the sound still reaches Master
        Assert.False(device.TryGetMeter("Nope", out _));
    }

    [Fact]
    public void Meters_HoldThePeak_AndReportClipping_UntilReset()
    {
        using var device = new NullAudioDevice();
        using var buffer = device.CreateBuffer(new AudioClipData([.. Enumerable.Repeat(short.MaxValue, 4800)], 1, 48000));
        var voice = device.CreateVoice();
        voice.Volume = 2f;
        voice.Play(buffer);
        device.Update(0.5f); // plays 0.1 s, then silence fills the meter window
        Peak(device, "Master", out var meter);
        Assert.Equal(0f, Math.Max(meter.PeakLeft, meter.PeakRight));
        Assert.True(meter.Clipped);
        Assert.True(meter.HoldPeak >= 1f);
        device.ResetMeter("Master");
        Peak(device, "Master", out meter);
        Assert.False(meter.Clipped);
        Assert.Equal(0f, meter.HoldPeak);
        Assert.Equal(VoiceState.Stopped, voice.State); // played to its end in simulated time
    }

    [Fact]
    public void StreamingVoices_AreResampled_AndMixedOnTheirBus()
    {
        using var device = new NullAudioDevice();
        device.SetBuses([new("Browser", "", 0.5f, false, [])]);
        using var stream = device.CreateStreamingVoice(2, 24000);
        stream.Bus = "Browser";
        stream.Queue([.. Enumerable.Repeat((short)16384, 24000 * 2)]); // a second of constant 0.5 at half the output rate
        device.Update(0.25f);
        Assert.Equal(0.75f, stream.QueuedSeconds, 2);
        Assert.Equal(0.25f, Peak(device, "Browser", out _), 2);
    }

    [Fact]
    public void MixerEffects_SurviveSerialization_AndReachTheDevice()
    {
        var scene = new Scene();
        var mixer = scene.CreateGameObject("Mixer").AddComponent<AudioMixer>();
        var reverb = AudioEffects.Find(AudioEffects.Reverb)!.CreateSettings();
        reverb.Parameters["size"] = 0.25f;
        mixer.Groups[0].Effects.Add(reverb);
        mixer.MasterEffects.Add(new AudioEffectSettings { Type = AudioEffects.Compressor, Enabled = false });
        var serializer = new SceneSerializer(AudioComponents.Register(ComponentRegistry.CreateDefault()));
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene.Find("Mixer")!.GetComponent<AudioMixer>()!;
        Assert.Equal(0.25f, copy.Groups[0].Effects.Single().Parameters["size"]);
        Assert.False(copy.MasterEffects.Single().Enabled);
        Assert.Equal(mixer.LayoutFingerprint(), copy.LayoutFingerprint());
        copy.Groups[0].Effects[0].Parameters["size"] = 0.5f;
        Assert.NotEqual(mixer.LayoutFingerprint(), copy.LayoutFingerprint()); // a parameter edit reaches the device
        Assert.Equal(["Master", "Music", "Effects"], copy.BusLayout().Select(b => b.Name));
    }
}
