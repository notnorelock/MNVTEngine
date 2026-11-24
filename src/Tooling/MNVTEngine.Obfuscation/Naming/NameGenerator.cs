// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Security.Cryptography;
using System.Text;
using MNVTEngine.Obfuscation.Configuration;

namespace MNVTEngine.Obfuscation.Naming;

/// <summary>
/// Makes new names. A name is a keyed hash (HMAC-SHA256 with the secret seed) of the symbol's original
/// identity, so it is the same in every build with the same seed and reveals nothing without the seed. A
/// collision in the same scope retries with the next attempt number, growing the name.
/// </summary>
internal sealed class NameGenerator
{
    private const string First = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Rest = First + "0123456789";

    // Glyphs for the "unicode" scheme: just these three — two Canadian syllabics and a Greek delta — all
    // valid C# identifier letters. Variety comes from their order and the name length, giving names like
    // ᐃᐄΔᐃΔΔ. A three-symbol alphabet needs more characters to stay unique, so unicode names run longer.
    private const string Glyphs = "ᐃᐄΔ";

    private readonly HMACSHA256 _hmac;
    private readonly string _scheme;

    public NameGenerator(byte[] seed, string scheme)
    {
        _hmac = new HMACSHA256(seed);
        _scheme = scheme;
    }

    public static byte[] SeedBytes(string seed) => Encoding.UTF8.GetBytes(seed);

    public static byte[] RandomSeed() => RandomNumberGenerator.GetBytes(32);

    // New-name lengths are clamped to this range so a one-character original does not become a one-character
    // name (too collision-prone) and a very long one does not produce an absurd name.
    private const int MinLength = 3;
    private const int MaxLength = 24;

    /// <summary>
    /// A name for <paramref name="id"/> that <paramref name="taken"/> does not reject. When
    /// <paramref name="originalLength"/> is given, the new name is about that long (clamped), so short members
    /// get short names and long ones get long names; otherwise a default length is used. A collision grows it.
    /// </summary>
    public string Create(string id, Func<string, bool> taken, int originalLength = 0)
    {
        int baseLength = originalLength > 0 ? Math.Clamp(originalLength, MinLength, MaxLength) : 4;
        for (int attempt = 0; ; attempt++)
        {
            string input = id + "\u0000" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int length = baseLength + attempt / 4;
            // The glyph scheme needs many positions (tiny alphabet), so it uses a longer keyed byte stream.
            byte[] bytes = _scheme == NamingSchemes.Unicode ? Stream(input, Math.Clamp(length * 4, 8, 96)) : Hash(input);
            string name = Encode(bytes, length);
            if (!taken(name)) return name;
            if (attempt > 10_000) throw new InvalidOperationException("Could not find a free name; the scope is too crowded.");
        }
    }

    /// <summary>Short positional names for parameters and generic parameters: a, b, ... z, ba, bb, ...</summary>
    public string Positional(int index)
    {
        string alphabet = _scheme == NamingSchemes.Unicode ? Glyphs : First[..26];
        var sb = new StringBuilder();
        do
        {
            sb.Insert(0, alphabet[index % alphabet.Length]);
            index = index / alphabet.Length;
        }
        while (index > 0);
        return sb.ToString();
    }

    private byte[] Hash(string input) => _hmac.ComputeHash(Encoding.UTF8.GetBytes(input));

    /// <summary>At least <paramref name="bytes"/> keyed bytes, by chaining HMAC blocks, so long glyph names
    /// stay non-periodic past the first 32 bytes.</summary>
    private byte[] Stream(string input, int bytes)
    {
        var result = new List<byte>(bytes + 32);
        byte[] block = Array.Empty<byte>();
        int counter = 0;
        while (result.Count < bytes)
        {
            block = _hmac.ComputeHash(Encoding.UTF8.GetBytes(input + "#" + counter++.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            result.AddRange(block);
        }
        return result.ToArray();
    }

    /// <summary>Encodes <paramref name="length"/> characters from the hash: a letter first, then letters/digits.</summary>
    private string Encode(byte[] hash, int length)
    {
        length = Math.Clamp(length, MinLength, MaxLength + 16); // + room for collision growth
        var sb = new StringBuilder(length);
        if (_scheme == NamingSchemes.Unicode)
        {
            // The alphabet is tiny (three glyphs), so each position carries little entropy. Use more positions
            // than the ASCII scheme would — roughly log2(62)/log2(3) ≈ 3.75× — so names stay unique with room
            // for the order to vary. Capped so they do not grow unreasonably.
            int glyphLength = Math.Clamp(length * 4, 8, 96);
            for (int i = 0; i < glyphLength && i < hash.Length; i++)
                sb.Append(Glyphs[hash[i] % Glyphs.Length]);
            return sb.ToString();
        }
        sb.Append(First[hash[0] % First.Length]);
        for (int i = 1; i < length; i++) sb.Append(Rest[hash[i % hash.Length] % Rest.Length]);
        return sb.ToString();
    }
}
