// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Net;
using MNVTEngine.Galaxy.Security;
using MNVTEngine.Galaxy.Transport;

namespace MNVTEngine.Galaxy.Tests;

/// <summary>An in-memory, deterministic datagram network that can drop, duplicate and reorder, so the reliability
/// layer is tested against adversarial conditions without real sockets or wall-clock time.</summary>
internal sealed class TestNetwork(int seed)
{
    private readonly Dictionary<IPEndPoint, List<(IPEndPoint From, byte[] Data)>> _inboxes = [];
    private readonly Random _random = new(seed);

    public double Loss { get; set; }

    public double Duplicate { get; set; }

    public bool Reorder { get; set; }

    public ITransport Host(IPEndPoint address)
    {
        _inboxes[address] = [];
        return new Endpoint(this, address);
    }

    private void Deliver(IPEndPoint from, IPEndPoint to, byte[] data)
    {
        if (!_inboxes.TryGetValue(to, out var box) || _random.NextDouble() < Loss)
        {
            return;
        }

        box.Add((from, data));
        if (_random.NextDouble() < Duplicate)
        {
            box.Add((from, data));
        }
    }

    private bool Receive(IPEndPoint self, Span<byte> buffer, out int length, out IPEndPoint remote)
    {
        length = 0;
        remote = self;
        var box = _inboxes[self];
        if (box.Count == 0)
        {
            return false;
        }

        int index = Reorder ? _random.Next(box.Count) : 0;
        (IPEndPoint from, byte[] data) = box[index];
        box.RemoveAt(index);
        if (data.Length > buffer.Length)
        {
            return false; // Too big for the caller's buffer: dropped, as a real socket would.
        }

        data.CopyTo(buffer);
        length = data.Length;
        remote = from;
        return true;
    }

    private sealed class Endpoint(TestNetwork network, IPEndPoint address) : ITransport
    {
        public IPEndPoint Bind(IPEndPoint local) => address;

        public bool TryReceive(Span<byte> buffer, out int length, out IPEndPoint remote) =>
            network.Receive(address, buffer, out length, out remote);

        public void Send(ReadOnlySpan<byte> data, IPEndPoint remote) => network.Deliver(address, remote, data.ToArray());

        public void Dispose()
        {
        }
    }
}

/// <summary>Builds a client/server session-crypto pair as the handshake would, for tests of the layers above it.</summary>
internal static class Handshakes
{
    public static (SessionCrypto Client, SessionCrypto Server) Pair()
    {
        using var client = SessionCrypto.CreateKeyExchange(out byte[] clientPublic);
        using var server = SessionCrypto.CreateKeyExchange(out byte[] serverPublic);
        SessionCrypto clientCrypto = SessionCrypto.Derive(client, serverPublic, clientPublic, serverPublic, isServer: false);
        SessionCrypto serverCrypto = SessionCrypto.Derive(server, clientPublic, clientPublic, serverPublic, isServer: true);
        return (clientCrypto, serverCrypto);
    }
}
