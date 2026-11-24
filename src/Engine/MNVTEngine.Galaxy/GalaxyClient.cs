// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Galaxy.Security;
using MNVTEngine.Galaxy.Transport;

namespace MNVTEngine.Galaxy;

/// <summary>The state of a <see cref="GalaxyClient"/>.</summary>
public enum GalaxyClientState
{
    Disconnected,
    Connecting,
    Connected,
}

/// <summary>
/// A game client's end of a Galaxy connection to one server. Call <see cref="Connect"/>, then <see cref="Tick"/> once
/// per frame to pump the socket, and <see cref="Send"/> to send messages. All work happens on the calling thread;
/// the client starts no threads. A received <see cref="Message"/> is valid only for the duration of the
/// <see cref="MessageReceived"/> handler and is recycled afterwards.
/// </summary>
public sealed partial class GalaxyClient : IDisposable
{
    private const int ConnectResendMilliseconds = 250;
    private const int ConnectTimeoutMilliseconds = 5000;

    private readonly ILogger _logger;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly byte[] _receiveBuffer = new byte[GalaxyConstants.MaxDatagramSize];
    private readonly List<Message> _inbound = [];

    private UdpTransport? _transport;
    private IPEndPoint? _server;
    private ECDiffieHellman? _keyExchange;
    private byte[] _clientPublicKey = [];
    private byte[] _connectRequest = [];
    private Connection? _connection;
    private long _connectStartedMs;
    private long _lastConnectSendMs;

    public GalaxyClient(ILogger<GalaxyClient>? logger = null) => _logger = logger ?? NullLogger<GalaxyClient>.Instance;

    /// <summary>Raised once the handshake completes and messages can flow.</summary>
    public event Action? Connected;

    /// <summary>Raised when the connection ends, with the reason.</summary>
    public event Action<DisconnectReason>? Disconnected;

    /// <summary>Raised for each application message from the server.</summary>
    public event Action<Message>? MessageReceived;

    public GalaxyClientState State { get; private set; } = GalaxyClientState.Disconnected;

    /// <summary>Begins connecting to <paramref name="host"/>:<paramref name="port"/>. Returns false if the host
    /// cannot be resolved.</summary>
    public bool Connect(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!TryResolve(host, port, out IPEndPoint? server))
        {
            return false;
        }

        Reset();
        _transport = new UdpTransport();
        _transport.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        _server = server;
        _keyExchange = SessionCrypto.CreateKeyExchange(out _clientPublicKey);

        byte[] request = new byte[GalaxyConstants.MaxDatagramSize];
        int length = HandshakePackets.BuildConnectRequest(request, _clientPublicKey);
        _connectRequest = request[..length];

        State = GalaxyClientState.Connecting;
        _connectStartedMs = _clock.ElapsedMilliseconds;
        _lastConnectSendMs = _connectStartedMs - ConnectResendMilliseconds; // So the first Tick sends immediately.
        return true;
    }

    /// <summary>Sends a message to the server. Ignored unless connected.</summary>
    public void Send(Message message, DeliveryMethod method = DeliveryMethod.Reliable)
    {
        ArgumentNullException.ThrowIfNull(message);
        _connection?.Send(message, method, _clock.ElapsedMilliseconds);
    }

    /// <summary>Pumps the socket and timers. Call once per frame.</summary>
    public void Tick()
    {
        if (_transport is null || State == GalaxyClientState.Disconnected)
        {
            return;
        }

        long now = _clock.ElapsedMilliseconds;
        PumpReceive(now);

        if (State == GalaxyClientState.Connecting)
        {
            if (now - _connectStartedMs >= ConnectTimeoutMilliseconds)
            {
                End(DisconnectReason.Timeout);
            }
            else if (now - _lastConnectSendMs >= ConnectResendMilliseconds)
            {
                _transport.Send(_connectRequest, _server!);
                _lastConnectSendMs = now;
            }
        }
        else if (State == GalaxyClientState.Connected && _connection is not null && !_connection.Tick(now))
        {
            End(DisconnectReason.Timeout);
        }
    }

    private void PumpReceive(long now)
    {
        for (int i = 0; i < 256 && _transport!.TryReceive(_receiveBuffer, out int length, out IPEndPoint remote); i++)
        {
            if (length < 1 || !remote.Equals(_server))
            {
                continue;
            }

            HandleDatagram(_receiveBuffer.AsSpan(0, length), now);
            if (State == GalaxyClientState.Disconnected)
            {
                return;
            }
        }
    }

    private void HandleDatagram(ReadOnlySpan<byte> datagram, long now)
    {
        switch ((PacketType)datagram[0])
        {
            case PacketType.ConnectAccept when State == GalaxyClientState.Connecting:
                OnAccept(datagram, now);
                break;

            case PacketType.ConnectReject when State == GalaxyClientState.Connecting:
                LogRejected(_logger, datagram.Length >= 2 ? (RejectReason)datagram[1] : RejectReason.None);
                End(DisconnectReason.Rejected);
                break;

            case PacketType.Payload when State == GalaxyClientState.Connected:
                OnPayload(datagram, now);
                break;

            default:
                break;
        }
    }

    private void OnAccept(ReadOnlySpan<byte> datagram, long now)
    {
        if (!HandshakePackets.TryParseAccept(datagram, out uint connectionId, out byte[] serverPublicKey))
        {
            return;
        }

        SessionCrypto crypto;
        try
        {
            crypto = SessionCrypto.Derive(_keyExchange!, serverPublicKey, _clientPublicKey, serverPublicKey, isServer: false);
        }
        catch (CryptographicException ex)
        {
            LogHandshakeFailed(_logger, ex);
            End(DisconnectReason.ProtocolError);
            return;
        }

        _connection = new Connection(connectionId, _server!, _transport!, crypto, now, _logger);
        _keyExchange!.Dispose();
        _keyExchange = null;
        State = GalaxyClientState.Connected;
        Connected?.Invoke();
    }

    private void OnPayload(ReadOnlySpan<byte> datagram, long now)
    {
        _connection!.Receive(datagram, now, _inbound, out bool remoteClosed);
        foreach (Message message in _inbound)
        {
            MessageReceived?.Invoke(message);
            message.Release();
        }

        _inbound.Clear();
        if (remoteClosed)
        {
            End(DisconnectReason.RemoteRequest);
        }
    }

    /// <summary>Disconnects from the server, telling it best-effort.</summary>
    public void Disconnect()
    {
        if (State == GalaxyClientState.Connected)
        {
            _connection?.SendDisconnect(_clock.ElapsedMilliseconds);
        }

        if (State != GalaxyClientState.Disconnected)
        {
            End(DisconnectReason.LocalRequest);
        }
    }

    private void End(DisconnectReason reason)
    {
        Reset();
        State = GalaxyClientState.Disconnected;
        Disconnected?.Invoke(reason);
    }

    private void Reset()
    {
        _connection?.Dispose();
        _connection = null;
        _keyExchange?.Dispose();
        _keyExchange = null;
        _transport?.Dispose();
        _transport = null;
        _server = null;
        foreach (Message message in _inbound)
        {
            message.Release();
        }

        _inbound.Clear();
    }

    private static bool TryResolve(string host, int port, out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            endpoint = new IPEndPoint(ToV6(address), port);
            return true;
        }

        try
        {
            IPAddress[] addresses = Dns.GetHostAddresses(host);
            if (addresses.Length == 0)
            {
                return false;
            }

            endpoint = new IPEndPoint(ToV6(addresses[0]), port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    // The client's socket is dual-stack IPv6, so it reports an IPv4 server's datagrams with a v6-mapped source
    // address. Storing the server endpoint in the same form makes the source check match.
    private static IPAddress ToV6(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork ? address.MapToIPv6() : address;

    public void Dispose() => Reset();

    [LoggerMessage(EventId = 9110, Level = LogLevel.Information, Message = "Connection refused by the server: {Reason}")]
    private static partial void LogRejected(ILogger logger, RejectReason reason);

    [LoggerMessage(EventId = 9111, Level = LogLevel.Error, Message = "The handshake failed while deriving session keys")]
    private static partial void LogHandshakeFailed(ILogger logger, Exception exception);
}
