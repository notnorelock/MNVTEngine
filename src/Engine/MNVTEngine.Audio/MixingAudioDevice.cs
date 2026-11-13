using MNVTEngine.Mathematics;

namespace MNVTEngine.Audio;

/// <summary>Fills a block of interleaved stereo float samples (called on the output's thread).</summary>
public delegate void AudioRenderCallback(Span<float> stereo);

/// <summary>Where the mixed sound goes (the OpenAL backend, or nothing). It pulls blocks from the mixer on its own thread.</summary>
public interface IAudioOutput : IDisposable
{
    string Name { get; }

    int SampleRate { get; }

    void Start(AudioRenderCallback render);
}

/// <summary>A mixer bus: a group of the scene's <c>AudioMixer</c> (or Master), with its fader and insert effects.</summary>
/// <param name="Parent">The bus it feeds; empty, unknown or cyclic parents feed Master.</param>
public sealed record AudioBusSettings(string Name, string Parent, float Volume, bool Muted, IReadOnlyList<AudioEffectSettings> Effects);

/// <summary>A bus's level after its fader: peaks and RMS of the last ~50 ms (linear, 1 = full scale), the highest peak and whether it clipped since the last reset.</summary>
public readonly record struct AudioMeter(float PeakLeft, float PeakRight, float RmsLeft, float RmsRight, float HoldPeak, bool Clipped);

/// <summary>
/// The engine's software mixer. Voices are mixed into their buses (<see cref="IAudioVoice.Bus"/>); every bus runs its
/// insert effects, then its fader and mute, is metered and feeds its parent, up to Master, which goes to the
/// <see cref="IAudioOutput"/>. Spatial voices are panned (equal power) and attenuated by distance (inverse distance,
/// clamped) here. The output renders on its own thread; the game's calls and the rendering share one lock. Without an
/// output (<see cref="NullAudioDevice"/>) <see cref="Update"/> renders the elapsed time, silently.
/// </summary>
public class MixingAudioDevice : IAudioDevice
{
    public const string MasterBus = "Master";
    private const int BlockFrames = 256;
    private const int MeterBlocks = 10; // ~50 ms at 48 kHz

    private readonly IAudioOutput? _output;
    private readonly Lock _lock = new();
    private readonly List<ClipVoice> _voices = [];
    private readonly List<StreamVoice> _streams = [];
    private readonly Dictionary<string, Bus> _buses = new(StringComparer.OrdinalIgnoreCase);
    private readonly float[] _block = new float[BlockFrames * 2];
    private List<Bus> _order = [];
    private Vector2 _listener;
    private double _pendingFrames;
    private bool _disposed;

    /// <param name="output">Null: nothing is heard and <see cref="Update"/> drives the rendering.</param>
    public MixingAudioDevice(IAudioOutput? output, int sampleRate = 48000)
    {
        _output = output;
        SampleRate = output?.SampleRate ?? sampleRate;
        SetBuses([]);
        output?.Start(Render);
    }

    public virtual string Name => _output?.Name ?? "Null (silent)";

    public int SampleRate { get; }

    /// <summary>Output gain after Master (the player's volume setting).</summary>
    public float MasterVolume { get; set; } = 1f;

    public Vector2 ListenerPosition
    {
        get => _listener;
        set
        {
            lock (_lock)
            {
                _listener = value;
            }
        }
    }

    public int LiveBuffers { get; private set; }

    public int LiveVoices => _voices.Count;

    /// <summary>Live voices, for inspecting what would be heard (volume, position, bus).</summary>
    public IReadOnlyList<IAudioVoice> Voices => _voices;

    /// <summary>Live streaming voices.</summary>
    public IReadOnlyList<IStreamingVoice> StreamingVoices => _streams;

    /// <summary>Average CPU time of rendering one millisecond of audio, in milliseconds (0.05 = 5% of a core).</summary>
    public double RenderLoad { get; private set; }

    public IAudioBuffer CreateBuffer(AudioClipData clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ObjectDisposedException.ThrowIf(_disposed, this);
        LiveBuffers++;
        return new ClipBuffer(this, clip);
    }

    public IAudioBuffer CreateStreamedBuffer(Func<IAudioSampleSource> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        ObjectDisposedException.ThrowIf(_disposed, this);
        int channels, rate;
        long? frames;
        using (var probe = open())
        {
            (channels, rate, frames) = (probe.Channels, probe.SampleRate, probe.Frames);
        }

        LiveBuffers++;
        return new StreamedBuffer(this, open, channels, rate, frames is { } f ? f / (float)rate : 0f);
    }

    /// <summary>Voices playing a streamed clip now.</summary>
    public int ActiveStreams
    {
        get
        {
            lock (_lock)
            {
                return _clipStreams.Count(s => !s.Closed);
            }
        }
    }

    /// <summary>Blocks of streamed audio that played silent because decoding fell behind, since start.</summary>
    public long StreamUnderruns => Interlocked.Read(ref _underruns);

    public IAudioVoice CreateVoice()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var voice = new ClipVoice(this);
        lock (_lock)
        {
            _voices.Add(voice);
        }

        return voice;
    }

    public IStreamingVoice CreateStreamingVoice(int channels, int sampleRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        var stream = new StreamVoice(this, channels, sampleRate);
        lock (_lock)
        {
            _streams.Add(stream);
        }

        return stream;
    }

    /// <summary>
    /// Replaces the bus layout. Buses keep their effect instances (and so their reverb and delay tails) where the
    /// same effect type stays at the same position. Settings are copied: later edits need another call.
    /// </summary>
    public void SetBuses(IReadOnlyList<AudioBusSettings> buses)
    {
        ArgumentNullException.ThrowIfNull(buses);
        lock (_lock)
        {
            var wanted = buses.Where(b => !string.IsNullOrWhiteSpace(b.Name)).GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            if (!wanted.Any(b => IsMaster(b.Name)))
            {
                wanted.Add(new AudioBusSettings(MasterBus, "", 1f, false, []));
            }

            foreach (var name in _buses.Keys.Where(n => !wanted.Any(b => string.Equals(b.Name, n, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                _buses.Remove(name);
            }

            foreach (var settings in wanted)
            {
                if (!_buses.TryGetValue(settings.Name, out var bus))
                {
                    _buses[settings.Name] = bus = new Bus(IsMaster(settings.Name) ? MasterBus : settings.Name);
                }

                bus.Configure(settings, SampleRate);
            }

            var master = _buses[MasterBus];
            foreach (var bus in _buses.Values)
            {
                bus.Target = bus == master ? null : _buses.GetValueOrDefault(bus.ParentName) is { } parent && parent != bus ? parent : master;
            }

            // Break cycles: a bus whose chain does not reach Master feeds Master directly.
            foreach (var bus in _buses.Values.Where(b => b != master))
            {
                var seen = new HashSet<Bus>();
                for (var current = bus; current is not null && current != master; current = current.Target)
                {
                    if (!seen.Add(current))
                    {
                        bus.Target = master;
                        break;
                    }
                }
            }

            // Children before parents, Master last.
            _order = [.. _buses.Values.Where(b => b != master).OrderByDescending(Depth), master];
        }

        int Depth(Bus bus)
        {
            var depth = 0;
            for (var current = bus.Target; current is not null; current = current.Target)
            {
                depth++;
            }

            return depth;
        }
    }

    public bool TryGetMeter(string bus, out AudioMeter meter)
    {
        lock (_lock)
        {
            if (_buses.GetValueOrDefault(string.IsNullOrEmpty(bus) ? MasterBus : bus) is not { } found)
            {
                meter = default;
                return false;
            }

            meter = found.Meter();
            return true;
        }
    }

    public void ResetMeter(string bus)
    {
        lock (_lock)
        {
            if (_buses.GetValueOrDefault(string.IsNullOrEmpty(bus) ? MasterBus : bus) is { } found)
            {
                (found.HoldPeak, found.Clipped) = (0f, false);
            }
        }
    }

    /// <summary>Without an output, renders (silently) the time that passed; with one, the output renders on its own.</summary>
    public void Update(float deltaTime)
    {
        if (_output is not null || _disposed)
        {
            return;
        }

        _pendingFrames += Math.Max(0f, deltaTime) * SampleRate;
        var frames = (int)_pendingFrames;
        _pendingFrames -= frames;
        while (frames > 0)
        {
            var n = Math.Min(frames, BlockFrames);
            Render(_block.AsSpan(0, n * 2));
            frames -= n;
        }
    }

    /// <summary>Mixes the next frames into <paramref name="stereo"/> (interleaved, overwritten). The output's thread.</summary>
    public void Render(Span<float> stereo)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_lock)
        {
            for (var offset = 0; offset < stereo.Length; offset += BlockFrames * 2)
            {
                var block = stereo.Slice(offset, Math.Min(BlockFrames * 2, stereo.Length - offset));
                RenderBlock(block);
            }
        }

        var milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var audioMilliseconds = stereo.Length / 2 * 1000.0 / SampleRate;
        RenderLoad = (RenderLoad * 0.95) + (milliseconds / Math.Max(audioMilliseconds, 0.001) * 0.05);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _output?.Dispose(); // stops the render thread first
        _streamerStop.Cancel();
        _streamer?.Join();
        _streamerStop.Dispose();
        _streamerWake.Dispose();
        foreach (var stream in _clipStreams)
        {
            stream.Source?.Dispose();
        }

        _clipStreams.Clear();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ streamed clips

    private readonly List<ClipStream> _clipStreams = [];
    private readonly CancellationTokenSource _streamerStop = new();
    private readonly AutoResetEvent _streamerWake = new(false);
    private Thread? _streamer;
    private long _underruns;

    /// <summary>Seconds of decoded audio kept ahead of each streamed voice.</summary>
    internal const float StreamAheadSeconds = 0.75f;

    private const int StreamBlockFrames = 4096;

    private readonly System.Collections.Concurrent.ConcurrentQueue<Exception> _streamErrors = new();

    /// <summary>A decoding or read error of a streamed clip (its voice stopped early); null when there is none.</summary>
    public Exception? TakeStreamError() => _streamErrors.TryDequeue(out var error) ? error : null;

    /// <summary>A stream for a voice about to play, with its decoder open and the first block decoded (on the caller's thread, outside the lock).</summary>
    private ClipStream PrepareStream(StreamedBuffer buffer, bool loop)
    {
        var stream = new ClipStream(buffer, Math.Max(StreamBlockFrames * 2, (int)(buffer.SampleRate * StreamAheadSeconds) + StreamBlockFrames)) { Loop = loop };
        var block = Array.Empty<short>();
        Fill(stream, ref block, maxBlocks: 1);
        return stream;
    }

    private ClipStream StartStream(ClipStream stream)
    {
        _clipStreams.Add(stream); // under the lock (from ClipVoice.Play)
        if (_streamer is null)
        {
            _streamer = new Thread(StreamLoop) { IsBackground = true, Name = "MNVT audio streaming", Priority = ThreadPriority.AboveNormal };
            _streamer.Start();
        }

        _streamerWake.Set();
        return stream;
    }

    /// <summary>
    /// The streaming thread: keeps every playing streamed voice's ring buffer filled, decoding (and reading content) outside
    /// the mixer's lock, so the render thread never waits for I/O. Closed streams' decoders are disposed here, their only user.
    /// </summary>
    private void StreamLoop()
    {
        var block = Array.Empty<short>();
        var token = _streamerStop.Token;
        while (!token.IsCancellationRequested)
        {
            ClipStream[] work;
            lock (_lock)
            {
                work = [.. _clipStreams];
            }

            foreach (var stream in work)
            {
                if (stream.Closed)
                {
                    stream.Source?.Dispose();
                    lock (_lock)
                    {
                        _clipStreams.Remove(stream);
                    }

                    continue;
                }

                Fill(stream, ref block);
            }

            WaitHandle.WaitAny([_streamerWake, token.WaitHandle], 10);
        }
    }

    private void Fill(ClipStream stream, ref short[] block, int maxBlocks = int.MaxValue)
    {
        try
        {
            stream.Source ??= stream.Buffer.Open();
            var channels = stream.Buffer.Channels;
            if (block.Length < StreamBlockFrames * channels)
            {
                block = new short[StreamBlockFrames * channels];
            }

            var emptyRewinds = 0;
            while (true)
            {
                bool loop;
                long free;
                lock (_lock)
                {
                    if (stream.Ended || stream.Closed)
                    {
                        return;
                    }

                    free = stream.Capacity - (stream.Written - (long)stream.ReadPosition);
                    loop = stream.Loop;
                }

                if (free < StreamBlockFrames || maxBlocks-- == 0)
                {
                    return;
                }

                var frames = stream.Source.Read(block.AsSpan(0, StreamBlockFrames * channels));
                if (frames == 0)
                {
                    if (loop && emptyRewinds++ == 0)
                    {
                        stream.Source.Rewind();
                        continue;
                    }

                    lock (_lock)
                    {
                        stream.Ended = true; // an empty clip that loops ends too
                    }

                    return;
                }

                emptyRewinds = 0;
                lock (_lock)
                {
                    for (var f = 0; f < frames; f++)
                    {
                        var at = (int)((stream.Written + f) % stream.Capacity) * channels;
                        block.AsSpan(f * channels, channels).CopyTo(stream.Ring.AsSpan(at, channels));
                    }

                    stream.Written += frames;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or UnauthorizedAccessException)
        {
            lock (_lock)
            {
                (stream.Ended, stream.Error) = (true, ex); // the voice plays what it has, then stops
            }
        }
    }

    private static bool IsMaster(string name) => string.IsNullOrEmpty(name) || string.Equals(name, MasterBus, StringComparison.OrdinalIgnoreCase);

    private void RenderBlock(Span<float> output)
    {
        var frames = output.Length / 2;
        foreach (var bus in _order)
        {
            bus.Buffer.AsSpan(0, frames * 2).Clear();
        }

        var master = _buses[MasterBus];
        foreach (var voice in _voices)
        {
            voice.Mix(BusOf(voice.Bus, master).Buffer.AsSpan(0, frames * 2), _listener, SampleRate);
        }

        foreach (var stream in _streams)
        {
            stream.Mix(BusOf(stream.Bus, master).Buffer.AsSpan(0, frames * 2), SampleRate);
        }

        foreach (var bus in _order)
        {
            var samples = bus.Buffer.AsSpan(0, frames * 2);
            bus.Process(samples, bus == master ? MasterVolume : 1f);
            if (bus.Target is { } target)
            {
                var into = target.Buffer.AsSpan(0, frames * 2);
                for (var i = 0; i < samples.Length; i++)
                {
                    into[i] += samples[i];
                }
            }
        }

        var mix = master.Buffer.AsSpan(0, frames * 2);
        for (var i = 0; i < mix.Length; i++)
        {
            output[i] = Math.Clamp(mix[i], -1f, 1f);
        }
    }

    private Bus BusOf(string name, Bus master) => string.IsNullOrEmpty(name) ? master : _buses.GetValueOrDefault(name) ?? master;

    /// <summary>Equal-power stereo gains of a mono voice; spatial ones are panned by direction and attenuated by distance.</summary>
    internal static (float Left, float Right) MonoGains(bool spatial, Vector2 position, Vector2 listener, float reference, float max)
    {
        if (!spatial)
        {
            return (MathF.Sqrt(0.5f), MathF.Sqrt(0.5f));
        }

        var offset = position - listener;
        var distance = offset.Magnitude;
        var clamped = Math.Clamp(distance, reference, Math.Max(reference, max));
        var attenuation = reference / (reference + (clamped - reference));
        var pan = distance > 1e-4f ? Math.Clamp(offset.X / distance, -1f, 1f) : 0f;
        var (sin, cos) = MathF.SinCos((pan + 1f) * MathF.PI / 4f);
        return (cos * attenuation, sin * attenuation);
    }

    private sealed class Bus(string name)
    {
        private readonly float[] _peakL = new float[MeterBlocks], _peakR = new float[MeterBlocks], _squareL = new float[MeterBlocks], _squareR = new float[MeterBlocks];
        private readonly int[] _frames = new int[MeterBlocks];
        private List<(AudioEffectSettings Settings, AudioEffectDefinition Definition, AudioEffect Effect)> _effects = [];
        private int _meterIndex;
        private float _gain = 1f, _lastGain = 1f;
        private bool _configured;

        public string Name { get; } = name;

        public string ParentName { get; private set; } = "";

        public Bus? Target { get; set; }

        public float[] Buffer { get; } = new float[BlockFrames * 2];

        public float HoldPeak { get; set; }

        public bool Clipped { get; set; }

        public void Configure(AudioBusSettings settings, int sampleRate)
        {
            ParentName = settings.Parent;
            _gain = settings.Muted ? 0f : Math.Max(0f, settings.Volume);
            if (!_configured)
            {
                (_lastGain, _configured) = (_gain, true); // a new bus starts at its fader: ramps are for changes
            }

            var effects = new List<(AudioEffectSettings, AudioEffectDefinition, AudioEffect)>();
            var index = 0;
            foreach (var source in settings.Effects)
            {
                if (AudioEffects.Find(source.Type) is not { } definition)
                {
                    continue; // an effect this engine does not know: skipped, the rest of the chain plays
                }

                var copy = new AudioEffectSettings { Type = definition.Id, Enabled = source.Enabled, Parameters = new Dictionary<string, float>(source.Parameters) };
                var effect = index < _effects.Count && _effects[index].Definition == definition ? _effects[index].Effect : definition.Create(sampleRate);
                effect.Configure(copy, definition);
                effects.Add((copy, definition, effect));
                index++;
            }

            _effects = effects;
        }

        /// <summary>Inserts, then the fader (ramped over the block), then the meter.</summary>
        public void Process(Span<float> samples, float extraGain)
        {
            foreach (var (settings, _, effect) in _effects)
            {
                if (settings.Enabled)
                {
                    effect.Process(samples);
                }
            }

            var frames = samples.Length / 2;
            var target = _gain * extraGain;
            float peakL = 0f, peakR = 0f, squareL = 0f, squareR = 0f;
            for (var i = 0; i < frames; i++)
            {
                var gain = _lastGain + ((target - _lastGain) * (i + 1) / frames);
                var (l, r) = (samples[i * 2] * gain, samples[(i * 2) + 1] * gain);
                (samples[i * 2], samples[(i * 2) + 1]) = (l, r);
                (peakL, peakR) = (Math.Max(peakL, Math.Abs(l)), Math.Max(peakR, Math.Abs(r)));
                squareL += l * l;
                squareR += r * r;
            }

            _lastGain = target;
            (_peakL[_meterIndex], _peakR[_meterIndex], _squareL[_meterIndex], _squareR[_meterIndex], _frames[_meterIndex]) = (peakL, peakR, squareL, squareR, frames);
            _meterIndex = (_meterIndex + 1) % MeterBlocks;
            HoldPeak = Math.Max(HoldPeak, Math.Max(peakL, peakR));
            Clipped |= peakL >= 1f || peakR >= 1f;
        }

        public AudioMeter Meter()
        {
            var frames = Math.Max(1, _frames.Sum());
            return new AudioMeter(_peakL.Max(), _peakR.Max(), MathF.Sqrt(_squareL.Sum() / frames), MathF.Sqrt(_squareR.Sum() / frames), HoldPeak, Clipped);
        }
    }

    private sealed class ClipBuffer(MixingAudioDevice device, AudioClipData clip) : IAudioBuffer
    {
        private bool _disposed;

        public AudioClipData Clip => clip;

        public float Duration => clip.Duration;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                device.LiveBuffers--;
            }
        }
    }

    private sealed class StreamedBuffer(MixingAudioDevice device, Func<IAudioSampleSource> open, int channels, int sampleRate, float duration) : IAudioBuffer
    {
        private bool _disposed;

        public int Channels => channels;

        public int SampleRate => sampleRate;

        public float Duration => duration;

        public bool IsStreamed => true;

        public IAudioSampleSource Open()
        {
            var source = open();
            if (source.Channels != channels || source.SampleRate != sampleRate)
            {
                source.Dispose();
                throw new InvalidDataException("The streamed clip changed its format.");
            }

            return source;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                device.LiveBuffers--;
            }
        }
    }

    /// <summary>One playback of a streamed clip: a ring of decoded frames, filled by the streaming thread, read by the mixer. Fields under the device lock, except <see cref="Source"/> (streaming thread only).</summary>
    private sealed class ClipStream(StreamedBuffer buffer, int capacity)
    {
        public StreamedBuffer Buffer { get; } = buffer;

        public int Capacity { get; } = capacity;

        public short[] Ring { get; } = new short[capacity * buffer.Channels];

        public IAudioSampleSource? Source { get; set; }

        /// <summary>Frames decoded into the ring since the start (looping continues counting).</summary>
        public long Written { get; set; }

        /// <summary>Frames played (fractional with pitch).</summary>
        public double ReadPosition { get; set; }

        public bool Loop { get; set; }

        public bool Ended { get; set; }

        public bool Closed { get; set; }

        public Exception? Error { get; set; }

        public short At(long frame, int channel) => Ring[((int)(frame % Capacity) * Buffer.Channels) + channel];
    }

    private sealed class ClipVoice(MixingAudioDevice device) : IAudioVoice
    {
        private AudioClipData? _clip;
        private ClipStream? _stream;
        private double _position;
        private float _lastLeft, _lastRight;

        public VoiceState State { get; private set; }

        public float Volume { get; set; } = 1f;

        public float Pitch { get; set; } = 1f;

        public bool Loop { get; set; }

        public bool Spatial { get; set; }

        public Vector2 Position { get; set; }

        public float ReferenceDistance { get; set; } = 1f;

        public float MaxDistance { get; set; } = 20f;

        public string Bus { get; set; } = "";

        public void Play(IAudioBuffer buffer)
        {
            var prepared = buffer is StreamedBuffer streamed ? device.PrepareStream(streamed, Loop) : null;
            lock (device._lock)
            {
                CloseStream();
                _clip = prepared is null ? ((ClipBuffer)buffer).Clip : null;
                _stream = prepared is null ? null : device.StartStream(prepared);
                (_position, _lastLeft, _lastRight) = (0, 0f, 0f);
                State = VoiceState.Playing;
            }
        }

        private void CloseStream()
        {
            if (_stream is { } stream)
            {
                stream.Closed = true; // the streaming thread disposes its decoder
                if (stream.Error is { } error)
                {
                    device._streamErrors.Enqueue(error);
                }

                _stream = null;
                device._streamerWake.Set();
            }
        }

        public void Pause()
        {
            lock (device._lock)
            {
                if (State == VoiceState.Playing)
                {
                    State = VoiceState.Paused;
                }
            }
        }

        public void Resume()
        {
            lock (device._lock)
            {
                if (State == VoiceState.Paused)
                {
                    State = VoiceState.Playing;
                }
            }
        }

        public void Stop()
        {
            lock (device._lock)
            {
                State = VoiceState.Stopped;
                CloseStream();
            }
        }

        public void Dispose()
        {
            lock (device._lock)
            {
                State = VoiceState.Stopped;
                CloseStream();
                device._voices.Remove(this);
            }
        }

        public void Mix(Span<float> bus, Vector2 listener, int outputRate)
        {
            if (State == VoiceState.Playing && _stream is { } stream)
            {
                MixStream(bus, listener, outputRate, stream);
                return;
            }

            if (State != VoiceState.Playing || _clip is not { } clip)
            {
                return;
            }

            var channels = clip.Channels;
            var samples = clip.Samples;
            var length = samples.Length / channels;
            var (left, right) = channels == 1 ? MonoGains(Spatial, Position, listener, ReferenceDistance, MaxDistance) : (1f, 1f);
            (left, right) = (left * Volume, right * Volume);
            var step = clip.SampleRate / (double)outputRate * Pitch;
            var frames = bus.Length / 2;
            for (var i = 0; i < frames; i++)
            {
                if (_position >= length)
                {
                    if (!Loop || length == 0)
                    {
                        State = VoiceState.Stopped;
                        break;
                    }

                    _position %= length;
                }

                var index = (int)_position;
                var fraction = (float)(_position - index);
                var next = index + 1 < length ? index + 1 : Loop ? 0 : index;
                var t = (i + 1f) / frames;
                var (gl, gr) = (_lastLeft + ((left - _lastLeft) * t), _lastRight + ((right - _lastRight) * t));
                if (channels == 1)
                {
                    var s = Sample(samples[index], samples[next], fraction);
                    bus[i * 2] += s * gl;
                    bus[(i * 2) + 1] += s * gr;
                }
                else
                {
                    bus[i * 2] += Sample(samples[index * 2], samples[next * 2], fraction) * gl;
                    bus[(i * 2) + 1] += Sample(samples[(index * 2) + 1], samples[(next * 2) + 1], fraction) * gr;
                }

                _position += step;
            }

            (_lastLeft, _lastRight) = (left, right);
        }

        /// <summary>Plays from the stream's ring; looping happens in the decoder, so frames are continuous across the loop point.</summary>
        private void MixStream(Span<float> bus, Vector2 listener, int outputRate, ClipStream stream)
        {
            stream.Loop = Loop;
            var channels = stream.Buffer.Channels;
            var (left, right) = channels == 1 ? MonoGains(Spatial, Position, listener, ReferenceDistance, MaxDistance) : (1f, 1f);
            (left, right) = (left * Volume, right * Volume);
            var step = stream.Buffer.SampleRate / (double)outputRate * Pitch;
            var frames = bus.Length / 2;
            for (var i = 0; i < frames; i++)
            {
                var index = (long)stream.ReadPosition;
                if (index >= stream.Written)
                {
                    if (stream.Ended)
                    {
                        State = VoiceState.Stopped;
                        CloseStream();
                    }
                    else
                    {
                        Interlocked.Increment(ref device._underruns); // decoding fell behind: silence until it catches up
                        device._streamerWake.Set();
                    }

                    break;
                }

                var next = index + 1 < stream.Written ? index + 1 : index;
                var fraction = (float)(stream.ReadPosition - index);
                var t = (i + 1f) / frames;
                var (gl, gr) = (_lastLeft + ((left - _lastLeft) * t), _lastRight + ((right - _lastRight) * t));
                if (channels == 1)
                {
                    var s = Sample(stream.At(index, 0), stream.At(next, 0), fraction);
                    bus[i * 2] += s * gl;
                    bus[(i * 2) + 1] += s * gr;
                }
                else
                {
                    bus[i * 2] += Sample(stream.At(index, 0), stream.At(next, 0), fraction) * gl;
                    bus[(i * 2) + 1] += Sample(stream.At(index, 1), stream.At(next, 1), fraction) * gr;
                }

                stream.ReadPosition += step;
            }

            (_lastLeft, _lastRight) = (left, right);
            if (stream.Written - (long)stream.ReadPosition < stream.Capacity / 2)
            {
                device._streamerWake.Set();
            }
        }
    }

    private sealed class StreamVoice(MixingAudioDevice device, int channels, int sampleRate) : IStreamingVoice
    {
        private readonly Queue<short[]> _chunks = new();
        private short[]? _current;
        private double _position; // frames into _current
        private long _queuedFrames;
        private float _lastGain;

        public int Channels => channels;

        public int SampleRate => sampleRate;

        public float Volume { get; set; } = 1f;

        public string Bus { get; set; } = "";

        public float QueuedSeconds
        {
            get
            {
                lock (device._lock)
                {
                    return _queuedFrames / (float)sampleRate;
                }
            }
        }

        public void Queue(ReadOnlySpan<short> interleaved)
        {
            if (interleaved.Length < channels)
            {
                return;
            }

            var chunk = interleaved[..(interleaved.Length / channels * channels)].ToArray();
            lock (device._lock)
            {
                _chunks.Enqueue(chunk);
                _queuedFrames += chunk.Length / channels;
            }
        }

        public void Dispose()
        {
            lock (device._lock)
            {
                device._streams.Remove(this);
            }
        }

        public void Mix(Span<float> bus, int outputRate)
        {
            var step = sampleRate / (double)outputRate;
            var gain = Volume;
            var mono = channels == 1 ? MathF.Sqrt(0.5f) : 1f;
            var frames = bus.Length / 2;
            for (var i = 0; i < frames; i++)
            {
                if (_current is null || _position >= _current.Length / channels)
                {
                    if (_current is not null)
                    {
                        _position -= _current.Length / channels;
                    }

                    if (!_chunks.TryDequeue(out _current))
                    {
                        _position = 0;
                        break; // ran dry: silent until more is queued
                    }
                }

                var index = (int)_position;
                var g = (_lastGain + ((gain - _lastGain) * (i + 1) / frames)) * mono;
                if (channels == 1)
                {
                    var s = _current[index] / 32768f * g;
                    bus[i * 2] += s;
                    bus[(i * 2) + 1] += s;
                }
                else
                {
                    bus[i * 2] += _current[index * 2] / 32768f * g;
                    bus[(i * 2) + 1] += _current[(index * 2) + 1] / 32768f * g;
                }

                var before = (long)_position;
                _position += step;
                _queuedFrames -= (long)_position - before;
            }

            _queuedFrames = Math.Max(0, _queuedFrames);
            _lastGain = gain;
        }
    }

    private static float Sample(short a, short b, float fraction) => (a + ((b - a) * fraction)) / 32768f;
}
