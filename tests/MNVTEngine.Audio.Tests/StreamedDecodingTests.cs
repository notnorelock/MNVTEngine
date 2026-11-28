// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Audio;

namespace MNVTEngine.Audio.Tests;

public sealed class StreamedDecodingTests
{
    private static readonly string Tone = Path.Combine(AppContext.BaseDirectory, "Audio", "tone-fragmented.m4a");

    [Fact]
    public void AacInFragmentedMp4_DecodesOnAnyPlatform_AndSeeks()
    {
        using var source = AudioDecoder.Open(new MemoryStream(File.ReadAllBytes(Tone)), "tone.m4a");

        Assert.Equal((2, 44100, (long?)null), (source.Channels, source.SampleRate, source.Frames));
        var clip = AudioDecoder.ReadAll(source, "tone.m4a");
        Assert.Equal(44 * 1024, clip.Samples.Length / 2); // 44 frames of 1024
        Assert.InRange(WebmOpusTests.Rms(clip.Samples.AsSpan(20000, 40000)), 4000, 7000); // the tone, at 8000 peak

        source.Seek(30000); // in the second fragment
        Assert.Equal((44 * 1024) - 30000, AudioDecoder.ReadAll(source, "tone.m4a").Samples.Length / 2);
        source.Seek(0);
        Assert.Equal(44 * 1024, AudioDecoder.ReadAll(source, "tone.m4a").Samples.Length / 2);
    }

    [Fact]
    public void AFileStillBeingWritten_PlaysAsItArrives()
    {
        // what a download gives: a few bytes per read, and no length to look at
        using var webm = AudioDecoder.Open(new Trickle(WebmOpusTests.Webm(seconds: 2)), "tone.webm");
        using var mp4 = AudioDecoder.Open(new Trickle(File.ReadAllBytes(Tone)), "tone.m4a");

        Assert.InRange(AudioDecoder.ReadAll(webm, "tone.webm").Samples.Length / 2, (2 * 48000) - 1000, 2 * 48000);
        Assert.Equal(44 * 1024, AudioDecoder.ReadAll(mp4, "tone.m4a").Samples.Length / 2);
    }

    private sealed class Trickle(byte[] data) : Stream
    {
        private readonly MemoryStream _data = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException("still growing");

        public override long Position { get => _data.Position; set => _data.Position = value; }

        public override int Read(byte[] buffer, int offset, int count) => _data.Read(buffer, offset, Math.Min(count, 64));

        public override long Seek(long offset, SeekOrigin origin) => _data.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
