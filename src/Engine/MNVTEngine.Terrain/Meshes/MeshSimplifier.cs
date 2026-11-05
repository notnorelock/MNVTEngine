using System.Globalization;
using System.Text;
using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Meshes;

/// <summary>
/// Port of three.js's SimplifyModifier (MIT): Stan Melax's 1998 progressive-mesh edge collapse. Vertices equal in
/// position and uv (to 1e-4, as three's mergeVertices) are welded first, then the cheapest edge is collapsed until
/// <c>keepRatio</c> of the welded vertices remain (never fewer than <c>minVertices</c>). Attributes are float32 as in
/// three.js. The result is expanded back into a triangle soup.
/// </summary>
/// <remarks>
/// TerrainHeaven passed a removal count measured on the unwelded soup (about six times the welded count for a closed
/// mesh) and read three's indexed result as a soup, which emptied or scrambled every LOD; this measures on the welded mesh.
/// </remarks>
public static class MeshSimplifier
{
    private const double Tolerance = 1e-4;

    public static (double[] Positions, double[] Uvs) Simplify(double[] positions, double[] uvs, double keepRatio, int minVertices = 12)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(uvs);
        var hasUv = uvs.Length >= positions.Length / 3 * 2;
        var (vertices, faces) = Weld(positions, hasUv ? uvs : null);

        foreach (var vertex in vertices)
        {
            ComputeEdgeCostAtVertex(vertex);
        }

        var count = vertices.Count - Math.Max(minVertices, (int)MathUtil.JsRound(vertices.Count * keepRatio));
        for (var z = count; z > 0; z--)
        {
            if (vertices.Count == 0)
            {
                break;
            }

            var next = MinimumCostEdge(vertices);
            Collapse(vertices, faces, next, next.CollapseNeighbor);
        }

        var outPositions = new List<double>(faces.Count * 9);
        var outUvs = new List<double>(faces.Count * 6);
        foreach (var face in faces)
        {
            foreach (var v in (Vertex[])[face.V1, face.V2, face.V3])
            {
                outPositions.AddRange([v.Position.X, v.Position.Y, v.Position.Z]);
                outUvs.AddRange(v.Uv is { } uv ? [uv.X, uv.Y] : [0, 0]);
            }
        }

        return ([.. outPositions], [.. outUvs]);
    }

    private static double F32(double value) => (float)value;

    private static (List<Vertex> Vertices, List<Face> Faces) Weld(double[] positions, double[]? uvs)
    {
        // three's mergeVertices: hash every attribute component truncated to the tolerance's decimal places.
        var multiplier = Math.Pow(10, Math.Log10(1 / Tolerance));
        var additive = Tolerance * 0.5 * multiplier;
        var hashToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var vertices = new List<Vertex>();
        var indices = new List<int>();
        var hash = new StringBuilder();
        var vertexCount = positions.Length / 3;
        for (var i = 0; i < vertexCount; i++)
        {
            var position = new Vec3(F32(positions[i * 3]), F32(positions[(i * 3) + 1]), F32(positions[(i * 3) + 2]));
            Vec2? uv = uvs is null ? null : new Vec2(F32(uvs[i * 2]), F32(uvs[(i * 2) + 1]));
            hash.Clear();
            foreach (var component in uv is { } u ? [position.X, position.Y, position.Z, u.X, u.Y] : (double[])[position.X, position.Y, position.Z])
            {
                hash.Append(((int)Math.Truncate((component * multiplier) + additive)).ToString(CultureInfo.InvariantCulture)).Append(',');
            }

            var key = hash.ToString();
            if (!hashToIndex.TryGetValue(key, out var index))
            {
                index = vertices.Count;
                hashToIndex[key] = index;
                vertices.Add(new Vertex(position, uv));
            }

            indices.Add(index);
        }

        var faces = new List<Face>();
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            faces.Add(new Face(vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]]));
        }

        return (vertices, faces);
    }

    private static double ComputeEdgeCollapseCost(Vertex u, Vertex v)
    {
        // If we collapse edge uv by moving u to v, how much does the model change.
        var edgeLength = v.Position.DistanceTo(u.Position);
        var curvature = 0.0;
        var sideFaces = u.Faces.Where(f => f.HasVertex(v)).ToList();

        // Use the triangle facing most away from the sides to determine the curvature term.
        foreach (var face in u.Faces)
        {
            var minCurvature = 1.0;
            foreach (var sideFace in sideFaces)
            {
                minCurvature = Math.Min(minCurvature, (1.001 - face.Normal.Dot(sideFace.Normal)) / 2);
            }

            curvature = Math.Max(curvature, minCurvature);
        }

        // Crude border preservation.
        if (sideFaces.Count < 2)
        {
            curvature = 1;
        }

        return edgeLength * curvature;
    }

    private static void ComputeEdgeCostAtVertex(Vertex v)
    {
        if (v.Neighbors.Count == 0)
        {
            v.CollapseNeighbor = null;
            v.CollapseCost = -0.01;
            return;
        }

        v.CollapseNeighbor = null;
        double totalCost = 0, minCost = 0;
        var costCount = 0;
        foreach (var neighbor in v.Neighbors)
        {
            var cost = ComputeEdgeCollapseCost(v, neighbor);
            if (v.CollapseNeighbor is null)
            {
                v.CollapseNeighbor = neighbor;
                minCost = cost;
            }

            costCount++;
            totalCost += cost;
            if (cost < minCost)
            {
                v.CollapseNeighbor = neighbor;
                minCost = cost;
            }
        }

        // The average cost of collapsing at this vertex.
        v.CollapseCost = totalCost / costCount;
    }

    private static void RemoveVertex(Vertex v, List<Vertex> vertices)
    {
        while (v.Neighbors.Count > 0)
        {
            var n = v.Neighbors[^1];
            v.Neighbors.RemoveAt(v.Neighbors.Count - 1);
            n.Neighbors.Remove(v);
        }

        vertices.Remove(v);
    }

    private static void RemoveFace(Face f, List<Face> faces)
    {
        faces.Remove(f);
        f.V1.Faces.Remove(f);
        f.V2.Faces.Remove(f);
        f.V3.Faces.Remove(f);
        Vertex[] vs = [f.V1, f.V2, f.V3];
        for (var i = 0; i < 3; i++)
        {
            vs[i].RemoveIfNonNeighbor(vs[(i + 1) % 3]);
            vs[(i + 1) % 3].RemoveIfNonNeighbor(vs[i]);
        }
    }

    private static void Collapse(List<Vertex> vertices, List<Face> faces, Vertex u, Vertex? v)
    {
        if (v is null)
        {
            // u is a vertex all by itself, so just delete it.
            RemoveVertex(u, vertices);
            return;
        }

        var neighborhood = u.Neighbors.ToList();

        // Delete triangles on edge uv.
        for (var i = u.Faces.Count - 1; i >= 0; i--)
        {
            if (i < u.Faces.Count && u.Faces[i].HasVertex(v))
            {
                RemoveFace(u.Faces[i], faces);
            }
        }

        // Update the remaining triangles to have v instead of u.
        for (var i = u.Faces.Count - 1; i >= 0; i--)
        {
            u.Faces[i].ReplaceVertex(u, v);
        }

        RemoveVertex(u, vertices);
        foreach (var vertex in neighborhood)
        {
            ComputeEdgeCostAtVertex(vertex);
        }
    }

    private static Vertex MinimumCostEdge(List<Vertex> vertices)
    {
        // O(n) per collapse, as three.js.
        var least = vertices[0];
        foreach (var vertex in vertices)
        {
            if (vertex.CollapseCost < least.CollapseCost)
            {
                least = vertex;
            }
        }

        return least;
    }

    private sealed class Vertex(Vec3 position, Vec2? uv)
    {
        public Vec3 Position { get; } = position;

        public Vec2? Uv { get; } = uv;

        public List<Face> Faces { get; } = [];

        public List<Vertex> Neighbors { get; } = [];

        public double CollapseCost { get; set; }

        public Vertex? CollapseNeighbor { get; set; }

        public void AddUniqueNeighbor(Vertex vertex)
        {
            if (!Neighbors.Contains(vertex))
            {
                Neighbors.Add(vertex);
            }
        }

        public void RemoveIfNonNeighbor(Vertex n)
        {
            if (Neighbors.Contains(n) && !Faces.Any(f => f.HasVertex(n)))
            {
                Neighbors.Remove(n);
            }
        }
    }

    private sealed class Face
    {
        public Face(Vertex v1, Vertex v2, Vertex v3)
        {
            (V1, V2, V3) = (v1, v2, v3);
            ComputeNormal();
            v1.Faces.Add(this);
            v1.AddUniqueNeighbor(v2);
            v1.AddUniqueNeighbor(v3);
            v2.Faces.Add(this);
            v2.AddUniqueNeighbor(v1);
            v2.AddUniqueNeighbor(v3);
            v3.Faces.Add(this);
            v3.AddUniqueNeighbor(v1);
            v3.AddUniqueNeighbor(v2);
        }

        public Vertex V1 { get; private set; }

        public Vertex V2 { get; private set; }

        public Vertex V3 { get; private set; }

        public Vec3 Normal { get; private set; }

        public bool HasVertex(Vertex v) => v == V1 || v == V2 || v == V3;

        public void ReplaceVertex(Vertex oldV, Vertex newV)
        {
            if (oldV == V1)
            {
                V1 = newV;
            }
            else if (oldV == V2)
            {
                V2 = newV;
            }
            else if (oldV == V3)
            {
                V3 = newV;
            }

            oldV.Faces.Remove(this);
            newV.Faces.Add(this);
            foreach (var v in (Vertex[])[V1, V2, V3])
            {
                oldV.RemoveIfNonNeighbor(v);
                v.RemoveIfNonNeighbor(oldV);
            }

            V1.AddUniqueNeighbor(V2);
            V1.AddUniqueNeighbor(V3);
            V2.AddUniqueNeighbor(V1);
            V2.AddUniqueNeighbor(V3);
            V3.AddUniqueNeighbor(V1);
            V3.AddUniqueNeighbor(V2);
            ComputeNormal();
        }

        private void ComputeNormal() => Normal = (V3.Position - V2.Position).Cross(V1.Position - V2.Position).Normalized();
    }
}
