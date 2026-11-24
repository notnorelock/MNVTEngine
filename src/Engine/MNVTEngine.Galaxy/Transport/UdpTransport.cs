// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MNVTEngine.Galaxy.Transport;

/// <summary>A non-blocking UDP socket. Dual-mode (IPv4 and IPv6) so a server bound to <c>::</c> also serves IPv4
/// clients.</summary>
public sealed class UdpTransport : ITransport
{
    // On Windows an ICMP "port unreachable" from a previous send surfaces on the NEXT receive as a
    // ConnectionReset (WSAECONNRESET), which would otherwise throw and kill a UDP server loop. SIO_UDP_CONNRESET
    // turns that off so a client that went away cannot disrupt the server.
    private const int SioUdpConnReset = -1744830452; // 0x9800000C

    private Socket? _socket;

    public IPEndPoint Bind(IPEndPoint local)
    {
        ArgumentNullException.ThrowIfNull(local);
        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
        {
            Blocking = false,
        };
        socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            catch (SocketException)
            {
                // Not fatal: without it, a stray ICMP reset is handled by the catch in TryReceive instead.
            }
        }

        IPAddress address = local.Address.Equals(IPAddress.Any) ? IPAddress.IPv6Any
            : local.Address.AddressFamily == AddressFamily.InterNetwork ? local.Address.MapToIPv6()
            : local.Address;
        socket.Bind(new IPEndPoint(address, local.Port));
        _socket = socket;
        return (IPEndPoint)socket.LocalEndPoint!;
    }

    public bool TryReceive(Span<byte> buffer, out int length, out IPEndPoint remote)
    {
        length = 0;
        remote = DefaultRemote;
        if (_socket is null)
        {
            return false;
        }

        try
        {
            EndPoint from = DefaultRemote;
            length = _socket.ReceiveFrom(buffer, SocketFlags.None, ref from);
            remote = Normalize((IPEndPoint)from);
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock)
        {
            return false;
        }
        catch (SocketException)
        {
            // A reset from an unreachable peer, or a datagram too big for the buffer: skip it, keep serving.
            return false;
        }
    }

    public void Send(ReadOnlySpan<byte> data, IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (_socket is null)
        {
            return;
        }

        try
        {
            _socket.SendTo(data, SocketFlags.None, remote.AddressFamily == AddressFamily.InterNetwork ? Normalize(remote) : remote);
        }
        catch (SocketException)
        {
            // Best effort: a transient send failure (buffer full, unreachable) is handled by retransmission upstream.
        }
    }

    private static IPEndPoint Normalize(IPEndPoint endpoint) =>
        endpoint.AddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(endpoint.Address.MapToIPv6(), endpoint.Port)
            : endpoint;

    private static IPEndPoint DefaultRemote => new(IPAddress.IPv6Any, 0);

    public void Dispose()
    {
        _socket?.Dispose();
        _socket = null;
    }
}
