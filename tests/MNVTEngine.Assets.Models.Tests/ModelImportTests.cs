using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Assets.Models.Tests;

/// <summary>Imports the fixtures in tests/TestAssets/Models through a real asset database.</summary>
public sealed class ModelImportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-models-").FullName;

    public ModelImportTests() => Directory.CreateDirectory(AssetsDir);

    private string AssetsDir => Path.Combine(_root, "Assets");

    private string LibraryDir => Path.Combine(_root, "Library");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Fixture(string relative) => Path.Combine(AppContext.BaseDirectory, "Models", relative);

    private void CopyFixtureFolder(string fixtureFolder, string target)
    {
        var source = Fixture(fixtureFolder);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(AssetsDir, target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private void CopyFixture(string fixture, string target)
    {
        var destination = Path.Combine(AssetsDir, target);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(Fixture(fixture), destination);
    }

    private AssetDatabase Open() => AssetDatabase.OpenForEditing(AssetsDir, LibraryDir, ModelImporters.WithDefaults());

    private static (AssetRecord Record, ModelData Model) Imported(AssetDatabase db, string path)
    {
        Assert.True(db.TryGetGuid(path, out var guid), path);
        var record = db.Get(guid)!;
        Assert.True(record.Status == AssetImportStatus.Imported, string.Join("\n", record.Messages));
        Assert.Equal(AssetTypes.Model, record.Type);
        var artifact = db.ReadArtifact(guid, ModelArtifact.Name);
        Assert.NotNull(artifact);
        return (record, ModelArtifact.Read(artifact));
    }

    [Fact]
    public void Fbx_Imports_WithMeters_ResolvedTexture_AndDependency()
    {
        CopyFixtureFolder("WoodenCrate/fbx", "Crate");
        var db = Open();
        var (record, model) = Imported(db, "Crate/wooden_crate_01_1k.fbx");

        Assert.NotEmpty(model.Meshes);
        Assert.True(model.TriangleCount > 0);
        var mesh = model.Meshes[0].Mesh;
        Assert.NotNull(mesh.Normals);
        Assert.NotNull(mesh.UV0);
        Assert.NotNull(mesh.Tangents);

        // The crate is about half a meter wide: unit conversion worked (centimeters would give ~50).
        Assert.InRange(WorldSize(model), 0.2f, 3f);

        Assert.True(db.TryGetGuid("Crate/textures/wooden_crate_01_diff_1k.jpg", out var texture));
        Assert.Contains(texture, record.Dependencies);
        var material = Assert.Single(model.Materials);
        Assert.Equal(texture, model.Textures[material.Textures[TextureRole.BaseColor]].ProjectAsset);
    }

    [Fact]
    public unsafe void AsciiFbx_ImportsLikeBinary()
    {
        // The ASCII file is written by Assimp's own FBX ASCII exporter from the binary fixture.
        CopyFixtureFolder("WoodenCrate/fbx", "Crate");
        var ascii = Path.Combine(AssetsDir, "Crate", "crate_ascii.fbx");
        var api = Silk.NET.Assimp.Assimp.GetApi();
        var scene = api.ImportFile(Path.Combine(AssetsDir, "Crate", "wooden_crate_01_1k.fbx"), 0);
        Assert.True(scene is not null, api.GetErrorStringS());
        try
        {
            Assert.Equal(Silk.NET.Assimp.Return.Success, api.ExportScene(scene, "fbxa", ascii, 0));
        }
        finally
        {
            api.ReleaseImport(scene);
        }

        Assert.StartsWith("; FBX", File.ReadAllText(ascii), StringComparison.Ordinal);
        var db = Open();
        var binary = Imported(db, "Crate/wooden_crate_01_1k.fbx").Model;
        var (_, model) = Imported(db, "Crate/crate_ascii.fbx");
        Assert.Equal(binary.TriangleCount, model.TriangleCount);
        Assert.Equal(binary.Materials.Count, model.Materials.Count);
        Assert.InRange(WorldSize(model), WorldSize(binary) * 0.99f, WorldSize(binary) * 1.01f);
    }

    [Fact]
    public void Gltf_Imports_PbrMaterial_WithBufferAndTextureDependencies()
    {
        CopyFixtureFolder("WoodenCrate/gltf", "Crate");
        var db = Open();
        var (record, model) = Imported(db, "Crate/wooden_crate_01_1k.gltf");

        var material = Assert.Single(model.Materials);
        Assert.Equal("glTF PBR", material.SourceModel);
        Assert.True(material.Textures.ContainsKey(TextureRole.BaseColor));
        Assert.True(material.Textures.ContainsKey(TextureRole.Normal));
        Assert.True(material.Textures.ContainsKey(TextureRole.MetallicRoughness));
        Assert.All(material.Textures.Values, i => Assert.NotNull(model.Textures[i].ProjectAsset));
        Assert.True(db.TryGetGuid("Crate/wooden_crate_01.bin", out var buffer));
        Assert.Contains(buffer, record.Dependencies);
        Assert.InRange(WorldSize(model), 0.2f, 3f);
    }

    [Fact]
    public void Glb_Skinned_ImportsSkinAndAnimation_WithEmbeddedTexture()
    {
        CopyFixture("Khronos/CesiumMan.glb", "Characters/CesiumMan.glb");
        var (_, model) = Imported(Open(), "Characters/CesiumMan.glb");

        var skin = Assert.Single(model.Skins);
        Assert.Equal(skin.Joints.Count, skin.InverseBindMatrices.Count);
        Assert.Contains(model.Nodes, n => n.Skin == 0 && n.Mesh >= 0);
        var mesh = model.Meshes[0];
        Assert.NotNull(mesh.Joints);
        Assert.NotNull(mesh.Weights);
        Assert.Equal(mesh.Mesh.VertexCount * 4, mesh.Weights.Length);
        for (var v = 0; v < mesh.Mesh.VertexCount; v++)
        {
            Assert.InRange(mesh.Weights[v * 4] + mesh.Weights[(v * 4) + 1] + mesh.Weights[(v * 4) + 2] + mesh.Weights[(v * 4) + 3], 0.999f, 1.001f);
            for (var i = 0; i < 4; i++)
            {
                Assert.True(mesh.Joints[(v * 4) + i] < skin.Joints.Count);
            }
        }

        var animation = Assert.Single(model.Animations);
        Assert.True(animation.Duration > 0f);
        Assert.Contains(model.Textures, t => t.Embedded is { Length: > 0 });
    }

    [Fact]
    public void Glb_NodeAnimation_TargetsHierarchy()
    {
        CopyFixture("Khronos/BoxAnimated.glb", "BoxAnimated.glb");
        var (_, model) = Imported(Open(), "BoxAnimated.glb");

        Assert.True(model.Nodes.Count > 2);
        Assert.Equal(-1, model.Nodes[0].Parent);
        Assert.All(model.Nodes.Skip(1), n => Assert.InRange(n.Parent, 0, model.Nodes.Count - 1));
        var animation = Assert.Single(model.Animations);
        Assert.Contains(animation.Channels, c => c.Path == AnimationPath.Rotation);
        Assert.Contains(animation.Channels, c => c.Path == AnimationPath.Translation);
        Assert.Equal(model.Nodes.Count, model.Nodes.Select(n => n.Name).Distinct().Count());
    }

    [Fact]
    public void CorruptModel_Fails_AndKeepsLastGoodArtifact()
    {
        CopyFixture("Khronos/BoxAnimated.glb", "Box.glb");
        var db = Open();
        var (_, good) = Imported(db, "Box.glb");
        Assert.True(db.TryGetGuid("Box.glb", out var guid));

        File.WriteAllBytes(Path.Combine(AssetsDir, "Box.glb"), [1, 2, 3, 4, 5]);
        db.Refresh();

        var record = db.Get(guid)!;
        Assert.Equal(AssetImportStatus.Failed, record.Status);
        Assert.NotEmpty(record.Messages);
        var kept = ModelArtifact.Read(db.ReadArtifact(guid, ModelArtifact.Name)!);
        Assert.Equal(good.Nodes.Count, kept.Nodes.Count);
    }

    [Fact]
    public void Fbx_MissingTexture_IsReportedNotGuessed()
    {
        CopyFixture("WoodenCrate/fbx/wooden_crate_01_1k.fbx", "Crate/crate.fbx");
        var (record, model) = Imported(Open(), "Crate/crate.fbx");

        Assert.Contains(record.Messages, m => m.Contains("not found", StringComparison.Ordinal));
        Assert.Contains(model.Textures, t => !t.IsResolved && t.SourcePath is not null);
    }

    [Fact]
    public void AddingMissingTexture_ReimportsModel()
    {
        CopyFixture("WoodenCrate/fbx/wooden_crate_01_1k.fbx", "Crate/crate.fbx");
        var db = Open();
        Assert.True(db.TryGetGuid("Crate/crate.fbx", out var guid));
        Assert.DoesNotContain(ModelArtifact.Read(db.ReadArtifact(guid, ModelArtifact.Name)!).Textures, t => t.IsResolved);

        CopyFixture("WoodenCrate/fbx/textures/wooden_crate_01_diff_1k.jpg", "Crate/textures/wooden_crate_01_diff_1k.jpg");
        db.Refresh();

        Assert.Contains(ModelArtifact.Read(db.ReadArtifact(guid, ModelArtifact.Name)!).Textures, t => t.ProjectAsset is not null);
    }

    [Fact]
    public void RemovedModel_DeletesArtifacts()
    {
        CopyFixture("Khronos/BoxAnimated.glb", "Box.glb");
        var db = Open();
        Assert.True(db.TryGetGuid("Box.glb", out var guid));
        var folder = Path.Combine(LibraryDir, "Artifacts", guid.ToString("D"));
        Assert.True(Directory.Exists(folder));

        File.Delete(Path.Combine(AssetsDir, "Box.glb"));
        File.Delete(Path.Combine(AssetsDir, "Box.glb.meta"));
        db.Refresh();
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void Settings_AreNormalized_AndScaleApplies()
    {
        var problems = new List<string>();
        var settings = ModelImportSettings.Read(new JObject { ["scale"] = -2, ["importSkins"] = "yes" }, problems);
        Assert.Equal(1f, settings.Scale);
        Assert.True(settings.ImportSkins);
        Assert.Equal(2, problems.Count);

        CopyFixture("Khronos/BoxAnimated.glb", "Box.glb");
        var meta = Path.Combine(AssetsDir, "Box.glb.meta");
        var db = Open();
        var baseline = WorldSize(Imported(db, "Box.glb").Model);
        var json = JObject.Parse(File.ReadAllText(meta));
        ((JObject)json["settings"]!)["scale"] = 10;
        File.WriteAllText(meta, json.ToString());
        db.Refresh();
        Assert.InRange(WorldSize(Imported(db, "Box.glb").Model), baseline * 9.9f, baseline * 10.1f);
    }

    [Fact]
    public void ReadOnlyDatabase_WithoutModelImporter_ReadsArtifactsFromLibrary()
    {
        CopyFixture("Khronos/BoxAnimated.glb", "Box.glb");
        Assert.True(Open().TryGetGuid("Box.glb", out var guid));

        var readOnly = AssetDatabase.OpenReadOnly(AssetsDir, artifactsLibrary: LibraryDir);
        Assert.Equal(AssetTypes.Model, readOnly.Get(guid)!.Type);
        Assert.NotNull(readOnly.ReadArtifact(guid, ModelArtifact.Name));
    }

    /// <summary>Largest extent of all meshes in world space (bind pose).</summary>
    private static float WorldSize(ModelData model)
    {
        var world = new Matrix4x4[model.Nodes.Count];
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (var i = 0; i < model.Nodes.Count; i++)
        {
            var n = model.Nodes[i];
            var local = Matrix4x4.TRS(n.Position, n.Rotation, n.Scale);
            world[i] = n.Parent < 0 ? local : world[n.Parent] * local;
            if (n.Mesh < 0)
            {
                continue;
            }

            foreach (var p in model.Meshes[n.Mesh].Mesh.Positions)
            {
                var w = world[i].MultiplyPoint(p);
                min = Vector3.Min(min, w);
                max = Vector3.Max(max, w);
            }
        }

        var extent = max - min;
        return MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z));
    }
}
