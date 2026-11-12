using Microsoft.Extensions.Logging;
using MNVTEngine.Mathematics;
using Silk.NET.OpenAL;

namespace MNVTEngine.Audio.OpenAL;

/// <summary>
/// OpenAL Soft output. Voices are OpenAL sources, clips are buffers; the world is the XY plane with the listener at
/// z = 0, attenuated with OpenAL's inverse-distance-clamped model. Non-spatial voices are listener-relative at the
/// origin. Use from one thread; dispose before the process exits to release the device.
/// </summary>
public sealed unsafe partial class OpenALAudioDevice : IAudioDevice
{
    private readonly ALContext _alc;
    private readonly AL _al;
    private readonly ILogger _logger;
    private readonly HashSet<uint> _buffers = [];
    private readonly HashSet<uint> _sources = [];
    private Device* _device;
    private Context* _context;
    private float _masterVolume = 1f;
    private Vector2 _listener;

    private OpenALAudioDevice(ALContext alc, AL al, Device* device, Context* context, ILogger logger)
    {
        _alc = alc;
        _al = al;
        _device = device;
        _context = context;
        _logger = logger;
        Name = alc.GetContextProperty(device, GetContextString.DeviceSpecifier) ?? "OpenAL";
    }

    public string Name { get; }

    public float MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = Math.Max(0f, value);
            _al.SetListenerProperty(ListenerFloat.Gain, _masterVolume);
        }
    }

    public Vector2 ListenerPosition
    {
        get => _listener;
        set
        {
            _listener = value;
            _al.SetListenerProperty(ListenerVector3.Position, value.X, value.Y, 0f);
        }
    }

    /// <exception cref="InvalidOperationException">No output device, or OpenAL Soft could not be loaded.</exception>
    public static OpenALAudioDevice Create(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ALContext alc;
        AL al;
        try
        {
            alc = ALContext.GetApi(soft: true);
            al = AL.GetApi(soft: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException($"OpenAL Soft (soft_oal.dll) could not be loaded: {ex.Message}", ex);
        }

        var device = alc.OpenDevice("");
        if (device == null)
        {
            throw new InvalidOperationException("No audio output device is available.");
        }

        var context = alc.CreateContext(device, null);
        if (context == null || !alc.MakeContextCurrent(context))
        {
            alc.CloseDevice(device);
            throw new InvalidOperationException("Could not create an OpenAL context.");
        }

        var result = new OpenALAudioDevice(alc, al, device, context, logger);
        al.DistanceModel(DistanceModel.InverseDistanceClamped);
        LogOpened(logger, result.Name);
        return result;
    }

    public IAudioBuffer CreateBuffer(AudioClipData clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ThrowIfDisposed();
        var buffer = _al.GenBuffer();
        _al.BufferData(buffer, clip.Channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16, clip.Samples, clip.SampleRate);
        Check("BufferData");
        _buffers.Add(buffer);
        return new Buffer(this, buffer, clip.Duration);
    }

    public IAudioVoice CreateVoice()
    {
        ThrowIfDisposed();
        var source = _al.GenSource();
        Check("GenSource");
        _sources.Add(source);
        return new Voice(this, source);
    }

    public IStreamingVoice CreateStreamingVoice(int channels, int sampleRate)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        var source = _al.GenSource();
        Check("GenSource");
        _sources.Add(source);
        return new StreamingVoice(this, source, channels, sampleRate);
    }

    public void Update(float deltaTime)
    {
    }

    public void Dispose()
    {
        if (_device == null)
        {
            return;
        }

        foreach (var source in _sources)
        {
            _al.SourceStop(source);
            _al.DeleteSource(source);
        }

        foreach (var buffer in _buffers)
        {
            _al.DeleteBuffer(buffer);
        }

        if (_sources.Count + _buffers.Count > 0)
        {
            LogLeaked(_logger, _sources.Count, _buffers.Count);
        }

        _sources.Clear();
        _buffers.Clear();
        _alc.MakeContextCurrent(null);
        _alc.DestroyContext(_context);
        _alc.CloseDevice(_device);
        _context = null;
        _device = null;
        _al.Dispose();
        _alc.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_device == null, this);

    private void Check(string operation)
    {
        var error = _al.GetError();
        if (error != AudioError.NoError)
        {
            throw new InvalidOperationException($"OpenAL {operation} failed: {error}.");
        }
    }

    [LoggerMessage(EventId = 2500, Level = LogLevel.Information, Message = "Audio device opened: {Device}")]
    private static partial void LogOpened(ILogger logger, string device);

    [LoggerMessage(EventId = 2501, Level = LogLevel.Warning, Message = "Audio device closed with {Sources} voices and {Buffers} clips still alive")]
    private static partial void LogLeaked(ILogger logger, int sources, int buffers);

    private sealed class Buffer(OpenALAudioDevice device, uint id, float duration) : IAudioBuffer
    {
        public uint Id => id;

        public float Duration => duration;

        public void Dispose()
        {
            if (device._device != null && device._buffers.Remove(id))
            {
                device._al.DeleteBuffer(id);
            }
        }
    }

    /// <summary>A non-spatial source with a queue of buffers: played buffers are reused for new samples.</summary>
    private sealed class StreamingVoice : IStreamingVoice
    {
        private readonly OpenALAudioDevice _device;
        private readonly uint _id;
        private readonly Queue<(uint Buffer, int Frames)> _queued = new();
        private readonly Stack<uint> _free = new();
        private float _volume = 1f;

        public StreamingVoice(OpenALAudioDevice device, uint id, int channels, int sampleRate)
        {
            (_device, _id, Channels, SampleRate) = (device, id, channels, sampleRate);
            Al.SetSourceProperty(_id, SourceBoolean.SourceRelative, true);
            Al.SetSourceProperty(_id, SourceFloat.RolloffFactor, 0f);
            Al.SetSourceProperty(_id, SourceVector3.Position, 0f, 0f, 0f);
        }

        public int Channels { get; }

        public int SampleRate { get; }

        public float Volume { get => _volume; set { _volume = value; Al.SetSourceProperty(_id, SourceFloat.Gain, value); } }

        public float QueuedSeconds
        {
            get
            {
                Reclaim();
                return _queued.Sum(q => q.Frames) / (float)SampleRate;
            }
        }

        private AL Al => _device._al;

        public void Queue(ReadOnlySpan<short> interleaved)
        {
            if (interleaved.IsEmpty || _device._device == null)
            {
                return;
            }

            Reclaim();
            if (!_free.TryPop(out var buffer))
            {
                buffer = Al.GenBuffer();
                _device._buffers.Add(buffer);
            }

            fixed (short* samples = interleaved)
            {
                Al.BufferData(buffer, Channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16, samples, interleaved.Length * sizeof(short), SampleRate);
            }

            Al.SourceQueueBuffers(_id, 1, &buffer);
            _queued.Enqueue((buffer, interleaved.Length / Channels));
            Al.GetSourceProperty(_id, GetSourceInteger.SourceState, out var state);
            if ((SourceState)state != SourceState.Playing)
            {
                Al.SourcePlay(_id); // first samples, or the queue ran dry
            }

            _device.Check("stream queue");
        }

        public void Dispose()
        {
            if (_device._device != null && _device._sources.Remove(_id))
            {
                Al.SourceStop(_id);
                Al.DeleteSource(_id);
                foreach (var buffer in _queued.Select(q => q.Buffer).Concat(_free))
                {
                    _device._buffers.Remove(buffer);
                    Al.DeleteBuffer(buffer);
                }
            }
        }

        /// <summary>Takes the buffers OpenAL has played back for reuse.</summary>
        private void Reclaim()
        {
            Al.GetSourceProperty(_id, GetSourceInteger.BuffersProcessed, out var processed);
            for (var i = 0; i < processed && _queued.Count > 0; i++)
            {
                uint buffer;
                Al.SourceUnqueueBuffers(_id, 1, &buffer);
                _queued.Dequeue();
                _free.Push(buffer);
            }
        }
    }

    private sealed class Voice : IAudioVoice
    {
        private readonly OpenALAudioDevice _device;
        private readonly uint _id;
        private float _volume = 1f;
        private float _pitch = 1f;
        private bool _loop;
        private bool _spatial;
        private Vector2 _position;
        private float _reference = 1f;
        private float _max = 20f;

        public Voice(OpenALAudioDevice device, uint id)
        {
            _device = device;
            _id = id;
            Spatial = false;
        }

        private AL Al => _device._al;

        public VoiceState State
        {
            get
            {
                Al.GetSourceProperty(_id, GetSourceInteger.SourceState, out var state);
                return (SourceState)state switch
                {
                    SourceState.Playing => VoiceState.Playing,
                    SourceState.Paused => VoiceState.Paused,
                    _ => VoiceState.Stopped,
                };
            }
        }

        public float Volume { get => _volume; set { _volume = value; Al.SetSourceProperty(_id, SourceFloat.Gain, value); } }

        public float Pitch { get => _pitch; set { _pitch = value; Al.SetSourceProperty(_id, SourceFloat.Pitch, value); } }

        public bool Loop { get => _loop; set { _loop = value; Al.SetSourceProperty(_id, SourceBoolean.Looping, value); } }

        public bool Spatial
        {
            get => _spatial;
            set
            {
                _spatial = value;
                Al.SetSourceProperty(_id, SourceBoolean.SourceRelative, !value);
                Al.SetSourceProperty(_id, SourceFloat.RolloffFactor, value ? 1f : 0f);
                Position = _position;
            }
        }

        public Vector2 Position
        {
            get => _position;
            set
            {
                _position = value;
                var p = _spatial ? value : Vector2.Zero;
                Al.SetSourceProperty(_id, SourceVector3.Position, p.X, p.Y, 0f);
            }
        }

        public float ReferenceDistance { get => _reference; set { _reference = Math.Max(0.01f, value); Al.SetSourceProperty(_id, SourceFloat.ReferenceDistance, _reference); } }

        public float MaxDistance { get => _max; set { _max = Math.Max(_reference, value); Al.SetSourceProperty(_id, SourceFloat.MaxDistance, _max); } }

        public void Play(IAudioBuffer buffer)
        {
            Al.SourceStop(_id);
            Al.SetSourceProperty(_id, SourceInteger.Buffer, (int)((Buffer)buffer).Id);
            Al.SourcePlay(_id);
            _device.Check("SourcePlay");
        }

        public void Pause() => Al.SourcePause(_id);

        public void Resume()
        {
            if (State == VoiceState.Paused)
            {
                Al.SourcePlay(_id);
            }
        }

        public void Stop() => Al.SourceStop(_id);

        public void Dispose()
        {
            if (_device._device != null && _device._sources.Remove(_id))
            {
                Al.SourceStop(_id);
                Al.DeleteSource(_id);
            }
        }
    }
}
