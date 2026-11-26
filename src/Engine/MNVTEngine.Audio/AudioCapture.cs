// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

namespace MNVTEngine.Audio;

/// <summary>
/// Microphones of the audio backend (docs/engine/voice.md): <see cref="IAudioDevice.Capture"/>, null when the backend
/// cannot record. Recording is 16-bit mono.
/// </summary>
public interface IAudioCaptureSource
{
    /// <summary>The names of the recording devices; empty when there are none.</summary>
    IReadOnlyList<string> Devices { get; }

    /// <summary>Opens a microphone, stopped; <see cref="IAudioCapture.Start"/> records.</summary>
    /// <param name="device">One of <see cref="Devices"/>; null or empty is the system's default.</param>
    /// <param name="sampleRate">Samples per second, e.g. 48000.</param>
    /// <exception cref="InvalidOperationException">There is no such microphone, or it cannot be opened.</exception>
    IAudioCapture Open(string? device, int sampleRate);
}

/// <summary>An open microphone. Recorded samples wait in the backend's buffer (about a second) until read.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716", Justification = "Start/Stop are the natural recording terms, as on IAudioVoice.")]
public interface IAudioCapture : IDisposable
{
    string Name { get; }

    int SampleRate { get; }

    bool IsRecording { get; }

    void Start();

    /// <summary>Stops recording; samples already recorded can still be read.</summary>
    void Stop();

    /// <summary>Recorded samples waiting to be read.</summary>
    int Available { get; }

    /// <summary>Reads up to <paramref name="samples"/>.Length recorded samples; returns how many it read (0 when none wait).</summary>
    int Read(Span<short> samples);
}
