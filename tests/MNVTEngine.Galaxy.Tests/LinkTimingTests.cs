// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Diagnostics;

namespace MNVTEngine.Galaxy.Tests;

/// <summary>Round trip and the peer's clock: the estimator, and both sides of a real loopback connection.</summary>
public sealed class LinkTimingTests
{
    [Fact]
    public void TheRoundTrip_IsSmoothed_AndTheClock_ComesFromTheFastestRecentSample()
    {
        var timing = new LinkTiming();
        Assert.Null(timing.RoundTripMs);

        // The peer is 5000 ms ahead. A 100 ms trip guesses the offset from half of it; a 20 ms one is closer.
        timing.Observe(sentMs: 0, receivedMs: 100, peerUnixMs: 1_005_030, localUnixMs: 1_000_100); // true one-way out: 30 ms
        Assert.Equal((100, 100), (timing.RoundTripMs, timing.LastRoundTripMs));
        Assert.Equal(4980, timing.ClockOffsetMs);

        timing.Observe(sentMs: 1000, receivedMs: 1020, peerUnixMs: 1_006_010, localUnixMs: 1_001_020);
        Assert.Equal(100 + ((20 - 100) / 8.0), timing.RoundTripMs);
        Assert.Equal(5000, timing.ClockOffsetMs);

        timing.Observe(sentMs: 2000, receivedMs: 2300, peerUnixMs: 1_007_250, localUnixMs: 1_002_300); // slow and lopsided
        Assert.Equal(5000, timing.ClockOffsetMs); // the fast sample still wins

        for (var i = 0; i < LinkTiming.Window; i++)
        {
            timing.Observe(sentMs: 3000 + (i * 1000), receivedMs: 3200 + (i * 1000), peerUnixMs: 1_008_100 + (i * 1000), localUnixMs: 1_003_200 + (i * 1000));
        }

        Assert.Equal(5000, timing.ClockOffsetMs); // the fast one aged out; these agree on 5000
    }

    [Fact]
    public void BothSides_MeasureTheRoundTrip_AndTheClientKnowsTheServersClock()
    {
        using var server = new GalaxyServer();
        int port = server.Start(0, maxClients: 2).Port;
        uint? id = null;
        server.ClientConnected += client => id = client;
        using var client = new GalaxyClient();
        Assert.True(client.Connect("127.0.0.1", port));

        var clock = Stopwatch.StartNew();
        while ((client.RoundTripMs is null || id is null || server.RoundTripMs(id.Value) is null) && clock.ElapsedMilliseconds < 5000)
        {
            server.Tick();
            client.Tick();
            Thread.Sleep(1);
        }

        Assert.InRange(client.RoundTripMs!.Value, 0, 1000);
        Assert.InRange(server.RoundTripMs(id!.Value)!.Value, 0, 1000);
        Assert.InRange(client.ServerClockOffsetMs!.Value, -1000, 1000); // the same machine's clock
        Assert.InRange(client.ServerUnixTimeMs!.Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), -1000, 1000);
        Assert.Null(server.RoundTripMs(12345));
    }
}
