using System.Text;
using MNVTEngine.Assets;
using MNVTEngine.IO;
using MNVTEngine.IO.Packages;
using MNVTEngine.Runtime;

namespace MNVTEngine.Runtime.Tests;

/// <summary>Exported content without a full build: packages, the signed manifest, the runtime configuration and the export guard.</summary>
public sealed class PackagedContentTests : IDisposable
{
    private const string Config = """{ "kind": "mnvt.runtimeConfig", "schemaVersion": 2, "name": "Game", "profile": "2d", "startupScene": "Scenes/Main.mnvt" }""";
    private static readonly Guid SceneGuid = Guid.Parse("5ce0e000-0000-4000-8000-000000000001");
    private static readonly Guid TextureGuid = Guid.Parse("7e470000-0000-4000-8000-000000000002");
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-content-").FullName;
    private readonly PackageEncryptionKey _key = PackageEncryptionKey.Create();
    private readonly PackageSigningKey _signer = PackageSigningKey.Create();

    public void Dispose()
    {
        _signer.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private string Data => Path.Combine(_root, "Game_Data");

    private PackageOpenOptions Options => new(new PackageKeyRing(_key), [_signer.PublicKey], RequireSignature: true);

    /// <summary>A minimal exported game's data folder: globals (config + index) and a level (scene + texture), with a signed manifest.</summary>
    private GamePackage WriteGame(Guid? build = null)
    {
        Directory.CreateDirectory(Data);
        var buildId = build ?? Guid.NewGuid();
        var index = $$"""{ "schemaVersion": 1, "assets": [ { "guid": "{{SceneGuid}}", "path": "Scenes/Main.mnvt", "type": "scene", "package": "level0.mnvtpack" }, { "guid": "{{TextureGuid}}", "path": "Web/logo.png", "type": "texture", "package": "level0.mnvtpack" } ] }""";
        var packages = new List<ContentPackage>();
        foreach (var (file, kind, files) in new (string, string, (string Path, string Text, Guid Guid)[])[]
        {
            ("globals.mnvtpack", ContentPackageKinds.Globals, [(GameProject.RuntimeConfigEntry, Config, Guid.Empty), (AssetDatabase.IndexEntry, index, Guid.Empty)]),
            ("level0.mnvtpack", ContentPackageKinds.Level, [("Assets/Scenes/Main.mnvt", """{ "schemaVersion": 3, "gameObjects": [] }""", SceneGuid), ("Assets/Web/logo.png", "not really a png", TextureGuid)]),
        })
        {
            using var writer = new PackageWriter(Path.Combine(Data, file), new PackageWriterOptions(Guid.NewGuid(), buildId, EncryptionKey: _key, SigningKey: _signer));
            foreach (var (path, text, guid) in files)
            {
                writer.Add(path, Encoding.UTF8.GetBytes(text), guid);
            }

            var result = writer.Complete();
            packages.Add(new ContentPackage(file, kind, Entries: result.Files, Guid: result.PackageGuid, HeaderSha256: result.HeaderSha256));
        }

        var manifest = new GamePackage { Name = "Game", ExecutableName = "Game", BuildId = buildId, Packages = packages };
        manifest.Save(Data, _signer);
        return manifest;
    }

    [Fact]
    public void ExportedContent_LoadsThroughTheVirtualFileSystem()
    {
        WriteGame();
        var manifest = GamePackage.Load(Data, _signer.PublicKey);
        using var content = manifest.OpenContent(Data, Options);
        var project = GameProject.LoadPackaged(content);
        Assert.Equal(("Game", "Scenes/Main.mnvt"), (project.Name, project.StartupScene));

        var assets = AssetDatabase.OpenPackaged(content.Files);
        Assert.True(assets.IsPackaged);
        Assert.Equal("not really a png", Encoding.UTF8.GetString(assets.ReadAllBytes(TextureGuid)));
        Assert.Equal("Assets/Web/logo.png", content.Files.FindAsset(TextureGuid)); // the package index knows asset GUIDs too
        using var stream = assets.OpenRead(SceneGuid);
        Assert.Equal(stream.Length, assets.ReadAllBytes("Scenes/Main.mnvt").Length);
        Assert.Equal("level0.mnvtpack", content.PackageOf("Assets/Scenes/Main.mnvt"));
    }

    [Fact]
    public void Manifest_MustBeSignedByTheTrustedKey()
    {
        WriteGame();
        using var stranger = PackageSigningKey.Create();
        Assert.Throws<Serialization.SerializationException>(() => GamePackage.Load(Data, stranger.PublicKey));
        File.AppendAllText(Path.Combine(Data, GamePackage.ManifestFile), " ");
        Assert.Throws<Serialization.SerializationException>(() => GamePackage.Load(Data, _signer.PublicKey));
        File.Delete(Path.Combine(Data, GamePackage.SignatureFile));
        Assert.Throws<Serialization.SerializationException>(() => GamePackage.Load(Data, _signer.PublicKey));
        Assert.NotNull(GamePackage.Load(Data)); // tools may read it unverified
    }

    [Fact]
    public void PackagesFromAnotherBuild_AreRefused_EvenWhenValidlySigned()
    {
        var manifest = WriteGame();
        var level = File.ReadAllBytes(Path.Combine(Data, "level0.mnvtpack"));
        WriteGame(); // a second build: new build id, new package GUIDs
        File.WriteAllBytes(Path.Combine(Data, "level0.mnvtpack"), level);
        var current = GamePackage.Load(Data, _signer.PublicKey);
        var error = Assert.Throws<PackageException>(() => current.OpenContent(Data, Options));
        Assert.Contains("not the package this build shipped", error.Message, StringComparison.Ordinal);
        Assert.NotEqual(manifest.BuildId, current.BuildId);
    }

    [Fact]
    public void GameContent_RefusesAFileInTwoPackages()
    {
        WriteGame();
        File.Copy(Path.Combine(Data, "level0.mnvtpack"), Path.Combine(Data, "copy.mnvtpack"));
        var error = Assert.Throws<PackageException>(() => GameContent.Open(Data, ["globals.mnvtpack", "level0.mnvtpack", "copy.mnvtpack"], Options));
        Assert.Contains("is in both", error.Message, StringComparison.Ordinal);
        Assert.Throws<PackageException>(() => GameContent.Open(Data, ["../globals.mnvtpack"], Options));
    }

    [Fact]
    public void UntrustedMounts_CannotProvideGameCode()
    {
        WriteGame();
        using var content = GamePackage.Load(Data).OpenContent(Data, Options);
        var mod = new MemoryFileSystemProvider("mod");
        mod.Add("Scripts/Game.Scripts.dll", "MZ evil"u8);
        mod.Add("Assets/Web/logo.png", "modded"u8);
        content.Files.Mount(mod, new MountOptions(Priority: 100, Trust: MountTrust.Untrusted));
        Assert.Null(content.TryRead("Scripts/Game.Scripts.dll"));
        Assert.Equal("modded", Encoding.UTF8.GetString(content.Read("Assets/Web/logo.png"))); // content overrides are possible
    }

    [Fact]
    public void ExportedGames_AreNotProjects()
    {
        WriteGame();
        File.WriteAllText(Path.Combine(_root, "Game.exe"), "");
        Assert.Equal(ProjectFolderKind.ExportedGame, GameProject.Classify(_root));
        Assert.Equal(ProjectFolderKind.ExportedGame, GameProject.Classify(Path.Combine(_root, "Game.exe")));
        Assert.Equal(ProjectFolderKind.GamePackages, GameProject.Classify(Data));
        Assert.Equal(ProjectFolderKind.GamePackages, GameProject.Classify(Path.Combine(Data, "globals.mnvtpack")));
        Assert.IsType<NotAProjectException>(Assert.ThrowsAny<FileNotFoundException>(() => GameProject.Load(_root)));
        Assert.Throws<NotAProjectException>(() => GameProject.Load(Data));

        // An extracted runtime configuration, also when renamed to a project file next to an Assets folder.
        var extracted = Path.Combine(_root, "Extracted");
        Directory.CreateDirectory(Path.Combine(extracted, "Assets", "Scenes"));
        File.WriteAllText(Path.Combine(extracted, "Assets", "Scenes", "Main.mnvt"), "{}");
        File.WriteAllText(Path.Combine(extracted, "Game.mnvtproj"), Config);
        Assert.Equal(ProjectFolderKind.ExportedGame, GameProject.Classify(extracted));
        Assert.Throws<NotAProjectException>(() => GameProject.Load(extracted));
        Assert.Throws<NotAProjectException>(() => GameProject.Load(Path.Combine(extracted, "Game.mnvtproj")));
    }

    [Fact]
    public void RuntimeConfig_WithoutItsMark_IsRefused()
    {
        var memory = new MemoryFileSystemProvider();
        memory.Add(GameProject.RuntimeConfigEntry, Encoding.UTF8.GetBytes(Config.Replace("\"kind\": \"mnvt.runtimeConfig\", ", "", StringComparison.Ordinal)));
        WriteGame();
        using var content = GamePackage.Load(Data).OpenContent(Data, Options);
        content.Files.Mount(memory, new MountOptions(Priority: 1)); // shadows the real one
        Assert.Contains("not an exported game's runtime configuration", Assert.Throws<Serialization.SerializationException>(() => GameProject.LoadPackaged(content)).Message, StringComparison.Ordinal);
    }

    /// <summary>An extra content package with a few files and its own asset index (as <c>mnvt package dlc</c> writes them).</summary>
    private static void WriteExtra(string file, string indexName, PackageEncryptionKey? key, PackageSigningKey? signer, params (string Path, string Text, Guid Guid)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var writer = new PackageWriter(file, new PackageWriterOptions(Guid.NewGuid(), Guid.NewGuid(), EncryptionKey: key, SigningKey: signer));
        var assets = new Newtonsoft.Json.Linq.JArray();
        foreach (var (path, text, guid) in files)
        {
            writer.Add(path, Encoding.UTF8.GetBytes(text), guid);
            if (guid != Guid.Empty)
            {
                assets.Add(new Newtonsoft.Json.Linq.JObject { ["guid"] = guid.ToString(), ["path"] = path["Assets/".Length..], ["type"] = "Text" });
            }
        }

        writer.Add($"{AssetDatabase.ExtraIndexFolder}/{indexName}.json", Encoding.UTF8.GetBytes(new Newtonsoft.Json.Linq.JObject { ["schemaVersion"] = 1, ["assets"] = assets }.ToString()));
        writer.Complete();
    }

    [Fact]
    public void ExtraContent_DlcAndMods_MountWithTheirTrust_AndExtendTheAssetIndex()
    {
        WriteGame();
        var dlcAsset = Guid.NewGuid();
        var modAsset = Guid.NewGuid();
        WriteExtra(Path.Combine(Data, "DLC", "forest.mnvtpack"), "forest", _key, _signer, ("Assets/DLC/tree.txt", "a tree", dlcAsset));
        WriteExtra(Path.Combine(_root, "Mods", "hat.mnvtpack"), "hat", null, null, ("Assets/Mods/hat.txt", "a hat", modAsset), ("Scripts/Game.Scripts.dll", "MZ evil", Guid.Empty), ("Assets/Web/logo.png", "modded logo", Guid.Empty));
        using var stranger = PackageSigningKey.Create();
        WriteExtra(Path.Combine(Data, "DLC", "fake.mnvtpack"), "fake", _key, stranger, ("Assets/DLC/fake.txt", "not official", Guid.NewGuid()));
        var paid = PackageEncryptionKey.Create(); // the server's key for a DLC the game did not ship with
        var paidAsset = Guid.NewGuid();
        WriteExtra(Path.Combine(Data, "DLC", "paid.mnvtpack"), "paid", paid, _signer, ("Assets/DLC/gold.txt", "gold", paidAsset));

        using var content = GamePackage.Load(Data, _signer.PublicKey).OpenContent(Data, Options);
        var results = content.MountExtraContent(new ContentSettings { Dlc = true, Mods = true }, _root).ToDictionary(r => r.Name, r => r.Status);
        Assert.Equal(ExtraContentStatus.Mounted, results["DLC/forest.mnvtpack"]);
        Assert.Equal(ExtraContentStatus.Failed, results["DLC/fake.mnvtpack"]); // DLC must be signed by the game's key
        Assert.Equal(ExtraContentStatus.WaitingForKey, results["DLC/paid.mnvtpack"]);
        Assert.Equal(ExtraContentStatus.Mounted, results["Mods/hat.mnvtpack"]);

        Assert.Equal("modded logo", Encoding.UTF8.GetString(content.Read("Assets/Web/logo.png"))); // mods override content
        Assert.Null(content.TryRead("Scripts/Game.Scripts.dll")); // ...never code
        var assets = AssetDatabase.OpenPackaged(content.Files);
        Assert.Equal("a tree", Encoding.UTF8.GetString(assets.ReadAllBytes(dlcAsset)));
        Assert.Equal("a hat", Encoding.UTF8.GetString(assets.ReadAllBytes(modAsset)));
        Assert.Null(assets.Get(paidAsset));

        // The server hands the key over: mounted, and its assets join the index.
        Assert.Equal(ExtraContentStatus.Failed, content.MountDlc("paid.mnvtpack", PackageEncryptionKey.Create()).Status);
        Assert.Equal(ExtraContentStatus.Mounted, content.MountDlc("paid.mnvtpack", paid).Status);
        Assert.Equal(1, assets.MergeExtraIndexes());
        Assert.Equal("gold", Encoding.UTF8.GetString(assets.ReadAllBytes(paidAsset)));
        Assert.Equal(ExtraContentStatus.Failed, content.MountDlc("../escape.mnvtpack", paid).Status);
    }

    [Fact]
    public void Mods_FollowTheLoadOrder_AndTheirRequirements()
    {
        WriteGame();
        var mods = Path.Combine(_root, "Mods");
        WriteExtra(Path.Combine(mods, "base.mnvtpack"), "base", null, null, ("mod.json", """{ "name": "Base", "version": "1.2" }""", Guid.Empty), ("Assets/Web/logo.png", "base logo", Guid.Empty));
        WriteExtra(Path.Combine(mods, "addon.mnvtpack"), "addon", null, null, ("mod.json", """{ "name": "Addon", "requires": ["base"] }""", Guid.Empty), ("Assets/Web/logo.png", "addon logo", Guid.Empty));
        WriteExtra(Path.Combine(mods, "early.mnvtpack"), "early", null, null, ("mod.json", """{ "name": "Early", "requires": ["Addon"] }""", Guid.Empty));
        WriteExtra(Path.Combine(mods, "broken.mnvtpack"), "broken", null, null, ("mod.json", """{ "name": 5 }""", Guid.Empty));
        WriteExtra(Path.Combine(mods, "unlisted.mnvtpack"), "unlisted", null, null, ("Assets/Web/logo.png", "unlisted logo", Guid.Empty));
        File.WriteAllLines(Path.Combine(mods, GameContent.ModLoadOrderFile), ["# first loads first", "early.mnvtpack", "base.mnvtpack", "addon.mnvtpack  # overrides base", "base.mnvtpack", "ghost.mnvtpack", "../escape.mnvtpack", "broken.mnvtpack"]);

        using var content = GamePackage.Load(Data).OpenContent(Data, Options);
        var results = content.MountExtraContent(new ContentSettings { Mods = true }, _root);
        Assert.Equal(
            [("Mods/ghost.mnvtpack", ExtraContentStatus.Failed), ("Mods/../escape.mnvtpack", ExtraContentStatus.Failed), ("Mods/early.mnvtpack", ExtraContentStatus.Failed),
             ("Mods/base.mnvtpack", ExtraContentStatus.Mounted), ("Mods/addon.mnvtpack", ExtraContentStatus.Mounted), ("Mods/broken.mnvtpack", ExtraContentStatus.Failed)],
            results.Select(r => (r.Name, r.Status)).ToArray());
        Assert.Contains("'Addon'", results.Single(r => r.Name == "Mods/early.mnvtpack").Detail, StringComparison.Ordinal); // needs a mod loaded later
        Assert.Equal(("Base", "1.2"), (results.Single(r => r.Name == "Mods/base.mnvtpack").Mod!.Name, results.Single(r => r.Name == "Mods/base.mnvtpack").Mod!.Version));
        Assert.Equal("addon logo", Encoding.UTF8.GetString(content.Read("Assets/Web/logo.png"))); // the later mod wins; the unlisted one is not loaded
    }

    [Fact]
    public void ExtraContent_IsOnlyMountedWhenAllowed()
    {
        WriteGame();
        WriteExtra(Path.Combine(_root, "Mods", "hat.mnvtpack"), "hat", null, null, ("Assets/Mods/hat.txt", "a hat", Guid.NewGuid()));
        using var content = GamePackage.Load(Data).OpenContent(Data, Options);
        Assert.Empty(content.MountExtraContent(new ContentSettings(), _root)); // mods are opt-in; no DLC folder
        Assert.False(content.Contains("Assets/Mods/hat.txt"));
    }

    [Fact]
    public void EmbeddedKey_RoundTrips_AndIsNotThePlainKey()
    {
        var salt = Guid.NewGuid().ToByteArray();
        var embedded = EmbeddedPackageKey.Encode(_key, salt);
        Assert.DoesNotContain(_key.ToHex(), embedded, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_key.Id, EmbeddedPackageKey.Decode(embedded).Id);
        Assert.Throws<FormatException>(() => EmbeddedPackageKey.Decode(_key.ToHex()));
    }

    [Fact]
    public void Development_ReadsTheProjectFolderThroughTheVirtualFileSystem()
    {
        var assets = Path.Combine(_root, "Project", "Assets");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "note.txt"), "dev");
        File.WriteAllText(Path.Combine(assets, "note.txt.meta"), $$"""{ "schemaVersion": 1, "guid": "{{Guid.NewGuid()}}", "settings": {} }""");
        var database = AssetDatabase.OpenReadOnly(assets);
        Assert.False(database.IsPackaged);
        Assert.Equal("dev", Encoding.UTF8.GetString(database.ReadAllBytes("note.txt")));
        Assert.Equal("Assets folder", ((VirtualFileSystem)database.Content).SourceOf("Assets/note.txt"));
        Assert.Throws<ArgumentException>(() => database.ReadAllBytes("../outside.txt"));
    }
}
