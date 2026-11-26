// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Text;
using MNVTEngine.Bundles;
using MNVTEngine.Serialization;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Assets.Tests;

public sealed class AssetDatabaseBundleTests : IDisposable
{
    private static readonly Guid Prefab = Guid.Parse("0b6f3c1e-2a44-4d8e-9a55-7c1d2e3f4a01");
    private static readonly Guid Tiles = Guid.Parse("0b6f3c1e-2a44-4d8e-9a55-7c1d2e3f4a02");
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-bundle-db-").FullName;

    public AssetDatabaseBundleTests() => Directory.CreateDirectory(Path.Combine(_root, "Assets"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private ContentBundleFile Bundle(string name, params (Guid Guid, string Path, string Type)[] assets)
    {
        var file = Path.Combine(_root, name + ContentBundleFile.Extension);
        ContentBundleFile.Write(file, new ContentBundleManifest("test.world", name, assets[0].Guid, new Dictionary<string, string>()),
            [.. assets.Select(a => (new ContentBundleAsset(a.Guid, a.Path, a.Type, null, new JObject { ["filter"] = "point" }), Encoding.UTF8.GetBytes(a.Path)))],
            Guid.NewGuid(), Guid.NewGuid());
        return ContentBundleFile.Open(file);
    }

    [Fact]
    public void AMountedBundle_LoadsByGuid_UnderItsOwnFolder_UntilUnmounted()
    {
        var db = AssetDatabase.OpenReadOnly(Path.Combine(_root, "Assets"));
        var mount = db.MountBundle(Bundle("lounge", (Prefab, "Lounge.prefab", AssetTypes.Prefab), (Tiles, "Art/Tiles.png", AssetTypes.Texture)));

        Assert.Equal($"Bundles/{mount.Name}/Art/Tiles.png", db.Get(Tiles)!.Path);
        Assert.Equal(AssetTypes.Texture, db.GetAssetType(Tiles));
        Assert.Equal("Art/Tiles.png", Encoding.UTF8.GetString(db.ReadAllBytes(Tiles)));
        Assert.Equal("point", db.GetSettings(Tiles).Value<string>("filter"));
        Assert.True(db.TryGetGuid($"Bundles/{mount.Name}/Lounge.prefab", out var byPath));
        Assert.Equal(Prefab, byPath);
        Assert.Empty(db.Assets); // the project's own records stay apart
        db.Refresh();
        Assert.NotNull(db.Get(Tiles)); // a refresh keeps mounted bundles

        Assert.True(db.UnmountBundle(mount));
        Assert.Null(db.Get(Tiles));
        Assert.False(db.UnmountBundle(mount));
    }

    [Fact]
    public void ABundle_NeverReplacesAnAssetThatIsLoaded()
    {
        var db = AssetDatabase.OpenReadOnly(Path.Combine(_root, "Assets"));
        db.MountBundle(Bundle("first", (Prefab, "Lounge.prefab", AssetTypes.Prefab)));
        using var second = Bundle("second", (Prefab, "Other.prefab", AssetTypes.Prefab));

        var refused = Assert.Throws<ContentBundleException>(() => db.MountBundle(second));
        Assert.Contains("already loaded", refused.Problems.Single(), StringComparison.Ordinal);
        Assert.Equal("Lounge.prefab", Path.GetFileName(db.Get(Prefab)!.Path));
    }
}
