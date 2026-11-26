// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

namespace MNVTEngine.Audio.Voice;

/// <summary>Cuts recorded samples, which arrive in blocks of any size, into whole voice frames.</summary>
public sealed class VoiceFramer
{
    private readonly short[] _pending = new short[VoiceFormat.FrameSamples];
    private readonly Queue<short[]> _frames = new();
    private int _filled;

    /// <summary>Most whole frames kept unread (about a second); older ones are dropped.</summary>
    public const int MaxFrames = 50;

    public int Count => _frames.Count;

    public void Push(ReadOnlySpan<short> samples)
    {
        while (!samples.IsEmpty)
        {
            var take = Math.Min(samples.Length, _pending.Length - _filled);
            samples[..take].CopyTo(_pending.AsSpan(_filled));
            _filled += take;
            samples = samples[take..];
            if (_filled == _pending.Length)
            {
                _frames.Enqueue((short[])_pending.Clone());
                _filled = 0;
                if (_frames.Count > MaxFrames)
                {
                    _frames.Dequeue();
                }
            }
        }
    }

    /// <summary>The oldest whole frame; false when none is complete.</summary>
    public bool TryTake(Span<short> frame)
    {
        if (!_frames.TryDequeue(out var next))
        {
            return false;
        }

        next.CopyTo(frame);
        return true;
    }

    public void Clear()
    {
        _frames.Clear();
        _filled = 0;
    }
}

/// <summary>
/// Whether a frame is speech, by its loudness: above <see cref="ThresholdDb"/> it speaks, and it keeps speaking for
/// <see cref="HangoverFrames"/> quiet frames after, so the ends of words and short pauses are not cut.
/// </summary>
public sealed class VoiceActivity
{
    private int _quiet = int.MaxValue;

    /// <summary>Loudness (RMS, dB below full scale) a frame needs to count as speech.</summary>
    public float ThresholdDb { get; set; } = -42f;

    public int HangoverFrames { get; set; } = 15; // 300 ms

    /// <summary>The last frame's loudness in dB below full scale (-96 for silence).</summary>
    public float LevelDb { get; private set; } = -96f;

    public bool IsSpeaking => _quiet <= HangoverFrames;

    /// <summary>Looks at the next frame; returns whether it should be sent.</summary>
    public bool Update(ReadOnlySpan<short> frame)
    {
        LevelDb = Loudness(frame);
        _quiet = LevelDb >= ThresholdDb ? 0 : _quiet == int.MaxValue ? int.MaxValue : _quiet + 1;
        return IsSpeaking;
    }

    /// <summary>RMS loudness in dB below full scale, at least -96.</summary>
    public static float Loudness(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return -96f;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }

        var rms = Math.Sqrt(sum / samples.Length) / short.MaxValue;
        return rms <= 0 ? -96f : (float)Math.Max(-96, 20 * Math.Log10(rms));
    }
}

/// <summary>What <see cref="VoiceJitterBuffer.Next"/> gives for the next 20 ms.</summary>
public enum VoiceSlot
{
    /// <summary>Nothing to play: not started, or the speaker stopped (play nothing).</summary>
    Silent,

    /// <summary>The frame's packet arrived.</summary>
    Packet,

    /// <summary>The frame was lost but later ones came: rebuild it (from the next packet's FEC when given, else conceal).</summary>
    Lost,
}

/// <summary>
/// One speaker's packets, reordered and paced (docs/engine/voice.md): playback starts once <see cref="TargetFrames"/>
/// are waiting, takes one frame per 20 ms, and a gap with later packets waiting is a lost frame. Running dry stops
/// playback and grows the target (up to <see cref="MaxTargetFrames"/>); a long smooth run shrinks it again, so the
/// delay follows the network's jitter between 40 and 120 ms. Sequence numbers wrap at 65536.
/// </summary>
public sealed class VoiceJitterBuffer
{
    public const int MinTargetFrames = 2;
    public const int MaxTargetFrames = 6;

    /// <summary>Frames played without running dry before the target shrinks by one (5 s).</summary>
    public const int ShrinkAfterFrames = 250;

    /// <summary>A packet this far from the next expected one restarts the stream (the speaker started again).</summary>
    public const int MaxSpan = 50;

    private readonly Dictionary<ushort, byte[]> _packets = [];
    private ushort _next;
    private bool _playing;
    private int _smooth;

    public int TargetFrames { get; private set; } = MinTargetFrames;

    /// <summary>Packets waiting.</summary>
    public int Buffered => _packets.Count;

    public bool IsPlaying => _playing;

    /// <summary>Adds a received packet; false when it was late, a duplicate or too big.</summary>
    public bool Add(ushort sequence, ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty || packet.Length > VoiceFormat.MaxPacketBytes)
        {
            return false;
        }

        if (_packets.Count == 0 && !_playing)
        {
            _next = sequence; // a new talk spurt starts here
        }

        var ahead = (short)(sequence - _next);
        if (ahead < 0 && ahead > -MaxSpan)
        {
            return false; // its time has passed
        }

        if (ahead < 0 || ahead >= MaxSpan)
        {
            _packets.Clear(); // far off: the speaker's stream restarted
            _playing = false;
            _next = sequence;
        }

        return _packets.TryAdd(sequence, packet.ToArray());
    }

    /// <summary>The next 20 ms. For <see cref="VoiceSlot.Packet"/>, <paramref name="packet"/> is its packet; for <see cref="VoiceSlot.Lost"/>, the next packet if it arrived (for FEC).</summary>
    public VoiceSlot Next(out byte[]? packet)
    {
        packet = null;
        if (!_playing)
        {
            if (_packets.Count < TargetFrames)
            {
                return VoiceSlot.Silent;
            }

            _playing = true;
        }

        if (_packets.Count == 0)
        {
            _playing = false; // ran dry: wait for more next time
            _smooth = 0;
            TargetFrames = Math.Min(MaxTargetFrames, TargetFrames + 1);
            return VoiceSlot.Silent;
        }

        var current = _next++;
        if (++_smooth >= ShrinkAfterFrames && TargetFrames > MinTargetFrames)
        {
            TargetFrames--;
            _smooth = 0;
        }

        if (_packets.Remove(current, out packet))
        {
            return VoiceSlot.Packet;
        }

        packet = _packets.GetValueOrDefault(_next);
        return VoiceSlot.Lost;
    }

    public void Clear()
    {
        _packets.Clear();
        _playing = false;
    }
}

/// <summary>A speaker on the receiving side: its <see cref="VoiceJitterBuffer"/> and <see cref="VoiceDecoder"/>, giving PCM frames.</summary>
public sealed class VoiceReceiver : IDisposable
{
    private readonly VoiceDecoder _decoder = new();

    public VoiceJitterBuffer Buffer { get; } = new();

    /// <summary>Frames lost on the way and rebuilt.</summary>
    public int LostFrames { get; private set; }

    public bool Add(ushort sequence, ReadOnlySpan<byte> packet) => Buffer.Add(sequence, packet);

    /// <summary>Decodes the next 20 ms into <paramref name="frame"/>; false when there is nothing to play.</summary>
    public bool Next(Span<short> frame)
    {
        switch (Buffer.Next(out var packet))
        {
            case VoiceSlot.Packet:
                _decoder.Decode(packet, frame);
                return true;
            case VoiceSlot.Lost:
                LostFrames++;
                if (packet is not null)
                {
                    _decoder.Recover(packet, frame);
                }
                else
                {
                    _decoder.Conceal(frame);
                }

                return true;
            default:
                return false;
        }
    }

    public void Dispose() => _decoder.Dispose();
}
