// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

namespace MNVTEngine.Scenes.Tests;

public sealed class RestrictedRegistryTests
{
    [Fact]
    public void ARestrictedRegistry_CreatesOnlyAllowedComponents_AndKeepsTheRestInert()
    {
        var full = TestRegistry.Create().Register<PlainMover>();
        var scene = new Scene("source");
        var root = scene.CreateGameObject("World");
        root.AddComponent<Tilemap>().SetTile(1, 2, 3);
        root.AddComponent<PlainMover>();
        var prefab = new SceneSerializer(full).SerializePrefab(root);

        var restricted = full.Where(t => t == typeof(Tilemap));
        Assert.Contains(typeof(Transform), restricted.Types.Values);
        Assert.DoesNotContain(typeof(PlainMover), restricted.Types.Values);

        var target = new Scene("target");
        var instance = new SceneSerializer(restricted).InstantiatePrefab(prefab, target);
        Assert.Equal(3, instance.Root.GetComponent<Tilemap>()!.GetTile(1, 2));
        Assert.Null(instance.Root.GetComponent<PlainMover>());
        Assert.NotNull(instance.Root.GetComponent<MissingComponent>()); // inert: kept as data, never runs
        Assert.Contains(instance.Diagnostics, d => d.Message.Contains("PlainMover", StringComparison.Ordinal));
    }
}
