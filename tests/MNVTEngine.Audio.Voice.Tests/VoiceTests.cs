// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

namespace MNVTEngine.Audio.Voice.Tests;

/// <summary>Voice: Opus round trips, the jitter buffer's ordering, loss and pacing, voice activity and framing.</summary>
public sealed class VoiceTests
{
    [Fact]
    public void Speech_SurvivesOpus_AtTheVoiceBitrate()
    {
        using var encoder = new VoiceEncoder();
        using var decoder = new VoiceDecoder();
        var packet = new byte[VoiceFormat.MaxPacketBytes];
        var output = new short[VoiceFormat.FrameSamples];
        float level = -96;
        for (var f = 0; f < 25; f++) // half a second: the codec settles in
        {
            var frame = Tone(f);
            var length = encoder.Encode(frame, packet);
            Assert.InRange(length, 1, 200); // ~60 bytes at 24 kbps
            Assert.True(decoder.Decode(packet.AsSpan(0, length), output));
            level = VoiceActivity.Loudness(output);
        }

        Assert.InRange(level, VoiceActivity.Loudness(Tone(0)) - 6, VoiceActivity.Loudness(Tone(0)) + 3);
    }

    [Fact]
    public void DamagedOrMissingPackets_AreConcealed_NotThrown()
    {
        using var decoder = new VoiceDecoder();
        var frame = new short[VoiceFormat.FrameSamples];

        decoder.Decode([0xFF, 0xFF, 0xFF], frame); // whatever Opus makes of it, it does not throw
        Assert.False(decoder.Decode(new byte[VoiceFormat.MaxPacketBytes + 1], frame));
        decoder.Conceal(frame);
        Assert.Throws<ArgumentOutOfRangeException>(() => new VoiceEncoder().Encode(new short[10], new byte[VoiceFormat.MaxPacketBytes]));
    }

    [Fact]
    public void TheJitterBuffer_WaitsForItsTarget_ReordersAndReportsLoss()
    {
        var buffer = new VoiceJitterBuffer();

        Assert.True(buffer.Add(100, [0]));
        Assert.Equal(VoiceSlot.Silent, buffer.Next(out _)); // one frame: still filling
        Assert.True(buffer.Add(102, [2]));
        Assert.True(buffer.Add(101, [1])); // out of order: put back in place
        Assert.True(buffer.Add(104, [4]));
        Assert.False(buffer.Add(104, [4])); // a duplicate
        Assert.False(buffer.Add(99, [9])); // before the talk spurt began

        byte[]?[] played = new byte[3][];
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(VoiceSlot.Packet, buffer.Next(out played[i]));
        }

        Assert.Equal([0, 1, 2], played.Select(p => p![0]));
        Assert.Equal(VoiceSlot.Lost, buffer.Next(out var fec)); // 103 never came
        Assert.Equal([4], fec); // the next packet, for its error correction
        Assert.Equal(VoiceSlot.Packet, buffer.Next(out var last));
        Assert.Equal([4], last);
        Assert.False(buffer.Add(103, [3])); // its time has passed
    }

    [Fact]
    public void RunningDry_GrowsTheDelay_AndASmoothRunShrinksIt()
    {
        var buffer = new VoiceJitterBuffer();
        ushort sequence = 0;
        Feed(buffer, ref sequence, VoiceJitterBuffer.MinTargetFrames);
        Assert.Equal(VoiceSlot.Packet, buffer.Next(out _));
        Assert.Equal(VoiceSlot.Packet, buffer.Next(out _));
        Assert.Equal(VoiceSlot.Silent, buffer.Next(out _)); // dry
        Assert.Equal(VoiceJitterBuffer.MinTargetFrames + 1, buffer.TargetFrames);
        Assert.False(buffer.IsPlaying);

        Feed(buffer, ref sequence, buffer.TargetFrames);
        for (var i = 0; i < VoiceJitterBuffer.ShrinkAfterFrames; i++)
        {
            Feed(buffer, ref sequence, 1);
            Assert.Equal(VoiceSlot.Packet, buffer.Next(out _));
        }

        Assert.Equal(VoiceJitterBuffer.MinTargetFrames, buffer.TargetFrames);
    }

    [Fact]
    public void SequenceNumbers_Wrap_AndAFarJump_RestartsTheStream()
    {
        var buffer = new VoiceJitterBuffer();
        buffer.Add(65535, [1]);
        buffer.Add(0, [2]);
        Assert.Equal(VoiceSlot.Packet, buffer.Next(out var a));
        Assert.Equal(VoiceSlot.Packet, buffer.Next(out var b));
        Assert.Equal([1], a);
        Assert.Equal([2], b);

        buffer.Add(5000, [9]);
        buffer.Add(5001, [9]);
        Assert.Equal(VoiceSlot.Packet, buffer.Next(out _));
        Assert.False(buffer.Add(1, new byte[VoiceFormat.MaxPacketBytes + 1]));
    }

    [Fact]
    public void VoiceActivity_HoldsThroughShortPauses()
    {
        var activity = new VoiceActivity { HangoverFrames = 2 };
        var quiet = new short[VoiceFormat.FrameSamples];

        Assert.False(activity.Update(quiet)); // silence from the start is not speech
        Assert.True(activity.Update(Tone(0)));
        Assert.True(activity.Update(quiet));
        Assert.True(activity.Update(quiet));
        Assert.False(activity.Update(quiet));
        Assert.Equal(-96f, activity.LevelDb);
    }

    [Fact]
    public void TheFramer_CutsAnyBlocks_IntoWholeFrames()
    {
        var framer = new VoiceFramer();
        framer.Push(new short[700]);
        Assert.Equal(0, framer.Count);
        framer.Push(Enumerable.Repeat((short)7, 1300).ToArray()); // 2000 samples: two frames and 80 over

        var frame = new short[VoiceFormat.FrameSamples];
        Assert.True(framer.TryTake(frame));
        Assert.Equal(7, frame[^1]);
        Assert.True(framer.TryTake(frame));
        Assert.False(framer.TryTake(frame));
    }

    private static void Feed(VoiceJitterBuffer buffer, ref ushort sequence, int count)
    {
        for (var i = 0; i < count; i++)
        {
            buffer.Add(sequence++, [1]);
        }
    }

    // A 220 Hz tone with an overtone at a speaking level, continuous across frames.
    private static short[] Tone(int frameIndex)
    {
        var frame = new short[VoiceFormat.FrameSamples];
        for (var i = 0; i < frame.Length; i++)
        {
            var t = (frameIndex * VoiceFormat.FrameSamples + i) / (double)VoiceFormat.SampleRate;
            frame[i] = (short)(6000 * Math.Sin(2 * Math.PI * 220 * t) + 2000 * Math.Sin(2 * Math.PI * 660 * t));
        }

        return frame;
    }
}
