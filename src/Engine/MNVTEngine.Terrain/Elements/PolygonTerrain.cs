using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshing;

namespace MNVTEngine.Terrain.Elements;

/// <summary>
/// A terrain patch shaped by any closed polygon. Every vertex's own height drives the surface, so dragging one vertex
/// shapes that corner and dragging the whole shape carries the surface with it. Like every cutter it carves a hole out
/// of the rectangular terrain tiles it overlaps.
/// </summary>
public sealed class PolygonTerrain : WorldElement
{
    private UVTransform _terrainUV = new();

    public PolygonTerrain(IEnumerable<Vector3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var index = 0;
        foreach (var point in points)
        {
            SetNode(index++, new WorldNode(point));
        }
    }

    public int PointCount => Nodes.Count;

    public override bool DoubleSided => true;

    public override bool CastsShadows => true;

    public Vector3 GetPoint(int index) => Nodes[index].Position;

    public void AddPoint(Vector3 point)
    {
        SetNode(Nodes.Count, new WorldNode(point));
        Update();
    }

    public bool RemoveLastPoint()
    {
        var index = Nodes.Count - 1;
        if (index < 0)
        {
            return false;
        }

        var node = Nodes[index];
        Nodes.RemoveAt(index);
        Detach(node);
        Update();
        return true;
    }

    /// <summary>Levels every vertex to their current average height (once: a later vertex drag shapes it again).</summary>
    public void FlattenToAverageHeight()
    {
        if (Nodes.Count == 0)
        {
            return;
        }

        var average = Nodes.Average(n => n.Position.Y);
        foreach (var node in Nodes)
        {
            node.Position = node.Position.WithY(average);
        }

        Update();
    }

    public override NodeBasis GetNodeBasis(int index) => new(Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);

    public override IReadOnlyList<string> GetUVGroups() => ["terrain"];

    public override UVTransform GetUVTransform(string group) => group == "terrain" ? _terrainUV.Clone() : base.GetUVTransform(group);

    public override void SetUVTransform(string group, UVTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (group != "terrain")
        {
            base.SetUVTransform(group, transform);
            return;
        }

        _terrainUV = new UVTransform
        {
            OffsetX = double.IsFinite(transform.OffsetX) ? transform.OffsetX : 0,
            OffsetY = double.IsFinite(transform.OffsetY) ? transform.OffsetY : 0,
            ScaleX = double.IsFinite(transform.ScaleX) ? Math.Max(0.1, transform.ScaleX) : 1,
            ScaleY = double.IsFinite(transform.ScaleY) ? Math.Max(0.1, transform.ScaleY) : 1,
        };
        Update();
    }

    public override IReadOnlyList<OccupiedTriangle> GetOccupiedArea() => BuildGeometry()[0].Triangles.Select(t => new OccupiedTriangle(t.A, t.B, t.C)).ToList();

    protected override List<GeometryGroup> GetGeometry() => BuildGeometry();

    /// <summary>No LOD variance: a hand-placed patch is already low enough in triangles.</summary>
    public List<GeometryGroup> GetExportGeometry(int lodIndex) => BuildGeometry();

    private List<GeometryGroup> BuildGeometry()
    {
        var positions = Nodes.Select(n => n.Position).ToList();
        if (positions.Count < 3)
        {
            return [new GeometryGroup("terrain", [])];
        }

        var points2D = positions.Select(p => p.Xz).ToList();
        var indices = ShapeUtils.TriangulateShape(points2D, []);
        if (indices.Count == 0)
        {
            return [new GeometryGroup("terrain", [])];
        }

        var minX = positions.Min(p => p.X);
        var maxX = positions.Max(p => p.X);
        var minZ = positions.Min(p => p.Z);
        var maxZ = positions.Max(p => p.Z);
        var width = Math.Max(0.0001, maxX - minX);
        var length = Math.Max(0.0001, maxZ - minZ);
        var uv = _terrainUV;
        Vector2 UvFor(Vector3 p) => new((((p.X - minX) / width) * uv.ScaleX) + uv.OffsetX, (((p.Z - minZ) / length) * uv.ScaleY) + uv.OffsetY);
        var triangles = new List<Triangle>();
        foreach (var face in indices)
        {
            var (a, b, c) = (positions[face[0]], positions[face[1]], positions[face[2]]);

            // The same winding fix the terrain mesher applies, so the patch faces like the tiles around it.
            var signedArea = (((points2D[face[1]].X - points2D[face[0]].X) * (points2D[face[2]].Y - points2D[face[0]].Y))
                - ((points2D[face[2]].X - points2D[face[0]].X) * (points2D[face[1]].Y - points2D[face[0]].Y))) / 2;
            triangles.Add(signedArea > 0 ? new Triangle(a, c, b, UvFor(a), UvFor(c), UvFor(b)) : new Triangle(a, b, c, UvFor(a), UvFor(b), UvFor(c)));
        }

        return [new GeometryGroup("terrain", triangles)];
    }

    public override ElementData Serialize(int id)
    {
        var (textures, rotations) = CollectTextureMaps();
        return new ElementData
        {
            Type = "terrainPolygon",
            Id = id,
            Nodes = [.. Nodes.Select(n => PointData.From(n.Position))],
            Textures = textures,
            TextureRotations = rotations,
            UvTransforms = new Dictionary<string, UVTransform> { ["terrain"] = _terrainUV.Clone() },
        };
    }

    public static PolygonTerrain Deserialize(ElementData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var points = data.Nodes.Count >= 3 ? data.Nodes.Select(n => n.ToVector3()).ToList() : [Vector3.Zero, new Vector3(1, 0, 0), new Vector3(0, 0, 1)];
        var polygon = new PolygonTerrain(points);
        if (data.UvTransforms?.GetValueOrDefault("terrain") is { } uv)
        {
            polygon._terrainUV = uv.Clone();
        }

        return polygon;
    }

    public override PropertyDefinition GetProperties() => new("Polygon Terrain", "★",
    [
        new PropertySection("Points", [.. Nodes.Select((node, index) => new Vector3Property($"Point {index + 1}", () => node.Position, v =>
        {
            node.Update(v);
            Update();
        }))]),
        new PropertySection("Terrain",
        [
            new ButtonProperty("Flatten Terrain", FlattenToAverageHeight),
            new NumberProperty("UV Size", () => _terrainUV.ScaleX, v =>
            {
                var size = MathUtil.Clamp(v, 0.1, 100);
                _terrainUV.ScaleX = size;
                _terrainUV.ScaleY = size;
                Update();
            }, 0.1, 100, 0.1),
        ]),
    ]);
}
