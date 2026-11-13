using System.Security.Cryptography;
using MNVTEngine.IO.Packages;

namespace MNVTEngine.Runtime;

/// <summary>
/// How a game executable carries its package encryption key: <c>"v1:&lt;salt&gt;:&lt;masked key&gt;"</c>, the key XORed
/// with a mask derived (HKDF-SHA256) from a per-build salt. This is obfuscation, not protection: it keeps the key from
/// appearing as a recognizable string in the executable, but anyone who reads this code can unmask it. A game that must
/// decrypt its content offline necessarily ships the means to do so; see docs/architecture/virtual-filesystem.md.
/// </summary>
public static class EmbeddedPackageKey
{
    private const string Prefix = "v1:";

    /// <param name="salt">16 bytes; a build passes bytes of its build id, so identical builds embed identical text.</param>
    public static string Encode(PackageEncryptionKey key, ReadOnlySpan<byte> salt)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (salt.Length != 16)
        {
            throw new ArgumentException("The salt is 16 bytes.", nameof(salt));
        }

        var material = Convert.FromHexString(key.ToHex());
        var mask = Mask(salt);
        for (var i = 0; i < material.Length; i++)
        {
            material[i] ^= mask[i];
        }

        return Prefix + Convert.ToHexString(salt) + ":" + Convert.ToHexString(material);
    }

    /// <exception cref="FormatException">Not an embedded key.</exception>
    public static PackageEncryptionKey Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = text.Split(':');
        if (parts.Length != 3 || parts[0] + ":" != Prefix)
        {
            throw new FormatException("Not an embedded package key.");
        }

        var salt = Convert.FromHexString(parts[1]);
        var material = Convert.FromHexString(parts[2]);
        if (salt.Length != 16 || material.Length != PackageEncryptionKey.Size)
        {
            throw new FormatException("Not an embedded package key.");
        }

        var mask = Mask(salt);
        for (var i = 0; i < material.Length; i++)
        {
            material[i] ^= mask[i];
        }

        try
        {
            return PackageEncryptionKey.FromBytes(material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
        }
    }

    private static byte[] Mask(ReadOnlySpan<byte> salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, salt.ToArray(), PackageEncryptionKey.Size, [], "MNVTEngine embedded package key v1"u8.ToArray());
}
