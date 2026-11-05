using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Assets.Models.Tests;

public sealed class ModelProcessingTests
{
    private static ModelMesh Triangle(string name, uint lastIndex)
    {
        var mesh = new Mesh(name) { Positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY] };
        mesh.SetIndices([0, 1, lastIndex], new SubMesh(0, 3));
        return new ModelMesh(name, mesh);
    }

    [Fact]
    public void InvalidMeshes_AreDropped_AndNodesKeepPointingAtTheRightMeshes()
    {
        var model = new ModelData();
        model.Meshes.AddRange([Triangle("A", 2), Triangle("Broken", 5), Triangle("C", 2)]);
        model.Nodes.AddRange([
            new ModelNode("Root", -1, Vector3.Zero, Quaternion.Identity, Vector3.One),
            new ModelNode("NodeA", 0, Vector3.Zero, Quaternion.Identity, Vector3.One, Mesh: 0),
            new ModelNode("NodeBroken", 0, Vector3.Zero, Quaternion.Identity, Vector3.One, Mesh: 1),
            new ModelNode("NodeC", 0, Vector3.Zero, Quaternion.Identity, Vector3.One, Mesh: 2),
        ]);
        var animation = new ModelAnimation("Blink");
        animation.Channels.Add(new AnimationChannel(2, AnimationPath.Weights, AnimationInterpolation.Linear, [0f], []));
        animation.Channels.Add(new AnimationChannel(3, AnimationPath.Translation, AnimationInterpolation.Linear, [0f], [0f, 0f, 0f]));
        model.Animations.Add(animation);
        var warnings = new List<string>();

        ModelProcessing.Finish(model, new ModelImportSettings { GenerateMissingNormals = false, GenerateTangents = false }, warnings, new HashSet<Guid>());

        Assert.Equal(["A", "C"], model.Meshes.Select(m => m.Name));
        Assert.Equal([-1, 0, -1, 1], model.Nodes.Select(n => n.Mesh)); // before the fix NodeC pointed past the end
        Assert.Contains(warnings, w => w.Contains("Node 'NodeBroken' draws nothing", StringComparison.Ordinal));
        Assert.Equal(AnimationPath.Translation, Assert.Single(animation.Channels).Path);
        Assert.Equal(["A", "C"], ModelArtifact.Read(ModelArtifact.Write(model)).Meshes.Select(m => m.Name)); // valid again
    }
}
