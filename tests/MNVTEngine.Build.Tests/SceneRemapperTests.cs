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
    public void RenamesDataKeys_ThroughNestedObjectsAndArrays_KeepingEngineKeys()
    {
        var members = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{typeof(ObfHero).FullName}::Speed"] = "AaA",
            [$"{typeof(ObfHero).FullName}::Loadout"] = "BbB",
            [$"{typeof(ObfHero).FullName}::Party"] = "CcC",
            [$"{typeof(ObfStats).FullName}::Health"] = "DdD",
            [$"{typeof(ObfStats).FullName}::Mana"] = "EeE",
        };
        var keyMap = SceneRemapper.BuildKeyMap([typeof(ObfHero)], members);

        var scene = $$"""
        {
          "gameObjects": [
            {
              "components": [
                { "type": "MNVTEngine.Camera", "data": { "fieldOfView": 60.0 } },
                { "type": "{{typeof(ObfHero).FullName}}", "data": {
                    "speed": 5.0,
                    "loadout": { "health": 10, "mana": 3 },
                    "party": [ { "health": 1, "mana": 2 } ]
                } }
              ]
            }
          ]
        }
        """;

        var result = JObject.Parse(Encoding.UTF8.GetString(SceneRemapper.Remap(Encoding.UTF8.GetBytes(scene), null, keyMap)));
        var components = result["gameObjects"]![0]!["components"]!;

        Assert.Equal(60.0, (double?)components[0]!["data"]!["fieldOfView"]); // engine component untouched
        var data = components[1]!["data"]!;
        Assert.Null(data["speed"]);
        Assert.Equal(5.0, (double?)data["aaA"]); // component member key renamed (JsonName of the new name)
        Assert.Equal(10, (int?)data["bbB"]!["ddD"]); // nested object key renamed (camelCase of the new name)
        Assert.Equal(3, (int?)data["bbB"]!["eeE"]);
        Assert.Equal(1, (int?)data["ccC"]![0]!["ddD"]); // through an array of nested objects
    }

    private sealed class ObfStats
    {
        public int Health = 1;
        public int Mana = 1;
    }

    private sealed class ObfHero : MNVTEngine.Component
    {
        public float Speed = 1f;
        public ObfStats Loadout = new();
        public ObfStats[] Party = [];
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
