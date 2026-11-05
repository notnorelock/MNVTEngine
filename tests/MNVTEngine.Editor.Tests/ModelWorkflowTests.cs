using MNVTEngine.Assets;
using MNVTEngine.Assets.Models;
using MNVTEngine.Editor.Services;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Editor.Tests;

/// <summary>Extract, remap and reset on real model fixtures, through a real asset database.</summary>
public sealed class ModelWorkflowTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-model-workflow-").FullName;

    public ModelWorkflowTests() => Directory.CreateDirectory(AssetsDir);

    private string AssetsDir => Path.Combine(_root, "Assets");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Fixture(string relative) => Path.Combine(AppContext.BaseDirectory, "Models", relative);

    private AssetDatabase Open() => AssetDatabase.OpenForEditing(AssetsDir, Path.Combine(_root, "Library"), ModelImporters.WithDefaults());

    private (AssetDatabase Db, ModelWorkflow Workflow) Import(string fixture, string target)
    {
        var source = Fixture(fixture);
        if (Directory.Exists(source))
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(AssetsDir, Path.GetDirectoryName(target)!, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(AssetsDir, target))!);
            File.Copy(source, Path.Combine(AssetsDir, target));
        }

        var db = Open();
        Assert.True(db.TryGetGuid(target, out var guid), target);
        return (db, new ModelWorkflow(db, guid));
    }

    private int AssetFileCount() => Directory.EnumerateFiles(AssetsDir, "*", SearchOption.AllDirectories).Count();

    [Fact]
    public void ExtractTextures_WritesEmbeddedImages_RemapsTheModel_AndIsIdempotent()
    {
        var (db, workflow) = Import("Khronos/CesiumMan.glb", "Characters/CesiumMan.glb");
        Assert.Contains(workflow.Load()!.Textures, t => t.Embedded is not null);

        var report = workflow.ExtractTextures(workflow.DefaultFolder("Textures"));
        Assert.Contains(report, line => line.Contains("extracted to Characters/Textures/CesiumMan_", StringComparison.Ordinal));
        var model = workflow.Load()!;
        Assert.All(model.Textures, t => Assert.Null(t.Embedded));
        Assert.All(model.Textures, t => Assert.Equal(AssetTypes.Texture, db.GetAssetType(t.ProjectAsset!.Value)));
        Assert.Equal(AssetImportStatus.Imported, db.Get(workflow.Guid)!.Status);

        var files = AssetFileCount();
        workflow.ExtractTextures(workflow.DefaultFolder("Textures"));
        Assert.Equal(files, AssetFileCount()); // nothing left to extract, nothing duplicated
    }

    [Fact]
    public void Relink_FindsReferencesToRenamedMeshes_AndPointsThemAtACurrentMesh()
    {
        var (db, workflow) = Import("Lights/PunctualLights.gltf", "Models/Lamps.gltf");
        var scene = new Scene();
        scene.CreateGameObject("Old").AddComponent<MeshRenderer>().Mesh = new AssetRef(workflow.Guid, "mesh/Ground"); // renamed to "Floor" since
        scene.CreateGameObject("Current").AddComponent<MeshRenderer>().Mesh = new AssetRef(workflow.Guid, "mesh/Floor");
        db.WriteText("Scenes/Level.mnvt", new SceneSerializer(Runtime.EngineComponents.CreateRegistry()).Serialize(scene));

        var missing = Assert.Single(workflow.FindMissingMeshReferences());
        Assert.Equal(("Ground", "Scenes/Level.mnvt"), (missing.Mesh, Assert.Single(missing.UsedBy)));

        Assert.Empty(workflow.Relink("Ground", "Floor"));
        Assert.Empty(workflow.FindMissingMeshReferences());
        Assert.Equal("Floor", workflow.Load()!.FindMesh("Ground")!.Name);

        workflow.Relink("Ground", null);
        Assert.Equal("Ground", Assert.Single(workflow.FindMissingMeshReferences()).Mesh);
    }

    [Fact]
    public void ExtractMaterials_KeepsTheUVSetOfEachMap()
    {
        var (db, workflow) = Import("Khronos/MultiUVTest.glb", "Models/MultiUV.glb");
        workflow.ExtractMaterials(workflow.DefaultFolder("Materials"), workflow.DefaultFolder("Textures"));

        var path = Assert.Single(Directory.EnumerateFiles(Path.Combine(AssetsDir, "Models", "Materials"), "*.mnvtmat"));
        var definition = MaterialDefinition.Parse(File.ReadAllText(path), path);
        Assert.Equal(1f, definition.Properties["_EmissiveUVSet"].Numbers![0]);
        Assert.False(definition.Properties.ContainsKey("_BaseColorUVSet")); // UV set 0 is the default
        Assert.Equal(AssetImportStatus.Imported, db.Get(workflow.Guid)!.Status);
    }

    [Fact]
    public void ExtractMaterials_WritesStandardMaterials_ReusesExistingFiles_AndKeepsEdits()
    {
        var (db, workflow) = Import("WoodenCrate/gltf", "Crate/wooden_crate_01_1k.gltf");
        var source = workflow.Load()!.Materials.Single();

        workflow.ExtractMaterials(workflow.DefaultFolder("Materials"), workflow.DefaultFolder("Textures"));
        var path = $"Crate/Materials/wooden_crate_01_1k_{source.Name}.mnvtmat";
        Assert.True(db.TryGetGuid(path, out var material), path);
        var model = workflow.Load()!;
        Assert.Equal(material, model.Materials.Single().Override);
        Assert.Contains(material, db.Get(workflow.Guid)!.Dependencies);
        var definition = MaterialDefinition.Parse(File.ReadAllText(Path.Combine(AssetsDir, path)), path);
        Assert.Equal(MaterialDefinition.BuiltinStandard, definition.Builtin);
        Assert.True(definition.Properties["_BaseColorMap"].IsTexture);
        Assert.True(definition.Properties["_NormalMap"].IsTexture);

        // An edited material survives extracting again: the existing file is reused, not rewritten.
        File.WriteAllText(Path.Combine(AssetsDir, path), File.ReadAllText(Path.Combine(AssetsDir, path)).Replace("\"Opaque\"", "\"Alpha\"", StringComparison.Ordinal));
        var edited = File.ReadAllText(Path.Combine(AssetsDir, path));
        workflow.ResetMaterialRemaps();
        Assert.Null(workflow.Load()!.Materials.Single().Override);
        var files = AssetFileCount();
        var report = workflow.ExtractMaterials(workflow.DefaultFolder("Materials"), workflow.DefaultFolder("Textures"));
        Assert.Contains(report, line => line.Contains("reused", StringComparison.Ordinal));
        Assert.Equal(files, AssetFileCount());
        Assert.Equal(edited, File.ReadAllText(Path.Combine(AssetsDir, path)));
        Assert.Equal(material, workflow.Load()!.Materials.Single().Override);
    }

    [Fact]
    public void Remap_PointsAMaterialAtAnyProjectMaterial_AndStaleRemapsAreReported()
    {
        var (db, workflow) = Import("Khronos/BoxAnimated.glb", "Box.glb");
        var name = workflow.Load()!.Materials[0].Name;
        var other = db.WriteText("Materials/Red.mnvtmat", """{ "schemaVersion": 2, "shader": "builtin:standard", "blend": "Opaque", "properties": { "_BaseColor": [1, 0, 0, 1] } }""");

        workflow.RemapMaterial(name, other);
        Assert.Equal(other, workflow.Load()!.FindMaterial(name)!.Override);

        workflow.RemapMaterial("Renamed In Source", other);
        Assert.Contains(db.Get(workflow.Guid)!.Messages, m => m.Contains("'Renamed In Source' matches no material", StringComparison.Ordinal));

        workflow.RemapMaterial(name, null);
        Assert.Null(workflow.Load()!.FindMaterial(name)!.Override);
        workflow.ResetMaterialRemaps();
        Assert.Empty(workflow.Settings.Materials);
    }

    [Fact]
    public void ImportSettings_KeepRemapsThroughNormalization_AndDropInvalidOnes()
    {
        var problems = new List<string>();
        var guid = Guid.NewGuid();
        var settings = ModelImportSettings.Read(new Newtonsoft.Json.Linq.JObject
        {
            ["materials"] = new Newtonsoft.Json.Linq.JObject { ["Body"] = $"asset:{guid}", ["Bad"] = "not a reference" },
            ["textures"] = "oops",
        }, problems);

        Assert.Equal(guid, settings.Materials["Body"]);
        Assert.False(settings.Materials.ContainsKey("Bad"));
        Assert.Empty(settings.Textures);
        Assert.Equal(2, problems.Count);
        Assert.Equal(settings.Materials, ModelImportSettings.Read(settings.ToJson()).Materials);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0 }, ".png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ".jpg")]
    [InlineData(new byte[] { 0x42, 0x4D, 0, 0 }, ".bmp")]
    [InlineData(new byte[] { 0x76, 0x2F, 0x31, 0x01 }, null)] // OpenEXR
    public void ImageExtension_FollowsTheSignature(byte[] bytes, string? expected) => Assert.Equal(expected, ModelWorkflow.ImageExtension(bytes));
}
