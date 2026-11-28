// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using System.Net;

namespace MNVTEngine.Galaxy.Tests;

/// <summary>Round-trip probes to a server over a real UDP loopback socket, without connecting.</summary>
public sealed class ProbeTests
{
    [Fact]
    public async Task AServer_AnswersAProbe_WithoutAConnection()
    {
        using var server = new GalaxyServer();
        var port = server.Start(0, maxClients: 1).Port;
        var ping = GalaxyProbe.PingAsync(new IPEndPoint(IPAddress.Loopback, port), TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        while (!ping.IsCompleted)
        {
            server.Tick();
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(await ping);
    }

    [Fact]
    public async Task NoServer_GivesNoRoundTrip()
    {
        using var server = new GalaxyServer();
        var port = server.Start(0, maxClients: 1).Port;
        server.Dispose(); // the port is closed again
        Assert.Null(await GalaxyProbe.PingAsync(new IPEndPoint(IPAddress.Loopback, port), TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OnlyAProbeOfThisVersion_IsAnswered_AndTheAnswerIsNoLarger()
    {
        var answer = new byte[GalaxyProbe.Size];
        var probe = new byte[GalaxyProbe.Size];
        probe[0] = 6;
        BitConverter.TryWriteBytes(probe.AsSpan(1, 4), GalaxyConstants.ProtocolVersion);
        Assert.True(GalaxyProbe.TryAnswer(probe, answer));
        Assert.Equal(7, answer[0]);
        Assert.False(GalaxyProbe.TryAnswer(probe.AsSpan(0, 12), answer)); // short
        BitConverter.TryWriteBytes(probe.AsSpan(1, 4), GalaxyConstants.ProtocolVersion + 1);
        Assert.False(GalaxyProbe.TryAnswer(probe, answer));
    }
}
