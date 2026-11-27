// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-27.

using Concentus;
using Concentus.Enums;
using MNVTEngine.Audio;

namespace MNVTEngine.Audio.Tests;

public sealed class WebmOpusTests
{
    private const int Rate = 48000, FrameSamples = 960; // 20 ms

    [Fact]
    public void OpusInWebm_DecodesToItsLength_AndSeeks()
    {
        using var source = AudioDecoder.Open(new MemoryStream(Webm(seconds: 2)), "tone.webm");

        Assert.Equal((2, 48000), (source.Channels, source.SampleRate));
        Assert.Equal((2 * Rate) - 312, source.Frames); // the 312-frame pre-skip is not heard
        var clip = AudioDecoder.ReadAll(source, "tone.webm");
        Assert.InRange(clip.Samples.Length / 2, (2 * Rate) - 1000, 2 * Rate);
        Assert.True(Rms(clip.Samples.AsSpan(Rate, Rate / 2)) > 3000, "the tone is heard");

        source.Seek(Rate * 3 / 2);
        var rest = AudioDecoder.ReadAll(source, "tone.webm");
        Assert.InRange(rest.Samples.Length / 2, (Rate / 2) - 1000, (Rate / 2) + 1000);
    }

    [Fact]
    public void AWebmWithoutOpus_IsRefused() =>
        Assert.Throws<InvalidDataException>(() => AudioDecoder.Open(new MemoryStream([0x1A, 0x45, 0xDF, 0xA3, 0x80]), "empty.webm"));

    private static double Rms(ReadOnlySpan<short> samples)
    {
        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * (double)s;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    /// <summary>A minimal WebM: one stereo Opus track of a 440 Hz tone, one cluster of simple blocks.</summary>
    private static byte[] Webm(int seconds)
    {
        var encoder = OpusCodecFactory.CreateEncoder(Rate, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
        var blocks = new List<byte>();
        var pcm = new short[FrameSamples * 2];
        var packet = new byte[1500];
        for (var frame = 0; frame < seconds * Rate / FrameSamples; frame++)
        {
            for (var i = 0; i < FrameSamples; i++)
            {
                pcm[i * 2] = pcm[(i * 2) + 1] = (short)(12000 * Math.Sin(2 * Math.PI * 440 * ((frame * FrameSamples) + i) / Rate));
            }

            var length = encoder.Encode(pcm, FrameSamples, packet, packet.Length);
            var time = (short)(frame * 20);
            blocks.AddRange(Element(0xA3, [0x81, (byte)(time >> 8), (byte)time, 0x80, .. packet.AsSpan(0, length)]));
        }

        byte[] head = [.. "OpusHead"u8, 1, 2, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 0];
        byte[] duration = BitConverter.GetBytes((float)(seconds * 1000));
        Array.Reverse(duration);
        return
        [
            .. Element(0x1A45DFA3, Element(0x4282, "webm"u8.ToArray())),
            .. Element(0x18538067,
            [
                .. Element(0x1549A966, [.. Element(0x2AD7B1, [0x0F, 0x42, 0x40]), .. Element(0x4489, duration)]),
                .. Element(0x1654AE6B, Element(0xAE, [.. Element(0xD7, [1]), .. Element(0x86, "A_OPUS"u8.ToArray()), .. Element(0x63A2, head)])),
                .. Element(0x1F43B675, [.. Element(0xE7, [0]), .. blocks]),
            ]),
        ];
    }

    private static byte[] Element(uint id, byte[] data)
    {
        var idBytes = BitConverter.GetBytes(id).Reverse().SkipWhile(b => b == 0).ToArray();
        var size = BitConverter.GetBytes((1UL << 56) + (uint)data.Length); // an 8-byte size
        Array.Reverse(size);
        return [.. idBytes, .. size, .. data];
    }
}
