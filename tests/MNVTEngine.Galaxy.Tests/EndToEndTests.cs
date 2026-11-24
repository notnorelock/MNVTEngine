// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Diagnostics;

namespace MNVTEngine.Galaxy.Tests;

/// <summary>Client and server over a real UDP loopback socket: the full handshake, encryption and message flow.</summary>
public sealed class EndToEndTests
{
    [Fact]
    public void Client_Connects_ExchangesMessagesBothWays_AndDisconnects()
    {
        using var server = new GalaxyServer();
        int port = server.Start(0, maxClients: 8).Port;

        var serverReceived = new List<string>();
        server.MessageReceived += (id, message) =>
        {
            serverReceived.Add(message.ReadString());
            Message reply = Message.Create(2);
            reply.Write("pong");
            server.Send(id, reply);
        };

        uint? disconnected = null;
        server.ClientDisconnected += (id, _) => disconnected = id;

        using var client = new GalaxyClient();
        bool connected = false;
        var clientReceived = new List<string>();
        client.Connected += () => connected = true;
        client.MessageReceived += message => clientReceived.Add(message.ReadString());

        Assert.True(client.Connect("127.0.0.1", port));
        Pump(server, client, () => connected, 5000);
        Assert.True(connected);

        Message ping = Message.Create(1);
        ping.Write("ping");
        client.Send(ping, DeliveryMethod.Reliable);

        Pump(server, client, () => clientReceived.Count > 0, 5000);
        Assert.Equal(["ping"], serverReceived);
        Assert.Equal(["pong"], clientReceived);

        client.Disconnect();
        Pump(server, client, () => disconnected is not null, 5000);
        Assert.NotNull(disconnected);
        Assert.Empty(server.Clients);
    }

    [Fact]
    public void Server_RefusesConnections_BeyondItsLimit()
    {
        using var server = new GalaxyServer();
        int port = server.Start(0, maxClients: 1).Port;

        using var first = new GalaxyClient();
        using var second = new GalaxyClient();
        bool firstConnected = false;
        DisconnectReason? secondReason = null;
        first.Connected += () => firstConnected = true;
        second.Disconnected += reason => secondReason = reason;

        Assert.True(first.Connect("127.0.0.1", port));
        Pump(server, first, () => firstConnected, 5000);
        Assert.True(firstConnected);

        Assert.True(second.Connect("127.0.0.1", port));
        for (var clock = Stopwatch.StartNew(); clock.ElapsedMilliseconds < 5000 && secondReason is null;)
        {
            server.Tick();
            first.Tick();
            second.Tick();
            Thread.Sleep(1);
        }

        Assert.Equal(DisconnectReason.Rejected, secondReason);
        Assert.Single(server.Clients);
    }

    private static void Pump(GalaxyServer server, GalaxyClient client, Func<bool> done, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < timeoutMs)
        {
            server.Tick();
            client.Tick();
            Thread.Sleep(1);
        }
    }
}
