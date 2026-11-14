using System.Runtime.InteropServices;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Meshes;

/// <summary>
/// The interleaved vertex every 3D mesh is uploaded as (72 bytes). Streams the mesh lacks get neutral values:
/// normal +Z, tangent +X, UVs 0 and white color.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct MeshVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector4 Tangent;
    public Vector2 UV0;
    public Vector2 UV1;
    public Color Color;

    /// <summary>Shader locations: 0 position, 1 normal, 2 tangent, 3 uv0, 4 uv1, 5 color.</summary>
    public static readonly VertexLayout Layout = new(Marshal.SizeOf<MeshVertex>(),
    [
        new VertexElement(0, 3, VertexElementType.Float32, 0),
        new VertexElement(1, 3, VertexElementType.Float32, 12),
        new VertexElement(2, 4, VertexElementType.Float32, 24),
        new VertexElement(3, 2, VertexElementType.Float32, 40),
        new VertexElement(4, 2, VertexElementType.Float32, 48),
        new VertexElement(5, 4, VertexElementType.Float32, 56),
    ]);
}

/// <summary>
/// A <see cref="MeshVertex"/> with four joint indices (as floats) and weights, for GPU skinning (104 bytes). Shader
/// locations 0–5 as <see cref="MeshVertex"/>, 6 joints, 7 weights.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct SkinnedMeshVertex
{
    public MeshVertex Vertex;
    public Vector4 Joints;
    public Vector4 Weights;

    public static readonly VertexLayout Layout = new(Marshal.SizeOf<SkinnedMeshVertex>(),
    [
        .. MeshVertex.Layout.Elements,
        new VertexElement(6, 4, VertexElementType.Float32, 72),
        new VertexElement(7, 4, VertexElementType.Float32, 88),
    ]);
}

/// <summary>
/// A mesh in GPU memory: one static vertex buffer and one index buffer (16-bit when the vertex count allows), shared by
/// every renderer that draws the mesh. Owned by whoever uploaded it (normally a <see cref="GpuMeshCache"/>).
/// <see cref="CreateDynamic"/> makes one whose vertices are rewritten by the CPU (skinned meshes).
/// </summary>
public sealed class GpuMesh : IDisposable
{
    private GpuMesh(string name, VertexBuffer vertices, IndexBuffer indices, IReadOnlyList<SubMesh> subMeshes, Bounds bounds, int version, long bytes)
    {
        Name = name;
        Vertices = vertices;
        Indices = indices;
        SubMeshes = subMeshes;
        Bounds = bounds;
        SourceVersion = version;
        SizeInBytes = bytes;
    }

    public string Name { get; }

    public VertexBuffer Vertices { get; }

    public IndexBuffer Indices { get; }

    public IReadOnlyList<SubMesh> SubMeshes { get; }

    /// <summary>Box around the vertices (updated with them for dynamic meshes).</summary>
    public Bounds Bounds { get; private set; }

    /// <summary>The <see cref="Mesh.Version"/> that was uploaded.</summary>
    public int SourceVersion { get; }

    /// <summary>Vertex plus index bytes in GPU memory.</summary>
    public long SizeInBytes { get; }

    /// <summary>
    /// UV0 units per object-space unit, averaged over the mesh's area (sqrt of UV area over surface area); 0 when unknown
    /// (no UVs, or degenerate). Texture streaming uses it to tell how many texels a draw needs.
    /// </summary>
    public float UVDensity { get; init; }

    /// <summary>See <see cref="UVDensity"/>.</summary>
    public static float UVDensityOf(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.UV0 is not { } uv)
        {
            return 0f;
        }

        var positions = mesh.Positions;
        var indices = mesh.IndexSpan;
        double uvArea = 0, area = 0;
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var (a, b, c) = ((int)indices[i], (int)indices[i + 1], (int)indices[i + 2]);
            area += Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).Magnitude;
            var (e1, e2) = (uv[b] - uv[a], uv[c] - uv[a]);
            uvArea += Math.Abs((e1.X * e2.Y) - (e1.Y * e2.X));
        }

        return area > 1e-12 && uvArea > 1e-12 ? (float)Math.Sqrt(uvArea / area) : 0f;
    }

    public bool IsDisposed => Vertices.IsDisposed;

    /// <exception cref="InvalidDataException">The mesh is invalid (see <see cref="Mesh.Validate"/>).</exception>
    public static GpuMesh Upload(IGraphicsDevice device, Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Validate() is { Count: > 0 } problems)
        {
            throw new InvalidDataException($"Mesh '{mesh.Name}' cannot be uploaded: {string.Join(" ", problems)}");
        }

        var vertices = Interleave(mesh);
        var vertexBytes = MemoryMarshal.AsBytes<MeshVertex>(vertices);
        var vertexBuffer = device.CreateStaticVertexBuffer(MeshVertex.Layout, vertexBytes, mesh.Name + " vertices");
        try
        {
            IndexBuffer indexBuffer;
            long indexBytes;
            if (mesh.IndexFormat == IndexFormat.UInt16)
            {
                var narrow = new ushort[mesh.IndexSpan.Length];
                for (var i = 0; i < narrow.Length; i++)
                {
                    narrow[i] = (ushort)mesh.IndexSpan[i];
                }

                indexBuffer = device.CreateIndexBuffer(narrow, mesh.Name + " indices");
                indexBytes = narrow.Length * sizeof(ushort);
            }
            else
            {
                indexBuffer = device.CreateIndexBuffer(mesh.IndexSpan, mesh.Name + " indices");
                indexBytes = mesh.IndexSpan.Length * sizeof(uint);
            }

            return new GpuMesh(mesh.Name, vertexBuffer, indexBuffer, [.. mesh.SubMeshes], mesh.Bounds, mesh.Version, vertexBytes.Length + indexBytes) { UVDensity = UVDensityOf(mesh) };
        }
        catch
        {
            vertexBuffer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A static mesh with four joints and weights per vertex (<see cref="SkinnedMeshVertex"/>), skinned on the GPU by the
    /// skinned shaders with the draw's joint matrices. Owned by the caller.
    /// </summary>
    /// <exception cref="InvalidDataException">The mesh is invalid, or the skin data does not have four entries per vertex.</exception>
    public static GpuMesh UploadSkinned(IGraphicsDevice device, Mesh mesh, ReadOnlySpan<ushort> joints, ReadOnlySpan<float> weights)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Validate() is { Count: > 0 } problems)
        {
            throw new InvalidDataException($"Mesh '{mesh.Name}' cannot be uploaded: {string.Join(" ", problems)}");
        }

        if (joints.Length != mesh.VertexCount * 4 || weights.Length != joints.Length)
        {
            throw new InvalidDataException($"Mesh '{mesh.Name}' needs 4 joints and weights per vertex for GPU skinning.");
        }

        var plain = Interleave(mesh);
        var vertices = new SkinnedMeshVertex[plain.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            var j = i * 4;
            vertices[i] = new SkinnedMeshVertex
            {
                Vertex = plain[i],
                Joints = new Vector4(joints[j], joints[j + 1], joints[j + 2], joints[j + 3]),
                Weights = new Vector4(weights[j], weights[j + 1], weights[j + 2], weights[j + 3]),
            };
        }

        var vertexBytes = MemoryMarshal.AsBytes<SkinnedMeshVertex>(vertices);
        var vertexBuffer = device.CreateStaticVertexBuffer(SkinnedMeshVertex.Layout, vertexBytes, mesh.Name + " skinned vertices");
        try
        {
            var indexBuffer = mesh.IndexFormat == IndexFormat.UInt16
                ? device.CreateIndexBuffer([.. mesh.IndexSpan.ToArray().Select(i => (ushort)i)], mesh.Name + " indices")
                : device.CreateIndexBuffer(mesh.IndexSpan, mesh.Name + " indices");
            return new GpuMesh(mesh.Name, vertexBuffer, indexBuffer, [.. mesh.SubMeshes], mesh.Bounds, mesh.Version,
                vertexBytes.Length + (mesh.IndexSpan.Length * (mesh.IndexFormat == IndexFormat.UInt16 ? 2 : 4))) { UVDensity = UVDensityOf(mesh) };
        }
        catch
        {
            vertexBuffer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A mesh whose vertices are replaced each frame with <see cref="UpdateVertices"/> (indices stay static). Starts with
    /// the mesh's own vertices. Owned by the caller.
    /// </summary>
    /// <exception cref="InvalidDataException">The mesh is invalid.</exception>
    public static GpuMesh CreateDynamic(IGraphicsDevice device, Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Validate() is { Count: > 0 } problems)
        {
            throw new InvalidDataException($"Mesh '{mesh.Name}' cannot be uploaded: {string.Join(" ", problems)}");
        }

        var vertexBuffer = device.CreateVertexBuffer(MeshVertex.Layout, mesh.VertexCount, mesh.Name + " skinned vertices");
        try
        {
            var vertices = Interleave(mesh);
            vertexBuffer.SetData<MeshVertex>(vertices);
            var indexBuffer = mesh.IndexFormat == IndexFormat.UInt16
                ? device.CreateIndexBuffer([.. mesh.IndexSpan.ToArray().Select(i => (ushort)i)], mesh.Name + " indices")
                : device.CreateIndexBuffer(mesh.IndexSpan, mesh.Name + " indices");
            return new GpuMesh(mesh.Name, vertexBuffer, indexBuffer, [.. mesh.SubMeshes], mesh.Bounds, mesh.Version,
                (vertices.Length * MeshVertex.Layout.Stride) + (mesh.IndexSpan.Length * (mesh.IndexFormat == IndexFormat.UInt16 ? 2 : 4)));
        }
        catch
        {
            vertexBuffer.Dispose();
            throw;
        }
    }

    /// <summary>Replaces the vertices of a dynamic mesh (<see cref="CreateDynamic"/>) and its bounds.</summary>
    /// <exception cref="InvalidOperationException">The mesh is static.</exception>
    public void UpdateVertices(ReadOnlySpan<MeshVertex> vertices, Bounds bounds)
    {
        Vertices.SetData(vertices);
        Bounds = bounds;
    }

    /// <summary>The upload format of a mesh's vertices.</summary>
    public static MeshVertex[] Interleave(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var result = new MeshVertex[mesh.VertexCount];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new MeshVertex
            {
                Position = mesh.Positions[i],
                Normal = mesh.Normals?[i] ?? Vector3.UnitZ,
                Tangent = mesh.Tangents?[i] ?? new Vector4(1f, 0f, 0f, 1f),
                UV0 = mesh.UV0?[i] ?? Vector2.Zero,
                UV1 = mesh.UV1?[i] ?? Vector2.Zero,
                Color = mesh.Colors?[i] ?? Color.White,
            };
        }

        return result;
    }

    public void Dispose()
    {
        Vertices.Dispose();
        Indices.Dispose();
    }
}

/// <summary>
/// One GPU copy per mesh: renderers sharing a mesh share its buffers. A mesh whose <see cref="Mesh.Version"/> changed is
/// uploaded again (the old buffers are released once frames in flight are done with them). Disposing the cache releases
/// every upload.
/// </summary>
public sealed class GpuMeshCache(IGraphicsDevice device) : IDisposable
{
    private readonly Dictionary<Mesh, GpuMesh> _meshes = new(ReferenceEqualityComparer.Instance);

    public int Count => _meshes.Count;

    /// <summary>GPU bytes of all cached meshes.</summary>
    public long SizeInBytes => _meshes.Values.Sum(m => m.SizeInBytes);

    /// <exception cref="InvalidDataException">The mesh is invalid.</exception>
    public GpuMesh Get(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (_meshes.TryGetValue(mesh, out var cached))
        {
            if (cached.SourceVersion == mesh.Version)
            {
                return cached;
            }

            cached.Dispose();
            _meshes.Remove(mesh);
        }

        var uploaded = GpuMesh.Upload(device, mesh);
        _meshes[mesh] = uploaded;
        return uploaded;
    }

    /// <summary>Releases a mesh's GPU copy (e.g. when its asset unloads).</summary>
    public bool Remove(Mesh mesh)
    {
        if (!_meshes.Remove(mesh, out var gpu))
        {
            return false;
        }

        gpu.Dispose();
        return true;
    }

    public void Dispose()
    {
        foreach (var mesh in _meshes.Values)
        {
            mesh.Dispose();
        }

        _meshes.Clear();
    }
}
