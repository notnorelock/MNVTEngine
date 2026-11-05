using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Audio.OpenAL;
using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;

namespace MNVTEngine.Audio.Tests;

public class AudioTests
{
    private static readonly AssetRef Beep = new(Guid.Parse("cccccccc-0000-4000-8000-000000000001"));
    private static readonly AssetRef Bad = new(Guid.Parse("cccccccc-0000-4000-8000-000000000002"));
    private static long _frame;

    /// <summary>A WAV file with the given encoding: format 1 = PCM, 3 = IEEE float.</summary>
    internal static byte[] Wav(int channels, int rate, int bits, int format, byte[] data)
    {
        var bytes = new byte[44 + data.Length];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 36 + data.Length);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), (short)format);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), rate * channels * bits / 8);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), (short)(channels * bits / 8));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), (short)bits);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), data.Length);
        data.CopyTo(bytes, 44);
        return bytes;
    }

    /// <summary>Mono 16-bit sine.</summary>
    internal static byte[] Sine(float seconds, int rate = 22050, float hz = 440f)
    {
        var count = (int)(seconds * rate);
        var data = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), (short)(MathF.Sin(i * hz * 2 * MathF.PI / rate) * 12000));
        }

        return Wav(1, rate, 16, 1, data);
    }

    [Fact]
    public void Decoder_ReadsPcmAndFloatWav_AndRejectsUnknownData()
    {
        var pcm16 = AudioDecoder.Decode(Wav(2, 8000, 16, 1, [0x00, 0x40, 0x00, 0xC0]), "a.wav");
        Assert.Equal((2, 8000), (pcm16.Channels, pcm16.SampleRate));
        Assert.Equal(new short[] { 0x4000, unchecked((short)0xC000) }, pcm16.Samples);

        var pcm8 = AudioDecoder.Decode(Wav(1, 8000, 8, 1, [128, 255, 0]), "b.wav");
        Assert.Equal(new short[] { 0, 127 << 8, -128 << 8 }, pcm8.Samples);

        var f32 = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(f32, 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(f32.AsSpan(4), -2f); // clipped
        Assert.Equal(new short[] { 16383, -32767 }, AudioDecoder.Decode(Wav(1, 8000, 32, 3, f32), "c.wav").Samples);

        Assert.Equal(1f, AudioDecoder.Decode(Sine(1f, 8000), "d.wav").Duration, 3);
        Assert.Throws<InvalidDataException>(() => AudioDecoder.Decode("not audio at all"u8.ToArray(), "e.txt"));
        Assert.Throws<InvalidDataException>(() => AudioDecoder.Decode(Wav(6, 8000, 16, 1, [0, 0]), "surround.wav"));
        Assert.Throws<InvalidDataException>(() => AudioDecoder.Decode([.. "OggS"u8, .. new byte[40]], "broken.ogg"));
    }

    private static (Scene Scene, NullAudioDevice Device, AudioSystem System, Func<int> Reads) NewScene()
    {
        var scene = new Scene { RethrowScriptExceptions = true };
        var device = new NullAudioDevice();
        var reads = 0;
        var system = AudioSystem.Attach(scene, device, clip =>
        {
            reads++;
            return clip == Beep ? Sine(0.5f) : "garbage"u8.ToArray();
        });
        return (scene, device, system, () => reads);
    }

    private static void Frames(Scene scene, int count, float dt = 0.1f)
    {
        for (var i = 0; i < count; i++)
        {
            _frame++;
            scene.Update(new FrameTime(dt, dt, _frame * dt, _frame * dt, _frame));
        }
    }

    [Fact]
    public void DontDestroyOnLoad_SourceKeepsPlaying_InTheNewScene()
    {
        var (first, device, system, _) = NewScene();
        var source = first.CreateGameObject("Music").AddComponent<AudioSource>();
        (source.Clip, source.Loop, source.PlayOnStart) = (Beep, true, true);
        first.Start();
        Frames(first, 1);
        Assert.True(source.IsPlaying);
        source.GameObject.DontDestroyOnLoad();

        var second = new Scene { RethrowScriptExceptions = true };
        var next = AudioSystem.Attach(second, device, _ => Sine(0.5f));
        first.MovePersistentObjectsTo(second);
        first.Unload();
        second.Start();
        Frames(second, 3);

        Assert.True(source.IsPlaying);
        Assert.Equal((0, 1), (system.VoiceCount, next.VoiceCount));
        Assert.Equal(1, device.LiveBuffers); // the clip it plays was handed over, not released
        second.Unload();
        Assert.Equal((0, 0), (device.LiveVoices, device.LiveBuffers));
    }

    [Fact]
    public void Source_PlaysPausesResumesAndFinishes()
    {
        var (scene, device, system, reads) = NewScene();
        var source = scene.CreateGameObject("Speaker").AddComponent<AudioSource>();
        (source.Clip, source.PlayOnStart) = (Beep, true);
        scene.Start();

        Frames(scene, 1);
        Assert.True(source.IsPlaying);
        source.Pause();
        Frames(scene, 10); // paused time does not count
        Assert.True(source.IsPaused);
        source.Resume();
        Frames(scene, 3);
        Assert.True(source.IsPlaying);
        Frames(scene, 3);
        Assert.False(source.IsPlaying); // 0.5 s clip

        source.Loop = true;
        source.Play();
        Frames(scene, 20);
        Assert.True(source.IsPlaying);
        source.Stop();
        Assert.False(source.IsPlaying);
        Assert.Equal(1, reads()); // decoded once
        Assert.Equal(1, device.LiveBuffers);
        Assert.Equal(1, system.VoiceCount);
    }

    [Fact]
    public void OneShots_AreRecycled_AndBadClipsAreLoggedNotThrown()
    {
        var (scene, _, system, _) = NewScene();
        var source = scene.CreateGameObject("Speaker").AddComponent<AudioSource>();
        scene.Start();

        source.PlayOneShot(Beep);
        source.PlayOneShot(Beep, 0.5f);
        Assert.Equal(2, system.VoiceCount);
        Frames(scene, 7);
        Assert.Equal(0, system.VoiceCount);

        source.Clip = Bad;
        source.Play();
        source.PlayOneShot(Bad);
        Assert.False(source.IsPlaying);
        Assert.Equal(0, system.VoiceCount);
    }

    [Fact]
    public void ListenerAndSpatialSources_FollowTheirTransforms_AndUnloadReleasesEverything()
    {
        var (scene, device, system, _) = NewScene();
        var listener = scene.CreateGameObject("Ears");
        listener.AddComponent<AudioListener>();
        listener.Transform.Position = new Vector2(3, 4);
        var source = scene.CreateGameObject("Speaker").AddComponent<AudioSource>();
        (source.Clip, source.Spatial, source.Loop) = (Beep, true, true);
        scene.Start();
        source.Play();
        Frames(scene, 1);
        Assert.Equal(new Vector2(3, 4), device.ListenerPosition);

        scene.Unload();
        Assert.Equal((0, 0), (device.LiveBuffers, device.LiveVoices));
        Assert.Null(AudioSystem.For(scene));
        Assert.Equal(0, system.VoiceCount);
    }

    [Fact]
    public void WithoutAnAudioSystem_SourcesDoNothing_AndSettingsSurviveSerialization()
    {
        var scene = new Scene();
        var source = scene.CreateGameObject("Speaker").AddComponent<AudioSource>();
        (source.Clip, source.Volume, source.Pitch, source.Loop, source.Spatial, source.MaxDistance) = (Beep, 0.25f, 1.5f, true, true, 12f);
        source.Play();
        Assert.False(source.IsPlaying);

        var serializer = new SceneSerializer(AudioComponents.Register(ComponentRegistry.CreateDefault()));
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene.Find("Speaker")!.GetComponent<AudioSource>()!;
        Assert.Equal((Beep, 0.25f, 1.5f, true, true, 12f), (copy.Clip, copy.Volume, copy.Pitch, copy.Loop, copy.Spatial, copy.MaxDistance));
    }

    [Fact]
    public void OpenAL_PlaysAClipToTheEnd()
    {
        OpenALAudioDevice device;
        try
        {
            device = OpenALAudioDevice.Create(NullLogger.Instance);
        }
        catch (InvalidOperationException ex)
        {
            Assert.Skip($"No OpenAL output on this machine: {ex.Message}");
            return;
        }

        using (device)
        {
            using var buffer = device.CreateBuffer(AudioDecoder.Decode(Sine(0.25f), "beep.wav"));
            using var voice = device.CreateVoice();
            voice.Volume = 0.05f; // quiet: this runs on developer machines
            voice.Play(buffer);
            Assert.Equal(VoiceState.Playing, voice.State);
            voice.Pause();
            Assert.Equal(VoiceState.Paused, voice.State);
            voice.Resume();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (voice.State == VoiceState.Playing && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }

            Assert.Equal(VoiceState.Stopped, voice.State);
        }
    }
}

public class AudioMixerTests
{
    private static readonly AssetRef Beep = new(Guid.Parse("cccccccc-0000-4000-8000-000000000003"));

    private static AudioMixer Mixer(Scene scene)
    {
        var mixer = scene.CreateGameObject("Mixer").AddComponent<AudioMixer>();
        mixer.Groups = [new() { Name = "Music", Volume = 0.5f }, new() { Name = "Effects", Volume = 0.8f }, new() { Name = "Footsteps", Parent = "Effects", Volume = 0.5f }];
        return mixer;
    }

    [Fact]
    public void EffectiveVolume_MultipliesUpToMaster_AndMuteSilences()
    {
        var mixer = Mixer(new Scene());
        mixer.MasterVolume = 0.5f;
        Assert.Equal(0.25f, mixer.EffectiveVolume("Music"), 5);
        Assert.Equal(0.2f, mixer.EffectiveVolume("footsteps"), 5); // 0.5 × 0.8 × 0.5, names are case-insensitive
        Assert.Equal(0.5f, mixer.EffectiveVolume("Unknown"), 5);   // plays through Master
        Assert.Equal(0.5f, mixer.EffectiveVolume(""), 5);

        mixer.SetMuted("Effects", true);
        Assert.Equal(0f, mixer.EffectiveVolume("Footsteps"));
        mixer.SetMuted("Master", true);
        Assert.Equal(0f, mixer.EffectiveVolume("Music"));
        Assert.Throws<KeyNotFoundException>(() => mixer.SetVolume("Nope", 1f));

        mixer.Groups[1].Parent = "Footsteps"; // cycle Effects ⇄ Footsteps
        mixer.SetMuted("Master", false);
        mixer.SetMuted("Effects", false);
        Assert.Equal(0.2f, mixer.EffectiveVolume("Footsteps"), 5); // each group counted once
    }

    [Fact]
    public void PlayingSourcesAndOneShots_FollowMixerChanges()
    {
        var scene = new Scene { RethrowScriptExceptions = true };
        var device = new NullAudioDevice();
        AudioSystem.Attach(scene, device, _ => AudioTests.Sine(5f));
        var mixer = Mixer(scene);
        var music = scene.CreateGameObject("Music").AddComponent<AudioSource>();
        (music.Clip, music.Group, music.Volume, music.Loop) = (Beep, "Music", 0.8f, true);
        var sfx = scene.CreateGameObject("Sfx").AddComponent<AudioSource>();
        sfx.Group = "Footsteps";
        scene.Start();

        music.Play();
        sfx.PlayOneShot(Beep);
        Frame(scene);
        Assert.Equal(0.4f, device.Voices[0].Volume, 5);  // 0.8 × Music 0.5
        Assert.Equal(0.4f, device.Voices[1].Volume, 5);  // 1 × Footsteps 0.5 × Effects 0.8

        mixer.SetVolume("Music", 1f);
        mixer.SetMuted("Effects", true);
        Frame(scene);
        Assert.Equal(0.8f, device.Voices[0].Volume, 5);
        Assert.Equal(0f, device.Voices[1].Volume);
    }

    [Fact]
    public void Groups_SurviveSerialization()
    {
        var scene = new Scene();
        Mixer(scene).MasterVolume = 0.7f;
        var serializer = new SceneSerializer(AudioComponents.Register(ComponentRegistry.CreateDefault()));
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene.Find("Mixer")!.GetComponent<AudioMixer>()!;
        Assert.Equal(0.7f, copy.MasterVolume);
        Assert.Equal(["Music", "Effects", "Footsteps"], copy.Groups.Select(g => g.Name));
        Assert.Equal("Effects", copy.Groups[2].Parent);
        Assert.Equal(0.28f, copy.EffectiveVolume("Footsteps"), 5);
    }

    private static long _frame;

    private static void Frame(Scene scene)
    {
        _frame++;
        scene.Update(new FrameTime(0.016f, 0.016f, _frame * 0.016, _frame * 0.016, _frame));
    }
}
