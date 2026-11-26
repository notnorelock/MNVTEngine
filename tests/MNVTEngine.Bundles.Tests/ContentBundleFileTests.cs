// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Text;
using MNVTEngine.IO.Packages;
using MNVTEngine.Serialization;

namespace MNVTEngine.Bundles.Tests;

public sealed class ContentBundleFileTests : IDisposable
{
    private static readonly Guid Prefab = Guid.Parse("6f0c1a52-6b1e-4f3e-9d61-0a8f3b1c2d01");
    private static readonly Guid Tiles = Guid.Parse("6f0c1a52-6b1e-4f3e-9d61-0a8f3b1c2d02");
    private readonly string _folder = Directory.CreateTempSubdirectory("mnvt-bundles-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string File(string name = "lounge") => Path.Combine(_folder, name + ContentBundleFile.Extension);

    private static ContentBundleManifest Manifest(string kind = "game.world") =>
        new(kind, "Lounge", Prefab, new Dictionary<string, string> { ["sdk"] = "1" });

    private static (ContentBundleAsset, byte[]) Asset(Guid guid, string path, string type) =>
        (new ContentBundleAsset(guid, path, type, null, []), Encoding.UTF8.GetBytes("{}"));

    private string WriteValid()
    {
        var file = File();
        ContentBundleFile.Write(file, Manifest(), [Asset(Prefab, "Lounge.prefab", AssetTypes.Prefab), Asset(Tiles, "Tiles.png", AssetTypes.Texture)], Guid.NewGuid(), Guid.NewGuid());
        return file;
    }

    [Fact]
    public void AValidBundle_Opens_WithItsManifestAndAssets()
    {
        using var bundle = ContentBundleFile.Open(WriteValid(), new ContentBundlePolicy { Kind = "game.world" });

        Assert.Equal(("game.world", "Lounge", Prefab, "1"), (bundle.Manifest.Kind, bundle.Manifest.Name, bundle.Manifest.Root, bundle.Manifest.Properties["sdk"]));
        Assert.Equal("Lounge.prefab", bundle.Root.Path);
        Assert.Equal(2, bundle.Assets.Count);
        Assert.Equal("{}", Encoding.UTF8.GetString(bundle.ReadAsset(Tiles)));
    }

    [Fact]
    public void AnEncryptedBundle_OpensOnlyWithItsKey()
    {
        var key = PackageEncryptionKey.Create();
        var file = File("sealed");
        ContentBundleFile.Write(file, Manifest(), [Asset(Prefab, "Lounge.prefab", AssetTypes.Prefab)], Guid.NewGuid(), Guid.NewGuid(), key: key);

        Assert.Equal(-1, System.IO.File.ReadAllBytes(file).AsSpan().IndexOf("Lounge"u8)); // the manifest and index are not readable
        using (var bundle = ContentBundleFile.Open(file, new ContentBundlePolicy { RequireEncrypted = true }, key))
        {
            Assert.Equal("{}", Encoding.UTF8.GetString(bundle.ReadAsset(Prefab)));
        }

        Assert.Throws<ContentBundleException>(() => ContentBundleFile.Open(file));
        Assert.Throws<ContentBundleException>(() => ContentBundleFile.Open(file, key: PackageEncryptionKey.Create()));
        Assert.Contains("not encrypted", Assert.Throws<ContentBundleException>(() => ContentBundleFile.Open(WriteValid(), new ContentBundlePolicy { RequireEncrypted = true })).Problems.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Bundles_OfAnotherKind_OrTooLarge_AreRefused()
    {
        var file = WriteValid();
        Assert.Contains("kind", Assert.Throws<ContentBundleException>(() => ContentBundleFile.Open(file, new ContentBundlePolicy { Kind = "game.avatar" })).Problems.Single(), StringComparison.Ordinal);
        Assert.Contains("bytes", Assert.Throws<ContentBundleException>(() => ContentBundleFile.Open(file, new ContentBundlePolicy { MaxBytes = 10 })).Problems.Single(), StringComparison.Ordinal);
        Assert.Throws<ContentBundleException>(() => ContentBundleFile.Open(file, new ContentBundlePolicy { MaxAssets = 1 }));
    }

    [Fact]
    public void Scripts_AndEntriesOutsideTheLayout_AreRefused_AllAtOnce()
    {
        var file = File("sneaky");
        using (var writer = new PackageWriter(file, new PackageWriterOptions(Guid.NewGuid(), Guid.NewGuid())))
        {
            writer.Add(ContentBundleFile.ManifestEntry, ContentBundleFile.WriteManifest(Manifest()));
            writer.Add(ContentBundleFile.IndexEntry, ContentBundleFile.WriteIndex([Asset(Prefab, "Lounge.prefab", AssetTypes.Prefab).Item1, Asset(Tiles, "Mission.mnvts", AssetTypes.Script).Item1]));
            writer.Add("Assets/Lounge.prefab", "{}"u8.ToArray(), Prefab);
            writer.Add("Assets/Mission.mnvts", "x"u8.ToArray(), Tiles);
            writer.Add("Scripts/Game.dll", [1, 2, 3]);
            writer.Add($"Artifacts/{Guid.NewGuid():D}/model", [1]);
            writer.Complete();
        }

        var problems = Assert.Throws<ContentBundleException>(() => ContentBundleFile.Open(file)).Problems;
        Assert.Contains(problems, p => p.StartsWith("Scripts/Game.dll", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("type Script", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("artifact of no asset", StringComparison.Ordinal));
    }

    [Fact]
    public void ARootThatIsNotAPrefab_OrAListedAssetMissingFromThePackage_IsRefused()
    {
        var file = File("broken");
        using (var writer = new PackageWriter(file, new PackageWriterOptions(Guid.NewGuid(), Guid.NewGuid())))
        {
            writer.Add(ContentBundleFile.ManifestEntry, ContentBundleFile.WriteManifest(Manifest() with { Root = Tiles }));
            writer.Add(ContentBundleFile.IndexEntry, ContentBundleFile.WriteIndex([Asset(Prefab, "Lounge.prefab", AssetTypes.Prefab).Item1, Asset(Tiles, "Tiles.png", AssetTypes.Texture).Item1]));
            writer.Add("Assets/Lounge.prefab", "{}"u8.ToArray(), Prefab);
            writer.Complete();
        }

        var problems = Assert.Throws<ContentBundleException>(() => ContentBundleFile.Open(file)).Problems;
        Assert.Contains(problems, p => p.Contains("Tiles.png: listed but not in the package", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("root: not a prefab", StringComparison.Ordinal));
    }
}
