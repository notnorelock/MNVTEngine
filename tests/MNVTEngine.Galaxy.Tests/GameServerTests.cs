// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Diagnostics;
using MNVTEngine.Galaxy.Server;

namespace MNVTEngine.Galaxy.Tests;

/// <summary>The dedicated-server framework: a subclass receives messages, and a throwing hook does not take the
/// server down.</summary>
public sealed class GameServerTests
{
    private sealed class EchoServer(GameServerOptions options) : GameServer(options)
    {
        public List<string> Received { get; } = [];

        public bool ThrowOnNextMessage { get; set; }

        protected override void OnMessage(uint clientId, Message message)
        {
            string text = message.ReadString();
            if (ThrowOnNextMessage)
            {
                ThrowOnNextMessage = false;
                throw new InvalidOperationException("Simulated handler bug.");
            }

            Received.Add(text);
            Message reply = Message.Create(2);
            reply.Write("echo:" + text);
            Send(clientId, reply);
        }
    }

    private sealed class SlowServer(GameServerOptions options) : GameServer(options)
    {
        public int SleepMs { get; set; }

        protected override void OnTick(float deltaSeconds) => Thread.Sleep(SleepMs);
    }

    [Fact]
    public void Timings_MeasureEachTick_AgainstTheTickRate()
    {
        using var server = new SlowServer(new GameServerOptions { Port = 0, MaxClients = 1, TickRate = 50 }); // 20 ms a tick
        server.Start();
        Assert.Equal((0L, 20.0), (server.Timings.Ticks, server.Timings.BudgetMs));

        server.TickOnce(0.02f);
        server.SleepMs = 30;
        server.TickOnce(0.02f);

        var timings = server.Timings;
        Assert.Equal(2, timings.Ticks);
        Assert.Equal(1, timings.OverBudget);
        Assert.True(timings.MaxMs >= 30 && timings.LastMs == timings.MaxMs);
        Assert.InRange(timings.AverageMs, timings.MaxMs / 2, timings.MaxMs);

        server.ResetTimings();
        Assert.Equal(0, server.Timings.Ticks);
    }

    [Fact]
    public void AThrowingHook_IsIsolated_AndTheServerKeepsServing()
    {
        using var server = new EchoServer(new GameServerOptions { Port = 0, MaxClients = 4, TickRate = 60 });
        int port = server.Start().Port;

        using var client = new GalaxyClient();
        bool connected = false;
        var replies = new List<string>();
        client.Connected += () => connected = true;
        client.MessageReceived += message => replies.Add(message.ReadString());

        Assert.True(client.Connect("127.0.0.1", port));
        Pump(server, client, () => connected, 5000);
        Assert.True(connected);

        // First message makes the handler throw; the guard must swallow it and keep the server alive.
        server.ThrowOnNextMessage = true;
        Message first = Message.Create(1);
        first.Write("boom");
        client.Send(first, DeliveryMethod.Reliable);
        Pump(server, client, () => false, 300); // Let it be processed (and thrown).

        // A second message must still be handled and answered.
        Message second = Message.Create(1);
        second.Write("hello");
        client.Send(second, DeliveryMethod.Reliable);
        Pump(server, client, () => replies.Count > 0, 5000);

        Assert.Equal(["hello"], server.Received);
        Assert.Equal(["echo:hello"], replies);
    }

    private static void Pump(EchoServer server, GalaxyClient client, Func<bool> done, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < timeoutMs)
        {
            server.TickOnce(0.016f);
            client.Tick();
            Thread.Sleep(1);
        }
    }
}
