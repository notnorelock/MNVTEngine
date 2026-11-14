using MNVTEngine.Scenes;

namespace MNVTEngine.Audio.Tests;

public class AudioStreamingTests
{
    [Fact]
    public void StreamDecoder_GivesTheSameSamplesAsDecodingWhole()
    {
        var wav = AudioTests.Sine(0.5f);
        var whole = AudioDecoder.Decode(wav, "sine.wav");
        using var source = AudioDecoder.Open(new MemoryStream(wav), "sine.wav");
        Assert.Equal((whole.Channels, whole.SampleRate, (long)whole.Samples.Length), (source.Channels, source.SampleRate, source.Frames!.Value * source.Channels));
        var streamed = new List<short>();
        var block = new short[333];
        int frames;
        while ((frames = source.Read(block)) > 0)
        {
            streamed.AddRange(block.AsSpan(0, frames * source.Channels));
        }

        Assert.Equal(whole.Samples, streamed);
        source.Seek(0);
        Assert.Equal(whole.Samples[..100], Read(source, 100).ToArray());
        Assert.Throws<InvalidDataException>(() => AudioDecoder.Open(new MemoryStream("not audio at all"u8.ToArray()), "x"));
    }

    [Fact]
    public void StreamedClip_PlaysToTheEnd_WithoutUnderruns_AndReleasesItsDecoder()
    {
        using var device = new NullAudioDevice();
        var wav = AudioTests.Sine(0.6f);
        var opened = 0;
        using var buffer = device.CreateStreamedBuffer(() =>
        {
            Interlocked.Increment(ref opened);
            return AudioDecoder.Open(new MemoryStream(wav), "sine.wav");
        });
        Assert.True(buffer.IsStreamed);
        Assert.Equal(0.6f, buffer.Duration, 0.01f);

        using var voice = device.CreateVoice();
        voice.Play(buffer);
        var heard = false;
        for (var i = 0; i < 60 && voice.State == VoiceState.Playing; i++)
        {
            Thread.Sleep(15); // let the streaming thread run as it would in real time
            device.Update(0.015f);
            heard |= device.TryGetMeter(MixingAudioDevice.MasterBus, out var meter) && meter.PeakLeft > 0.1f;
        }

        Assert.True(heard);
        Assert.Equal(VoiceState.Stopped, voice.State);
        Assert.Equal(0, device.StreamUnderruns);
        Assert.Equal(2, opened); // once to probe the format, once to play
        Assert.True(SpinWait.SpinUntil(() => device.ActiveStreams == 0, 2000));
    }

    [Fact]
    public void Seek_ReadsFromTheFrame()
    {
        var wav = AudioTests.Sine(0.5f);
        var whole = AudioDecoder.Decode(wav, "sine.wav");
        using var source = AudioDecoder.Open(new MemoryStream(wav), "sine.wav");
        source.Seek(5000);
        Assert.Equal(whole.Samples[5000..5100], Read(source, 100).ToArray());
        source.Seek(long.MaxValue);
        Assert.Empty(Read(source, 100));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Time_Seeks_StreamedAndDecodedClips(bool streamed)
    {
        using var device = new NullAudioDevice();
        var wav = AudioTests.Sine(2f);
        using var buffer = streamed
            ? device.CreateStreamedBuffer(() => AudioDecoder.Open(new MemoryStream(wav), "sine.wav"))
            : device.CreateBuffer(AudioDecoder.Decode(wav, "sine.wav"));
        using var voice = device.CreateVoice();
        Assert.Equal(0f, voice.Time);
        voice.Play(buffer);
        voice.Time = 1.5f;
        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(10);
            device.Update(0.01f);
        }

        Assert.InRange(voice.Time, 1.5f, 1.8f);
        Assert.Equal(VoiceState.Playing, voice.State);
        voice.Time = 10f; // clamped to the end: the clip finishes
        for (var i = 0; i < 20 && voice.State == VoiceState.Playing; i++)
        {
            Thread.Sleep(10);
            device.Update(0.01f);
        }

        Assert.Equal(VoiceState.Stopped, voice.State);
        Assert.Equal(0f, voice.Time);
        Assert.Equal(0, device.StreamUnderruns);
        Assert.True(SpinWait.SpinUntil(() => device.ActiveStreams == 0, 2000));
    }

    [Fact]
    public void LoopingStream_KeepsPlaying_UntilStopped()
    {
        using var device = new NullAudioDevice();
        var wav = AudioTests.Sine(0.1f);
        using var buffer = device.CreateStreamedBuffer(() => AudioDecoder.Open(new MemoryStream(wav), "short.wav"));
        using var voice = device.CreateVoice();
        voice.Loop = true;
        voice.Play(buffer);
        for (var i = 0; i < 30; i++)
        {
            Thread.Sleep(10);
            device.Update(0.01f);
        }

        Assert.Equal(VoiceState.Playing, voice.State); // 0.3 s of a 0.1 s clip
        voice.Stop();
        Assert.True(SpinWait.SpinUntil(() => device.ActiveStreams == 0, 2000));
    }

    [Fact]
    public void ReadErrors_StopTheVoice_AndAreReported()
    {
        using var device = new NullAudioDevice();
        var wav = AudioTests.Sine(2f);
        var calls = 0;
        using var buffer = device.CreateStreamedBuffer(() => Interlocked.Increment(ref calls) == 1
            ? AudioDecoder.Open(new MemoryStream(wav), "ok.wav")
            : AudioDecoder.Open(new FailingStream(wav, failAfter: 20_000), "broken.wav"));
        using var voice = device.CreateVoice();
        voice.Play(buffer);
        for (var i = 0; i < 100 && voice.State == VoiceState.Playing; i++)
        {
            Thread.Sleep(10);
            device.Update(0.01f);
        }

        Assert.Equal(VoiceState.Stopped, voice.State);
        Assert.IsType<IOException>(device.TakeStreamError());
    }

    [Fact]
    public void AudioSystem_StreamsBySettingOrSize()
    {
        using var device = new NullAudioDevice();
        var assets = new Assets
        {
            [Guid.Parse("a0000000-0000-4000-8000-000000000001")] = (AudioTests.Sine(0.2f), null),
            [Guid.Parse("a0000000-0000-4000-8000-000000000002")] = (AudioTests.Sine(0.2f), "streaming"),
            [Guid.Parse("a0000000-0000-4000-8000-000000000003")] = (AudioTests.Sine(3f), null),
            [Guid.Parse("a0000000-0000-4000-8000-000000000004")] = (AudioTests.Sine(3f), "decompressOnLoad"),
        };
        var scene = new Scene();
        var system = AudioSystem.Attach(scene, device, assets);
        system.StreamingThreshold = 64 * 1024; // 3 s of 22 kHz mono is 132 KB
        var source = scene.CreateGameObject("Player").AddComponent<AudioSource>();
        foreach (var guid in assets.Keys)
        {
            source.Clip = new AssetRef(guid);
            source.Play();
        }

        Assert.Equal(2, system.StreamedClipCount);
        Assert.InRange(system.BufferBytes, (0.2 * 22050 * 2) + (3 * 22050 * 2) - 8, (0.2 * 22050 * 2) + (3 * 22050 * 2) + 8); // only the decoded ones count
    }

    private static List<short> Read(IAudioSampleSource source, int samples)
    {
        var block = new short[samples];
        var frames = source.Read(block);
        return [.. block[..(frames * source.Channels)]];
    }

    private sealed class Assets : Dictionary<Guid, (byte[] Data, string? LoadType)>, IAssetReader
    {
        public byte[] ReadAllBytes(AssetRef asset) => this[asset.Guid].Data;

        public string? GetImportSetting(AssetRef asset, string name) => name == AudioSystem.LoadTypeSetting ? this[asset.Guid].LoadType : null;
    }

    /// <summary>A stream that fails after a number of bytes (a damaged package chunk, a read error).</summary>
    private sealed class FailingStream(byte[] data, int failAfter) : MemoryStream(data, writable: false)
    {
        // Stream.Read(Span) of a derived MemoryStream comes here too.
        public override int Read(byte[] buffer, int offset, int count) => Position + count > failAfter ? throw new IOException("chunk damaged") : base.Read(buffer, offset, count);
    }
}
