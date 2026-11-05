using MNVTEngine.Mathematics;

namespace MNVTEngine.Assets.Models.Tests;

/// <summary>Blend shapes (morph targets) from glTF and FBX, and their weight animations.</summary>
public sealed class BlendShapeImportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-blendshapes-").FullName;

    public BlendShapeImportTests() => Directory.CreateDirectory(AssetsDir);

    private string AssetsDir => Path.Combine(_root, "Assets");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Fixture(string relative) => Path.Combine(AppContext.BaseDirectory, "Models", relative);

    private ModelData Import(string file)
    {
        var db = AssetDatabase.OpenForEditing(AssetsDir, Path.Combine(_root, "Library"), ModelImporters.WithDefaults());
        Assert.True(db.TryGetGuid(file, out var guid), file);
        var record = db.Get(guid)!;
        Assert.True(record.Status == AssetImportStatus.Imported, string.Join("\n", record.Messages));
        return ModelArtifact.Read(db.ReadArtifact(guid, ModelArtifact.Name)!);
    }

    [Fact]
    public void Gltf_MorphTargets_ImportWithNamesDefaultsAndAnAnimatedWeightsChannel()
    {
        File.Copy(Fixture("Khronos/AnimatedMorphCube.glb"), Path.Combine(AssetsDir, "Cube.glb"));
        var model = Import("Cube.glb");

        var mesh = model.Meshes.Single(m => m.BlendShapes.Count > 0);
        Assert.Equal(2, mesh.BlendShapes.Count);
        Assert.Equal(mesh.BlendShapes.Count, mesh.DefaultBlendShapeWeights.Count);
        Assert.Equal(mesh.BlendShapes.Count, mesh.BlendShapes.Select(s => s.Name).Distinct().Count()); // unique names
        Assert.All(mesh.BlendShapes, s => Assert.Equal(mesh.Mesh.VertexCount, s.PositionDeltas.Length));
        Assert.All(mesh.BlendShapes, s => Assert.Contains(s.PositionDeltas, d => d != Vector3.Zero)); // each shape moves something

        var channel = Assert.Single(model.Animations.SelectMany(a => a.Channels), c => c.Path == AnimationPath.Weights);
        Assert.Equal(mesh, model.Meshes[model.Nodes[channel.Node].Mesh]);
        var weights = new float[2];
        var keyTimes = channel.Times;
        AnimationSampling.SampleWeights(channel, keyTimes[keyTimes.Length / 2], weights);
        Assert.Contains(weights, w => w > 0f);
    }

    [Fact]
    public void MorphTargets_OfSeveralPrimitives_AreJoinedLikeTheirVertices()
    {
        File.Copy(Fixture("Khronos/MorphPrimitivesTest.glb"), Path.Combine(AssetsDir, "Primitives.glb"));
        var model = Import("Primitives.glb");

        var mesh = model.Meshes.Single(m => m.BlendShapes.Count > 0);
        Assert.True(mesh.Mesh.SubMeshes.Count >= 2);
        Assert.All(mesh.BlendShapes, s => Assert.Equal(mesh.Mesh.VertexCount, s.PositionDeltas.Length));
    }

    [Fact]
    public unsafe void Fbx_BlendShapes_ImportThroughAssimp()
    {
        // The FBX is written by Assimp's exporter from the Khronos cube; Assimp keeps blend shapes as anim meshes.
        var fbx = Path.Combine(AssetsDir, "Cube.fbx");
        var api = Silk.NET.Assimp.Assimp.GetApi();
        var scene = api.ImportFile(Fixture("Khronos/AnimatedMorphCube.glb"), 0);
        Assert.True(scene is not null, api.GetErrorStringS());
        try
        {
            Assert.True(scene->MMeshes[0]->MNumAnimMeshes > 0, "the source has morph targets for Assimp");
            Assert.Equal(Silk.NET.Assimp.Return.Success, api.ExportScene(scene, "fbx", fbx, 0));
        }
        finally
        {
            api.ReleaseImport(scene);
        }

        var model = Import("Cube.fbx");
        var mesh = model.Meshes.Single(m => m.BlendShapes.Count > 0);
        Assert.Equal(2, mesh.BlendShapes.Count);
        Assert.All(mesh.BlendShapes, s => Assert.Contains(s.PositionDeltas, d => d.Magnitude > 1e-4f));
    }

    [Fact]
    public void WeightSampling_InterpolatesEveryShape()
    {
        var channel = new AnimationChannel(0, AnimationPath.Weights, AnimationInterpolation.Linear, [0f, 2f], [0f, 1f, 1f, 0f]);
        var weights = new float[2];
        AnimationSampling.SampleWeights(channel, 0.5f, weights);
        Assert.Equal(0.25f, weights[0], 4);
        Assert.Equal(0.75f, weights[1], 4);

        var step = channel with { Interpolation = AnimationInterpolation.Step };
        AnimationSampling.SampleWeights(step, 1.9f, weights);
        Assert.Equal((0f, 1f), (weights[0], weights[1]));
    }
}
