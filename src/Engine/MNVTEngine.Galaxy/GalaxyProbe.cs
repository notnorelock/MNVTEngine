// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace MNVTEngine.Galaxy;

/// <summary>
/// Measures the round trip to a <see cref="GalaxyServer"/> without connecting: a probe datagram the server echoes at
/// once (no handshake, no state), e.g. to pick the nearest of several servers. The answer is no larger than the probe,
/// so a forged source address gains an attacker nothing, and a server answers at most
/// <see cref="GalaxyConstants.MaxProbesPerSecond"/> a second.
/// </summary>
public static class GalaxyProbe
{
    /// <summary>A probe and its answer: type, protocol version (4 bytes), nonce (8 bytes).</summary>
    internal const int Size = 13;

    /// <summary>The round trip to the server, or null when no answer came within <paramref name="timeout"/> (none
    /// there, another protocol version, the probe or its answer lost: try again).</summary>
    public static async Task<TimeSpan?> PingAsync(IPEndPoint server, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        using var socket = new UdpClient(server.AddressFamily);
        var probe = new byte[Size];
        var nonce = RandomNumberGenerator.GetBytes(8);
        Write(probe, PacketType.Probe, nonce);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await socket.SendAsync(probe, server, timer.Token).ConfigureAwait(false);
            while (true)
            {
                var answer = await socket.ReceiveAsync(timer.Token).ConfigureAwait(false);
                if (answer.RemoteEndPoint.Equals(server) && IsAnswer(answer.Buffer, nonce))
                {
                    return Stopwatch.GetElapsedTime(started);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // timed out
        }
        catch (SocketException)
        {
            return null; // unreachable (on Windows an ICMP port-unreachable surfaces here)
        }
    }

    /// <summary>The server's answer to <paramref name="probe"/>: the same bytes with the answer's type; false when it is no probe of this version.</summary>
    internal static bool TryAnswer(ReadOnlySpan<byte> probe, Span<byte> answer)
    {
        if (probe.Length != Size || BinaryPrimitives.ReadInt32LittleEndian(probe[1..5]) != GalaxyConstants.ProtocolVersion)
        {
            return false;
        }

        probe.CopyTo(answer);
        answer[0] = (byte)PacketType.ProbeAnswer;
        return true;
    }

    private static void Write(Span<byte> datagram, PacketType type, ReadOnlySpan<byte> nonce)
    {
        datagram[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(datagram[1..5], GalaxyConstants.ProtocolVersion);
        nonce.CopyTo(datagram[5..]);
    }

    private static bool IsAnswer(ReadOnlySpan<byte> datagram, ReadOnlySpan<byte> nonce) =>
        datagram.Length == Size && datagram[0] == (byte)PacketType.ProbeAnswer
        && BinaryPrimitives.ReadInt32LittleEndian(datagram[1..5]) == GalaxyConstants.ProtocolVersion && datagram[5..].SequenceEqual(nonce);
}
