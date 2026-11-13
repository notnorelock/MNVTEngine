using Microsoft.Extensions.Logging;
using Silk.NET.OpenAL;

namespace MNVTEngine.Audio.OpenAL;

/// <summary>The OpenAL Soft backend: the engine's <see cref="MixingAudioDevice"/> playing through an <see cref="OpenALAudioOutput"/>.</summary>
public static class OpenALAudioDevice
{
    /// <exception cref="InvalidOperationException">No output device, or OpenAL Soft could not be loaded.</exception>
    public static IAudioDevice Create(ILogger logger) => new MixingAudioDevice(OpenALAudioOutput.Open(logger));
}

/// <summary>
/// OpenAL Soft as a plain output: one source fed with the engine's mixed stereo through a queue of buffers, refilled
/// by a render thread. Mixing, effects, panning and attenuation happen in <see cref="MixingAudioDevice"/>.
/// </summary>
public sealed unsafe partial class OpenALAudioOutput : IAudioOutput
{
    private const int Buffers = 4;
    private const int BufferFrames = 512; // 4 x 512 frames: about 43 ms queued at 48 kHz

    private readonly ALContext _alc;
    private readonly AL _al;
    private readonly ILogger _logger;
    private readonly uint _source;
    private readonly uint[] _buffers;
    private Device* _device;
    private Context* _context;
    private Thread? _thread;
    private volatile bool _running;

    private OpenALAudioOutput(ALContext alc, AL al, Device* device, Context* context, ILogger logger)
    {
        (_alc, _al, _logger) = (alc, al, logger);
        _device = device;
        _context = context;
        Name = alc.GetContextProperty(device, GetContextString.DeviceSpecifier) ?? "OpenAL";
        _source = al.GenSource();
        _buffers = al.GenBuffers(Buffers);
        al.SetSourceProperty(_source, SourceBoolean.SourceRelative, true);
    }

    public string Name { get; }

    public int SampleRate => 48000;

    /// <exception cref="InvalidOperationException">No output device, or OpenAL Soft could not be loaded.</exception>
    public static OpenALAudioOutput Open(ILogger logger)
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

        var output = new OpenALAudioOutput(alc, al, device, context, logger);
        LogOpened(logger, output.Name);
        return output;
    }

    public void Start(AudioRenderCallback render)
    {
        ArgumentNullException.ThrowIfNull(render);
        ObjectDisposedException.ThrowIf(_device == null, this);
        _running = true;
        _thread = new Thread(() => Run(render)) { Name = "Audio output", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Dispose()
    {
        if (_device == null)
        {
            return;
        }

        _running = false;
        _thread?.Join(1000);
        _al.SourceStop(_source);
        _al.DeleteSource(_source);
        _al.DeleteBuffers(_buffers);
        _alc.MakeContextCurrent(null);
        _alc.DestroyContext(_context);
        _alc.CloseDevice(_device);
        _context = null;
        _device = null;
        _al.Dispose();
        _alc.Dispose();
    }

    private void Run(AudioRenderCallback render)
    {
        var mix = new float[BufferFrames * 2];
        var pcm = new short[BufferFrames * 2];
        try
        {
            foreach (var buffer in _buffers)
            {
                Fill(buffer, render, mix, pcm);
            }

            fixed (uint* all = _buffers)
            {
                _al.SourceQueueBuffers(_source, Buffers, all);
            }

            _al.SourcePlay(_source);
            while (_running)
            {
                _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out var processed);
                if (processed == 0)
                {
                    Thread.Sleep(2);
                    continue;
                }

                for (var i = 0; i < processed; i++)
                {
                    uint buffer;
                    _al.SourceUnqueueBuffers(_source, 1, &buffer);
                    Fill(buffer, render, mix, pcm);
                    _al.SourceQueueBuffers(_source, 1, &buffer);
                }

                _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out var state);
                if ((SourceState)state != SourceState.Playing)
                {
                    LogUnderrun(_logger);
                    _al.SourcePlay(_source); // the queue ran dry (the thread was starved): restart
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A failure in the mixer or the device ends the sound, not the game; it is logged once.
            LogOutputFailed(_logger, ex);
        }
    }

    private void Fill(uint buffer, AudioRenderCallback render, float[] mix, short[] pcm)
    {
        render(mix);
        for (var i = 0; i < mix.Length; i++)
        {
            pcm[i] = (short)(mix[i] * short.MaxValue);
        }

        fixed (short* samples = pcm)
        {
            _al.BufferData(buffer, BufferFormat.Stereo16, samples, pcm.Length * sizeof(short), SampleRate);
        }
    }

    [LoggerMessage(EventId = 2500, Level = LogLevel.Information, Message = "Audio device opened: {Device}")]
    private static partial void LogOpened(ILogger logger, string device);

    [LoggerMessage(EventId = 2501, Level = LogLevel.Debug, Message = "Audio output underrun: the mixer did not keep up; playback restarted")]
    private static partial void LogUnderrun(ILogger logger);

    [LoggerMessage(EventId = 2502, Level = LogLevel.Error, Message = "Audio output stopped")]
    private static partial void LogOutputFailed(ILogger logger, Exception exception);
}
