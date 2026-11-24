// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using Mono.Cecil;

namespace MNVTEngine.Obfuscation.Analysis;

/// <summary>Every place in a module that can carry custom attributes.</summary>
internal static class AttributeWalker
{
    public static IEnumerable<ICustomAttributeProvider> Providers(ModuleDefinition module)
    {
        yield return module.Assembly;
        yield return module;
        foreach (TypeDefinition t in module.GetTypes())
        {
            yield return t;
            foreach (ICustomAttributeProvider g in GenericParameters(t)) yield return g;
            foreach (InterfaceImplementation i in t.Interfaces) yield return i;
            foreach (MethodDefinition m in t.Methods)
            {
                yield return m;
                yield return m.MethodReturnType;
                foreach (ParameterDefinition p in m.Parameters) yield return p;
                foreach (ICustomAttributeProvider g in GenericParameters(m)) yield return g;
            }
            foreach (FieldDefinition f in t.Fields) yield return f;
            foreach (PropertyDefinition p in t.Properties) yield return p;
            foreach (EventDefinition e in t.Events) yield return e;
        }
    }

    private static IEnumerable<ICustomAttributeProvider> GenericParameters(IGenericParameterProvider owner)
    {
        if (!owner.HasGenericParameters) yield break;
        foreach (GenericParameter g in owner.GenericParameters)
        {
            yield return g;
            foreach (GenericParameterConstraint c in g.Constraints) yield return c;
        }
    }
}
