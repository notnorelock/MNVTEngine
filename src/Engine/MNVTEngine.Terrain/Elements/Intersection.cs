using MNVTEngine.Mathematics;
using System.Globalization;
using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshing;

namespace MNVTEngine.Terrain.Elements;

/// <summary>
/// A fixed-footprint T junction or crossroads: a centre with three or four outlets (west, east, north, south; the
/// rotation picks which side a T junction leaves closed), optional L-shaped sidewalks, and rigid outlet mouths roads
/// connect to. Connections never change its geometry.
/// </summary>
public sealed class Intersection : WorldElement
{
    private const double Eps = 1e-7;

    /// <summary>A 3-way junction uses west, east and north.</summary>
    private static readonly Vector2[] Normals = [new(-1, 0), new(1, 0), new(0, -1), new(0, 1)];
    private static readonly string[] Labels = ["West", "East", "North", "South"];
    private static readonly SelectOption[] RotationOptions = [new("0°", "0"), new("90°", "90"), new("180°", "180"), new("270°", "270")];
    private Vector3 _center;
    private int _nodeCount;

    private sealed record Rect2(double MinX, double MaxX, double MinZ, double MaxZ);

    public Intersection(Vector3 position, int nodeCount = 4)
    {
        _center = position;
        _nodeCount = ClampNodeCount(nodeCount);
        RebuildNodes();
    }

    public double Width { get; set; } = 8;

    public double Length { get; set; } = 8;

    public double OutletWidth { get; set; } = 4;

    public double OutletLength { get; set; } = 2;

    public double Rotation { get; set; }

    /// <summary>none or sidewalk.</summary>
    public string EdgeType { get; set; } = "none";

    public double SidewalkWidth { get; set; } = 1;

    public double CurbHeight { get; set; } = 0.15;

    public double RoadTexWidth { get; set; } = 3;

    public double RoadTexHeight { get; set; } = 3;

    public double RoadTexOffsetX { get; set; }

    public double RoadTexOffsetY { get; set; }

    public double SidewalkTexWidth { get; set; } = 1;

    public double SidewalkTexHeight { get; set; } = 1;

    public double SidewalkTexOffsetX { get; set; }

    public double SidewalkTexOffsetY { get; set; }

    public Vector3 Center => _center;

    public int OutletCount
    {
        get => _nodeCount;
        set
        {
            var next = ClampNodeCount(value);
            if (next == _nodeCount)
            {
                return;
            }

            _nodeCount = next;
            RebuildNodes();
            RefreshLayout();
        }
    }

    private static int ClampNodeCount(int value) => value <= 3 ? 3 : 4;

    private double EffectiveOutletWidth => MathUtil.Clamp(OutletWidth, 0.2, Math.Min(Width, Length));

    public override double GetWidth() => EffectiveOutletWidth;

    public override double GetSidewalkWidth() => EdgeType == "sidewalk" ? SidewalkWidth : 0;

    public override double GetCurbHeight() => EdgeType == "sidewalk" ? CurbHeight : 0;

    public override ConnectionProfile GetFixedConnectionProfile(int index) => new(EffectiveOutletWidth / 2, GetSidewalkWidth(), GetCurbHeight());

    public override IReadOnlyList<string> GetUVGroups() => EdgeType == "sidewalk" ? ["road", "sidewalk"] : ["road"];

    public override UVTransform GetUVTransform(string group) => group switch
    {
        "road" => new UVTransform { OffsetX = RoadTexOffsetX, OffsetY = RoadTexOffsetY, ScaleX = RoadTexWidth, ScaleY = RoadTexHeight },
        "sidewalk" => new UVTransform { OffsetX = SidewalkTexOffsetX, OffsetY = SidewalkTexOffsetY, ScaleX = SidewalkTexWidth, ScaleY = SidewalkTexHeight },
        _ => base.GetUVTransform(group),
    };

    public override void SetUVTransform(string group, UVTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (group == "road")
        {
            (RoadTexOffsetX, RoadTexOffsetY, RoadTexWidth, RoadTexHeight) = (transform.OffsetX, transform.OffsetY, Math.Max(0.1, transform.ScaleX), Math.Max(0.1, transform.ScaleY));
        }
        else if (group == "sidewalk")
        {
            (SidewalkTexOffsetX, SidewalkTexOffsetY, SidewalkTexWidth, SidewalkTexHeight) = (transform.OffsetX, transform.OffsetY, Math.Max(0.1, transform.ScaleX), Math.Max(0.1, transform.ScaleY));
        }
        else
        {
            return;
        }

        base.Update();
    }

    private Vector3 LocalToWorld(Vector2 point, double height = 0) =>
        (Quaternion.AngleAxis((float)Rotation, Vector3.UnitY) * new Vector3(point.X, height, point.Y)) + _center;

    private Vector2 OutletLocal(int index)
    {
        var hx = Width / 2;
        var hz = Length / 2;
        return index switch
        {
            0 => new Vector2(-hx - OutletLength, 0),
            1 => new Vector2(hx + OutletLength, 0),
            2 => new Vector2(0, -hz - OutletLength),
            _ => new Vector2(0, hz + OutletLength),
        };
    }

    private Vector3 OutletWorld(int index) => LocalToWorld(OutletLocal(index));

    private void RebuildNodes()
    {
        DisconnectAll();
        foreach (var node in Nodes.ToList())
        {
            Detach(node);
        }

        Nodes = [];
        for (var index = 0; index < _nodeCount; index++)
        {
            SetNode(index, new WorldNode(OutletWorld(index)));
        }
    }

    private bool SyncNodes()
    {
        var changed = false;
        for (var index = 0; index < _nodeCount; index++)
        {
            var target = OutletWorld(index);
            if (Nodes[index].Position.DistanceToSquared(target) <= Eps * Eps)
            {
                continue;
            }

            Nodes[index].Position = target;
            changed = true;
        }

        return changed;
    }

    private void RefreshLayout()
    {
        SyncNodes();
        base.Update();
        foreach (var connection in Connections.Values.ToList())
        {
            connection.Element.Update();
        }
    }

    /// <summary>Accepts a rigid whole-element transform (the gizmo) but rejects deforming one outlet.</summary>
    public override void Update()
    {
        var expected = Enumerable.Range(0, Nodes.Count).Select(OutletWorld).ToList();
        var actual = Nodes.Select(n => n.Position).ToList();
        if (expected.Where((point, index) => point.DistanceToSquared(actual[index]) > Eps * Eps).Any())
        {
            var expectedMean = expected.Aggregate(Vector3.Zero, (sum, p) => sum + p) / expected.Count;
            var actualMean = actual.Aggregate(Vector3.Zero, (sum, p) => sum + p) / actual.Count;
            double dot = 0, cross = 0;
            for (var index = 0; index < expected.Count; index++)
            {
                var (ex, ez) = (expected[index].X - expectedMean.X, expected[index].Z - expectedMean.Z);
                var (ax, az) = (actual[index].X - actualMean.X, actual[index].Z - actualMean.Z);
                dot += (ex * ax) + (ez * az);
                cross += (ex * az) - (ez * ax);
            }

            var deltaAngle = Math.Atan2(-cross, dot);
            var (oldCenter, oldRotation) = (_center, Rotation);
            var meanOffset = Quaternion.AngleAxis((float)MathUtil.RadToDeg(deltaAngle), Vector3.UnitY) * (expectedMean - _center);
            _center = actualMean - meanOffset;
            Rotation += MathUtil.RadToDeg(deltaAngle);
            var rigid = actual.Select((point, index) => point.DistanceToSquared(OutletWorld(index)) < 1e-8).All(ok => ok);
            if (!rigid)
            {
                (_center, Rotation) = (oldCenter, oldRotation);
            }
        }

        var corrected = SyncNodes();
        base.Update();
        if (corrected)
        {
            foreach (var connection in Connections.Values.ToList())
            {
                connection.Element.Update();
            }
        }
    }

    public override NodeBasis GetNodeBasis(int index)
    {
        var normal = Normals[index];
        var forward = Quaternion.AngleAxis((float)Rotation, Vector3.UnitY) * new Vector3(normal.X, 0, normal.Y);
        return new NodeBasis(forward, forward.Cross(Vector3.UnitY).Normalized, Vector3.UnitY);
    }

    public void ConnectWith(int thisNodeIndex, WorldElement other, int otherNodeIndex) => Connect(thisNodeIndex, other, otherNodeIndex);

    public override IReadOnlyList<OccupiedTriangle> GetOccupiedArea() =>
        GetGeometry().SelectMany(g => g.Triangles)
            .Where(t => Math.Abs(((t.B.X - t.A.X) * (t.C.Z - t.A.Z)) - ((t.B.Z - t.A.Z) * (t.C.X - t.A.X))) > 1e-8)
            .Select(t => new OccupiedTriangle(t.A, t.B, t.C)).ToList();

    public override ElementData Serialize(int id)
    {
        var (textures, rotations) = CollectTextureMaps();
        return new ElementData
        {
            Type = "intersection",
            Id = id,
            Nodes = [PointData.From(_center)],
            Textures = textures,
            TextureRotations = rotations,
            Width = Width,
            Length = Length,
            Rotation = Rotation,
            NodeCount = OutletCount,
            OutletWidth = OutletWidth,
            OutletLength = OutletLength,
            EdgeType = EdgeType,
            SidewalkWidth = SidewalkWidth,
            CurbHeight = CurbHeight,
            RoadTexWidth = RoadTexWidth,
            RoadTexHeight = RoadTexHeight,
            RoadTexOffsetX = RoadTexOffsetX,
            RoadTexOffsetY = RoadTexOffsetY,
            SidewalkTexWidth = SidewalkTexWidth,
            SidewalkTexHeight = SidewalkTexHeight,
            SidewalkTexOffsetX = SidewalkTexOffsetX,
            SidewalkTexOffsetY = SidewalkTexOffsetY,
        };
    }

    public static Intersection Deserialize(ElementData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var center = data.Nodes.Aggregate(Vector3.Zero, (sum, node) => sum + node.ToVector3()) / Math.Max(1, data.Nodes.Count);
        var value = new Intersection(center, data.NodeCount ?? 4)
        {
            Width = data.Width ?? 8,
            Length = data.Length ?? 8,
            Rotation = data.Rotation ?? 0,
        };
        value.OutletWidth = data.OutletWidth ?? Math.Min(value.Width, Math.Min(value.Length, 4));
        value.OutletLength = data.OutletLength ?? 2;
        value.EdgeType = data.EdgeType ?? "none";
        value.SidewalkWidth = data.SidewalkWidth ?? 1;
        value.CurbHeight = data.CurbHeight ?? 0.15;
        value.RoadTexWidth = data.RoadTexWidth ?? 3;
        value.RoadTexHeight = data.RoadTexHeight ?? 3;
        value.RoadTexOffsetX = data.RoadTexOffsetX ?? 0;
        value.RoadTexOffsetY = data.RoadTexOffsetY ?? 0;
        value.SidewalkTexWidth = data.SidewalkTexWidth ?? 1;
        value.SidewalkTexHeight = data.SidewalkTexHeight ?? 1;
        value.SidewalkTexOffsetX = data.SidewalkTexOffsetX ?? 0;
        value.SidewalkTexOffsetY = data.SidewalkTexOffsetY ?? 0;
        value.RefreshLayout();
        return value;
    }

    public override PropertyDefinition GetProperties()
    {
        NumberProperty Number(string label, Func<double> get, Action<double> set, double min, double step = 0.1) => new(label, get, set, min, null, step);
        SelectProperty RotationProperty(string label, string group) => new(label,
            RotationOptions,
            () => Js.Num(TextureRotations.GetValueOrDefault(group)),
            v => SetTextureRotation(group, double.Parse(v, CultureInfo.InvariantCulture)));
        var edges = new List<PropertyItem>
        {
            new SelectProperty("Type", [new("None", "none"), new("Sidewalk", "sidewalk")], () => EdgeType, v =>
            {
                EdgeType = v;
                RefreshLayout();
                OnPropertiesChanged?.Invoke();
            }),
        };
        if (EdgeType == "sidewalk")
        {
            edges.Add(Number("Sidewalk Width", () => SidewalkWidth, v => Layout(() => SidewalkWidth = Math.Max(0.1, v)), 0.1));
            edges.Add(Number("Curb Height", () => CurbHeight, v => Layout(() => CurbHeight = Math.Max(0, v)), 0, 0.05));
        }

        var sections = new List<PropertySection>
        {
            new("Intersection",
            [
                new Vector3Property("Position", () => _center, v => Layout(() => _center = v)),
                new SelectProperty("Outlets", [new("3 (T junction)", "3"), new("4 (crossroads)", "4")], () => OutletCount.ToString(CultureInfo.InvariantCulture), v =>
                {
                    OutletCount = int.Parse(v, CultureInfo.InvariantCulture);
                    OnPropertiesChanged?.Invoke();
                }),
                Number("Center Width", () => Width, v => Layout(() => Width = Math.Max(0.5, v)), 0.5),
                Number("Center Length", () => Length, v => Layout(() => Length = Math.Max(0.5, v)), 0.5),
                Number("Outlet Width", () => OutletWidth, v => Layout(() => OutletWidth = Math.Max(0.2, v)), 0.2),
                Number("Outlet Length", () => OutletLength, v => Layout(() => OutletLength = Math.Max(0.1, v)), 0.1),
                Number("Rotation", () => Rotation, v => Layout(() => Rotation = v), -360, 1),
            ]),
            new("Edges", edges),
            new("Textures", [RotationProperty("Road Rot.", "road"), RotationProperty("Sidewalk Rot.", "sidewalk")]),
        };
        for (var index = 0; index < Nodes.Count; index++)
        {
            var i = index;
            sections.Add(new PropertySection($"Outlet {Labels[i]}",
            [
                new ButtonProperty(IsConnected(i) ? "Disconnect" : "Not connected", () =>
                {
                    if (IsConnected(i))
                    {
                        Disconnect(i);
                    }

                    OnPropertiesChanged?.Invoke();
                }),
            ]));
        }

        return new PropertyDefinition("Intersection", "⬭", sections);
    }

    private void Layout(Action change)
    {
        change();
        RefreshLayout();
    }

    public List<GeometryGroup> GetExportGeometry(int lodIndex) => GetGeometry();

    private List<Rect2> FootprintRects()
    {
        var hx = Width / 2;
        var hz = Length / 2;
        var outletHalf = EffectiveOutletWidth / 2;
        var tipX = hx + OutletLength;
        var tipZ = hz + OutletLength;

        // The asphalt follows the sidewalk corners' inner edges: one horizontal and one vertical corridor. A large
        // centre rectangle would protrude into every outer corner beyond the L-shaped sidewalks.
        return
        [
            new Rect2(-tipX, tipX, -outletHalf, outletHalf),
            new Rect2(-outletHalf, outletHalf, -tipZ, _nodeCount == 4 ? tipZ : outletHalf),
        ];
    }

    /// <summary>The rectangle union split on its own coordinate grid: a deterministic cell mesh.</summary>
    private List<Rect2> Cells()
    {
        var rects = FootprintRects();
        var xs = rects.SelectMany(r => new[] { r.MinX, r.MaxX }).Distinct().Order().ToList();
        var zs = rects.SelectMany(r => new[] { r.MinZ, r.MaxZ }).Distinct().Order().ToList();
        var cells = new List<Rect2>();
        for (var x = 0; x < xs.Count - 1; x++)
        {
            for (var z = 0; z < zs.Count - 1; z++)
            {
                var cell = new Rect2(xs[x], xs[x + 1], zs[z], zs[z + 1]);
                var mx = (cell.MinX + cell.MaxX) / 2;
                var mz = (cell.MinZ + cell.MaxZ) / 2;
                if (rects.Any(r => mx > r.MinX - Eps && mx < r.MaxX + Eps && mz > r.MinZ - Eps && mz < r.MaxZ + Eps))
                {
                    cells.Add(cell);
                }
            }
        }

        return cells;
    }

    private static void AddTriangle(List<Triangle> output, Vector3 a, Vector3 b, Vector3 c, Vector2 uvA, Vector2 uvB, Vector2 uvC)
    {
        var normal = (b - a).Cross(c - a);
        output.Add(normal.Y < -Eps ? new Triangle(a, c, b, uvA, uvC, uvB) : new Triangle(a, b, c, uvA, uvB, uvC));
    }

    private void AddSidewalkStrip(List<Triangle> output, Vector2 innerA2, Vector2 innerB2, Vector2 outerA2, Vector2 outerB2)
    {
        var innerLength = innerA2.DistanceTo(innerB2);
        var outerLength = outerA2.DistanceTo(outerB2);
        if (innerLength < Eps || outerLength < Eps)
        {
            return;
        }

        var u0 = SidewalkTexOffsetX;
        var curbU = u0 + (CurbHeight / SidewalkTexWidth);
        var outerU = u0 + (SidewalkWidth / SidewalkTexWidth);
        var v0 = SidewalkTexOffsetY;
        var v1 = v0 + (innerLength / SidewalkTexHeight);
        var innerA = LocalToWorld(innerA2);
        var innerB = LocalToWorld(innerB2);
        var innerAUp = LocalToWorld(innerA2, CurbHeight);
        var innerBUp = LocalToWorld(innerB2, CurbHeight);
        var outerA = LocalToWorld(outerA2, CurbHeight);
        var outerB = LocalToWorld(outerB2, CurbHeight);
        var tangent = innerB - innerA;
        var outward = outerA - innerAUp;
        if (tangent.Cross(Vector3.UnitY).Dot(outward) <= 0)
        {
            AddTriangle(output, innerA, innerB, innerBUp, new(u0, v0), new(u0, v1), new(curbU, v1));
            AddTriangle(output, innerA, innerBUp, innerAUp, new(u0, v0), new(curbU, v1), new(curbU, v0));
        }
        else
        {
            AddTriangle(output, innerB, innerA, innerAUp, new(u0, v1), new(u0, v0), new(curbU, v0));
            AddTriangle(output, innerB, innerAUp, innerBUp, new(u0, v1), new(curbU, v0), new(curbU, v1));
        }

        if (innerLength > outerLength + Eps)
        {
            // A mitred elbow has a longer inner than outer edge: a rectangular strip plus one small 45-degree corner
            // triangle, instead of shearing the texture over the whole arm.
            var ratio = outerLength / innerLength;
            var tangent2 = innerA2 + ((innerB2 - innerA2) * ratio);
            var tangentUp = LocalToWorld(tangent2, CurbHeight);
            var tangentV = v0 + (outerLength / SidewalkTexHeight);
            AddTriangle(output, innerAUp, tangentUp, outerB, new(u0, v0), new(u0, tangentV), new(outerU, tangentV));
            AddTriangle(output, innerAUp, outerB, outerA, new(u0, v0), new(outerU, tangentV), new(outerU, v0));
            AddTriangle(output, tangentUp, innerBUp, outerB, new(u0, tangentV), new(u0, v1), new(outerU, tangentV));
        }
        else
        {
            AddTriangle(output, innerAUp, innerBUp, outerB, new(u0, v0), new(u0, v1), new(outerU, v1));
            AddTriangle(output, innerAUp, outerB, outerA, new(u0, v0), new(outerU, v1), new(outerU, v0));
        }
    }

    /// <summary>One clean L-shaped corner joining two perpendicular sidewalks at their shared mitre.</summary>
    private void AddSidewalkCorner(List<Triangle> output, int sideA, int sideB)
    {
        var (normalA, normalB) = (Normals[sideA], Normals[sideB]);
        var (nodeA, nodeB) = (OutletLocal(sideA), OutletLocal(sideB));
        var halfWidth = EffectiveOutletWidth / 2;
        var outerDistance = halfWidth + SidewalkWidth;
        var innerA = nodeA + (normalB * halfWidth);
        var innerB = nodeB + (normalA * halfWidth);
        var innerMitre = (normalA + normalB) * halfWidth;
        var outerA = nodeA + (normalB * outerDistance);
        var outerB = nodeB + (normalA * outerDistance);
        var outerMitre = (normalA + normalB) * outerDistance;
        AddSidewalkStrip(output, innerA, innerMitre, outerA, outerMitre);

        // Both strips start at their road mouths, so only the final one-width triangle gets the mitre UVs.
        AddSidewalkStrip(output, innerB, innerMitre, outerB, outerMitre);
    }

    private void AddThreeWayClosedSidewalk(List<Triangle> output)
    {
        var south = Normals[3];
        var west = OutletLocal(0);
        var east = OutletLocal(1);
        var halfWidth = EffectiveOutletWidth / 2;
        var outerDistance = halfWidth + SidewalkWidth;
        AddSidewalkStrip(output, west + (south * halfWidth), east + (south * halfWidth), west + (south * outerDistance), east + (south * outerDistance));
    }

    protected override List<GeometryGroup> GetGeometry()
    {
        var road = new List<Triangle>();
        var sidewalk = new List<Triangle>();
        Vector2 RoadUV(Vector3 point) => new(((point.X - _center.X) / RoadTexWidth) + RoadTexOffsetX, ((point.Z - _center.Z) / RoadTexHeight) + RoadTexOffsetY);
        foreach (var cell in Cells())
        {
            var nw = LocalToWorld(new Vector2(cell.MinX, cell.MinZ));
            var ne = LocalToWorld(new Vector2(cell.MaxX, cell.MinZ));
            var se = LocalToWorld(new Vector2(cell.MaxX, cell.MaxZ));
            var sw = LocalToWorld(new Vector2(cell.MinX, cell.MaxZ));
            AddTriangle(road, nw, ne, se, RoadUV(nw), RoadUV(ne), RoadUV(se));
            AddTriangle(road, nw, se, sw, RoadUV(nw), RoadUV(se), RoadUV(sw));
        }

        if (EdgeType == "sidewalk")
        {
            AddSidewalkCorner(sidewalk, 0, 2); // north-west
            AddSidewalkCorner(sidewalk, 2, 1); // north-east
            if (_nodeCount == 4)
            {
                AddSidewalkCorner(sidewalk, 1, 3); // south-east
                AddSidewalkCorner(sidewalk, 3, 0); // south-west
            }
            else
            {
                AddThreeWayClosedSidewalk(sidewalk);
            }
        }

        var groups = new List<GeometryGroup> { new("road", road) };
        if (sidewalk.Count > 0)
        {
            groups.Add(new GeometryGroup("sidewalk", sidewalk));
        }

        return groups;
    }
}
