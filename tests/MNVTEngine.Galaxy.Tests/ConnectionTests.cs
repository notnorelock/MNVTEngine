// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Galaxy.Security;
using MNVTEngine.Galaxy.Transport;

namespace MNVTEngine.Galaxy.Tests;

public sealed class ConnectionTests
{
    private static readonly IPEndPoint ClientAddress = new(IPAddress.Parse("::1"), 1);
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("::1"), 2);

    [Fact]
    public void ReliableMessages_AllArriveInOrder_OverALossyReorderingDuplicatingNetwork()
    {
        var net = new TestNetwork(seed: 20261124) { Loss = 0.3, Duplicate = 0.1, Reorder = true };
        (SessionCrypto clientCrypto, SessionCrypto serverCrypto) = Handshakes.Pair();
        ITransport clientTransport = net.Host(ClientAddress);
        ITransport serverTransport = net.Host(ServerAddress);

        var client = new Connection(1, ServerAddress, clientTransport, clientCrypto, 0, NullLogger.Instance);
        var server = new Connection(1, ClientAddress, serverTransport, serverCrypto, 0, NullLogger.Instance);

        const int count = 80;
        for (int i = 0; i < count; i++)
        {
            Message message = Message.Create(i);
            message.Write(i);
            client.Send(message, DeliveryMethod.Reliable, 0);
        }

        var received = new List<int>();
        var inbound = new List<Message>();
        long now = 0;
        for (int iteration = 0; iteration < 5000 && received.Count < count; iteration++)
        {
            now += 10;
            client.Tick(now);
            server.Tick(now);
            Drain(serverTransport, server, now, inbound, received);
            Drain(clientTransport, client, now, inbound, null); // Client drains acks only.
        }

        Assert.Equal(count, received.Count);
        Assert.Equal(Enumerable.Range(0, count).ToList(), received);
    }

    [Fact]
    public void UnreliableMessages_ArriveWhenTheNetworkIsClean()
    {
        var net = new TestNetwork(seed: 7);
        (SessionCrypto clientCrypto, SessionCrypto serverCrypto) = Handshakes.Pair();
        ITransport clientTransport = net.Host(ClientAddress);
        ITransport serverTransport = net.Host(ServerAddress);

        var client = new Connection(1, ServerAddress, clientTransport, clientCrypto, 0, NullLogger.Instance);
        var server = new Connection(1, ClientAddress, serverTransport, serverCrypto, 0, NullLogger.Instance);

        const int count = 20;
        for (int i = 0; i < count; i++)
        {
            Message message = Message.Create(i);
            message.Write(i);
            client.Send(message, DeliveryMethod.Unreliable, 0);
        }

        var received = new List<int>();
        var inbound = new List<Message>();
        Drain(serverTransport, server, 0, inbound, received);

        Assert.Equal(count, received.Count);
        Assert.Equal(Enumerable.Range(0, count).ToHashSet(), received.ToHashSet());
    }

    [Fact]
    public void Tick_ReportsTimeout_AfterSilence()
    {
        var net = new TestNetwork(seed: 1);
        (SessionCrypto clientCrypto, _) = Handshakes.Pair();
        ITransport transport = net.Host(ClientAddress);
        var connection = new Connection(1, ServerAddress, transport, clientCrypto, 0, NullLogger.Instance);

        Assert.True(connection.Tick(GalaxyConstants.TimeoutMilliseconds - 1));
        Assert.False(connection.Tick(GalaxyConstants.TimeoutMilliseconds + 1));
    }

    private static void Drain(ITransport transport, Connection connection, long now, List<Message> inbound, List<int>? sink)
    {
        byte[] buffer = new byte[GalaxyConstants.MaxDatagramSize];
        while (transport.TryReceive(buffer, out int length, out _))
        {
            connection.Receive(buffer.AsSpan(0, length), now, inbound, out _);
            foreach (Message message in inbound)
            {
                sink?.Add(message.ReadInt());
                message.Release();
            }

            inbound.Clear();
        }
    }
}
