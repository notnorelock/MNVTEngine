using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Tests;

public sealed class SkinningTests
{
    [Fact]
    public void Vertices_FollowTheirJoints_Weighted_AndBoundsCoverTheResult()
    {
        MeshVertex[] bind =
        [
            new() { Position = new Vector3(1f, 0f, 0f), Normal = Vector3.Up, Tangent = new Vector4(1f, 0f, 0f, -1f) },
            new() { Position = new Vector3(0f, 1f, 0f), Normal = Vector3.Up, Tangent = new Vector4(1f, 0f, 0f, 1f) },
        ];
        ushort[] joints = [0, 0, 0, 0, 0, 1, 0, 0];
        float[] weights = [1f, 0f, 0f, 0f, 0.5f, 0.5f, 0f, 0f];
        Matrix4x4[] matrices = [Matrix4x4.Translate(new Vector3(0f, 0f, 2f)), Matrix4x4.TRS(Vector3.Zero, Quaternion.AngleAxis(90f, Vector3.Forward), Vector3.One)];
        var output = new MeshVertex[2];

        var bounds = Skinning.Apply(bind, joints, weights, matrices, output);

        Assert.Equal(new Vector3(1f, 0f, 2f), output[0].Position);       // fully on joint 0: moved by +2 z
        Assert.Equal(-1f, output[0].Tangent.W);                          // handedness kept
        var p = output[1].Position;                                     // half translated, half rotated
        Assert.True(Vector3.Distance(p, (new Vector3(0f, 1f, 2f) + (Quaternion.AngleAxis(90f, Vector3.Forward) * new Vector3(0f, 1f, 0f))) * 0.5f) < 1e-4f, $"got {p}");
        Assert.Equal(1f, output[1].Normal.Magnitude, 4);                 // renormalized
        Assert.True(bounds.Min.X <= p.X + 1e-5f && bounds.Max.Z >= 2f - 1e-5f, $"bounds {bounds}, p {p}");
    }

    [Fact]
    public void AJointOutOfRange_IsRejected() =>
        Assert.Throws<ArgumentException>(() => Skinning.Apply(new MeshVertex[1], [3, 0, 0, 0], [1f, 0f, 0f, 0f], [Matrix4x4.Identity], new MeshVertex[1]));

    [Fact]
    public void BlendShapes_AddWeightedOffsets_AndSkipZeroWeights()
    {
        MeshVertex[] bind = [new() { Position = Vector3.Zero, Normal = Vector3.Up, Tangent = new Vector4(1f, 0f, 0f, 1f) }];
        List<(Vector3[] Positions, Vector3[]? Normals, Vector3[]? Tangents)> shapes =
        [
            ([new Vector3(2f, 0f, 0f)], null, null),
            ([new Vector3(0f, 4f, 0f)], [new Vector3(1f, -1f, 0f)], null),
        ];
        var output = new MeshVertex[1];

        var bounds = Skinning.Morph(bind, shapes, [0.5f, 0.25f], output);

        Assert.Equal(new Vector3(1f, 1f, 0f), output[0].Position);
        Assert.Equal(1f, output[0].Normal.Magnitude, 4); // renormalized after the normal offset
        Assert.Equal(new Vector3(1f, 1f, 0f), bounds.Center);
        Skinning.Morph(bind, shapes, [0f, 0f], output);
        Assert.Equal(Vector3.Zero, output[0].Position);
    }
}
