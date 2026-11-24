// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Collections;
using System.Reflection;
using System.Text;
using MNVTEngine.Scenes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace MNVTEngine.Build;

/// <summary>
/// Rewrites a scene or prefab so a game whose script assembly was obfuscated still loads its content. Obfuscation
/// renames the component classes and every serialized member reachable from them (including plain data types nested
/// inside component data). This replaces each old type id with the new one wherever it appears as a <c>"type"</c>,
/// and rewrites the JSON keys of a component's <c>"data"</c> — descending into nested objects, lists and dictionaries
/// by the members' static types, which the JSON itself does not record. Engine types and object references (written
/// as ids) are left untouched. The key maps come from <see cref="BuildKeyMap"/>, which mirrors the real serializer
/// (<see cref="SceneSerializer.SerializedMembers"/>), so the rewritten keys always agree with what a game reads.
/// </summary>
public static class SceneRemapper
{
    /// <summary>How a renamed member's value is descended into when remapping nested keys.</summary>
    public enum Descent
    {
        /// <summary>A scalar, reference or engine-typed value: nothing to descend into.</summary>
        None,

        /// <summary>The value is one nested object of <see cref="KeyRemap.ChildType"/>.</summary>
        Object,

        /// <summary>The value is an array whose elements are objects of <see cref="KeyRemap.ChildType"/>.</summary>
        Array,

        /// <summary>The value is a JSON object (a dictionary) whose values are objects of <see cref="KeyRemap.ChildType"/>.</summary>
        DictionaryValues,
    }

    /// <summary>One data key's remap: its new name, and how (if at all) to descend into its value for further renames.</summary>
    public sealed record KeyRemap(string NewKey, string? ChildType, Descent Descent);

    /// <summary>Per-type key maps (type full name → old JSON key → <see cref="KeyRemap"/>) for <see cref="Remap"/>.</summary>
    public interface IKeyMap
    {
        IReadOnlyDictionary<string, KeyRemap>? KeysFor(string typeFullName);
    }

    /// <summary>
    /// Returns <paramref name="sceneJson"/> with component type ids remapped by <paramref name="typeRenames"/> and
    /// component data keys remapped by <paramref name="keyMap"/>, or the input unchanged when nothing matches.
    /// Throws on invalid JSON.
    /// </summary>
    public static byte[] Remap(byte[] sceneJson, IReadOnlyDictionary<string, string>? typeRenames, IKeyMap? keyMap = null)
    {
        ArgumentNullException.ThrowIfNull(sceneJson);
        var types = typeRenames ?? EmptyTypes;
        if (types.Count == 0 && keyMap is null)
        {
            return sceneJson;
        }

        var root = JToken.Parse(Encoding.UTF8.GetString(sceneJson));
        var changed = Rewrite(root, types, keyMap);
        return changed == 0 ? sceneJson : Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyTypes = new Dictionary<string, string>();

    private static int Rewrite(JToken token, IReadOnlyDictionary<string, string> typeRenames, IKeyMap? keyMap)
    {
        var changed = 0;
        switch (token)
        {
            case JObject obj:
                if (obj["type"] is JValue { Type: JTokenType.String } typeValue && typeValue.Value<string>() is { } id)
                {
                    if (keyMap?.KeysFor(id) is { } keys && obj["data"] is JObject data)
                    {
                        changed += RemapObject(data, keys, keyMap);
                    }

                    if (typeRenames.TryGetValue(id, out var renamed))
                    {
                        obj["type"] = renamed;
                        changed++;
                    }
                }

                foreach (var property in obj.Properties())
                {
                    changed += Rewrite(property.Value, typeRenames, keyMap);
                }

                break;
            case JArray array:
                foreach (var item in array)
                {
                    changed += Rewrite(item, typeRenames, keyMap);
                }

                break;
        }

        return changed;
    }

    // Renames a data object's top-level keys, descending into nested values first so their keys are renamed too.
    private static int RemapObject(JObject data, IReadOnlyDictionary<string, KeyRemap> keys, IKeyMap keyMap)
    {
        var changed = 0;
        foreach (var property in data.Properties())
        {
            if (keys.TryGetValue(property.Name, out var remap) && remap.ChildType is { } child)
            {
                changed += Descend(property.Value, remap.Descent, child, keyMap);
            }
        }

        var renames = data.Properties().Count(p => keys.TryGetValue(p.Name, out var r) && r.NewKey != p.Name);
        if (renames == 0)
        {
            return changed;
        }

        var rebuilt = data.Properties()
            .Select(p => new JProperty(keys.TryGetValue(p.Name, out var r) ? r.NewKey : p.Name, p.Value.DeepClone()))
            .Cast<object>()
            .ToArray();
        data.ReplaceAll(rebuilt);
        return changed + renames;
    }

    private static int Descend(JToken value, Descent descent, string childType, IKeyMap keyMap)
    {
        if (keyMap.KeysFor(childType) is not { } keys)
        {
            return 0;
        }

        switch (descent)
        {
            case Descent.Object when value is JObject o:
                return RemapObject(o, keys, keyMap);
            case Descent.Array when value is JArray a:
                return a.OfType<JObject>().Sum(o => RemapObject(o, keys, keyMap));
            case Descent.DictionaryValues when value is JObject map:
                return map.Properties().Select(p => p.Value).OfType<JObject>().Sum(o => RemapObject(o, keys, keyMap));
            default:
                return 0;
        }
    }

    // ── Building the key map ───────────────────────────────────────────────

    private sealed class KeyMap(Dictionary<string, IReadOnlyDictionary<string, KeyRemap>> types) : IKeyMap
    {
        public IReadOnlyDictionary<string, KeyRemap>? KeysFor(string typeFullName) => types.GetValueOrDefault(typeFullName);

        public int Count => types.Count;
    }

    private static readonly CamelCaseNamingStrategy Camel = new();

    /// <summary>
    /// The game types whose serialized members become scene/prefab keys: the component types and, transitively, the
    /// plain data types nested in their data. The obfuscator renames the members of exactly these, and nothing else,
    /// so every renamed key can be remapped. Reference members (ids) and engine types are not followed.
    /// </summary>
    public static HashSet<string> SceneDataTypes(IEnumerable<Type> componentTypes)
    {
        ArgumentNullException.ThrowIfNull(componentTypes);
        var roots = componentTypes.ToList();
        var gameAssemblies = roots.Select(t => t.Assembly).ToHashSet();
        var visited = new HashSet<Type>();
        foreach (var root in roots)
        {
            Walk(root, gameAssemblies, visited);
        }

        return visited.Select(t => t.FullName).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    private static void Walk(Type type, HashSet<Assembly> gameAssemblies, HashSet<Type> visited)
    {
        if (!visited.Add(type))
        {
            return;
        }

        foreach (var member in SceneSerializer.SerializedMembers(type))
        {
            if (!member.IsReference && ChildType(member.MemberType) is { } child && gameAssemblies.Contains(child.Assembly))
            {
                Walk(child, gameAssemblies, visited);
            }
        }
    }

    /// <summary>
    /// Builds the key map from the obfuscator's member renames ("DeclaringTypeFullName::member" → new member),
    /// mirroring the serializer so each scene key maps to what the obfuscated game reads. Only types in
    /// <see cref="SceneDataTypes"/> have renamed members, so the walk and the renames cover the same graph.
    /// </summary>
    public static IKeyMap BuildKeyMap(IEnumerable<Type> componentTypes, IReadOnlyDictionary<string, string> memberRenames)
    {
        ArgumentNullException.ThrowIfNull(componentTypes);
        ArgumentNullException.ThrowIfNull(memberRenames);
        var roots = componentTypes.ToList();
        var gameAssemblies = roots.Select(t => t.Assembly).ToHashSet();
        var built = new Dictionary<string, IReadOnlyDictionary<string, KeyRemap>>(StringComparer.Ordinal);
        var visited = new HashSet<Type>();
        foreach (var root in roots)
        {
            Build(root, gameAssemblies, memberRenames, built, visited);
        }

        return new KeyMap(built);
    }

    private static void Build(Type type, HashSet<Assembly> gameAssemblies, IReadOnlyDictionary<string, string> memberRenames,
        Dictionary<string, IReadOnlyDictionary<string, KeyRemap>> built, HashSet<Type> visited)
    {
        if (!visited.Add(type) || type.FullName is not { } id)
        {
            return;
        }

        var keys = new Dictionary<string, KeyRemap>(StringComparer.Ordinal);
        foreach (var member in SceneSerializer.SerializedMembers(type))
        {
            string? childId = null;
            var descent = Descent.None;
            if (!member.IsReference && ChildType(member.MemberType) is { } child && gameAssemblies.Contains(child.Assembly))
            {
                Build(child, gameAssemblies, memberRenames, built, visited);
                if (built.ContainsKey(child.FullName!))
                {
                    childId = child.FullName;
                    descent = DescentOf(member.MemberType);
                }
            }

            var newKey = member.DeclaringType.FullName is { } declaring && memberRenames.TryGetValue(declaring + "::" + member.MemberName, out var newName)
                ? KeyFor(member.DeclaringType, newName)
                : member.JsonKey;

            if (newKey != member.JsonKey || childId is not null)
            {
                keys[member.JsonKey] = new KeyRemap(newKey, childId, descent);
            }
        }

        if (keys.Count > 0)
        {
            built[id] = keys;
        }
    }

    // The JSON key the serializer gives a member named <paramref name="name"/> on <paramref name="declaring"/>:
    // components use ComponentTypeInfo's rule (trim a leading underscore, lower-case the first letter); nested data
    // types use the camel-case naming strategy. Matches ComponentContractResolver.
    private static string KeyFor(Type declaring, string name)
    {
        if (!typeof(Component).IsAssignableFrom(declaring))
        {
            return Camel.GetPropertyName(name, hasSpecifiedName: false);
        }

        var trimmed = name.TrimStart('_');
        return trimmed.Length == 0 ? name : char.ToLowerInvariant(trimmed[0]) + trimmed[1..];
    }

    // The object type to descend into for a member of this type, or null for scalars/engine types handled as leaves.
    private static Type? ChildType(Type type)
    {
        if (type.IsArray)
        {
            return Elementish(type.GetElementType());
        }

        if (type.IsGenericType)
        {
            var args = type.GetGenericArguments();
            if (args.Length == 2 && typeof(IDictionary).IsAssignableFrom(type))
            {
                return Elementish(args[1]);
            }

            if (args.Length == 1 && typeof(IEnumerable).IsAssignableFrom(type))
            {
                return Elementish(args[0]);
            }

            return null;
        }

        return Elementish(type);
    }

    // Keeps only a type worth descending into: a non-primitive class or struct that serializes as a JSON object.
    private static Type? Elementish(Type? type) =>
        type is null || type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
            ? null
            : type;

    private static Descent DescentOf(Type type)
    {
        if (type.IsArray)
        {
            return Descent.Array;
        }

        if (type.IsGenericType)
        {
            var args = type.GetGenericArguments();
            if (args.Length == 2 && typeof(IDictionary).IsAssignableFrom(type))
            {
                return Descent.DictionaryValues;
            }

            if (args.Length == 1 && typeof(IEnumerable).IsAssignableFrom(type))
            {
                return Descent.Array;
            }
        }

        return Descent.Object;
    }
}
