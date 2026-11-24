// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Buffers.Binary;

namespace MNVTEngine.Galaxy;

/// <summary>
/// Builds and parses the three unencrypted handshake datagrams. They carry only a protocol version and ephemeral
/// public keys, so there is nothing secret to protect before the shared key exists; every later datagram is
/// encrypted. Parsing is strict and bounded: any malformed field fails the parse instead of throwing.
/// </summary>
internal static class HandshakePackets
{
    public static int BuildConnectRequest(Span<byte> buffer, ReadOnlySpan<byte> clientPublicKey)
    {
        buffer[0] = (byte)PacketType.ConnectRequest;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(1, 4), GalaxyConstants.ProtocolVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(5, 2), (ushort)clientPublicKey.Length);
        clientPublicKey.CopyTo(buffer[7..]);
        return 7 + clientPublicKey.Length;
    }

    public static bool TryParseConnectRequest(ReadOnlySpan<byte> datagram, out int protocolVersion, out byte[] clientPublicKey)
    {
        protocolVersion = 0;
        clientPublicKey = [];
        if (datagram.Length < 7)
        {
            return false;
        }

        protocolVersion = BinaryPrimitives.ReadInt32LittleEndian(datagram.Slice(1, 4));
        int length = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(5, 2));
        if (length is < 32 or > 256 || datagram.Length < 7 + length)
        {
            return false;
        }

        clientPublicKey = datagram.Slice(7, length).ToArray();
        return true;
    }

    public static int BuildAccept(Span<byte> buffer, uint connectionId, ReadOnlySpan<byte> serverPublicKey)
    {
        buffer[0] = (byte)PacketType.ConnectAccept;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(1, 4), connectionId);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(5, 2), (ushort)serverPublicKey.Length);
        serverPublicKey.CopyTo(buffer[7..]);
        return 7 + serverPublicKey.Length;
    }

    public static bool TryParseAccept(ReadOnlySpan<byte> datagram, out uint connectionId, out byte[] serverPublicKey)
    {
        connectionId = 0;
        serverPublicKey = [];
        if (datagram.Length < 7)
        {
            return false;
        }

        connectionId = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(1, 4));
        int length = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(5, 2));
        if (length is < 32 or > 256 || datagram.Length < 7 + length)
        {
            return false;
        }

        serverPublicKey = datagram.Slice(7, length).ToArray();
        return true;
    }

    public static int BuildReject(Span<byte> buffer, RejectReason reason)
    {
        buffer[0] = (byte)PacketType.ConnectReject;
        buffer[1] = (byte)reason;
        return 2;
    }
}
