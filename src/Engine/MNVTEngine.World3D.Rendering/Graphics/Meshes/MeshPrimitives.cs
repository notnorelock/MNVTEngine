using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Meshes;

/// <summary>
/// Procedural meshes, centered on the origin with counter-clockwise front faces, outward normals, UVs in [0, 1] and
/// tangents. Seams duplicate vertices so normals and UVs stay sharp where they must.
/// </summary>
public static class MeshPrimitives
{
    /// <summary>Axis-aligned cube; each face maps the whole texture.</summary>
    public static Mesh Cube(float size = 1f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var b = new Builder();
        var h = size * 0.5f;
        // (normal, right, up) per face, with Cross(right, up) == normal so the quad is counter-clockwise from outside.
        foreach (var (n, u, v) in (ReadOnlySpan<(Vector3, Vector3, Vector3)>)
        [
            (Vector3.Right, Vector3.Forward, Vector3.Up),
            (Vector3.Left, Vector3.Back, Vector3.Up),
            (Vector3.Up, Vector3.Right, Vector3.Forward),
            (Vector3.Down, Vector3.Right, Vector3.Back),
            (Vector3.Back, Vector3.Right, Vector3.Up),
            (Vector3.Forward, Vector3.Left, Vector3.Up),
        ])
        {
            b.Grid(n * h, u * h, v * h, n, 1, 1);
        }

        return b.Build("Cube");
    }

    /// <summary>Flat grid in the XZ plane facing +Y.</summary>
    public static Mesh Plane(float width = 10f, float depth = 10f, int subdivisions = 10)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subdivisions);
        var b = new Builder();
        b.Grid(Vector3.Zero, Vector3.Right * (width * 0.5f), Vector3.Forward * (depth * 0.5f), Vector3.Up, subdivisions, subdivisions);
        return b.Build("Plane");
    }

    /// <summary>Single quad in the XY plane facing +Z (towards a default camera).</summary>
    public static Mesh Quad(float width = 1f, float height = 1f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var b = new Builder();
        b.Grid(Vector3.Zero, Vector3.Right * (width * 0.5f), Vector3.Up * (height * 0.5f), Vector3.Back, 1, 1);
        return b.Build("Quad");
    }

    /// <summary>UV sphere: <paramref name="segments"/> around Y, <paramref name="rings"/> from pole to pole.</summary>
    public static Mesh Sphere(float radius = 0.5f, int segments = 32, int rings = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        ArgumentOutOfRangeException.ThrowIfLessThan(segments, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(rings, 2);
        var phis = Enumerable.Range(0, rings + 1).Select(r => (Phi: MathF.PI * r / rings, Offset: 0f)).ToArray();
        return Lathe("Sphere", radius, segments, phis, radius * 2f);
    }

    /// <summary>Capsule along Y: <paramref name="height"/> is the total height (at least the diameter).</summary>
    public static Mesh Capsule(float radius = 0.5f, float height = 2f, int segments = 32, int hemisphereRings = 8)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, radius * 2f);
        ArgumentOutOfRangeException.ThrowIfLessThan(segments, 3);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hemisphereRings);
        var half = (height * 0.5f) - radius;
        var top = Enumerable.Range(0, hemisphereRings + 1).Select(k => (Phi: MathF.PI * 0.5f * k / hemisphereRings, Offset: half));
        var bottom = Enumerable.Range(0, hemisphereRings + 1).Select(k => (Phi: MathF.PI * 0.5f * (1f + ((float)k / hemisphereRings)), Offset: -half));
        return Lathe("Capsule", radius, segments, [.. top, .. bottom], height);
    }

    /// <summary>Cylinder along Y with caps.</summary>
    public static Mesh Cylinder(float radius = 0.5f, float height = 2f, int segments = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(segments, 3);
        var b = new Builder();
        var h = height * 0.5f;
        var start = b.Count;
        for (var s = 0; s <= segments; s++)
        {
            var (sin, cos) = MathF.SinCos(MathF.Tau * s / segments);
            var n = new Vector3(sin, 0f, cos);
            b.Add(new Vector3(sin * radius, -h, cos * radius), n, new Vector2((float)s / segments, 0f));
            b.Add(new Vector3(sin * radius, h, cos * radius), n, new Vector2((float)s / segments, 1f));
        }

        for (var s = 0; s < segments; s++)
        {
            int a = start + (s * 2), d = a + 1, bb = a + 2, c = a + 3; // bottom-left, top-left, bottom-right, top-right
            b.Triangle(a, bb, c);
            b.Triangle(a, c, d);
        }

        b.Disc(new Vector3(0f, h, 0f), radius, segments, up: true);
        b.Disc(new Vector3(0f, -h, 0f), radius, segments, up: false);
        return b.Build("Cylinder");
    }

    /// <summary>Cone along Y: base at -height/2, apex at +height/2, with a base cap.</summary>
    public static Mesh Cone(float radius = 0.5f, float height = 1f, int segments = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThan(segments, 3);
        var b = new Builder();
        var h = height * 0.5f;
        Vector3 SideNormal(float angle)
        {
            var (sin, cos) = MathF.SinCos(angle);
            return new Vector3(sin * height, radius, cos * height).Normalized;
        }

        for (var s = 0; s < segments; s++)
        {
            float a0 = MathF.Tau * s / segments, a1 = MathF.Tau * (s + 1) / segments;
            var (s0, c0) = MathF.SinCos(a0);
            var (s1, c1) = MathF.SinCos(a1);
            var i = b.Count;
            b.Add(new Vector3(s0 * radius, -h, c0 * radius), SideNormal(a0), new Vector2((float)s / segments, 0f));
            b.Add(new Vector3(s1 * radius, -h, c1 * radius), SideNormal(a1), new Vector2((float)(s + 1) / segments, 0f));
            b.Add(new Vector3(0f, h, 0f), SideNormal((a0 + a1) * 0.5f), new Vector2((s + 0.5f) / segments, 1f)); // one apex per face: smooth sides
            b.Triangle(i, i + 1, i + 2);
        }

        b.Disc(new Vector3(0f, -h, 0f), radius, segments, up: false);
        return b.Build("Cone");
    }

    /// <summary>Surface of revolution around Y through rows of (polar angle from +Y, vertical offset).</summary>
    private static Mesh Lathe(string name, float radius, int segments, (float Phi, float Offset)[] rows, float height)
    {
        var b = new Builder();
        foreach (var (phi, offset) in rows)
        {
            var (sinPhi, cosPhi) = MathF.SinCos(phi);
            for (var s = 0; s <= segments; s++)
            {
                var (sin, cos) = MathF.SinCos(MathF.Tau * s / segments);
                var n = new Vector3(sinPhi * sin, cosPhi, sinPhi * cos);
                var p = (n * radius) + new Vector3(0f, offset, 0f);
                b.Add(p, n, new Vector2((float)s / segments, (p.Y + (height * 0.5f)) / height));
            }
        }

        var stride = segments + 1;
        for (var r = 0; r + 1 < rows.Length; r++)
        {
            for (var s = 0; s < segments; s++)
            {
                int a = (r * stride) + s, d = a + 1, bb = a + stride, c = bb + 1; // top-left, top-right, bottom-left, bottom-right
                if (r + 2 < rows.Length)
                {
                    b.Triangle(a, bb, c); // skipped on the last band: bb and c are the same pole point
                }

                if (r > 0)
                {
                    b.Triangle(a, c, d); // skipped on the first band: a and d are the same pole point
                }
            }
        }

        return b.Build(name);
    }

    private sealed class Builder
    {
        private readonly List<Vector3> _positions = [];
        private readonly List<Vector3> _normals = [];
        private readonly List<Vector2> _uvs = [];
        private readonly List<uint> _indices = [];

        public int Count => _positions.Count;

        public void Add(Vector3 position, Vector3 normal, Vector2 uv)
        {
            _positions.Add(position);
            _normals.Add(normal);
            _uvs.Add(uv);
        }

        public void Triangle(int a, int b, int c) => _indices.AddRange([(uint)a, (uint)b, (uint)c]);

        /// <summary>Grid spanning center ± right ± up; Cross(right, up) must point along <paramref name="normal"/>.</summary>
        public void Grid(Vector3 center, Vector3 right, Vector3 up, Vector3 normal, int columns, int rows)
        {
            var start = Count;
            for (var j = 0; j <= rows; j++)
            {
                for (var i = 0; i <= columns; i++)
                {
                    float u = (float)i / columns, v = (float)j / rows;
                    Add(center + (right * ((u * 2f) - 1f)) + (up * ((v * 2f) - 1f)), normal, new Vector2(u, v));
                }
            }

            for (var j = 0; j < rows; j++)
            {
                for (var i = 0; i < columns; i++)
                {
                    var a = start + (j * (columns + 1)) + i;
                    int b = a + 1, d = a + columns + 1, c = d + 1;
                    Triangle(a, b, c);
                    Triangle(a, c, d);
                }
            }
        }

        /// <summary>Flat cap facing +Y (or -Y) around <paramref name="center"/>.</summary>
        public void Disc(Vector3 center, float radius, int segments, bool up)
        {
            var normal = up ? Vector3.Up : Vector3.Down;
            var middle = Count;
            Add(center, normal, new Vector2(0.5f, 0.5f));
            for (var s = 0; s <= segments; s++)
            {
                var (sin, cos) = MathF.SinCos(MathF.Tau * s / segments);
                Add(center + new Vector3(sin * radius, 0f, cos * radius), normal, new Vector2(0.5f + (sin * 0.5f), 0.5f + (cos * (up ? -0.5f : 0.5f))));
            }

            for (var s = 0; s < segments; s++)
            {
                int a = middle + 1 + s, b = a + 1;
                if (up)
                {
                    Triangle(middle, a, b);
                }
                else
                {
                    Triangle(middle, b, a);
                }
            }
        }

        public Mesh Build(string name)
        {
            var mesh = new Mesh(name)
            {
                Positions = [.. _positions],
                Normals = [.. _normals],
                UV0 = [.. _uvs],
            };
            mesh.SetIndices([.. _indices]);
            mesh.RecalculateTangents();
            return mesh;
        }
    }
}
