// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MNVTEngine.Audio.OpenAL;

/// <summary>
/// Microphones through OpenAL Soft's capture functions (<c>alcCapture*</c>), called from <c>soft_oal</c> directly:
/// the Silk.NET binding has no capture. 16-bit mono, about a second buffered by OpenAL until read.
/// </summary>
public sealed unsafe partial class OpenALCaptureSource : IAudioCaptureSource
{
    private const int CaptureDeviceSpecifier = 0x310;
    private const int CaptureSamples = 0x312;
    private const int FormatMono16 = 0x1101;

    private readonly ILogger _logger;
    private readonly delegate* unmanaged[Cdecl]<byte*, uint, int, int, nint> _open;
    private readonly delegate* unmanaged[Cdecl]<nint, byte> _close;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _start;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _stop;
    private readonly delegate* unmanaged[Cdecl]<nint, void*, int, void> _samples;
    private readonly delegate* unmanaged[Cdecl]<nint, int, int, int*, void> _getInteger;
    private readonly delegate* unmanaged[Cdecl]<nint, int, byte*> _getString;

    private OpenALCaptureSource(nint library, ILogger logger)
    {
        _logger = logger;
        _open = (delegate* unmanaged[Cdecl]<byte*, uint, int, int, nint>)NativeLibrary.GetExport(library, "alcCaptureOpenDevice");
        _close = (delegate* unmanaged[Cdecl]<nint, byte>)NativeLibrary.GetExport(library, "alcCaptureCloseDevice");
        _start = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(library, "alcCaptureStart");
        _stop = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(library, "alcCaptureStop");
        _samples = (delegate* unmanaged[Cdecl]<nint, void*, int, void>)NativeLibrary.GetExport(library, "alcCaptureSamples");
        _getInteger = (delegate* unmanaged[Cdecl]<nint, int, int, int*, void>)NativeLibrary.GetExport(library, "alcGetIntegerv");
        _getString = (delegate* unmanaged[Cdecl]<nint, int, byte*>)NativeLibrary.GetExport(library, "alcGetString");
    }

    /// <summary>The capture functions of the OpenAL Soft library the output uses; null (logged) when they cannot be found.</summary>
    public static OpenALCaptureSource? TryCreate(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        foreach (var name in (string[])["soft_oal", "openal", "libopenal.so.1"])
        {
            if (!NativeLibrary.TryLoad(name, typeof(OpenALCaptureSource).Assembly, null, out var library))
            {
                continue;
            }

            try
            {
                return new OpenALCaptureSource(library, logger);
            }
            catch (EntryPointNotFoundException ex)
            {
                LogNoCapture(logger, ex.Message);
                return null;
            }
        }

        LogNoCapture(logger, "OpenAL Soft could not be loaded");
        return null;
    }

    public IReadOnlyList<string> Devices
    {
        get
        {
            // A list of names, each ending in a zero byte, ending in an empty name.
            var list = _getString(0, CaptureDeviceSpecifier);
            var names = new List<string>();
            while (list != null && *list != 0)
            {
                var length = 0;
                while (list[length] != 0)
                {
                    length++;
                }

                names.Add(Encoding.UTF8.GetString(list, length));
                list += length + 1;
            }

            return names;
        }
    }

    public IAudioCapture Open(string? device, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 192000);
        nint handle;
        if (string.IsNullOrEmpty(device))
        {
            handle = _open(null, (uint)sampleRate, FormatMono16, sampleRate);
        }
        else
        {
            var name = Encoding.UTF8.GetBytes(device + "\0");
            fixed (byte* bytes = name)
            {
                handle = _open(bytes, (uint)sampleRate, FormatMono16, sampleRate);
            }
        }

        if (handle == 0)
        {
            throw new InvalidOperationException(string.IsNullOrEmpty(device) ? "There is no microphone to record from." : $"The microphone '{device}' cannot be opened.");
        }

        var capture = new Capture(this, handle, string.IsNullOrEmpty(device) ? (Devices is [var first, ..] ? first : "Microphone") : device, sampleRate);
        LogOpened(_logger, capture.Name, sampleRate);
        return capture;
    }

    private sealed class Capture(OpenALCaptureSource api, nint device, string name, int sampleRate) : IAudioCapture
    {
        private nint _device = device;

        public string Name { get; } = name;

        public int SampleRate { get; } = sampleRate;

        public bool IsRecording { get; private set; }

        public int Available
        {
            get
            {
                ObjectDisposedException.ThrowIf(_device == 0, this);
                int samples;
                api._getInteger(_device, CaptureSamples, 1, &samples);
                return Math.Max(0, samples);
            }
        }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_device == 0, this);
            api._start(_device);
            IsRecording = true;
        }

        public void Stop()
        {
            ObjectDisposedException.ThrowIf(_device == 0, this);
            api._stop(_device);
            IsRecording = false;
        }

        public int Read(Span<short> samples)
        {
            var count = Math.Min(Available, samples.Length);
            if (count > 0)
            {
                fixed (short* buffer = samples)
                {
                    api._samples(_device, buffer, count);
                }
            }

            return count;
        }

        public void Dispose()
        {
            if (_device == 0)
            {
                return;
            }

            if (IsRecording)
            {
                api._stop(_device);
            }

            api._close(_device);
            _device = 0;
        }
    }

    [LoggerMessage(EventId = 2503, Level = LogLevel.Information, Message = "Microphone opened: {Device} at {SampleRate} Hz")]
    private static partial void LogOpened(ILogger logger, string device, int sampleRate);

    [LoggerMessage(EventId = 2504, Level = LogLevel.Warning, Message = "No microphone support: {Reason}")]
    private static partial void LogNoCapture(ILogger logger, string reason);
}
