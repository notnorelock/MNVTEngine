// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using System.Text;
using MNVTEngine.Build;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Build.Tests;

public sealed class SceneRemapperTests
{
    private static readonly Dictionary<string, string> Renames = new(StringComparer.Ordinal)
    {
        ["Game.PlayerController"] = "ÌÍÎ",
        ["Game.Enemy"] = "ÏÌÍ",
    };

    [Fact]
    public void RenamesGameComponentTypes_KeepsEngineTypesAndData()
    {
        const string Scene = """
        {
          "schemaVersion": 3,
          "gameObjects": [
            {
              "name": "Player",
              "components": [
                { "type": "MNVTEngine.Camera", "data": { "fieldOfView": 60.0 } },
                { "type": "Game.PlayerController", "data": { "speed": 5.0, "mode": { "type": "patrol" } } }
              ]
            }
          ]
        }
        """;

        var result = JObject.Parse(Encoding.UTF8.GetString(SceneRemapper.Remap(Encoding.UTF8.GetBytes(Scene), Renames)));
        var components = result["gameObjects"]![0]!["components"]!;

        Assert.Equal("MNVTEngine.Camera", (string?)components[0]!["type"]); // engine type kept
        Assert.Equal("ÌÍÎ", (string?)components[1]!["type"]); // game type renamed
        Assert.Equal(5.0, (double?)components[1]!["data"]!["speed"]); // serialized member names kept
        Assert.Equal("patrol", (string?)components[1]!["data"]!["mode"]!["type"]); // a nested data value that is not a game type id is untouched
    }

    [Fact]
    public void WithNoMatchingTypes_ReturnsTheInputUnchanged()
    {
        var bytes = Encoding.UTF8.GetBytes("""{ "gameObjects": [ { "components": [ { "type": "MNVTEngine.Camera" } ] } ] }""");
        Assert.Same(bytes, SceneRemapper.Remap(bytes, Renames));
    }

    [Fact]
    public void RemapsPrefabNestingAndChildren()
    {
        const string Prefab = """
        { "root": { "name": "Enemy", "components": [ { "type": "Game.Enemy", "data": {} } ],
          "children": [ { "components": [ { "type": "Game.PlayerController" } ] } ] } }
        """;
        var result = JObject.Parse(Encoding.UTF8.GetString(SceneRemapper.Remap(Encoding.UTF8.GetBytes(Prefab), Renames)));
        Assert.Equal("ÏÌÍ", (string?)result["root"]!["components"]![0]!["type"]);
        Assert.Equal("ÌÍÎ", (string?)result["root"]!["children"]![0]!["components"]![0]!["type"]);
    }
}
