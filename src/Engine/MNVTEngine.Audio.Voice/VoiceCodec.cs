// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using Concentus;
using Concentus.Enums;

namespace MNVTEngine.Audio.Voice;

/// <summary>The voice format: 48 kHz mono 16-bit, in 20 ms frames.</summary>
public static class VoiceFormat
{
    public const int SampleRate = 48000;

    public const int FrameMilliseconds = 20;

    /// <summary>Samples in one frame (960).</summary>
    public const int FrameSamples = SampleRate * FrameMilliseconds / 1000;

    /// <summary>The largest packet the encoder writes; a received packet above it is not a voice packet.</summary>
    public const int MaxPacketBytes = 400;
}

/// <summary>
/// Opus encoding of voice frames (Concentus, managed): speech-tuned, variable bitrate around <see cref="Bitrate"/>, with
/// forward error correction so the receiver can rebuild a lost frame from the next one. One per speaker, one thread.
/// </summary>
public sealed class VoiceEncoder : IDisposable
{
    private readonly IOpusEncoder _encoder;

    /// <param name="bitrate">Bits per second; 24 kbps is clear speech.</param>
    /// <param name="expectedLossPercent">How much loss to protect against with FEC (0-100).</param>
    public VoiceEncoder(int bitrate = 24000, int expectedLossPercent = 10)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bitrate, 6000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitrate, 128000);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLossPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(expectedLossPercent, 100);
        _encoder = OpusCodecFactory.CreateEncoder(VoiceFormat.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = bitrate;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = expectedLossPercent;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        Bitrate = bitrate;
    }

    public int Bitrate { get; }

    /// <summary>Encodes one frame of <see cref="VoiceFormat.FrameSamples"/> samples; returns the packet's length.</summary>
    /// <param name="packet">At least <see cref="VoiceFormat.MaxPacketBytes"/> bytes.</param>
    public int Encode(ReadOnlySpan<short> frame, Span<byte> packet)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(frame.Length, VoiceFormat.FrameSamples);
        ArgumentOutOfRangeException.ThrowIfLessThan(packet.Length, VoiceFormat.MaxPacketBytes);
        return _encoder.Encode(frame, VoiceFormat.FrameSamples, packet, VoiceFormat.MaxPacketBytes);
    }

    public void Dispose() => (_encoder as IDisposable)?.Dispose();
}

/// <summary>
/// Opus decoding of one speaker's packets, in order: <see cref="Decode"/> a packet, <see cref="Recover"/> a lost frame
/// from the packet after it (FEC), or <see cref="Conceal"/> one with none. One per speaker, one thread.
/// </summary>
public sealed class VoiceDecoder : IDisposable
{
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(VoiceFormat.SampleRate, 1);

    /// <summary>Decodes a packet into <paramref name="frame"/> (<see cref="VoiceFormat.FrameSamples"/>); false when it is damaged (the frame is concealed instead).</summary>
    public bool Decode(ReadOnlySpan<byte> packet, Span<short> frame) => Run(packet, frame, fec: false);

    /// <summary>Rebuilds the frame before <paramref name="nextPacket"/> from its error correction; concealed when it has none.</summary>
    public bool Recover(ReadOnlySpan<byte> nextPacket, Span<short> frame) => Run(nextPacket, frame, fec: true);

    /// <summary>A lost frame with nothing to rebuild it from: Opus fills it in from what came before.</summary>
    public void Conceal(Span<short> frame) => Run([], frame, fec: false);

    public void Dispose() => (_decoder as IDisposable)?.Dispose();

    private bool Run(ReadOnlySpan<byte> packet, Span<short> frame, bool fec)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frame.Length, VoiceFormat.FrameSamples);
        if (packet.Length > VoiceFormat.MaxPacketBytes)
        {
            Conceal(frame);
            return false;
        }

        try
        {
            var samples = _decoder.Decode(packet, frame, VoiceFormat.FrameSamples, fec);
            frame[Math.Max(0, samples)..VoiceFormat.FrameSamples].Clear();
            return true;
        }
        catch (Exception ex) when (ex is OpusException or ArgumentException or IndexOutOfRangeException)
        {
            // Packets come from the network: a damaged one becomes a concealed frame, not a crash.
            if (packet.Length == 0)
            {
                frame[..VoiceFormat.FrameSamples].Clear();
                return false;
            }

            Conceal(frame);
            return false;
        }
    }
}
