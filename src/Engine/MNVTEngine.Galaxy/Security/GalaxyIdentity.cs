// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Security.Cryptography;

namespace MNVTEngine.Galaxy.Security;

/// <summary>
/// A server's long-term identity: an ECDSA (P-256) key pair the server signs its handshake with. A client that has
/// pinned the matching public key verifies the signature and so detects a man-in-the-middle, which cannot forge it
/// without the private key. Create one, persist the private key (<see cref="ExportPrivateKey"/>) with the server and
/// ship the public key (<see cref="PublicKey"/>) with the client. ECDSA is used rather than Ed25519, which this
/// runtime does not yet provide.
/// </summary>
public sealed class GalaxyIdentity : IDisposable
{
    private readonly ECDsa _key;

    private GalaxyIdentity(ECDsa key)
    {
        _key = key;
        PublicKey = key.ExportSubjectPublicKeyInfo();
    }

    /// <summary>The identity public key (SubjectPublicKeyInfo), to pin on clients.</summary>
    public byte[] PublicKey { get; }

    /// <summary>Generates a new identity.</summary>
    public static GalaxyIdentity Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Loads an identity from a previously exported PKCS#8 private key.</summary>
    public static GalaxyIdentity FromPrivateKey(ReadOnlySpan<byte> pkcs8PrivateKey)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
        }
        catch
        {
            key.Dispose();
            throw;
        }

        return new GalaxyIdentity(key);
    }

    /// <summary>Exports the private key (PKCS#8) to persist with the server. Keep it secret.</summary>
    public byte[] ExportPrivateKey() => _key.ExportPkcs8PrivateKey();

    internal byte[] Sign(ReadOnlySpan<byte> data) => _key.SignData(data, HashAlgorithmName.SHA256);

    internal static bool Verify(ReadOnlySpan<byte> publicKeySpki, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public void Dispose() => _key.Dispose();
}
