using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Meshes;

/// <summary>A range of a mesh's indices drawn with one material (triangle list).</summary>
public readonly record struct SubMesh(int IndexStart, int IndexCount);

/// <summary>
/// Triangle mesh in CPU memory: vertex streams (positions required; normals, tangents, two UV sets and colors
/// optional), 32-bit indices and submeshes. Front faces are counter-clockwise (right-handed, as in glTF).
/// <para>
/// Assigning a stream or <see cref="SetIndices"/> bumps <see cref="Version"/> and recomputes <see cref="Bounds"/>, so GPU
/// copies (<see cref="GpuMeshCache"/>) refresh. After changing array elements in place, call <see cref="MarkChanged"/>.
/// </para>
/// </summary>
public sealed class Mesh
{
    private Vector3[] _positions = [];
    private Vector3[]? _normals;
    private Vector4[]? _tangents;
    private Vector2[]? _uv0;
    private Vector2[]? _uv1;
    private Color[]? _colors;
    private uint[] _indices = [];
    private SubMesh[] _subMeshes = [];

    public Mesh(string name = "Mesh") => Name = name;

    public string Name { get; set; }

    public Vector3[] Positions { get => _positions; set => Set(ref _positions, value ?? throw new ArgumentNullException(nameof(value))); }

    public Vector3[]? Normals { get => _normals; set => Set(ref _normals, value); }

    /// <summary>XYZ = tangent direction, W = bitangent sign (±1), as in glTF.</summary>
    public Vector4[]? Tangents { get => _tangents; set => Set(ref _tangents, value); }

    public Vector2[]? UV0 { get => _uv0; set => Set(ref _uv0, value); }

    public Vector2[]? UV1 { get => _uv1; set => Set(ref _uv1, value); }

    public Color[]? Colors { get => _colors; set => Set(ref _colors, value); }

    public IReadOnlyList<uint> Indices => _indices;

    public IReadOnlyList<SubMesh> SubMeshes => _subMeshes;

    public int VertexCount => _positions.Length;

    public int TriangleCount => _indices.Length / 3;

    /// <summary>Axis-aligned box around all positions (in mesh space).</summary>
    public Bounds Bounds { get; private set; }

    /// <summary>16-bit indices suffice when every index fits.</summary>
    public IndexFormat IndexFormat => _positions.Length <= ushort.MaxValue + 1 ? IndexFormat.UInt16 : IndexFormat.UInt32;

    /// <summary>Changes whenever the data changes.</summary>
    public int Version { get; private set; }

    /// <summary>Raw indices (for uploads and processing). Do not modify without <see cref="MarkChanged"/>.</summary>
    public ReadOnlySpan<uint> IndexSpan => _indices;

    /// <summary>Sets the triangle list; without submeshes, one submesh covers all indices.</summary>
    public void SetIndices(uint[] indices, params SubMesh[] subMeshes)
    {
        ArgumentNullException.ThrowIfNull(indices);
        _indices = indices;
        _subMeshes = subMeshes is { Length: > 0 } ? subMeshes : [new SubMesh(0, indices.Length)];
        MarkChanged();
    }

    /// <summary>Call after editing stream or index arrays in place.</summary>
    public void MarkChanged()
    {
        Version++;
        RecalculateBounds();
    }

    public void RecalculateBounds()
    {
        if (_positions.Length == 0)
        {
            Bounds = default;
            return;
        }

        Vector3 min = _positions[0], max = _positions[0];
        foreach (var p in _positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        Bounds = Bounds.FromMinMax(min, max);
    }

    /// <summary>Problems that make the mesh unusable; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (_positions.Length == 0)
        {
            problems.Add("The mesh has no vertices.");
        }

        void Stream(Array? stream, string name)
        {
            if (stream is not null && stream.Length != _positions.Length)
            {
                problems.Add($"{name} has {stream.Length} entries for {_positions.Length} vertices.");
            }
        }

        Stream(_normals, nameof(Normals));
        Stream(_tangents, nameof(Tangents));
        Stream(_uv0, nameof(UV0));
        Stream(_uv1, nameof(UV1));
        Stream(_colors, nameof(Colors));
        if (_indices.Length == 0 || _indices.Length % 3 != 0)
        {
            problems.Add($"The index count {_indices.Length} is not a positive multiple of 3 (triangle list).");
        }

        var outOfRange = Array.FindIndex(_indices, i => i >= _positions.Length);
        if (outOfRange >= 0)
        {
            problems.Add($"Index {outOfRange} refers to vertex {_indices[outOfRange]}, but there are {_positions.Length}.");
        }

        foreach (var sub in _subMeshes)
        {
            if (sub.IndexStart < 0 || sub.IndexCount <= 0 || sub.IndexCount % 3 != 0 || sub.IndexStart + sub.IndexCount > _indices.Length)
            {
                problems.Add($"Submesh {sub.IndexStart}+{sub.IndexCount} is outside the {_indices.Length} indices or not whole triangles.");
            }
        }

        if (Array.Exists(_positions, p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)))
        {
            problems.Add("A position is NaN or infinite.");
        }

        return problems;
    }

    /// <summary>Smooth normals: each vertex gets the area-weighted average of the faces using it.</summary>
    public void RecalculateNormals()
    {
        var normals = new Vector3[_positions.Length];
        for (var i = 0; i + 2 < _indices.Length; i += 3)
        {
            uint a = _indices[i], b = _indices[i + 1], c = _indices[i + 2];
            var face = Vector3.Cross(_positions[b] - _positions[a], _positions[c] - _positions[a]); // length = 2 * area
            normals[a] += face;
            normals[b] += face;
            normals[c] += face;
        }

        for (var i = 0; i < normals.Length; i++)
        {
            normals[i] = normals[i].SqrMagnitude > 0f ? normals[i].Normalized : Vector3.Up;
        }

        Normals = normals;
    }

    /// <summary>Per-vertex tangents from UV0 and normals (Lengyel's method), with the bitangent sign in W.</summary>
    /// <exception cref="InvalidOperationException">The mesh has no normals or no UV0.</exception>
    public void RecalculateTangents()
    {
        if (_normals is null || _uv0 is null)
        {
            throw new InvalidOperationException($"Mesh '{Name}' needs normals and UV0 to compute tangents.");
        }

        var tan = new Vector3[_positions.Length];
        var bitan = new Vector3[_positions.Length];
        for (var i = 0; i + 2 < _indices.Length; i += 3)
        {
            uint a = _indices[i], b = _indices[i + 1], c = _indices[i + 2];
            var e1 = _positions[b] - _positions[a];
            var e2 = _positions[c] - _positions[a];
            var d1 = _uv0[b] - _uv0[a];
            var d2 = _uv0[c] - _uv0[a];
            var det = (d1.X * d2.Y) - (d2.X * d1.Y);
            if (MathF.Abs(det) < 1e-12f)
            {
                continue; // degenerate UVs: contributes nothing
            }

            var r = 1f / det;
            var t = ((e1 * d2.Y) - (e2 * d1.Y)) * r;
            var s = ((e2 * d1.X) - (e1 * d2.X)) * r;
            foreach (var v in (ReadOnlySpan<uint>)[a, b, c])
            {
                tan[v] += t;
                bitan[v] += s;
            }
        }

        var tangents = new Vector4[_positions.Length];
        for (var i = 0; i < tangents.Length; i++)
        {
            var n = _normals[i];
            var t = tan[i] - (n * Vector3.Dot(n, tan[i])); // Gram-Schmidt
            if (t.SqrMagnitude < 1e-12f)
            {
                t = Vector3.Cross(MathF.Abs(n.Y) < 0.99f ? Vector3.Up : Vector3.Right, n); // any perpendicular
            }

            t = t.Normalized;
            var w = Vector3.Dot(Vector3.Cross(n, t), bitan[i]) < 0f ? -1f : 1f;
            tangents[i] = new Vector4(t.X, t.Y, t.Z, w);
        }

        Tangents = tangents;
    }

    private void Set<T>(ref T field, T value)
    {
        field = value;
        MarkChanged();
    }
}
