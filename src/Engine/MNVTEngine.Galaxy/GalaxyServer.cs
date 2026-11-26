// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Galaxy.Security;
using MNVTEngine.Galaxy.Transport;

namespace MNVTEngine.Galaxy;

/// <summary>
/// The server end of Galaxy: accepts many clients over one UDP socket, performs the encrypted handshake and routes
/// messages. A fresh key pair per connection gives each one its own session keys. All work happens in <see cref="Tick"/>
/// on the calling thread — a dedicated server drives it from its loop — so the server starts no threads and touches no
/// files, graphics or assets. A received <see cref="Message"/> is valid only for the duration of the
/// <see cref="MessageReceived"/> handler.
/// </summary>
public sealed partial class GalaxyServer : IDisposable
{
    // A floor of basic flood resistance: cap brand-new handshakes accepted within one second. A legitimate client
    // retransmits its request, so a dropped one still connects shortly after. (Fuller DoS hardening — connection
    // cookies, per-IP limits — is future work; see docs/architecture/networking.md.)
    private const int MaxHandshakesPerSecond = 256;

    private sealed record Peer(Connection Connection, byte[] AcceptDatagram);

    private readonly ILogger _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly byte[] _receiveBuffer = new byte[GalaxyConstants.MaxDatagramSize];
    private readonly List<Message> _inbound = [];
    private readonly Dictionary<uint, Peer> _peers = [];
    private readonly Dictionary<IPEndPoint, uint> _byEndpoint = [];
    private readonly Dictionary<IPAddress, int> _perAddress = [];
    private readonly Dictionary<uint, (long SecondStartMs, int Count)> _inboundBudget = [];
    private readonly List<uint> _scratchIds = [];
    private readonly byte[] _cookieSecret = RandomNumberGenerator.GetBytes(32);

    private ITransport? _transport;
    private GalaxyIdentity? _identity;
    private int _maxClients;
    private long _handshakeSecondStartMs;
    private int _handshakesThisSecond;

    public GalaxyServer(ILogger<GalaxyServer>? logger = null) => _logger = logger ?? NullLogger<GalaxyServer>.Instance;

    /// <summary>Raised when a client finishes the handshake, with its id.</summary>
    public event Action<uint>? ClientConnected;

    /// <summary>Raised when a client's connection ends, with its id and the reason.</summary>
    public event Action<uint, DisconnectReason>? ClientDisconnected;

    /// <summary>Raised for each application message, with the sending client's id.</summary>
    public event Action<uint, Message>? MessageReceived;

    public bool IsRunning => _transport is not null;

    /// <summary>The connected clients' ids.</summary>
    public IReadOnlyCollection<uint> Clients => _peers.Keys;

    /// <summary>
    /// Messages taken from one client in a second; more are dropped before <see cref="MessageReceived"/>, and a client
    /// sending <see cref="GalaxyConstants.FloodFactor"/> times as many is disconnected (<see cref="DisconnectReason.ProtocolError"/>).
    /// Raise it for games that send more than ~600 small messages a second per player.
    /// </summary>
    public int MaxMessagesPerSecond { get; set; } = GalaxyConstants.MaxMessagesPerSecond;

    /// <summary>Messages dropped for going over <see cref="MaxMessagesPerSecond"/> since the start.</summary>
    public long DroppedOverBudget { get; private set; }

    /// <summary>Binds the server to <paramref name="port"/> and returns the actual local endpoint. Pass an
    /// <paramref name="identity"/> to authenticate the server to clients that pin its public key. Pass a custom
    /// <paramref name="transport"/> to test over an in-memory channel.</summary>
    public IPEndPoint Start(int port, int maxClients = 64, GalaxyIdentity? identity = null, ITransport? transport = null)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("The server is already running.");
        }

        _maxClients = maxClients;
        _identity = identity;
        _transport = transport ?? new UdpTransport();
        IPEndPoint local = _transport.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        LogStarted(_logger, local, maxClients);
        return local;
    }

    /// <summary>Sends a message to one client. Ignored if the client is unknown.</summary>
    public void Send(uint clientId, Message message, DeliveryMethod method = DeliveryMethod.Reliable)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_peers.TryGetValue(clientId, out Peer? peer))
        {
            peer.Connection.Send(message, method, _clock.ElapsedMilliseconds);
        }
    }

    /// <summary>Sends the same message to every connected client.</summary>
    public void SendToAll(Message message, DeliveryMethod method = DeliveryMethod.Reliable)
    {
        ArgumentNullException.ThrowIfNull(message);
        long now = _clock.ElapsedMilliseconds;
        foreach (Peer peer in _peers.Values)
        {
            peer.Connection.Send(message, method, now);
        }
    }

    /// <summary>Disconnects one client, telling it best-effort.</summary>
    /// <summary>The smoothed round trip to a client in milliseconds; null for an unknown client or before the first measurement.</summary>
    public double? RoundTripMs(uint clientId) => _peers.TryGetValue(clientId, out Peer? peer) ? peer.Connection.Timing.RoundTripMs : null;

    public void Disconnect(uint clientId)
    {
        if (_peers.TryGetValue(clientId, out Peer? peer))
        {
            peer.Connection.SendDisconnect(_clock.ElapsedMilliseconds);
            Remove(clientId, peer, DisconnectReason.LocalRequest);
        }
    }

    /// <summary>Pumps the socket and advances every connection. A dedicated server calls this at a fixed rate.</summary>
    public void Tick()
    {
        if (_transport is null)
        {
            return;
        }

        long now = _clock.ElapsedMilliseconds;
        PumpReceive(now);

        _scratchIds.Clear();
        foreach ((uint id, Peer peer) in _peers)
        {
            if (!peer.Connection.Tick(now))
            {
                _scratchIds.Add(id);
            }
        }

        foreach (uint id in _scratchIds)
        {
            if (_peers.TryGetValue(id, out Peer? peer))
            {
                Remove(id, peer, DisconnectReason.Timeout);
            }
        }
    }

    private void PumpReceive(long now)
    {
        for (int i = 0; i < 1024 && _transport!.TryReceive(_receiveBuffer, out int length, out IPEndPoint remote); i++)
        {
            if (length < 1)
            {
                continue;
            }

            ReadOnlySpan<byte> datagram = _receiveBuffer.AsSpan(0, length);
            switch ((PacketType)datagram[0])
            {
                case PacketType.ConnectRequest:
                    HandleConnectRequest(datagram, remote, now);
                    break;

                case PacketType.Payload:
                    HandlePayload(datagram, remote, now);
                    break;

                default:
                    break;
            }
        }
    }

    private void HandleConnectRequest(ReadOnlySpan<byte> datagram, IPEndPoint remote, long now)
    {
        if (_byEndpoint.TryGetValue(remote, out uint existing) && _peers.TryGetValue(existing, out Peer? known))
        {
            _transport!.Send(known.AcceptDatagram, remote); // Retransmitted request: resend the same accept.
            return;
        }

        if (!HandshakePackets.TryParseConnectRequest(datagram, out int protocolVersion, out byte[] clientPublicKey, out byte[] cookie))
        {
            SendReject(remote, RejectReason.Malformed);
            return;
        }

        if (protocolVersion != GalaxyConstants.ProtocolVersion)
        {
            SendReject(remote, RejectReason.ProtocolMismatch);
            return;
        }

        if (!AllowHandshake(now))
        {
            return; // Over the per-second budget; the client will retransmit.
        }

        if (!ConnectionCookie.Verify(cookie, _cookieSecret, remote, clientPublicKey, now))
        {
            SendChallenge(remote, clientPublicKey, now); // Stateless: prove the source address before any state is kept.
            return;
        }

        if (_perAddress.GetValueOrDefault(remote.Address) >= GalaxyConstants.MaxConnectionsPerAddress || _peers.Count >= _maxClients)
        {
            SendReject(remote, RejectReason.ServerFull);
            return;
        }

        AcceptConnection(remote, clientPublicKey, now);
    }

    private void SendChallenge(IPEndPoint remote, ReadOnlySpan<byte> clientPublicKey, long now)
    {
        Span<byte> cookie = stackalloc byte[ConnectionCookie.Size];
        ConnectionCookie.Write(cookie, _cookieSecret, remote, clientPublicKey, now);
        Span<byte> buffer = stackalloc byte[3 + ConnectionCookie.Size];
        int length = HandshakePackets.BuildChallenge(buffer, cookie);
        _transport!.Send(buffer[..length], remote);
    }

    private void AcceptConnection(IPEndPoint remote, byte[] clientPublicKey, long now)
    {
        using ECDiffieHellman keyExchange = SessionCrypto.CreateKeyExchange(out byte[] serverPublicKey);
        SessionCrypto crypto;
        try
        {
            crypto = SessionCrypto.Derive(keyExchange, clientPublicKey, clientPublicKey, serverPublicKey, isServer: true);
        }
        catch (CryptographicException ex)
        {
            LogHandshakeFailed(_logger, ex);
            SendReject(remote, RejectReason.Malformed);
            return;
        }

        byte[] identityPublicKey = _identity?.PublicKey ?? [];
        byte[] signature = [];
        if (_identity is not null)
        {
            byte[] transcript = new byte[clientPublicKey.Length + serverPublicKey.Length];
            clientPublicKey.CopyTo(transcript, 0);
            serverPublicKey.CopyTo(transcript, clientPublicKey.Length);
            signature = _identity.Sign(transcript);
        }

        uint connectionId = NewConnectionId();
        byte[] accept = new byte[GalaxyConstants.MaxDatagramSize];
        int length = HandshakePackets.BuildAccept(accept, connectionId, serverPublicKey, identityPublicKey, signature);
        accept = accept[..length];

        var connection = new Connection(connectionId, remote, _transport!, crypto, now, _logger);
        _peers[connectionId] = new Peer(connection, accept);
        _byEndpoint[remote] = connectionId;
        _perAddress[remote.Address] = _perAddress.GetValueOrDefault(remote.Address) + 1;

        _transport!.Send(accept, remote);
        LogClientConnected(_logger, connectionId, remote);
        ClientConnected?.Invoke(connectionId);
    }

    private void HandlePayload(ReadOnlySpan<byte> datagram, IPEndPoint remote, long now)
    {
        if (datagram.Length < 5)
        {
            return;
        }

        uint connectionId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(1, 4));
        if (!_peers.TryGetValue(connectionId, out Peer? peer) || !peer.Connection.Remote.Equals(remote))
        {
            return; // Unknown connection or a spoofed source address: drop silently, send nothing back.
        }

        peer.Connection.Receive(datagram, now, _inbound, out bool remoteClosed);
        var (secondStart, count) = _inboundBudget.GetValueOrDefault(connectionId);
        if (now - secondStart >= 1000)
        {
            (secondStart, count) = (now, 0);
        }

        foreach (Message message in _inbound)
        {
            if (++count <= MaxMessagesPerSecond)
            {
                MessageReceived?.Invoke(connectionId, message);
            }
            else
            {
                DroppedOverBudget++;
            }

            message.Release();
        }

        _inbound.Clear();
        _inboundBudget[connectionId] = (secondStart, count);
        if (count > (long)MaxMessagesPerSecond * GalaxyConstants.FloodFactor && _peers.ContainsKey(connectionId))
        {
            LogFlooding(_logger, connectionId, count);
            peer.Connection.SendDisconnect(now);
            Remove(connectionId, peer, DisconnectReason.ProtocolError);
            return;
        }

        if (remoteClosed)
        {
            Remove(connectionId, peer, DisconnectReason.RemoteRequest);
        }
    }

    private bool AllowHandshake(long now)
    {
        if (now - _handshakeSecondStartMs >= 1000)
        {
            _handshakeSecondStartMs = now;
            _handshakesThisSecond = 0;
        }

        if (_handshakesThisSecond >= MaxHandshakesPerSecond)
        {
            return false;
        }

        _handshakesThisSecond++;
        return true;
    }

    private uint NewConnectionId()
    {
        Span<byte> bytes = stackalloc byte[4];
        uint id;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        while (id == 0 || _peers.ContainsKey(id));

        return id;
    }

    private void SendReject(IPEndPoint remote, RejectReason reason)
    {
        Span<byte> buffer = stackalloc byte[2];
        int length = HandshakePackets.BuildReject(buffer, reason);
        _transport!.Send(buffer[..length], remote);
    }

    private void Remove(uint connectionId, Peer peer, DisconnectReason reason)
    {
        _peers.Remove(connectionId);
        _inboundBudget.Remove(connectionId);
        _byEndpoint.Remove(peer.Connection.Remote);
        IPAddress address = peer.Connection.Remote.Address;
        int remaining = _perAddress.GetValueOrDefault(address) - 1;
        if (remaining <= 0)
        {
            _perAddress.Remove(address);
        }
        else
        {
            _perAddress[address] = remaining;
        }

        peer.Connection.Dispose();
        LogClientDisconnected(_logger, connectionId, reason);
        ClientDisconnected?.Invoke(connectionId, reason);
    }

    /// <summary>Stops the server, dropping every client.</summary>
    public void Stop()
    {
        long now = _clock.ElapsedMilliseconds;
        foreach (Peer peer in _peers.Values)
        {
            peer.Connection.SendDisconnect(now);
            peer.Connection.Dispose();
        }

        _peers.Clear();
        _byEndpoint.Clear();
        _perAddress.Clear();
        foreach (Message message in _inbound)
        {
            message.Release();
        }

        _inbound.Clear();
        _transport?.Dispose();
        _transport = null;
    }

    public void Dispose() => Stop();

    [LoggerMessage(EventId = 9120, Level = LogLevel.Information, Message = "Galaxy server listening on {Endpoint} for up to {MaxClients} clients")]
    private static partial void LogStarted(ILogger logger, IPEndPoint endpoint, int maxClients);

    [LoggerMessage(EventId = 9121, Level = LogLevel.Information, Message = "Client {ClientId} connected from {Endpoint}")]
    private static partial void LogClientConnected(ILogger logger, uint clientId, IPEndPoint endpoint);

    [LoggerMessage(EventId = 9122, Level = LogLevel.Information, Message = "Client {ClientId} disconnected: {Reason}")]
    private static partial void LogClientDisconnected(ILogger logger, uint clientId, DisconnectReason reason);

    [LoggerMessage(EventId = 9124, Level = LogLevel.Warning, Message = "Client {ClientId} sent {Count} messages within a second; disconnected for flooding")]
    private static partial void LogFlooding(ILogger logger, uint clientId, int count);

    [LoggerMessage(EventId = 9123, Level = LogLevel.Error, Message = "A handshake failed while deriving session keys")]
    private static partial void LogHandshakeFailed(ILogger logger, Exception exception);
}
