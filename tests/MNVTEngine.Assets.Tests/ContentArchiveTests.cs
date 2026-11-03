using System.Text;
using MNVTEngine.Serialization;

namespace MNVTEngine.Assets.Tests;

public sealed class ContentArchiveTests : IDisposable
{
    private static readonly byte[] Key = [.. Enumerable.Range(1, ContentArchive.KeySize).Select(i => (byte)i)];
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-archive-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string name, params (string Path, byte[] Data)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var writer = new ContentArchive.Writer(path, Key);
        foreach (var (entryPath, data) in entries)
        {
            writer.Add(entryPath, data);
        }

        return path;
    }

    private static readonly byte[] Text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("mission \"secret_plan\" { } ", 50)));
    private static readonly byte[] Binary = [.. Enumerable.Range(0, 300).Select(i => (byte)(i * 7919 % 256))];

    [Fact]
    public void RoundTrips_CompressibleAndIncompressibleEntries_WithoutPlaintextOnDisk()
    {
        var path = Write("a.mnvtpak", ("Assets/Missions/Plan.mnvts", Text), ("Scripts\\Game.dll", Binary), ("empty", []));

        using var archive = ContentArchive.Open(path, Key);
        Assert.Equal(["Assets/Missions/Plan.mnvts", "Scripts/Game.dll", "empty"], archive.Entries);
        Assert.Equal(Text, archive.Read("assets/missions/plan.mnvts"));
        Assert.Equal(Binary, archive.Read("Scripts/Game.dll"));
        Assert.Empty(archive.Read("empty"));
        Assert.Null(archive.TryRead("missing"));
        Assert.Throws<FileNotFoundException>(() => archive.Read("missing"));

        var raw = File.ReadAllBytes(path);
        Assert.True(raw.Length < Text.Length); // compressed
        Assert.DoesNotContain("secret_plan", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        Assert.DoesNotContain("Plan.mnvts", Encoding.UTF8.GetString(raw), StringComparison.Ordinal); // the index is encrypted too
    }

    [Fact]
    public void SameInput_GivesIdenticalArchives()
    {
        var a = File.ReadAllBytes(Write("a.mnvtpak", ("x", Text), ("y", Binary)));
        var b = File.ReadAllBytes(Write("b.mnvtpak", ("x", Text), ("y", Binary)));
        Assert.Equal(a, b);
    }

    [Fact]
    public void WrongKey_IsRejected()
    {
        var path = Write("a.mnvtpak", ("x", Text));
        var other = (byte[])Key.Clone();
        other[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => ContentArchive.Open(path, other));
        Assert.Throws<ArgumentException>(() => ContentArchive.Open(path, [1, 2, 3]));
    }

    [Fact]
    public void ModifiedEntry_IsRejected_OtherEntriesStillRead()
    {
        var path = Write("a.mnvtpak", ("x", Text), ("y", Binary));
        var bytes = File.ReadAllBytes(path);
        bytes[60] ^= 0x40; // inside the first entry's ciphertext (header is 52 bytes)
        File.WriteAllBytes(path, bytes);

        using var archive = ContentArchive.Open(path, Key);
        Assert.Throws<InvalidDataException>(() => archive.Read("x"));
        Assert.Equal(Binary, archive.Read("y"));
    }

    [Fact]
    public void ModifiedIndexOrHeader_IsRejected_AndDuplicatesCannotBeWritten()
    {
        var path = Write("a.mnvtpak", ("x", Text));
        var bytes = File.ReadAllBytes(path);
        bytes[^3] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => ContentArchive.Open(path, Key));

        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("definitely not an archive, but long enough for a header....."));
        Assert.Throws<InvalidDataException>(() => ContentArchive.Open(path, Key));

        using var writer = new ContentArchive.Writer(Path.Combine(_root, "d.mnvtpak"), Key);
        writer.Add("x", Text);
        Assert.Throws<ArgumentException>(() => writer.Add("X", Text));
    }

    [Fact]
    public void PackagedAssetDatabase_ServesIndexDataAndBytecode_AndRefusesEscapingPaths()
    {
        var assets = Path.Combine(_root, "Assets");
        Directory.CreateDirectory(Path.Combine(assets, "Text"));
        File.WriteAllText(Path.Combine(assets, "Text", "readme.txt"), "hello");
        File.WriteAllText(Path.Combine(assets, "Text", "readme.txt.meta"), """{ "schemaVersion": 1, "guid": "44444444-4444-4444-8444-444444444444", "settings": {} }""");
        var source = AssetDatabase.OpenReadOnly(assets);
        var guid = Guid.Parse("44444444-4444-4444-8444-444444444444");
        Assert.Null(source.ReadCompiled(guid));

        var path = Write(
            "game.mnvtpak",
            (AssetDatabase.IndexEntry, Encoding.UTF8.GetBytes(source.ExportIndex().ToString())),
            ("Assets/Text/readme.txt", Encoding.UTF8.GetBytes("hello")),
            (AssetDatabase.CompiledEntry(guid), [9, 9]));
        using var archive = ContentArchive.Open(path, Key);
        var packaged = AssetDatabase.OpenPackaged(archive);

        Assert.True(packaged.IsPackaged);
        Assert.Equal(AssetTypes.Text, packaged.GetAssetType(guid));
        Assert.Equal("hello", Encoding.UTF8.GetString(packaged.ReadAllBytes(guid)));
        Assert.Equal([9, 9], packaged.ReadCompiled(guid));
        Assert.Throws<ArgumentException>(() => packaged.ReadAllBytes("../outside.txt"));
        Assert.Throws<InvalidOperationException>(() => packaged.FullPath(guid));
        Assert.False(packaged.Refresh().HasChanges);
    }
}
