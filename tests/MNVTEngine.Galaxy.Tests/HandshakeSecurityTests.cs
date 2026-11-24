// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using MNVTEngine.Galaxy.Security;
using MNVTEngine.Galaxy.Transport;

namespace MNVTEngine.Galaxy.Tests;

/// <summary>Server identity pinning (anti man-in-the-middle) and the stateless connection cookie.</summary>
public sealed class HandshakeSecurityTests
{
    [Fact]
    public void APinnedClient_ConnectsToTheMatchingSignedServer()
    {
        using var identity = GalaxyIdentity.Create();
        using var server = new GalaxyServer();
        int port = server.Start(0, maxClients: 8, identity: identity).Port;
        using var client = new GalaxyClient();
        bool connected = false;
        client.Connected += () => connected = true;

        Assert.True(client.Connect("127.0.0.1", port, identity.PublicKey));
        Run(server, client, () => connected, 5000);
        Assert.True(connected);
    }

    [Fact]
    public void APinnedClient_RejectsAServerWithTheWrongKey()
    {
        using var identity = GalaxyIdentity.Create();
        using var wrongKey = GalaxyIdentity.Create();
        using var server = new GalaxyServer();
        int port = server.Start(0, maxClients: 8, identity: identity).Port;
        using var client = new GalaxyClient();
        bool connected = false;
        DisconnectReason? reason = null;
        client.Connected += () => connected = true;
        client.Disconnected += r => reason = r;

        client.Connect("127.0.0.1", port, wrongKey.PublicKey);
        Run(server, client, () => reason is not null, 3000);

        Assert.False(connected);
        Assert.Equal(DisconnectReason.ProtocolError, reason);
    }

    [Fact]
    public void APinnedClient_RejectsAnUnsignedServer()
    {
        using var identity = GalaxyIdentity.Create();
        using var server = new GalaxyServer();
        int port = server.Start(0, maxClients: 8).Port; // No identity: the accept is unsigned.
        using var client = new GalaxyClient();
        bool connected = false;
        DisconnectReason? reason = null;
        client.Connected += () => connected = true;
        client.Disconnected += r => reason = r;

        client.Connect("127.0.0.1", port, identity.PublicKey);
        Run(server, client, () => reason is not null, 3000);

        Assert.False(connected);
        Assert.Equal(DisconnectReason.ProtocolError, reason);
    }

    [Fact]
    public void TheServer_AnswersAnUncookiedRequestWithAChallenge_AndKeepsNoState()
    {
        var net = new TestNetwork(seed: 42);
        var serverAddress = new IPEndPoint(IPAddress.Parse("::1"), 2);
        var clientAddress = new IPEndPoint(IPAddress.Parse("::1"), 3);
        ITransport serverTransport = net.Host(serverAddress);
        ITransport clientTransport = net.Host(clientAddress);

        using var server = new GalaxyServer();
        server.Start(0, maxClients: 8, identity: null, transport: serverTransport);

        using ECDiffieHellman keyExchange = SessionCrypto.CreateKeyExchange(out byte[] publicKey);
        byte[] request = new byte[GalaxyConstants.MaxDatagramSize];
        int length = HandshakePackets.BuildConnectRequest(request, publicKey, cookie: []);
        clientTransport.Send(request.AsSpan(0, length), serverAddress);

        server.Tick();

        Assert.Empty(server.Clients); // No state allocated before the cookie is proven.
        byte[] reply = new byte[GalaxyConstants.MaxDatagramSize];
        Assert.True(clientTransport.TryReceive(reply, out int replyLength, out _));
        Assert.Equal((byte)PacketType.ConnectChallenge, reply[0]);
        Assert.True(replyLength > 1);
    }

    [Fact]
    public void TheCookie_ValidatesOnlyForItsEndpointKeyAndTimeWindow()
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        var endpoint = new IPEndPoint(IPAddress.Parse("::1"), 1234);
        byte[] key = [1, 2, 3, 4, 5, 6, 7, 8];
        Span<byte> cookie = stackalloc byte[ConnectionCookie.Size];
        ConnectionCookie.Write(cookie, secret, endpoint, key, nowMs: 1000);

        Assert.True(ConnectionCookie.Verify(cookie, secret, endpoint, key, 1000));
        Assert.True(ConnectionCookie.Verify(cookie, secret, endpoint, key, 1000 + 10_000));   // Previous bucket still valid.
        Assert.False(ConnectionCookie.Verify(cookie, secret, endpoint, key, 1000 + 25_000));  // Too old.
        Assert.False(ConnectionCookie.Verify(cookie, secret, new IPEndPoint(IPAddress.Parse("::1"), 9999), key, 1000));
        Assert.False(ConnectionCookie.Verify(cookie, secret, endpoint, [9, 9, 9], 1000));
        Assert.False(ConnectionCookie.Verify(cookie, RandomNumberGenerator.GetBytes(32), endpoint, key, 1000));
    }

    private static void Run(GalaxyServer server, GalaxyClient client, Func<bool> done, int timeoutMs)
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
