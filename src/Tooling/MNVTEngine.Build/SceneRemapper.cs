// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Build;

/// <summary>
/// Rewrites a scene or prefab so a game whose script assembly was obfuscated still loads its content. Obfuscation
/// renames the component classes and their serialized members; this replaces each old type id (the class's full
/// name) with the new one wherever it appears as a <c>"type"</c>, and renames the keys inside that component's
/// <c>"data"</c> to match the renamed members. Nested values (engine types, or a component's own plain data types)
/// carry no type id in the JSON, so their keys are left untouched — the engine reads them by their kept names.
/// </summary>
public static class SceneRemapper
{
    /// <summary>
    /// Returns <paramref name="sceneJson"/> with component type ids remapped by <paramref name="typeRenames"/>
    /// (old full name → new full name) and each component's <c>"data"</c> keys remapped by
    /// <paramref name="memberRenames"/> (old full name → old JSON key → new JSON key), or the input unchanged when
    /// nothing matches. Throws on invalid JSON.
    /// </summary>
    public static byte[] Remap(
        byte[] sceneJson,
        IReadOnlyDictionary<string, string>? typeRenames,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? memberRenames = null)
    {
        ArgumentNullException.ThrowIfNull(sceneJson);
        var types = typeRenames ?? EmptyTypes;
        var members = memberRenames ?? EmptyMembers;
        if (types.Count == 0 && members.Count == 0) return sceneJson;

        var root = JToken.Parse(Encoding.UTF8.GetString(sceneJson));
        var changed = Rewrite(root, types, members);
        if (changed == 0) return sceneJson;
        return Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyTypes = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> EmptyMembers = new Dictionary<string, IReadOnlyDictionary<string, string>>();

    private static int Rewrite(
        JToken token,
        IReadOnlyDictionary<string, string> typeRenames,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> memberRenames)
    {
        var changed = 0;
        switch (token)
        {
            case JObject obj:
                // A component is an object with a string "type"; everything else is traversed for nested objects.
                if (obj["type"] is JValue { Type: JTokenType.String } typeValue
                    && typeValue.Value<string>() is { } id)
                {
                    if (memberRenames.TryGetValue(id, out var keyMap) && obj["data"] is JObject data)
                    {
                        changed += RenameKeys(data, keyMap);
                    }

                    if (typeRenames.TryGetValue(id, out var renamed))
                    {
                        obj["type"] = renamed;
                        changed++;
                    }
                }

                foreach (JProperty property in obj.Properties())
                {
                    changed += Rewrite(property.Value, typeRenames, memberRenames);
                }

                break;
            case JArray array:
                foreach (JToken item in array)
                {
                    changed += Rewrite(item, typeRenames, memberRenames);
                }

                break;
        }

        return changed;
    }

    // Renames a component's top-level data keys, preserving order; nested values are left as-is.
    private static int RenameKeys(JObject data, IReadOnlyDictionary<string, string> keyMap)
    {
        var hits = data.Properties().Count(p => keyMap.ContainsKey(p.Name));
        if (hits == 0) return 0;

        var rebuilt = data.Properties()
            .Select(p => new JProperty(keyMap.TryGetValue(p.Name, out var newKey) ? newKey : p.Name, p.Value.DeepClone()))
            .Cast<object>()
            .ToArray();
        data.ReplaceAll(rebuilt);
        return hits;
    }
}
