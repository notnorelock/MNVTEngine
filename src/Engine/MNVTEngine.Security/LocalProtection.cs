// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Security.Cryptography;
using System.Text;

namespace MNVTEngine.Security;

/// <summary>
/// Protects small secrets a game keeps on the player's machine (session tokens, a remembered sign-in), so they cannot
/// be read from a copied file, by another Windows account or on another computer (docs/engine/local-protection.md).
/// Windows DPAPI for the current user; each <c>purpose</c> mixes in its own entropy, so data protected for one purpose
/// does not unprotect for another. Not available elsewhere: callers decide what to do without it (keep nothing, or
/// ask again).
/// </summary>
public static class LocalProtection
{
    /// <summary>Whether this platform can protect data (Windows).</summary>
    public static bool IsAvailable => OperatingSystem.IsWindows();

    /// <summary>Encrypts <paramref name="data"/> for the current user on this machine.</summary>
    /// <param name="purpose">What the data is for, e.g. "MyGame.SessionTokens"; the same purpose unprotects it.</param>
    /// <exception cref="PlatformNotSupportedException">Not on Windows (<see cref="IsAvailable"/>).</exception>
    public static byte[] Protect(ReadOnlySpan<byte> data, string purpose)
    {
        var entropy = Entropy(purpose);
        return OperatingSystem.IsWindows()
            ? ProtectedData.Protect(data.ToArray(), entropy, DataProtectionScope.CurrentUser)
            : throw new PlatformNotSupportedException("Local data protection needs Windows (DPAPI).");
    }

    /// <summary>The data <see cref="Protect"/> encrypted with the same <paramref name="purpose"/>; null when it cannot be (another user, another machine, another purpose, damaged) or the platform has no protection.</summary>
    public static byte[]? Unprotect(byte[] data, string purpose)
    {
        ArgumentNullException.ThrowIfNull(data);
        var entropy = Entropy(purpose);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null; // not ours to read: the caller treats it as missing
        }
    }

    /// <summary><see cref="Protect"/> for text, as Base64 (for JSON and settings files).</summary>
    public static string ProtectString(string text, string purpose)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(text), purpose));
    }

    /// <summary><see cref="Unprotect"/> for <see cref="ProtectString"/>'s Base64; null when it cannot be read.</summary>
    public static string? UnprotectString(string? protectedText, string purpose)
    {
        if (string.IsNullOrEmpty(protectedText))
        {
            return null;
        }

        var bytes = new byte[protectedText.Length];
        return Convert.TryFromBase64String(protectedText, bytes, out var length) && Unprotect(bytes[..length], purpose) is { } plain
            ? Encoding.UTF8.GetString(plain)
            : null;
    }

    private static byte[] Entropy(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return SHA256.HashData(Encoding.UTF8.GetBytes("MNVTEngine.LocalProtection/" + purpose));
    }
}
