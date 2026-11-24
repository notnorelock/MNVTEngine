// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using Mono.Cecil;

namespace MNVTEngine.Obfuscation.Analysis;

/// <summary>
/// What the .NET runtime, reflection and serializers reach by name, independent of any game engine. MNVTEngine
/// itself finds nothing in a game assembly by string: components run through virtual overrides of the engine's
/// <c>Component</c> base (kept by the override analysis), and scene data is remapped at export with the rename
/// map. So the only names that must survive here are the ones the BCL and serializers look up by string.
/// </summary>
internal static class EngineKnowledge
{
    /// <summary>An attribute that means "keep this" by simple name; the analyzer also matches <c>PreserveAttribute</c>.</summary>
    public const string Preserve = "MNVTEngine.PreserveAttribute";

    // MNVTEngine serialization: scene and prefab files store a component's serialized members by JSON name, so those
    // member names are kept and only the component type name is renamed (and remapped in scene data at export).
    public const string SerializeField = "MNVTEngine.SerializeFieldAttribute";
    public const string DontSerialize = "MNVTEngine.DontSerializeAttribute";

    /// <summary>Attributes whose methods something invokes by reflection. MNVTEngine registers callbacks explicitly, so none.</summary>
    public static readonly HashSet<string> EntryPointAttributes = new(StringComparer.Ordinal);

    /// <summary>
    /// Methods that look members or types up by a string argument, by declaring type. A method that calls one
    /// of these has its string literals kept as names.
    /// </summary>
    public static readonly Dictionary<string, HashSet<string>> StringLookups = new(StringComparer.Ordinal)
    {
        ["System.Type"] = new() { "GetType", "GetMethod", "GetField", "GetProperty", "GetEvent", "GetMember", "GetNestedType", "InvokeMember", "GetInterface" },
        ["System.Reflection.TypeInfo"] = new() { "GetDeclaredMethod", "GetDeclaredMethods", "GetDeclaredField", "GetDeclaredProperty", "GetDeclaredEvent", "GetDeclaredNestedType" },
        ["System.Reflection.Assembly"] = new() { "GetType", "CreateInstance" },
        ["System.Reflection.Module"] = new() { "GetType" },
        ["System.Activator"] = new() { "CreateInstance", "CreateInstanceFrom" },
        ["System.Delegate"] = new() { "CreateDelegate" },
        ["System.AppDomain"] = new() { "CreateInstance", "CreateInstanceAndUnwrap" },
    };

    /// <summary>Serializer namespaces: types handed to them keep member names (JSON keys come from them).</summary>
    public static readonly string[] SerializerNamespaces =
    {
        "Newtonsoft.Json", "System.Text.Json", "System.Runtime.Serialization", "System.Xml.Serialization", "MessagePack", "ProtoBuf",
    };

    /// <summary>The MNVTEngine component base; its subclasses' serialized members become scene/prefab JSON keys.</summary>
    public const string ComponentBase = "MNVTEngine.Component";

    /// <summary>True when <paramref name="type"/> derives from <see cref="ComponentBase"/> (walking resolvable bases).</summary>
    public static bool IsComponentType(TypeDefinition type)
    {
        for (TypeReference? b = type.BaseType; b != null; b = b.Resolve()?.BaseType)
        {
            if (b.FullName == ComponentBase) return true;
        }

        return false;
    }

    public static bool IsSerializerType(TypeReference declaring)
    {
        string ns = declaring.Namespace;
        if (declaring.DeclaringType != null)
        {
            TypeReference outer = declaring;
            while (outer.DeclaringType != null) outer = outer.DeclaringType;
            ns = outer.Namespace;
        }
        foreach (string n in SerializerNamespaces)
            if (ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal)) return true;
        return false;
    }
}
