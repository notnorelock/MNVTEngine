// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

namespace MNVTEngine.IO.Tests;

public sealed class PrefixedFileSystemProviderTests
{
    [Fact]
    public void ChosenFolders_AppearUnderOtherFolders_AndNothingElseIsVisible()
    {
        var inner = new MemoryFileSystemProvider();
        inner.Add("Assets/Tiles.png", [1, 2]);
        inner.Add("Artifacts/abc/proxy", [3]);
        inner.Add("bundle.json", [4]);
        using var files = new VirtualFileSystem();
        files.Mount(new PrefixedFileSystemProvider(inner, new Dictionary<string, string> { ["Assets/Bundles/b1"] = "Assets", ["Artifacts"] = "Artifacts" }));

        Assert.Equal([1, 2], files.ReadAllBytes("Assets/Bundles/b1/Tiles.png"));
        Assert.Equal([3], files.ReadAllBytes("Artifacts/abc/proxy"));
        Assert.False(files.FileExists("bundle.json"));
        Assert.False(files.FileExists("Assets/Tiles.png"));
        Assert.True(files.DirectoryExists("Assets/Bundles"));
        Assert.Equal(["Artifacts/abc/proxy", "Assets/Bundles/b1/Tiles.png"], files.EnumerateFiles("", recursive: true).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FoldersInsideOneAnother_AreRefused() =>
        Assert.Throws<ArgumentException>(() => new PrefixedFileSystemProvider(new MemoryFileSystemProvider(), new Dictionary<string, string> { ["A"] = "x", ["A/B"] = "y" }));
}
