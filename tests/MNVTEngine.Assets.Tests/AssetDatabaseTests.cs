using MNVTEngine.Graphics;
using MNVTEngine.Serialization;
using Newtonsoft.Json.Linq;
using StbImageWriteSharp;

namespace MNVTEngine.Assets.Tests;

public sealed class AssetDatabaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-assets-").FullName;

    public AssetDatabaseTests() => Directory.CreateDirectory(AssetsDir);

    private string AssetsDir => Path.Combine(_root, "Assets");

    private string LibraryDir => Path.Combine(_root, "Library");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AssetDatabase OpenEditing() => AssetDatabase.OpenForEditing(AssetsDir, LibraryDir);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(AssetsDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string WritePng(string relative, int width = 4, int height = 2)
    {
        var path = Path.Combine(AssetsDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        new ImageWriter().WritePng(new byte[width * height * 4], width, height, ColorComponents.RedGreenBlueAlpha, stream);
        return path;
    }

    private static Guid MetaGuid(string assetPath) => Guid.Parse(JObject.Parse(File.ReadAllText(assetPath + ".meta")).Value<string>("guid")!);

    [Fact]
    public void NewAssets_GetMetaWithStableGuid_AndAreImported()
    {
        var png = WritePng("Textures/hero.png", 8, 4);
        var db = OpenEditing();

        Assert.True(File.Exists(png + ".meta"));
        var guid = MetaGuid(png);
        var record = db.Get(guid)!;
        Assert.Equal("Textures/hero.png", record.Path);
        Assert.Equal(AssetTypes.Texture, record.Type);
        Assert.Equal(AssetImportStatus.Imported, record.Status);
        Assert.Equal("8", record.Properties["width"]);
        Assert.True(db.TryGetGuid("Textures\\hero.png", out var byPath));
        Assert.Equal(guid, byPath);

        // Reopening keeps the identity.
        Assert.Equal(guid, AssetDatabase.OpenForEditing(AssetsDir, LibraryDir).Get(guid)!.Guid);
    }

    [Fact]
    public void TextureMeta_ContainsCompleteDefaultSettings()
    {
        var png = WritePng("a.png");
        var db = OpenEditing();
        var settings = JObject.Parse(File.ReadAllText(png + ".meta"))["settings"]!;
        Assert.Equal("linear", settings.Value<string>("filter"));
        Assert.Equal(100f, settings.Value<float>("pixelsPerUnit"));
        Assert.Equal(TextureFilter.Linear, db.GetTextureSettings(MetaGuid(png)).Filter);
    }

    [Fact]
    public void Move_KeepsGuidAndMovesMeta()
    {
        var png = WritePng("Textures/hero.png");
        var db = OpenEditing();
        var guid = MetaGuid(png);

        db.Move(guid, "Art/Characters/hero2.png");
        Assert.False(File.Exists(png));
        Assert.False(File.Exists(png + ".meta"));
        Assert.Equal("Art/Characters/hero2.png", db.Get(guid)!.Path);
        Assert.True(db.TryGetGuid("Art/Characters/hero2.png", out var moved));
        Assert.Equal(guid, moved);
        Assert.Throws<ArgumentException>(() => db.Move(guid, "../outside.png"));
    }

    [Fact]
    public void ExternalRenameWithMeta_IsDetectedAsMove()
    {
        var png = WritePng("old.png");
        var db = OpenEditing();
        var guid = MetaGuid(png);
        File.Move(png, Path.Combine(AssetsDir, "new.png"));
        File.Move(png + ".meta", Path.Combine(AssetsDir, "new.png.meta"));

        var result = db.Refresh();
        Assert.Equal([guid], result.Moved);
        Assert.Equal("new.png", db.Get(guid)!.Path);
    }

    [Fact]
    public void CopiedAssetWithMeta_GetsNewGuid()
    {
        var png = WritePng("a.png");
        OpenEditing();
        File.Copy(png, Path.Combine(AssetsDir, "b.png"));
        File.Copy(png + ".meta", Path.Combine(AssetsDir, "b.png.meta"));

        var db = OpenEditing();
        Assert.NotEqual(MetaGuid(png), MetaGuid(Path.Combine(AssetsDir, "b.png")));
        Assert.Equal(2, db.Assets.Count);
        Assert.Contains(db.Diagnostics, d => d.Message.Contains("Duplicate GUID", StringComparison.Ordinal));
    }

    [Fact]
    public void Changes_AreReimportedOnlyWhenNeeded()
    {
        var path = Write("data.json", """{ "a": 1 }""");
        var db = OpenEditing();
        var guid = MetaGuid(path);

        Assert.Empty(db.Refresh().Reimported);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1)); // touched, same content
        Assert.Empty(db.Refresh().Reimported);

        File.WriteAllText(path, """{ "a": 2 }""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(2));
        Assert.Equal([guid], db.Refresh().Reimported);
    }

    [Fact]
    public void SettingsChange_RewritesMetaAndReimports()
    {
        var png = WritePng("a.png");
        var db = OpenEditing();
        var guid = MetaGuid(png);
        var changed = 0;
        db.AssetsChanged += ids => changed += ids.Count(i => i == guid);

        var problems = db.UpdateSettings(guid, JObject.Parse("""{ "filter": "point", "wrap": "repeat", "pixelsPerUnit": -5, "bogus": 1 }"""));
        Assert.Contains(problems, p => p.Contains("pixelsPerUnit", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("bogus", StringComparison.Ordinal));
        var settings = db.GetTextureSettings(guid);
        Assert.Equal(TextureFilter.Point, settings.Filter);
        Assert.Equal(100f, settings.PixelsPerUnit);
        Assert.Equal(1, changed);
        Assert.Equal("point", JObject.Parse(File.ReadAllText(png + ".meta"))["settings"]!.Value<string>("filter"));
    }

    [Fact]
    public void FailedImports_AreReported()
    {
        Write("broken.png", "not a png");
        Write("broken.json", "{ nope");
        Write("broken.wav", "XXXXXXXXXXXX");
        Write("prefab.mnvtprefab", """{ "schemaVersion": 2, "gameObjects": [ { "id": "1", "parent": null }, { "id": "2" } ] }""");
        var db = OpenEditing();
        Assert.All(db.Assets, a => Assert.Equal(AssetImportStatus.Failed, a.Status));
        Assert.Equal(4, db.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void UnknownFileTypes_StillGetGuids()
    {
        var path = Write("notes.xyz", "?");
        var db = OpenEditing();
        var record = db.Get(MetaGuid(path))!;
        Assert.Equal(AssetTypes.Unknown, record.Type);
        Assert.Equal(AssetImportStatus.NotImported, record.Status);
    }

    [Fact]
    public void Dependencies_AndDependentsAndMissingReferences()
    {
        var png = WritePng("a.png");
        var db = OpenEditing();
        var texture = MetaGuid(png);
        var missing = Guid.NewGuid();
        var scene = Write("Scenes/Main.mnvt", $$"""
            { "schemaVersion": 2, "name": "Main", "gameObjects": [
              { "id": "{{Guid.NewGuid()}}", "components": [ { "type": "MNVTEngine.SpriteRenderer", "data": { "texture": "asset:{{texture}}" } },
                                                         { "type": "X", "data": { "other": "asset:{{missing}}" } } ] } ] }
            """);
        db.Refresh();
        var sceneGuid = MetaGuid(scene);

        Assert.Contains(texture, db.Get(sceneGuid)!.Dependencies);
        Assert.Equal([sceneGuid], db.GetDependents(texture).Select(r => r.Guid));
        Assert.Contains(db.Diagnostics, d => d.Message.Contains(missing.ToString(), StringComparison.Ordinal));
        Assert.Equal(new HashSet<Guid> { sceneGuid, texture, missing }, db.CollectDependencies([sceneGuid]));
    }

    [Fact]
    public void OrphanAndCorruptMetas_AreReportedNotOverwritten()
    {
        Write("gone.png.meta", """{ "schemaVersion": 1, "guid": "11111111-1111-1111-1111-111111111111", "settings": {} }""");
        var json = Write("x.json", "{}");
        File.WriteAllText(json + ".meta", "{ corrupt");
        var db = OpenEditing();

        Assert.Contains(db.Diagnostics, d => d.Message.Contains("Orphan", StringComparison.Ordinal));
        Assert.Contains(db.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Path == "x.json.meta");
        Assert.Equal("{ corrupt", File.ReadAllText(json + ".meta"));
        Assert.Empty(db.Assets);
    }

    [Fact]
    public void ReadOnly_NeverWrites_AndSkipsAssetsWithoutMeta()
    {
        var png = WritePng("a.png");
        OpenEditing();
        WritePng("new.png");
        File.Delete(Path.Combine(LibraryDir, "AssetDatabase.json"));

        var db = AssetDatabase.OpenReadOnly(AssetsDir);
        Assert.True(db.IsReadOnly);
        Assert.True(db.TryGetPath(MetaGuid(png), out _));
        Assert.False(db.TryGetGuid("new.png", out _));
        Assert.False(File.Exists(Path.Combine(AssetsDir, "new.png.meta")));
        Assert.False(File.Exists(Path.Combine(LibraryDir, "AssetDatabase.json")));
        Assert.Throws<InvalidOperationException>(() => db.Delete(MetaGuid(png)));
    }

    [Fact]
    public void CorruptCache_IsRebuilt()
    {
        var png = WritePng("a.png");
        OpenEditing();
        File.WriteAllText(Path.Combine(LibraryDir, "AssetDatabase.json"), "garbage");
        var db = OpenEditing();
        Assert.Equal(AssetImportStatus.Imported, db.Get(MetaGuid(png))!.Status);
    }

    [Fact]
    public void WriteText_AndDelete()
    {
        var db = OpenEditing();
        var guid = db.WriteText("Prefabs/thing.mnvtprefab", """{ "schemaVersion": 2, "name": "T", "gameObjects": [ { "id": "1", "parent": null } ] }""");
        Assert.Equal(AssetTypes.Prefab, db.GetAssetType(guid));
        Assert.False(File.Exists(Path.Combine(AssetsDir, "Prefabs", "thing.mnvtprefab.tmp")));

        db.Delete(guid);
        Assert.Null(db.Get(guid));
        Assert.False(File.Exists(Path.Combine(AssetsDir, "Prefabs", "thing.mnvtprefab.meta")));
    }

    [Fact]
    public void Importers_ValidateSignatures()
    {
        File.WriteAllBytes(Path.Combine(AssetsDir, "s.wav"), [.. "RIFF\0\0\0\0WAVE"u8]);
        File.WriteAllBytes(Path.Combine(AssetsDir, "f.ttf"), [0, 1, 0, 0, 0]);
        Write("shader.glsl", "#version 450 core\nvoid main() {}\n");
        var db = OpenEditing();
        Assert.All(db.Assets, a => Assert.Equal(AssetImportStatus.Imported, a.Status));
        Assert.Equal(new[] { AssetTypes.Font, AssetTypes.Audio, AssetTypes.Shader }, db.Assets.OrderBy(a => a.Path, StringComparer.Ordinal).Select(a => a.Type));
    }

    [Fact]
    public void AnimationClips_AreValidated_AndDependOnTheirSheet()
    {
        var db = OpenEditing();
        var png = WritePng("Sheets/hero.png", 64, 16);
        db.Refresh(); // creates the sheet's meta
        var sheet = MetaGuid(png);
        Write("Anim/walk.mnvtanim", $$"""{ "schemaVersion": 1, "texture": "asset:{{sheet}}", "frameWidth": 16, "frameHeight": 16, "columns": 4, "frames": [0, 1, 2, 3], "frameRate": 8 }""");
        Write("Anim/broken.mnvtanim", """{ "schemaVersion": 1, "texture": "asset:nope", "frameWidth": 16, "frameHeight": 16, "frames": [] }""");
        db.Refresh();

        var walk = db.Get(db.Assets.Single(a => a.Path == "Anim/walk.mnvtanim").Guid)!;
        Assert.Equal((AssetTypes.Animation, AssetImportStatus.Imported, "0.5 s"), (walk.Type, walk.Status, walk.Properties["duration"]));
        Assert.Equal([sheet], walk.Dependencies);
        Assert.Contains(walk, db.GetDependents(sheet));

        var broken = db.Assets.Single(a => a.Path == "Anim/broken.mnvtanim");
        Assert.Equal(AssetImportStatus.Failed, broken.Status);
        Assert.Contains(broken.Messages, m => m.Contains("texture", StringComparison.Ordinal));
        Assert.Contains(broken.Messages, m => m.Contains("frames", StringComparison.Ordinal));
    }
}
