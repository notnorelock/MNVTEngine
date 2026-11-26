// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

namespace MNVTEngine.Galaxy;

/// <summary>
/// Round-trip time and the peer's clock, from ping/pong samples (docs/architecture/networking.md, "Round-trip time and
/// clock"). The round trip is smoothed as TCP smooths it (1/8 of each new sample). The peer's clock is the offset of
/// its UTC time from this machine's, taken from the sample with the shortest round trip of the last
/// <see cref="Window"/>: the one whose half-RTT guess of the one-way delay is least wrong. Pure, so it is unit-tested.
/// </summary>
public sealed class LinkTiming
{
    /// <summary>How many recent samples the clock offset is chosen from.</summary>
    public const int Window = 8;

    private readonly (double RoundTrip, double Offset)[] _samples = new (double, double)[Window];
    private int _count, _next;

    /// <summary>The smoothed round trip in milliseconds; null before the first pong.</summary>
    public double? RoundTripMs { get; private set; }

    /// <summary>The latest round trip in milliseconds; null before the first pong.</summary>
    public double? LastRoundTripMs { get; private set; }

    /// <summary>The peer's UTC time minus this machine's, in milliseconds; null before the first pong.</summary>
    public double? ClockOffsetMs { get; private set; }

    /// <summary>A pong arrived.</summary>
    /// <param name="sentMs">When the ping left, on this side's monotonic clock (echoed by the peer).</param>
    /// <param name="receivedMs">When the pong arrived, on the same clock.</param>
    /// <param name="peerUnixMs">The peer's UTC time when it answered, in Unix milliseconds.</param>
    /// <param name="localUnixMs">This machine's UTC time when the pong arrived, in Unix milliseconds.</param>
    public void Observe(long sentMs, long receivedMs, long peerUnixMs, long localUnixMs)
    {
        var roundTrip = (double)Math.Max(0, receivedMs - sentMs);
        LastRoundTripMs = roundTrip;
        RoundTripMs = RoundTripMs is { } smoothed ? smoothed + ((roundTrip - smoothed) / 8) : roundTrip;
        _samples[_next] = (roundTrip, peerUnixMs + (roundTrip / 2) - localUnixMs);
        _next = (_next + 1) % Window;
        _count = Math.Min(_count + 1, Window);
        var best = _samples[0];
        for (var i = 1; i < _count; i++)
        {
            if (_samples[i].RoundTrip < best.RoundTrip)
            {
                best = _samples[i];
            }
        }

        ClockOffsetMs = best.Offset;
    }
}
