// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Audio.OpenAL;

namespace MNVTEngine.Audio.Tests;

/// <summary>Microphones: none on the silent device; OpenAL's records when the machine has one.</summary>
public sealed class CaptureTests
{
    [Fact]
    public void TheSilentDevice_CannotRecord()
    {
        using var device = new NullAudioDevice();

        Assert.Null(device.Capture);
    }

    [Fact]
    public void OpenAL_RecordsFromTheDefaultMicrophone()
    {
        var source = OpenALCaptureSource.TryCreate(NullLogger.Instance);
        Assert.SkipWhen(source is null, "OpenAL Soft is not available.");
        Assert.SkipWhen(source!.Devices.Count == 0, "No microphone on this machine.");

        using var capture = source.Open(null, 48000);
        capture.Start();
        var samples = new short[4800];
        var read = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (read < samples.Length && clock.ElapsedMilliseconds < 3000)
        {
            read += capture.Read(samples.AsSpan(read));
            Thread.Sleep(10);
        }

        capture.Stop();
        Assert.True(capture.IsRecording is false);
        Assert.Equal(samples.Length, read); // 100 ms recorded (silence is fine)
        Assert.Throws<InvalidOperationException>(() => source.Open("No such microphone " + Guid.NewGuid(), 48000).Dispose());
    }
}
