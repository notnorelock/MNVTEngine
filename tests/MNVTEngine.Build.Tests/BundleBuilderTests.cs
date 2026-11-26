// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using MNVTEngine.Bundles;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build.Tests;

/// <summary>Content bundles from a fresh project (nothing imported yet): what goes in, what stays out.</summary>
public sealed class BundleBuilderTests : IDisposable
{
    private const string Prefab = "8c1d0000-0000-4000-8000-000000000001";
    private const string Sheet = "8c1d0000-0000-4000-8000-000000000002";
    private const string SdkTiles = "8c1d0000-0000-4000-8000-000000000003";
    private const string Clip = "8c1d0000-0000-4000-8000-000000000004";
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-bundle-build-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string path, string content, string? guid = null)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        if (guid is not null)
        {
            File.WriteAllText(full + ".meta", $$"""{ "schemaVersion": 1, "guid": "{{guid}}", "settings": {} }""");
        }
    }

    private static string PrefabReferencing(params string[] guids) => $$"""
        { "schemaVersion": 3, "name": "Lounge", "gameObjects": [ { "id": "{{Guid.NewGuid()}}", "name": "Lounge", "active": true, "parent": null,
          "components": [ { "type": "Game.Decor", "id": "{{Guid.NewGuid()}}", "enabled": true,
            "data": { "refs": [ {{string.Join(", ", guids.Select(g => $"\"asset:{g}\""))}} ] } } ] } ] }
        """;

    private GameProject Project(string prefab)
    {
        Write("Game.mnvtproj", """{ "schemaVersion": 2, "name": "Game", "profile": "2d", "startupScene": "Main.mnvt" }""");
        Write("Assets/Main.mnvt", """{ "schemaVersion": 3, "name": "Main", "gameObjects": [] }""", "8c1d0000-0000-4000-8000-0000000000ff");
        Write("Assets/Lounge.mnvtprefab", prefab, Prefab);
        Write("Assets/Art/Sheet.png", "not decoded by the builder", Sheet);
        Write("Assets/Sdk/Tiles.png", "shipped by the game", SdkTiles);
        Write("Assets/Art/Walk.mnvtanim", $$"""{ "texture": "asset:{{Sheet}}" }""", Clip);
        Write("Assets/Logic.mnvts", "mission Logic {}", "8c1d0000-0000-4000-8000-000000000005");
        return GameProject.Load(Path.Combine(_root, "Game.mnvtproj"));
    }

    [Fact]
    public void ABundle_HoldsWhatThePrefabReaches_ThroughDocuments_ButNotProvidedFolders()
    {
        var project = Project(PrefabReferencing(Clip, SdkTiles));
        var result = BundleBuilder.Build(project, new BundleRequest("Assets/Lounge.mnvtprefab", "game.world", "Lounge", Path.Combine(_root, "out"), ["Sdk"]));

        Assert.NotNull(result.File);
        using var bundle = ContentBundleFile.Open(result.File, new ContentBundlePolicy { Kind = "game.world" });
        Assert.Equal(["Art/Sheet.png", "Art/Walk.mnvtanim", "Lounge.mnvtprefab"], bundle.Assets.Select(a => a.Path).Order(StringComparer.Ordinal));
        Assert.Equal(Guid.Parse(Prefab), bundle.Manifest.Root);
    }

    [Fact]
    public void AReferencedScript_FailsTheBuild_AndWritesNothing()
    {
        var project = Project(PrefabReferencing("8c1d0000-0000-4000-8000-000000000005"));
        var result = BundleBuilder.Build(project, new BundleRequest("Lounge.mnvtprefab", "game.world", "Lounge", Path.Combine(_root, "out")));

        Assert.Null(result.File);
        Assert.Contains(result.Diagnostics, d => d.Code == "MNVB123");
        Assert.False(Directory.Exists(Path.Combine(_root, "out")) && Directory.EnumerateFiles(Path.Combine(_root, "out")).Any());
    }
}
