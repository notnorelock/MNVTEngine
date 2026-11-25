// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MNVTEngine.Core.Logging;
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
        using var logs = new InMemoryLoggerProvider();
        using var loggerFactory = new LoggerFactory([logs]);
        NetworkServer? started = null;
        Task<int> host = Task.Run(() => DedicatedServerHost.RunAsync(project, port: 0, tickRate: 60, loggerFactory, cts.Token, server => started = server), cts.Token);

        // Compiling the game's scripts is slow on a loaded CI runner, alongside the export tests.
        var clock = Stopwatch.StartNew();
        while (started is null && !host.IsCompleted && clock.Elapsed < TimeSpan.FromMinutes(5))
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        string Log() => string.Join('\n', logs.Snapshot().Select(e => e.Message));
        if (host.IsCompleted)
        {
            Assert.Fail($"The server exited with {await host} before listening:\n{Log()}"); // a fault is rethrown by the await
        }

        Assert.True(started is not null, $"The server did not start listening:\n{Log()}");
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
