// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MNVTEngine.Galaxy.Security;

/// <summary>
/// The per-connection encryption state established by the handshake. A fresh elliptic-curve Diffie-Hellman key pair
/// is exchanged per connection; the shared secret is run through HKDF into two AES-GCM keys (one per direction) and
/// two nonce salts, so the two directions never share a (key, nonce) pair. Every payload datagram is sealed with
/// AES-GCM — encrypted and authenticated, with the clear header as associated data — and the receiver rejects
/// replays. All primitives are from the .NET base class library; this defines no cryptography of its own.
/// </summary>
internal sealed class SessionCrypto : IDisposable
{
    public const int TagSize = 16;
    private const int NonceSize = 12;
    private const int SaltSize = 4;

    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private readonly byte[] _sendSalt;
    private readonly byte[] _receiveSalt;
    private readonly ReplayWindow _replay = new();
    private ulong _sendCounter;

    private SessionCrypto(byte[] sendKey, byte[] receiveKey, byte[] sendSalt, byte[] receiveSalt)
    {
        _send = new AesGcm(sendKey, TagSize);
        _receive = new AesGcm(receiveKey, TagSize);
        _sendSalt = sendSalt;
        _receiveSalt = receiveSalt;
    }

    /// <summary>Generates an ephemeral key pair for one connection. The caller sends <paramref name="publicKey"/>
    /// in the handshake and keeps the returned key exchange to derive the shared keys.</summary>
    public static ECDiffieHellman CreateKeyExchange(out byte[] publicKey)
    {
        var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        publicKey = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        return ecdh;
    }

    /// <summary>Derives the session keys from the local private key and the peer's public key. Both ends pass the
    /// client and server public keys in the same order, so they derive the same material.</summary>
    public static SessionCrypto Derive(ECDiffieHellman local, ReadOnlySpan<byte> peerPublicKey, ReadOnlySpan<byte> clientPublicKey, ReadOnlySpan<byte> serverPublicKey, bool isServer)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerPublicKey, out _);
        byte[] shared = local.DeriveRawSecretAgreement(peer.PublicKey);

        byte[] salt = new byte[clientPublicKey.Length + serverPublicKey.Length];
        clientPublicKey.CopyTo(salt);
        serverPublicKey.CopyTo(salt.AsSpan(clientPublicKey.Length));

        Span<byte> material = stackalloc byte[(32 * 2) + (SaltSize * 2)];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, material, salt, "MNVTEngine.Galaxy v1"u8);
        CryptographicOperations.ZeroMemory(shared);

        byte[] keyC2S = material[..32].ToArray();
        byte[] keyS2C = material[32..64].ToArray();
        byte[] saltC2S = material[64..68].ToArray();
        byte[] saltS2C = material[68..72].ToArray();
        CryptographicOperations.ZeroMemory(material);

        return isServer
            ? new SessionCrypto(keyS2C, keyC2S, saltS2C, saltC2S)
            : new SessionCrypto(keyC2S, keyS2C, saltC2S, saltS2C);
    }

    /// <summary>Encrypts <paramref name="plaintext"/> into <paramref name="output"/> (ciphertext followed by the
    /// 16-byte tag) and reports the counter used, which the caller puts in the clear header. Returns the number of
    /// bytes written.</summary>
    public int Seal(ReadOnlySpan<byte> plaintext, Span<byte> output, ReadOnlySpan<byte> associatedData, out ulong counter)
    {
        counter = _sendCounter++;
        Span<byte> nonce = stackalloc byte[NonceSize];
        BuildNonce(_sendSalt, counter, nonce);

        Span<byte> ciphertext = output[..plaintext.Length];
        Span<byte> tag = output.Slice(plaintext.Length, TagSize);
        _send.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return plaintext.Length + TagSize;
    }

    /// <summary>Verifies and decrypts a sealed blob (ciphertext + tag) carried with <paramref name="counter"/>.
    /// Returns false — without decrypting — on a replayed counter or a failed tag, leaving the replay window
    /// untouched so forged datagrams cannot advance it.</summary>
    public bool Open(ulong counter, ReadOnlySpan<byte> sealedData, Span<byte> plaintext, ReadOnlySpan<byte> associatedData, out int length)
    {
        length = 0;
        if (sealedData.Length < TagSize || sealedData.Length - TagSize > plaintext.Length || !_replay.CanAccept(counter))
        {
            return false;
        }

        Span<byte> nonce = stackalloc byte[NonceSize];
        BuildNonce(_receiveSalt, counter, nonce);

        int bodyLength = sealedData.Length - TagSize;
        ReadOnlySpan<byte> ciphertext = sealedData[..bodyLength];
        ReadOnlySpan<byte> tag = sealedData.Slice(bodyLength, TagSize);
        try
        {
            _receive.Decrypt(nonce, ciphertext, tag, plaintext[..bodyLength], associatedData);
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }

        _replay.Commit(counter);
        length = bodyLength;
        return true;
    }

    private static void BuildNonce(ReadOnlySpan<byte> salt, ulong counter, Span<byte> nonce)
    {
        salt.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[SaltSize..], counter);
    }

    public void Dispose()
    {
        _send.Dispose();
        _receive.Dispose();
    }
}
