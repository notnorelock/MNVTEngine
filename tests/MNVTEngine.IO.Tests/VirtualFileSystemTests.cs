using System.Text;

namespace MNVTEngine.IO.Tests;

public sealed class VirtualPathTests
{
    [Theory]
    [InlineData("Assets/Textures/a.png", "Assets/Textures/a.png")]
    [InlineData("Assets\\Textures\\a.png", "Assets/Textures/a.png")]
    [InlineData("Assets//./Textures/../Sprites/b.png/", "Assets/Sprites/b.png")]
    [InlineData("", "")]
    public void Normalize_ProducesCanonicalForm(string input, string expected) => Assert.Equal(expected, VirtualPath.Normalize(input));

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("Assets/../../x")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("a/b:stream")]
    [InlineData("a/\u0001b")]
    [InlineData("a/name. ")]
    public void Normalize_RefusesEscapingRootedAndInvalidPaths(string input) => Assert.Throws<ArgumentException>(() => VirtualPath.Normalize(input));

    [Fact]
    public void IsUnder_MatchesWholeSegmentsOnly()
    {
        Assert.True(VirtualPath.IsUnder("Assets/Web/index.html", "Assets/Web"));
        Assert.True(VirtualPath.IsUnder("assets/web/index.html", "Assets/Web"));
        Assert.False(VirtualPath.IsUnder("Assets/WebExtra/index.html", "Assets/Web"));
        Assert.True(VirtualPath.IsUnder("anything", ""));
    }
}

public sealed class VirtualFileSystemTests
{
    private static MemoryFileSystemProvider Memory(string name, params (string Path, string Text)[] files)
    {
        var provider = new MemoryFileSystemProvider(name);
        foreach (var (path, text) in files)
        {
            provider.Add(path, Encoding.UTF8.GetBytes(text));
        }

        return provider;
    }

    private static string Text(VirtualFileSystem vfs, string path)
    {
        using var stream = vfs.OpenRead(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void HigherPriority_Wins_AndUnmountRevealsTheLowerOne()
    {
        using var vfs = new VirtualFileSystem();
        vfs.Mount(Memory("base", ("Assets/a.txt", "base"), ("Assets/b.txt", "only base")));
        var patch = vfs.Mount(Memory("patch", ("Assets/a.txt", "patch")), new MountOptions(Priority: 10));

        Assert.Equal("patch", Text(vfs, "Assets/a.txt"));
        Assert.Equal("only base", Text(vfs, "assets/B.TXT"));
        Assert.Equal("patch", vfs.SourceOf("Assets/a.txt"));
        Assert.Equal(["Assets/a.txt", "Assets/b.txt"], vfs.EnumerateFiles("Assets"));

        Assert.True(vfs.Unmount(patch));
        Assert.False(vfs.Unmount(patch));
        Assert.Equal("base", Text(vfs, "Assets/a.txt"));
    }

    [Fact]
    public void MountPoints_PlaceSourcesInFolders()
    {
        using var vfs = new VirtualFileSystem();
        vfs.Mount(Memory("web", ("index.html", "<html>"), ("ui/menu.html", "menu")), new MountOptions(MountPoint: "Assets/Web"));

        Assert.Equal("<html>", Text(vfs, "Assets/Web/index.html"));
        Assert.True(vfs.DirectoryExists("Assets"));
        Assert.True(vfs.DirectoryExists("Assets/Web/ui"));
        Assert.False(vfs.DirectoryExists("Assets/Other"));
        Assert.Equal(["Assets/Web/index.html"], vfs.EnumerateFiles("Assets/Web", recursive: false));
        Assert.Throws<FileNotFoundException>(() => vfs.OpenRead("index.html"));
    }

    [Fact]
    public void UntrustedMounts_NeverServeCode()
    {
        using var vfs = new VirtualFileSystem();
        vfs.Mount(Memory("mod", ("Scripts/Evil.dll", "MZ"), ("Assets/mod.txt", "mod")), new MountOptions(Priority: 100, Trust: MountTrust.Untrusted));

        Assert.False(vfs.FileExists("Scripts/Evil.dll"));
        Assert.Throws<FileNotFoundException>(() => vfs.OpenRead("Scripts/Evil.dll"));
        Assert.Equal(["Assets/mod.txt"], vfs.EnumerateFiles(""));
        Assert.Equal("mod", Text(vfs, "Assets/mod.txt"));
    }

    [Fact]
    public void InvalidPaths_AreRefusedOrNotFound()
    {
        using var vfs = new VirtualFileSystem();
        vfs.Mount(Memory("m", ("a.txt", "a")));
        Assert.Throws<ArgumentException>(() => vfs.OpenRead("../a.txt"));
        Assert.False(vfs.FileExists("../a.txt"));
        Assert.False(vfs.FileExists("C:/a.txt"));
        Assert.Throws<FileNotFoundException>(() => vfs.ReadAllBytes("missing.txt"));
        Assert.Null(vfs.TryReadAllBytes("missing.txt"));
        Assert.Equal(1, vfs.Statistics.Snapshot().FailedReads);
    }

    [Fact]
    public void FindAsset_IgnoresShadowedFiles()
    {
        var guid = Guid.NewGuid();
        using var vfs = new VirtualFileSystem();
        var low = new MemoryFileSystemProvider("low");
        low.Add("Assets/a.png", [1], guid);
        vfs.Mount(low);
        Assert.Equal("Assets/a.png", vfs.FindAsset(guid));

        vfs.Mount(Memory("high", ("Assets/a.png", "other")), new MountOptions(Priority: 5));
        Assert.Null(vfs.FindAsset(guid)); // the file at that path is no longer the asset
    }

    [Fact]
    public void PhysicalProvider_StaysInsideItsRoot_AndHidesExcludedFiles()
    {
        var root = Directory.CreateTempSubdirectory("mnvt-vfs-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Assets", ".hidden"));
            File.WriteAllText(Path.Combine(root, "Assets", "a.txt"), "a");
            File.WriteAllText(Path.Combine(root, "Assets", "a.txt.meta"), "meta");
            File.WriteAllText(Path.Combine(root, "Assets", ".hidden", "x.txt"), "x");
            File.WriteAllText(Path.Combine(root, "outside.txt"), "outside");

            using var vfs = new VirtualFileSystem();
            vfs.Mount(new PhysicalFileSystemProvider(Path.Combine(root, "Assets"), excludedExtensions: [".meta"]), new MountOptions(MountPoint: "Assets"));

            Assert.Equal("a", Text(vfs, "Assets/a.txt"));
            Assert.False(vfs.FileExists("Assets/a.txt.meta"));
            Assert.False(vfs.FileExists("Assets/.hidden/x.txt"));
            Assert.Throws<ArgumentException>(() => vfs.OpenRead("Assets/../outside.txt/../../outside.txt"));
            Assert.False(vfs.FileExists("outside.txt"));
            Assert.Equal(["Assets/a.txt"], vfs.EnumerateFiles(""));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentReads_WhileMountingAndUnmounting_SeeConsistentContent()
    {
        using var vfs = new VirtualFileSystem();
        vfs.Mount(Memory("base", ("a.txt", "base")));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var reads = 0;
            while (!stop.IsCancellationRequested)
            {
                var text = Encoding.UTF8.GetString(vfs.ReadAllBytes("a.txt"));
                Assert.True(text is "base" or "patch", text);
                reads++;
            }

            return reads;
        })).ToList();

        while (!stop.IsCancellationRequested)
        {
            var handle = vfs.Mount(Memory("patch", ("a.txt", "patch")), new MountOptions(Priority: 1));
            vfs.Unmount(handle);
        }

        Assert.All(await Task.WhenAll(readers), reads => Assert.True(reads > 0));
    }
}
