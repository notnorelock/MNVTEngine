// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Text;
using System.Text.RegularExpressions;

namespace MNVTEngine.Obfuscation.Rules;

/// <summary>
/// A wildcard pattern: <c>*</c> matches any run of characters (dots and slashes included), <c>?</c> one
/// character. Matching is case-sensitive and whole-string. Patterns come from configuration files, so their
/// size is capped and matching has a timeout.
/// </summary>
public sealed class Glob
{
    public const int MaxLength = 1024;
    private readonly Regex _regex;

    public string Pattern { get; }

    private Glob(string pattern, Regex regex)
    {
        Pattern = pattern;
        _regex = regex;
    }

    public static bool TryCreate(string? pattern, out Glob? glob, out string? error)
    {
        glob = null;
        error = null;
        if (string.IsNullOrEmpty(pattern))
        {
            error = "The pattern is empty.";
            return false;
        }
        if (pattern.Length > MaxLength)
        {
            error = $"The pattern is longer than {MaxLength} characters.";
            return false;
        }
        if (pattern.Any(char.IsControl))
        {
            error = "The pattern contains control characters.";
            return false;
        }
        var sb = new StringBuilder("^");
        foreach (char c in pattern)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }
        sb.Append('$');
        glob = new Glob(pattern, new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(250)));
        return true;
    }

    public static Glob Create(string pattern) =>
        TryCreate(pattern, out Glob? g, out string? error) ? g! : throw new ArgumentException(error, nameof(pattern));

    public bool IsMatch(string value) => _regex.IsMatch(value);

    public bool HasWildcards => Pattern.IndexOfAny(new[] { '*', '?' }) >= 0;

    public override string ToString() => Pattern;
}
