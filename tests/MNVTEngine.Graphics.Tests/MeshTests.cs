using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Tests;

public sealed class MeshTests
{
    public static TheoryData<string, Func<Mesh>, Vector3, float> Primitives => new()
    {
        { "Cube", () => MeshPrimitives.Cube(2f), new Vector3(2f, 2f, 2f), 8f },
        { "Sphere", () => MeshPrimitives.Sphere(1f, 48, 24), new Vector3(2f, 2f, 2f), 4f / 3f * MathF.PI },
        { "Cylinder", () => MeshPrimitives.Cylinder(0.5f, 2f, 48), new Vector3(1f, 2f, 1f), MathF.PI * 0.25f * 2f },
        { "Capsule", () => MeshPrimitives.Capsule(0.5f, 3f, 48, 12), new Vector3(1f, 3f, 1f), (MathF.PI * 0.25f * 2f) + (4f / 3f * MathF.PI * 0.125f) },
        { "Cone", () => MeshPrimitives.Cone(0.5f, 1f, 48), new Vector3(1f, 1f, 1f), MathF.PI * 0.25f / 3f },
    };

    private static float SignedVolume(Mesh mesh)
    {
        var volume = 0f;
        var p = mesh.Positions;
        var i = mesh.IndexSpan;
        for (var t = 0; t < i.Length; t += 3)
        {
            volume += Vector3.Dot(p[i[t]], Vector3.Cross(p[i[t + 1]], p[i[t + 2]])) / 6f;
        }

        return volume;
    }

    [Theory]
    [MemberData(nameof(Primitives))]
    public void ClosedPrimitives_AreValid_Outward_AndHaveTheRightSizeAndVolume(string name, Func<Mesh> create, Vector3 size, float volume)
    {
        var mesh = create();
        Assert.Equal(name, mesh.Name);
        Assert.Empty(mesh.Validate());
        Assert.True(Vector3.Distance(size, mesh.Bounds.Size) < 1e-4f, $"size {mesh.Bounds.Size}");
        Assert.True(Vector3.Distance(Vector3.Zero, mesh.Bounds.Center) < 1e-4f);

        // Counter-clockwise outward faces enclose a positive volume; tessellation makes curved shapes slightly smaller.
        var actual = SignedVolume(mesh);
        Assert.InRange(actual, volume * 0.97f, volume * 1.0001f);
        AssertSurface(mesh);
    }

    [Fact]
    public void FlatPrimitives_FaceTheirNormal()
    {
        var plane = MeshPrimitives.Plane(4f, 2f, 3);
        Assert.Empty(plane.Validate());
        Assert.Equal(16, plane.VertexCount);
        Assert.Equal(18, plane.TriangleCount);
        Assert.Equal(new Vector3(4f, 0f, 2f), plane.Bounds.Size);
        Assert.All(plane.Normals!, n => Assert.Equal(Vector3.Up, n));
        AssertSurface(plane);

        var quad = MeshPrimitives.Quad(2f, 3f);
        Assert.Equal(new Vector3(2f, 3f, 0f), quad.Bounds.Size);
        Assert.All(quad.Normals!, n => Assert.Equal(Vector3.Back, n));
        AssertSurface(quad);
    }

    /// <summary>Unit normals and tangents; each face's winding agrees with its vertex normals; UVs in [0, 1].</summary>
    private static void AssertSurface(Mesh mesh)
    {
        Assert.All(mesh.Normals!, n => Assert.Equal(1f, n.Magnitude, 3));
        Assert.All(mesh.Tangents!, t => Assert.Equal(1f, new Vector3(t.X, t.Y, t.Z).Magnitude, 3));
        Assert.All(mesh.Tangents!, t => Assert.True(MathF.Abs(t.W) == 1f));
        Assert.All(mesh.UV0!, uv => Assert.True(uv.X is >= -1e-5f and <= 1.00001f && uv.Y is >= -1e-5f and <= 1.00001f, $"uv {uv}"));
        var p = mesh.Positions;
        var i = mesh.IndexSpan;
        for (var t = 0; t < i.Length; t += 3)
        {
            var face = Vector3.Cross(p[i[t + 1]] - p[i[t]], p[i[t + 2]] - p[i[t]]);
            Assert.True(face.SqrMagnitude > 0f, $"{mesh.Name}: degenerate triangle {t / 3}");
            var normal = mesh.Normals![i[t]] + mesh.Normals[i[t + 1]] + mesh.Normals[i[t + 2]];
            Assert.True(Vector3.Dot(face, normal) > 0f, $"{mesh.Name}: triangle {t / 3} winds against its normals");
        }
    }

    [Fact]
    public void Validate_ReportsEveryProblem()
    {
        var mesh = new Mesh("Bad") { Positions = [Vector3.Zero, Vector3.One], Normals = [Vector3.Up], UV0 = [Vector2.Zero, Vector2.One] };
        mesh.SetIndices([0, 1, 5, 0], new SubMesh(0, 9));
        var problems = mesh.Validate();
        Assert.Contains(problems, p => p.Contains("Normals has 1", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("multiple of 3", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("vertex 5", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Submesh 0+9", StringComparison.Ordinal));
        Assert.Contains("No vertices", new Mesh().Validate()[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Changes_BumpTheVersion_AndBounds_Follow()
    {
        var mesh = MeshPrimitives.Quad();
        var version = mesh.Version;
        mesh.Positions = [.. mesh.Positions.Select(p => p * 4f)];
        Assert.True(mesh.Version > version);
        Assert.Equal(new Vector3(4f, 4f, 0f), mesh.Bounds.Size);

        version = mesh.Version;
        mesh.Positions[0] = new Vector3(-10f, 0f, 0f);
        mesh.MarkChanged();
        Assert.True(mesh.Version > version);
        Assert.Equal(-10f, mesh.Bounds.Min.X);
    }

    [Fact]
    public void RecalculateNormalsAndTangents_FollowGeometryAndUVs()
    {
        var mesh = new Mesh("Tri")
        {
            Positions = [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            UV0 = [new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1)],
        };
        mesh.SetIndices([0, 1, 2]);
        Assert.Throws<InvalidOperationException>(mesh.RecalculateTangents);
        mesh.RecalculateNormals();
        Assert.All(mesh.Normals!, n => Assert.Equal(Vector3.Back, n));
        mesh.RecalculateTangents();
        Assert.All(mesh.Tangents!, t => Assert.Equal(new Vector4(1, 0, 0, 1), t)); // U along +X, V along +Y: right-handed

        mesh.UV0 = [new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, -1)];   // mirrored V
        mesh.RecalculateTangents();
        Assert.All(mesh.Tangents!, t => Assert.Equal(-1f, t.W));
    }

    [Fact]
    public void IndexFormat_Is16BitUntilTheVerticesNeedMore()
    {
        Assert.Equal(IndexFormat.UInt16, new Mesh { Positions = new Vector3[65536] }.IndexFormat);
        Assert.Equal(IndexFormat.UInt32, new Mesh { Positions = new Vector3[65537] }.IndexFormat);
    }

    [Fact]
    public void GpuMeshes_AreShared_Interleaved_AndRefreshedOnChange()
    {
        var device = new FakeGraphicsDevice();
        var mesh = MeshPrimitives.Cube();
        mesh.Colors = Enumerable.Repeat(Color.Red, mesh.VertexCount).ToArray();
        using var cache = new GpuMeshCache(device);
        var first = cache.Get(mesh);
        Assert.Same(first, cache.Get(mesh));
        Assert.Equal(1, cache.Count);
        Assert.Equal((24 * 72) + (36 * 2), first.SizeInBytes);
        Assert.Equal(24, first.Vertices.Capacity);
        Assert.Equal(IndexFormat.UInt16, first.Indices.Format);

        var vertex = GpuMesh.Interleave(mesh)[0];
        Assert.Equal((mesh.Positions[0], mesh.Normals![0], Color.Red), (vertex.Position, vertex.Normal, vertex.Color));
        Assert.Equal(72, MeshVertex.Layout.Stride);

        mesh.MarkChanged();
        var second = cache.Get(mesh);
        Assert.NotSame(first, second);
        Assert.True(first.IsDisposed);
        Assert.Throws<InvalidDataException>(() => cache.Get(new Mesh("Empty")));
        cache.Dispose();
        Assert.True(second.IsDisposed);
    }
}
