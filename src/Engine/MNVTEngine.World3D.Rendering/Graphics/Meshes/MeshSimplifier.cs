using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Meshes;

/// <summary>
/// Reduces a mesh's triangle count for levels of detail by quadric error edge collapse (Garland and Heckbert): each
/// step merges a vertex into a neighbour where that changes the surface least. Vertices only merge into existing
/// vertices, so the result uses a subset of the source's vertices (with their normals, UVs, skin weights and so on
/// unchanged) and <see cref="SimplifiedMesh.SourceVertices"/> maps them back. Vertices on open borders, on UV or normal
/// seams (a split vertex is a border of the index topology) and between submeshes stay put, so outlines, seams and
/// material boundaries keep their shape.
/// </summary>
public static class MeshSimplifier
{
    /// <param name="ratio">Fraction of the triangles to keep (0 to 1); fewer remain only when the shape allows.</param>
    /// <returns>The reduced mesh; equal in triangles to the source when nothing could be collapsed.</returns>
    public static SimplifiedMesh Simplify(Mesh mesh, float ratio)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ratio);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ratio, 1f);
        var positions = mesh.Positions;
        var indices = mesh.IndexSpan.ToArray();
        var submeshOf = new int[indices.Length / 3];
        for (var s = 0; s < mesh.SubMeshes.Count; s++)
        {
            var sub = mesh.SubMeshes[s];
            for (var i = sub.IndexStart; i < sub.IndexStart + sub.IndexCount; i += 3)
            {
                submeshOf[i / 3] = s;
            }
        }

        var locked = LockedVertices(positions.Length, indices, submeshOf);
        var quadrics = new Quadric[positions.Length];
        for (var t = 0; t < indices.Length; t += 3)
        {
            var (a, b, c) = (indices[t], indices[t + 1], indices[t + 2]);
            var plane = Quadric.FromTriangle(positions[a], positions[b], positions[c]);
            quadrics[a].Add(plane);
            quadrics[b].Add(plane);
            quadrics[c].Add(plane);
        }

        var target = Math.Max(1, (int)(indices.Length / 3 * ratio));
        var remap = new uint[positions.Length];
        var live = indices.Length / 3;
        while (live > target)
        {
            var collapsed = Pass(positions, indices, live - target, locked, quadrics, remap);
            if (collapsed == 0)
            {
                break;
            }

            live = Compact(ref indices, ref submeshOf, remap);
        }

        return Build(mesh, indices, submeshOf);
    }

    /// <summary>One round of non-overlapping collapses, cheapest first; returns how many were made.</summary>
    private static int Pass(Vector3[] positions, uint[] indices, int trianglesToRemove, bool[] locked, Quadric[] quadrics, uint[] remap)
    {
        // Vertex to triangles, for the flip test.
        var start = new int[positions.Length + 1];
        foreach (var v in indices)
        {
            start[v + 1]++;
        }

        for (var v = 0; v < positions.Length; v++)
        {
            start[v + 1] += start[v];
        }

        var fill = (int[])start.Clone();
        var triangles = new int[indices.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            triangles[fill[indices[i]]++] = i / 3;
        }

        var candidates = new List<(double Cost, uint From, uint To)>(indices.Length);
        for (var t = 0; t < indices.Length; t += 3)
        {
            for (var e = 0; e < 3; e++)
            {
                var (from, to) = (indices[t + e], indices[t + ((e + 1) % 3)]);
                foreach (var (u, v) in new[] { (from, to), (to, from) })
                {
                    if (!locked[u])
                    {
                        candidates.Add((quadrics[u].Evaluate(positions[v]), u, v));
                    }
                }
            }
        }

        candidates.Sort((x, y) => x.Cost.CompareTo(y.Cost));
        for (var v = 0u; v < remap.Length; v++)
        {
            remap[v] = v;
        }

        var touched = new bool[positions.Length];
        var collapsed = 0;
        var removed = 0;
        foreach (var (_, u, v) in candidates)
        {
            if (removed >= trianglesToRemove)
            {
                break;
            }

            if (touched[u] || touched[v] || Flips(positions, indices, triangles, start, u, v, out var lost))
            {
                continue;
            }

            remap[u] = v;
            quadrics[v].Add(quadrics[u]);
            touched[u] = touched[v] = true;
            for (var i = start[u]; i < start[u + 1]; i++)
            {
                var t = triangles[i] * 3;
                for (var k = 0; k < 3; k++)
                {
                    touched[indices[t + k]] = true; // the neighbourhood changed: no second collapse here this round
                }
            }

            collapsed++;
            removed += lost;
        }

        return collapsed;
    }

    /// <summary>True when moving <paramref name="u"/> onto <paramref name="v"/> would turn a remaining triangle around.</summary>
    private static bool Flips(Vector3[] positions, uint[] indices, int[] triangles, int[] start, uint u, uint v, out int lost)
    {
        lost = 0;
        for (var i = start[u]; i < start[u + 1]; i++)
        {
            var t = triangles[i] * 3;
            var (a, b, c) = (indices[t], indices[t + 1], indices[t + 2]);
            if (a == v || b == v || c == v)
            {
                lost++; // degenerate after the collapse: removed
                continue;
            }

            var before = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            Vector3 P(uint x) => x == u ? positions[v] : positions[x];
            var after = Vector3.Cross(P(b) - P(a), P(c) - P(a));
            if (Vector3.Dot(before, after) <= 0f)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Applies the remap and drops triangles that became degenerate; returns the triangles left.</summary>
    private static int Compact(ref uint[] indices, ref int[] submeshOf, uint[] remap)
    {
        var kept = new List<uint>(indices.Length);
        var keptSubmesh = new List<int>(submeshOf.Length);
        for (var t = 0; t < indices.Length; t += 3)
        {
            var (a, b, c) = (remap[indices[t]], remap[indices[t + 1]], remap[indices[t + 2]]);
            if (a != b && b != c && a != c)
            {
                kept.Add(a);
                kept.Add(b);
                kept.Add(c);
                keptSubmesh.Add(submeshOf[t / 3]);
            }
        }

        (indices, submeshOf) = ([.. kept], [.. keptSubmesh]);
        return submeshOf.Length;
    }

    /// <summary>Vertices on an edge used by one triangle only (open borders and seams), or used by more than one submesh.</summary>
    private static bool[] LockedVertices(int vertexCount, uint[] indices, int[] submeshOf)
    {
        var edges = new Dictionary<(uint, uint), int>();
        for (var t = 0; t < indices.Length; t += 3)
        {
            for (var e = 0; e < 3; e++)
            {
                var (a, b) = (indices[t + e], indices[t + ((e + 1) % 3)]);
                var key = a < b ? (a, b) : (b, a);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }

        var locked = new bool[vertexCount];
        foreach (var ((a, b), count) in edges)
        {
            if (count != 2)
            {
                locked[a] = locked[b] = true;
            }
        }

        var submesh = new int[vertexCount];
        Array.Fill(submesh, -1);
        for (var i = 0; i < indices.Length; i++)
        {
            var v = indices[i];
            var s = submeshOf[i / 3];
            if (submesh[v] >= 0 && submesh[v] != s)
            {
                locked[v] = true;
            }

            submesh[v] = s;
        }

        return locked;
    }

    /// <summary>The reduced mesh over the vertices still used, submeshes in their original order.</summary>
    private static SimplifiedMesh Build(Mesh source, uint[] indices, int[] submeshOf)
    {
        var map = new Dictionary<uint, uint>();
        var sourceVertices = new List<int>();
        var ordered = new List<uint>(indices.Length);
        var subMeshes = new SubMesh[source.SubMeshes.Count];
        for (var s = 0; s < subMeshes.Length; s++)
        {
            var first = ordered.Count;
            for (var t = 0; t < submeshOf.Length; t++)
            {
                if (submeshOf[t] != s)
                {
                    continue;
                }

                for (var k = 0; k < 3; k++)
                {
                    var v = indices[(t * 3) + k];
                    if (!map.TryGetValue(v, out var mapped))
                    {
                        mapped = (uint)sourceVertices.Count;
                        map[v] = mapped;
                        sourceVertices.Add((int)v);
                    }

                    ordered.Add(mapped);
                }
            }

            subMeshes[s] = new SubMesh(first, ordered.Count - first);
        }

        var vertices = sourceVertices.ToArray();
        T[]? Pick<T>(T[]? stream) => stream is null ? null : [.. vertices.Select(v => stream[v])];
        var mesh = new Mesh(source.Name)
        {
            Positions = Pick(source.Positions)!,
            Normals = Pick(source.Normals),
            Tangents = Pick(source.Tangents),
            UV0 = Pick(source.UV0),
            UV1 = Pick(source.UV1),
            Colors = Pick(source.Colors),
        };
        mesh.SetIndices([.. ordered], subMeshes);
        return new SimplifiedMesh(mesh, vertices);
    }

    /// <summary>A symmetric 4x4 error matrix: the sum of squared distances to a set of (area-weighted) planes.</summary>
    private struct Quadric
    {
        private double _xx, _xy, _xz, _xw, _yy, _yz, _yw, _zz, _zw, _ww;

        public static Quadric FromTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var cross = Vector3.Cross(b - a, c - a);
            var length = cross.Magnitude;
            if (length <= 1e-12f)
            {
                return default;
            }

            var n = cross / length;
            double area = length * 0.5f, x = n.X, y = n.Y, z = n.Z, w = -Vector3.Dot(n, a);
            return new Quadric
            {
                _xx = area * x * x, _xy = area * x * y, _xz = area * x * z, _xw = area * x * w,
                _yy = area * y * y, _yz = area * y * z, _yw = area * y * w,
                _zz = area * z * z, _zw = area * z * w, _ww = area * w * w,
            };
        }

        public void Add(in Quadric q)
        {
            _xx += q._xx; _xy += q._xy; _xz += q._xz; _xw += q._xw;
            _yy += q._yy; _yz += q._yz; _yw += q._yw;
            _zz += q._zz; _zw += q._zw; _ww += q._ww;
        }

        public readonly double Evaluate(Vector3 p)
        {
            double x = p.X, y = p.Y, z = p.Z;
            return (_xx * x * x) + (2 * _xy * x * y) + (2 * _xz * x * z) + (2 * _xw * x)
                + (_yy * y * y) + (2 * _yz * y * z) + (2 * _yw * y)
                + (_zz * z * z) + (2 * _zw * z) + _ww;
        }
    }
}

/// <param name="Mesh">The reduced mesh.</param>
/// <param name="SourceVertices">For each of its vertices, the source vertex it is (to carry skin weights and other per-vertex data over).</param>
public sealed record SimplifiedMesh(Mesh Mesh, int[] SourceVertices);
