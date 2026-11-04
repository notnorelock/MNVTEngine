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
/// A mesh in GPU memory: one static vertex buffer and one index buffer (16-bit when the vertex count allows), shared by
/// every renderer that draws the mesh. Owned by whoever uploaded it (normally a <see cref="GpuMeshCache"/>).
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

    public Bounds Bounds { get; }

    /// <summary>The <see cref="Mesh.Version"/> that was uploaded.</summary>
    public int SourceVersion { get; }

    /// <summary>Vertex plus index bytes in GPU memory.</summary>
    public long SizeInBytes { get; }

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

            return new GpuMesh(mesh.Name, vertexBuffer, indexBuffer, [.. mesh.SubMeshes], mesh.Bounds, mesh.Version, vertexBytes.Length + indexBytes);
        }
        catch
        {
            vertexBuffer.Dispose();
            throw;
        }
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
