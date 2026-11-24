// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Build;

/// <summary>
/// Rewrites the component type ids in a scene or prefab so a game whose script assembly was obfuscated still loads
/// its content. Obfuscation renames the component classes; this replaces each old type id (the class's full name)
/// with the new one wherever it appears as a <c>"type"</c> in the document. Serialized member names are kept by the
/// obfuscator, so a component's <c>"data"</c> is left untouched and nested serialized values stay valid.
/// </summary>
public static class SceneRemapper
{
    /// <summary>
    /// Returns <paramref name="sceneJson"/> with component type ids remapped by <paramref name="typeRenames"/>
    /// (old full name → new full name), or the input unchanged when nothing matches. Throws on invalid JSON.
    /// </summary>
    public static byte[] Remap(byte[] sceneJson, IReadOnlyDictionary<string, string> typeRenames)
    {
        ArgumentNullException.ThrowIfNull(sceneJson);
        ArgumentNullException.ThrowIfNull(typeRenames);
        if (typeRenames.Count == 0) return sceneJson;

        var root = JToken.Parse(Encoding.UTF8.GetString(sceneJson));
        var changed = Rewrite(root, typeRenames);
        if (changed == 0) return sceneJson;
        return Encoding.UTF8.GetBytes(root.ToString(Formatting.None));
    }

    private static int Rewrite(JToken token, IReadOnlyDictionary<string, string> typeRenames)
    {
        var changed = 0;
        switch (token)
        {
            case JObject obj:
                // A component is an object with a string "type"; everything else is traversed for nested objects.
                if (obj["type"] is JValue { Type: JTokenType.String } typeValue
                    && typeValue.Value<string>() is { } id
                    && typeRenames.TryGetValue(id, out var renamed))
                {
                    obj["type"] = renamed;
                    changed++;
                }

                foreach (JProperty property in obj.Properties())
                {
                    changed += Rewrite(property.Value, typeRenames);
                }

                break;
            case JArray array:
                foreach (JToken item in array)
                {
                    changed += Rewrite(item, typeRenames);
                }

                break;
        }

        return changed;
    }
}
