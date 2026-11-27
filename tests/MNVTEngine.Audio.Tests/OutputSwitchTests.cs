// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-27.

using MNVTEngine.Audio;

namespace MNVTEngine.Audio.Tests;

public sealed class OutputSwitchTests
{
    [Fact]
    public void UseOutput_StopsTheOldOutput_AndPlaysThroughTheNewOne()
    {
        var outputs = new FakeOutputs("Speakers", "Headphones");
        using var device = new MixingAudioDevice(outputs.Open(null)) { Outputs = outputs };

        device.UseOutput("Headphones");

        Assert.True(outputs.Opened[0].Disposed);
        Assert.True(outputs.Opened[1].Started);
        Assert.Equal("Headphones", device.Name);
    }

    [Fact]
    public void AnOutputThatCannotBeOpened_FallsBackToTheDefault_AndSaysWhy()
    {
        var outputs = new FakeOutputs("Speakers");
        using var device = new MixingAudioDevice(outputs.Open(null)) { Outputs = outputs };

        Assert.Throws<InvalidOperationException>(() => device.UseOutput("Unplugged"));

        Assert.Equal("Speakers", device.Name);
        Assert.True(outputs.Opened[^1].Started);
    }

    [Fact]
    public void UseOutput_WithoutOutputsToChooseFrom_IsNotSupported()
    {
        using var device = new NullAudioDevice();

        Assert.Throws<NotSupportedException>(() => device.UseOutput(null));
    }

    private sealed class FakeOutputs(params string[] devices) : IAudioOutputSource
    {
        public List<FakeOutput> Opened { get; } = [];

        public IReadOnlyList<string> Devices => devices;

        public IAudioOutput Open(string? device)
        {
            var name = string.IsNullOrEmpty(device) ? devices[0] : device;
            if (!devices.Contains(name))
            {
                throw new InvalidOperationException($"The audio output '{name}' cannot be opened.");
            }

            var output = new FakeOutput(name);
            Opened.Add(output);
            return output;
        }
    }

    private sealed class FakeOutput(string name) : IAudioOutput
    {
        public string Name => name;

        public int SampleRate => 48000;

        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public void Start(AudioRenderCallback render) => Started = true;

        public void Dispose() => Disposed = true;
    }
}
