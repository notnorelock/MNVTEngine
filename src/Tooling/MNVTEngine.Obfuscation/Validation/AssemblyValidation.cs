// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using Mono.Cecil;
using MNVTEngine.Obfuscation.Reporting;

namespace MNVTEngine.Obfuscation.Validation;

/// <summary>Public entry to the output structural check, for the CLI's <c>validate</c> command.</summary>
public static class AssemblyValidation
{
    /// <summary>Whether the file is a managed assembly (so it is worth validating).</summary>
    public static bool Looks(string path) => OutputValidator.Looks(path);

    /// <summary>Checks one written assembly loads and its bodies, branches and member names are intact.</summary>
    public static void Check(string path, IAssemblyResolver resolver, DiagnosticBag diagnostics) => OutputValidator.Validate(path, resolver, diagnostics);
}
