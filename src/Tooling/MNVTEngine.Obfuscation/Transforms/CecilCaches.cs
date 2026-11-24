// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Collections;
using System.Reflection;
using Mono.Cecil;
using MNVTEngine.Obfuscation.Reporting;

namespace MNVTEngine.Obfuscation.Transforms;

/// <summary>
/// Works around a Cecil (0.11.x) quirk: <see cref="ModuleDefinition.GetType(string)"/> and type resolution go
/// through a name → type cache (<c>TypeDefinitionCollection.name_cache</c>) that is built when the module is
/// read and is NOT updated when a <see cref="TypeDefinition.Name"/> or namespace changes. After renaming, the
/// writer resolves cross-assembly enum constants (a parameter default of an enum defined in another protected
/// assembly) through that cache and fails with "Failed to resolve &lt;new name&gt;". Rebuilding the cache from
/// the current names fixes it.
///
/// This reaches into Cecil internals by reflection, which is why the pinned Cecil version matters. It fails
/// soft: if the field shape ever changes, it logs and leaves the cache alone (only cross-assembly enum-default
/// cases would then break, and the output validator would catch the result).
/// </summary>
internal static class CecilCaches
{
    private static readonly FieldInfo? NameCacheField =
        typeof(TypeDefinition).Assembly.GetType("Mono.Cecil.TypeDefinitionCollection")
            ?.GetField("name_cache", BindingFlags.NonPublic | BindingFlags.Instance);

    private static bool _warned;

    public static void RefreshTypeNameCache(ModuleDefinition module, DiagnosticBag diagnostics)
    {
        if (NameCacheField == null)
        {
            WarnOnce(diagnostics, "the TypeDefinitionCollection.name_cache field is not present");
            return;
        }
        try
        {
            if (NameCacheField.GetValue(module.Types) is not IDictionary cache) return;
            // Keyed by Mono.Cecil.Metadata.Row<string, string> (namespace, name).
            ConstructorInfo? rowCtor = cache.GetType().GetGenericArguments()[0].GetConstructor(new[] { typeof(string), typeof(string) });
            if (rowCtor == null)
            {
                WarnOnce(diagnostics, "the name cache key type is not the expected Row<string,string>");
                return;
            }
            cache.Clear();
            foreach (TypeDefinition t in module.Types)
                cache[rowCtor.Invoke(new object[] { t.Namespace, t.Name })] = t;
        }
        catch (Exception e) when (e is TargetInvocationException or InvalidOperationException or ArgumentException or MemberAccessException)
        {
            WarnOnce(diagnostics, e.Message);
        }
    }

    private static void WarnOnce(DiagnosticBag diagnostics, string detail)
    {
        if (_warned) return;
        _warned = true;
        diagnostics.Warning(DiagnosticCodes.Internal,
            $"Could not refresh Cecil's type-name cache after renaming ({detail}). Cross-assembly enum default parameters may fail to write; validate the output. This points to a Cecil version change.");
    }
}
