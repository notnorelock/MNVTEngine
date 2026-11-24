// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Buffers.Binary;

namespace MNVTEngine.Galaxy;

/// <summary>
/// Builds and parses the unencrypted handshake datagrams. The handshake is: request (ephemeral key, no cookie) →
/// challenge (a stateless cookie) → request (echoing the cookie) → accept (the server's ephemeral key, its identity
/// public key and a signature over both ephemeral keys). There is nothing secret before the shared key exists; the
/// signature authenticates the server and every later datagram is encrypted. Parsing is strict and bounded: any
/// malformed field fails the parse instead of throwing.
/// </summary>
internal static class HandshakePackets
{
    private const int MaxKeyLength = 256;
    private const int MaxCookieLength = 64;
    private const int MaxSignatureLength = 160;

    public static int BuildConnectRequest(Span<byte> buffer, ReadOnlySpan<byte> clientPublicKey, ReadOnlySpan<byte> cookie)
    {
        buffer[0] = (byte)PacketType.ConnectRequest;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(1, 4), GalaxyConstants.ProtocolVersion);
        int offset = WriteBlock(buffer, 5, clientPublicKey);
        offset = WriteBlock(buffer, offset, cookie);
        return offset;
    }

    public static bool TryParseConnectRequest(ReadOnlySpan<byte> datagram, out int protocolVersion, out byte[] clientPublicKey, out byte[] cookie)
    {
        protocolVersion = 0;
        clientPublicKey = [];
        cookie = [];
        if (datagram.Length < 5)
        {
            return false;
        }

        protocolVersion = BinaryPrimitives.ReadInt32LittleEndian(datagram.Slice(1, 4));
        int offset = 5;
        return TryReadBlock(datagram, ref offset, 32, MaxKeyLength, out clientPublicKey)
            && TryReadBlock(datagram, ref offset, 0, MaxCookieLength, out cookie);
    }

    public static int BuildChallenge(Span<byte> buffer, ReadOnlySpan<byte> cookie)
    {
        buffer[0] = (byte)PacketType.ConnectChallenge;
        return WriteBlock(buffer, 1, cookie);
    }

    public static bool TryParseChallenge(ReadOnlySpan<byte> datagram, out byte[] cookie)
    {
        cookie = [];
        if (datagram.Length < 1)
        {
            return false;
        }

        int offset = 1;
        return TryReadBlock(datagram, ref offset, 1, MaxCookieLength, out cookie);
    }

    public static int BuildAccept(Span<byte> buffer, uint connectionId, ReadOnlySpan<byte> serverPublicKey, ReadOnlySpan<byte> identityPublicKey, ReadOnlySpan<byte> signature)
    {
        buffer[0] = (byte)PacketType.ConnectAccept;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(1, 4), connectionId);
        int offset = WriteBlock(buffer, 5, serverPublicKey);
        offset = WriteBlock(buffer, offset, identityPublicKey);
        offset = WriteBlock(buffer, offset, signature);
        return offset;
    }

    public static bool TryParseAccept(ReadOnlySpan<byte> datagram, out uint connectionId, out byte[] serverPublicKey, out byte[] identityPublicKey, out byte[] signature)
    {
        connectionId = 0;
        serverPublicKey = [];
        identityPublicKey = [];
        signature = [];
        if (datagram.Length < 5)
        {
            return false;
        }

        connectionId = BinaryPrimitives.ReadUInt32LittleEndian(datagram.Slice(1, 4));
        int offset = 5;
        return TryReadBlock(datagram, ref offset, 32, MaxKeyLength, out serverPublicKey)
            && TryReadBlock(datagram, ref offset, 0, MaxKeyLength, out identityPublicKey)
            && TryReadBlock(datagram, ref offset, 0, MaxSignatureLength, out signature);
    }

    public static int BuildReject(Span<byte> buffer, RejectReason reason)
    {
        buffer[0] = (byte)PacketType.ConnectReject;
        buffer[1] = (byte)reason;
        return 2;
    }

    private static int WriteBlock(Span<byte> buffer, int offset, ReadOnlySpan<byte> block)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset, 2), (ushort)block.Length);
        block.CopyTo(buffer[(offset + 2)..]);
        return offset + 2 + block.Length;
    }

    private static bool TryReadBlock(ReadOnlySpan<byte> datagram, ref int offset, int min, int max, out byte[] block)
    {
        block = [];
        if (offset + 2 > datagram.Length)
        {
            return false;
        }

        int length = BinaryPrimitives.ReadUInt16LittleEndian(datagram.Slice(offset, 2));
        offset += 2;
        if (length < min || length > max || offset + length > datagram.Length)
        {
            return false;
        }

        block = datagram.Slice(offset, length).ToArray();
        offset += length;
        return true;
    }
}
