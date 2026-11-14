using MNVTEngine.Graphics.Meshes;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Assets.Models.Tests;

/// <summary>Levels of detail: generated at import, written with streamed full-detail geometry, read back lazily.</summary>
public sealed class ModelLodImportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-lods-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Fixture(string relative) => Path.Combine(AppContext.BaseDirectory, "Models", relative);

    [Fact]
    public void Import_GeneratesLods_AndStreamsTheFullDetail()
    {
        var assets = Path.Combine(_root, "Assets");
        foreach (var file in Directory.EnumerateFiles(Fixture("WoodenCrate/gltf"), "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(assets, "Crate", Path.GetRelativePath(Fixture("WoodenCrate/gltf"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        var library = Path.Combine(_root, "Library");
        _ = AssetDatabase.OpenForEditing(assets, library, ModelImporters.WithDefaults());
        var metaPath = Path.Combine(assets, "Crate", "wooden_crate_01_1k.gltf.meta");
        var meta = JObject.Parse(File.ReadAllText(metaPath));
        meta["settings"]!["lods"] = new JArray(new JObject { ["ratio"] = 0.5, ["screenSize"] = 0.4 }, new JObject { ["ratio"] = 0.2, ["screenSize"] = 0.1 });
        File.WriteAllText(metaPath, meta.ToString());

        var db = AssetDatabase.OpenForEditing(assets, library, ModelImporters.WithDefaults());
        Assert.True(db.TryGetGuid("Crate/wooden_crate_01_1k.gltf", out var guid));
        var artifact = db.ReadArtifact(guid, ModelArtifact.Name)!;
        Assert.Throws<InvalidDataException>(() => ModelArtifact.Read(artifact)); // streamed geometry needs the other artifacts

        var reads = 0;
        var model = ModelArtifact.Read(artifact, name =>
        {
            reads++;
            return db.ReadArtifact(guid, name);
        });
        Assert.Equal(model.Meshes.Count(m => m.Lods.Count > 0).ToString(System.Globalization.CultureInfo.InvariantCulture), db.Get(guid)!.Properties["lodMeshes"]);
        var mesh = model.Meshes.MaxBy(m => m.TriangleCount)!;
        Assert.True(mesh.IsDetailStreamed);
        Assert.False(mesh.IsDetailLoaded);
        Assert.Equal(2, mesh.Lods.Count);
        Assert.True(mesh.Lods[0].Mesh.TriangleCount < mesh.TriangleCount && mesh.Lods[1].Mesh.TriangleCount < mesh.Lods[0].Mesh.TriangleCount);
        Assert.Equal((0.4f, 0.1f), (mesh.Lods[0].ScreenSize, mesh.Lods[1].ScreenSize));
        var counts = (mesh.VertexCount, mesh.TriangleCount, mesh.Bounds.Size);
        Assert.Equal(0, reads); // counts and bounds come without the geometry

        Assert.Equal(counts, (mesh.Mesh.VertexCount, mesh.Mesh.TriangleCount, mesh.Mesh.Bounds.Size));
        Assert.Equal((1, true), (reads, mesh.IsDetailLoaded));
        Assert.Equal(3, mesh.LoadedMeshes.Count());
        mesh.ReleaseDetail();
        Assert.False(mesh.IsDetailLoaded);
        Assert.Equal(2, mesh.LoadedMeshes.Count());
        Assert.Equal(counts.VertexCount, mesh.Mesh.VertexCount); // loads again
        Assert.Equal(2, reads);

        // A damaged or foreign detail artifact is refused when it is needed, not drawn.
        var broken = ModelArtifact.Read(artifact, _ => [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => broken.Meshes[model.Meshes.IndexOf(mesh)].Mesh);
    }

    [Fact]
    public void Settings_RejectLevelsThatDoNotDecrease()
    {
        var problems = new List<string>();
        var settings = ModelImportSettings.Read(JObject.Parse("""{ "lods": [ { "ratio": 0.5, "screenSize": 0.3 }, { "ratio": 0.6, "screenSize": 0.1 } ] }"""), problems);
        Assert.Equal([new ModelLodSetting(0.5f, 0.3f)], settings.Lods);
        Assert.Single(problems);
        Assert.Equal(settings.Lods, ModelImportSettings.Read(settings.ToJson()).Lods);
    }

    [Fact]
    public void SkinnedAndSmallMeshes_GetNoLods()
    {
        var model = new ModelData();
        model.Meshes.Add(new ModelMesh("small", MeshPrimitives.Cube()));
        var skinned = new ModelMesh("skinned", MeshPrimitives.Sphere(1f, 32, 16));
        skinned.Joints = new ushort[skinned.Mesh.VertexCount * 4];
        skinned.Weights = new float[skinned.Mesh.VertexCount * 4];
        model.Meshes.Add(skinned);
        model.Meshes.Add(new ModelMesh("sphere", MeshPrimitives.Sphere(1f, 32, 16)));
        Assert.Equal(1, ModelImporter.GenerateLods(model, ModelImportSettings.DefaultLods));
        Assert.Equal([0, 0, 2], model.Meshes.Select(m => m.Lods.Count));

        // Inline (no detail artifacts): the levels round-trip in the model artifact itself.
        var copy = ModelArtifact.Read(ModelArtifact.Write(model));
        Assert.False(copy.Meshes[2].IsDetailStreamed);
        Assert.Equal(model.Meshes[2].Lods.Select(l => l.Mesh.TriangleCount), copy.Meshes[2].Lods.Select(l => l.Mesh.TriangleCount));
    }
}
