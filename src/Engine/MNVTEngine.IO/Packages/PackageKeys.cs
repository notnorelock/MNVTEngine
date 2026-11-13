using System.Security.Cryptography;

namespace MNVTEngine.IO.Packages;

/// <summary>
/// A 256-bit master key for package encryption. Packages never use it directly: each derives its own data key with
/// HKDF-SHA256 from the master key, a per-package salt and the package GUID, so every package of every build has a
/// different key. <see cref="Id"/> (a hash of the key) is stored in the package header so readers can pick the key
/// from a <see cref="PackageKeyRing"/> and report a missing key clearly (key versioning and rotation).
/// </summary>
public sealed class PackageEncryptionKey
{
    public const int Size = 32;

    private readonly byte[] _material;

    private PackageEncryptionKey(byte[] material)
    {
        _material = material;
        Id = ComputeId(material);
    }

    /// <summary>Public identifier: the first 8 bytes of SHA-256("MNVTPACK key id v1" || key).</summary>
    public ulong Id { get; }

    /// <summary><see cref="Id"/> as 16 hex digits.</summary>
    public string IdText => Id.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A new random key (cryptographic RNG).</summary>
    public static PackageEncryptionKey Create() => new(RandomNumberGenerator.GetBytes(Size));

    /// <exception cref="ArgumentException">Not 32 bytes.</exception>
    public static PackageEncryptionKey FromBytes(ReadOnlySpan<byte> key) =>
        key.Length == Size ? new PackageEncryptionKey(key.ToArray()) : throw new ArgumentException($"Package keys are {Size} bytes.", nameof(key));

    /// <exception cref="FormatException">Not hex.</exception>
    /// <exception cref="ArgumentException">Not 32 bytes.</exception>
    public static PackageEncryptionKey FromHex(string hex) => FromBytes(Convert.FromHexString((hex ?? throw new ArgumentNullException(nameof(hex))).Trim()));

    public string ToHex() => Convert.ToHexString(_material);

    public static string FormatId(ulong id) => id.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The data key of one package: HKDF-SHA256(master, salt, "MNVTPACK v1 data key" || package GUID).</summary>
    internal byte[] DeriveDataKey(ReadOnlySpan<byte> salt, Guid package)
    {
        var info = new byte[20 + 16];
        "MNVTPACK v1 data key"u8.CopyTo(info);
        PackageFormat.WriteGuid(info, 20, package);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, _material, Size, salt.ToArray(), info);
    }

    /// <summary>Salt for a package's data key: unique per package and build, reproducible for identical builds.</summary>
    internal byte[] DeriveSalt(Guid package, Guid build)
    {
        var input = new byte[4 + 32];
        "salt"u8.CopyTo(input);
        PackageFormat.WriteGuid(input, 4, package);
        PackageFormat.WriteGuid(input, 20, build);
        return HMACSHA256.HashData(_material, input);
    }

    private static ulong ComputeId(byte[] material)
    {
        var hash = SHA256.HashData([.. "MNVTPACK key id v1"u8, .. material]);
        return BitConverter.ToUInt64(hash, 0);
    }
}

/// <summary>The encryption keys a reader has, by id. Thread-safe for reading once populated.</summary>
public sealed class PackageKeyRing
{
    private readonly Dictionary<ulong, PackageEncryptionKey> _keys = [];

    public PackageKeyRing(params IEnumerable<PackageEncryptionKey> keys)
    {
        foreach (var key in keys)
        {
            Add(key);
        }
    }

    public IReadOnlyCollection<ulong> Ids => _keys.Keys;

    public PackageKeyRing Add(PackageEncryptionKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _keys[key.Id] = key;
        return this;
    }

    public bool TryGet(ulong id, out PackageEncryptionKey key) => _keys.TryGetValue(id, out key!);
}

/// <summary>
/// A package signing key pair: ECDSA over NIST P-256 with SHA-256 (the BCL's implementation; this .NET has no Ed25519).
/// The private key belongs to the developer and is never shipped; games embed only <see cref="PublicKey"/>.
/// </summary>
public sealed class PackageSigningKey : IDisposable
{
    private readonly ECDsa _key;

    private PackageSigningKey(ECDsa key)
    {
        _key = key;
        PublicKey = PackageVerificationKey.FromSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo());
    }

    public PackageVerificationKey PublicKey { get; }

    public static PackageSigningKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>A PKCS#8 "PRIVATE KEY" PEM, as <see cref="ExportPrivateKeyPem"/> writes.</summary>
    /// <exception cref="ArgumentException">Not a P-256 private key.</exception>
    public static PackageSigningKey FromPem(string pem)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            if (key.KeySize != 256 || key.ExportParameters(includePrivateParameters: true).D is null)
            {
                throw new ArgumentException("Package signing keys are ECDSA P-256 private keys.", nameof(pem));
            }

            return new PackageSigningKey(key);
        }
        catch (CryptographicException ex)
        {
            key.Dispose();
            throw new ArgumentException($"Not a signing key: {ex.Message}", nameof(pem), ex);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public string ExportPrivateKeyPem() => _key.ExportPkcs8PrivateKeyPem();

    internal byte[] Sign(ReadOnlySpan<byte> data) => _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}

/// <summary>The public half of a <see cref="PackageSigningKey"/>: safe to embed in games and to publish.</summary>
public sealed class PackageVerificationKey
{
    private readonly byte[] _spki;

    private PackageVerificationKey(byte[] spki)
    {
        _spki = spki;
        Fingerprint = SHA256.HashData(spki);
    }

    /// <summary>SHA-256 of the DER SubjectPublicKeyInfo; identifies the signer in a package's signature block.</summary>
    public ReadOnlyMemory<byte> Fingerprint { get; }

    /// <summary>First 16 hex digits of <see cref="Fingerprint"/>, for display.</summary>
    public string Id => Convert.ToHexStringLower(Fingerprint.Span[..8]);

    /// <exception cref="ArgumentException">Not a P-256 public key.</exception>
    public static PackageVerificationKey FromSubjectPublicKeyInfo(ReadOnlySpan<byte> spki)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            if (key.KeySize != 256)
            {
                throw new ArgumentException("Package verification keys are ECDSA P-256 public keys.", nameof(spki));
            }
        }
        catch (CryptographicException ex)
        {
            throw new ArgumentException($"Not a public key: {ex.Message}", nameof(spki), ex);
        }

        return new PackageVerificationKey(spki.ToArray());
    }

    /// <exception cref="FormatException">Not base64.</exception>
    /// <exception cref="ArgumentException">Not a P-256 public key.</exception>
    public static PackageVerificationKey FromBase64(string base64) => FromSubjectPublicKeyInfo(Convert.FromBase64String(base64));

    public string ToBase64() => Convert.ToBase64String(_spki);

    public string ToPem() => PemEncoding.WriteString("PUBLIC KEY", _spki);

    internal bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(_spki, out _);
        return key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Signs and verifies arbitrary data (build manifests) with the same scheme as packages.</summary>
    public bool VerifyDetached(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) => signature.Length == 64 && Verify(data, signature);

    public override string ToString() => Id;
}

/// <summary>Detached signatures over other build files (the build manifest), with package signing keys.</summary>
public static class DetachedSignature
{
    /// <summary>A 64-byte ECDSA P-256 / SHA-256 signature of <paramref name="data"/>.</summary>
    public static byte[] Sign(PackageSigningKey key, ReadOnlySpan<byte> data) => (key ?? throw new ArgumentNullException(nameof(key))).Sign(data);
}
