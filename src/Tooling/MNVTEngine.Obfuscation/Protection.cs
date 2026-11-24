// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

namespace MNVTEngine.Obfuscation;

/// <summary>The transformations a symbol or a method body can be excluded from.</summary>
[Flags]
public enum Protection
{
    None = 0,
    Rename = 1,
    Strings = 2,
    ControlFlow = 4,
    Constants = 8,
    All = Rename | Strings | ControlFlow | Constants,
}

public static class ProtectionNames
{
    /// <summary>
    /// Parses a feature name as rules and <c>[Obfuscation(Feature = ...)]</c> spell it. Case does not matter;
    /// "renaming" and "rename" are the same.
    /// </summary>
    public static bool TryParse(string? name, out Protection protection)
    {
        protection = (name ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "all" or "" => Protection.All,
            "rename" or "renaming" => Protection.Rename,
            "strings" or "string" => Protection.Strings,
            "controlflow" or "control-flow" => Protection.ControlFlow,
            "constants" or "constant" => Protection.Constants,
            _ => Protection.None,
        };
        return protection != Protection.None;
    }
}
