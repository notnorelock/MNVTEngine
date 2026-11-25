// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Galaxy;

namespace MNVTEngine.Build.Tests;

/// <summary>The headless dedicated server runs a real game's scene with no window and accepts a client.</summary>
public sealed class DedicatedServerTests
{
    [Fact]
    public async Task HeadlessServer_RunsARealGameScene_AndAcceptsAClient()
    {
        string project = Path.Combine(EngineLocation.FindRoot(), "samples", "Platformer");
        using var cts = new CancellationTokenSource();
        NetworkServer? started = null;
        Task<int> host = Task.Run(() => DedicatedServerHost.RunAsync(project, port: 0, tickRate: 60, NullLoggerFactory.Instance, cts.Token, server => started = server), cts.Token);

        var clock = Stopwatch.StartNew();
        while (started is null && !host.IsFaulted && clock.ElapsedMilliseconds < 90_000)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        if (host.IsFaulted)
        {
            await host; // Surface a loading failure.
        }

        Assert.NotNull(started);
        Assert.True(started!.BoundPort != 0);

        using var client = new GalaxyClient();
        bool connected = false;
        client.Connected += () => connected = true;
        Assert.True(client.Connect("127.0.0.1", started.BoundPort));

        clock.Restart();
        while (!connected && clock.ElapsedMilliseconds < 10_000)
        {
            client.Tick();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.True(connected);

        cts.Cancel();
        await host;
    }
}
