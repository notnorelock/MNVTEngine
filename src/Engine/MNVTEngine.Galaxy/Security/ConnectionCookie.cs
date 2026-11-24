// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace MNVTEngine.Galaxy.Security;

/// <summary>
/// A stateless connection cookie (as in DTLS/QUIC): the server answers a first connect request with a cookie it can
/// later recompute, and allocates no state until the client echoes it. The cookie is an HMAC over the client's
/// address and ephemeral key and a coarse time bucket, keyed by a per-server secret, so only a client that actually
/// receives datagrams at the claimed address can present a valid one. That blocks spoofed-source floods and
/// reflection, and keeps the server from spending memory on an address that has not proven it can receive.
/// </summary>
internal static class ConnectionCookie
{
    public const int Size = 16;
    private const long BucketMilliseconds = 10_000;

    public static void Write(Span<byte> destination, ReadOnlySpan<byte> secret, IPEndPoint endpoint, ReadOnlySpan<byte> ephemeralKey, long nowMs) =>
        Compute(destination, secret, endpoint, ephemeralKey, nowMs / BucketMilliseconds);

    public static bool Verify(ReadOnlySpan<byte> cookie, ReadOnlySpan<byte> secret, IPEndPoint endpoint, ReadOnlySpan<byte> ephemeralKey, long nowMs)
    {
        if (cookie.Length != Size)
        {
            return false;
        }

        long bucket = nowMs / BucketMilliseconds;
        Span<byte> expected = stackalloc byte[Size];
        Compute(expected, secret, endpoint, ephemeralKey, bucket);
        if (CryptographicOperations.FixedTimeEquals(cookie, expected))
        {
            return true;
        }

        Compute(expected, secret, endpoint, ephemeralKey, bucket - 1); // Accept the previous bucket too.
        return CryptographicOperations.FixedTimeEquals(cookie, expected);
    }

    private static void Compute(Span<byte> destination, ReadOnlySpan<byte> secret, IPEndPoint endpoint, ReadOnlySpan<byte> ephemeralKey, long bucket)
    {
        Span<byte> message = stackalloc byte[16 + 2 + 8 + 256];
        message[..26].Clear(); // The address may be shorter than 16 bytes; keep the unused tail deterministic.
        endpoint.Address.TryWriteBytes(message[..16], out _);
        BinaryPrimitives.WriteUInt16LittleEndian(message.Slice(16, 2), (ushort)endpoint.Port);
        BinaryPrimitives.WriteInt64LittleEndian(message.Slice(18, 8), bucket);
        int keyLength = Math.Min(ephemeralKey.Length, 256);
        ephemeralKey[..keyLength].CopyTo(message.Slice(26, keyLength));

        Span<byte> full = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(secret, message[..(26 + keyLength)], full);
        full[..Size].CopyTo(destination);
    }
}
