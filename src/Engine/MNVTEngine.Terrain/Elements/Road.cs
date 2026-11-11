using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshing;

namespace MNVTEngine.Terrain.Elements;

/// <summary>
/// A curved road segment: lanes with an optional crown, sidewalks with curbs or bridge edges (solid walls, planes,
/// spaced walls with caps, a steel truss), and an optional bridge deck on pillars that reach the terrain. Its ends
/// connect to other roads and intersections, blending width, sidewalks, curbs, crown and deck thickness.
/// </summary>
public sealed class Road : SplineElement
{
    private static readonly string[] UvGroupNames = ["road", "sidewalk", "bridgeEdges", "bridgeDeck", "bridgePillars"];

    /// <summary>Bridge pillars sink this far below the sampled terrain so a slope between samples never pokes through.</summary>
    private const double PillarEmbedDepth = 0.5;

    private readonly Dictionary<string, UVTransform> _uvTransforms = UvGroupNames.ToDictionary(g => g, _ => new UVTransform(), StringComparer.Ordinal);
    private SurfaceHeightSampler? _terrainSampler;

    public Road(Vector3 positionA, Vector3 positionB)
        : base(positionA, positionB, NodeKind.Node)
    {
    }

    private sealed record GeometryLineContext(double HalfWidth, double SidewalkWidth, double CurbHeight, double DeckThickness,
        double LeftDistance, double RightDistance, double CenterDistance, double CenterTotal, double CrownHeight);

    private sealed record EdgeData(List<Vector3> Points, List<Vector3> RightVecs, List<double> HalfWidths, List<double> LeftCumDist,
        List<double> RightCumDist, List<double> CenterCumDist, double CenterTotal);

    public double Width { get; set; } = 3;

    public int Lanes { get; set; } = 2;

    /// <summary>none, sidewalk or bridge.</summary>
    public string EdgeType { get; set; } = "none";

    public double SidewalkWidth { get; set; } = 1;

    public double CurbHeight { get; set; } = 0.15;

    public double RoadTexStretch { get; set; } = 1;

    public double SidewalkTexStretch { get; set; } = 1;

    public double RoadCrown { get; set; }

    /// <summary>solid, plane, spaced or truss.</summary>
    public string BridgeEdgeStyle { get; set; } = "solid";

    public double BridgeEdgeHeight { get; set; } = 0.8;

    public double BridgeEdgeWidth { get; set; } = 0.2;

    public double BridgeEdgeDistance { get; set; } = 3;

    public double BridgeEdgeLength { get; set; } = 1;

    public bool BridgeEdgeCapEnabled { get; set; }

    public double BridgeEdgeCapHeight { get; set; } = 0.12;

    public double BridgeEdgeCapWidth { get; set; } = 0.36;

    public bool BridgeEdgeCapJoined { get; set; }

    public bool BridgeEnabled { get; set; }

    /// <summary>box or circular.</summary>
    public string BridgePillarShape { get; set; } = "box";

    public int BridgePillarSegments { get; set; } = 12;

    public double BridgePillarDistance { get; set; } = 8;

    public int BridgePillarCount { get; set; } = 2;

    public double BridgePillarWidth { get; set; } = 0.8;

    public double BridgePillarInset { get; set; } = 0.4;

    public double BridgeDeckThickness { get; set; } = 0.5;

    protected override int MinimumCurveDivisions => 1;

    public override double GetWidth() => Width;

    public override double GetSidewalkWidth() => EdgeType == "sidewalk" ? SidewalkWidth : 0;

    public override double GetCurbHeight() => EdgeType == "sidewalk" ? CurbHeight : 0;

    public override bool DependsOnTerrainSurface() => BridgeEnabled;

    /// <summary>Ground-level roads need not shadow the terrain; bridges do.</summary>
    public override bool CastsShadows => BridgeEnabled;

    public override MaterialHint? GetMaterialHint(string groupName) =>
        groupName == "bridgeEdges" && EdgeType == "bridge" && BridgeEdgeStyle == "plane" ? new MaterialHint(DoubleSided: true) : null;

    /// <summary>Moving a curve handle reshapes every road joined to this one (their blended ends follow).</summary>
    public override IReadOnlyList<WorldElement> GetAffectedElementsForNode(WorldNode node)
    {
        var affected = new List<WorldElement>(base.GetAffectedElementsForNode(node));
        if (node != CurvePointA && node != CurvePointB)
        {
            return affected;
        }

        var queue = new Queue<WorldElement>([this]);
        while (queue.TryDequeue(out var element))
        {
            if (affected.Contains(element) && element != this)
            {
                continue;
            }

            if (!affected.Contains(element))
            {
                affected.Add(element);
            }

            foreach (var connection in element.Connections.Values)
            {
                if (!affected.Contains(connection.Element))
                {
                    queue.Enqueue(connection.Element);
                }
            }
        }

        return affected;
    }

    public override IReadOnlyList<GuideLine> GetGuideLines()
    {
        var lines = new List<GuideLine>(base.GetGuideLines());
        if (CurvePointA is null || CurvePointB is null)
        {
            return lines;
        }

        // Lane boundary lines: edges (white) and interior dividers (grey).
        var points = GetBezierCurvePoints();
        var halfWidthStart = GetResolvedHalfWidth(0);
        var halfWidthEnd = GetResolvedHalfWidth(1);
        var laneCount = Math.Max(1, Lanes);
        for (var b = 0; b <= laneCount; b++)
        {
            var fraction = b / (double)laneCount;
            var offsetPoints = new List<Vector3>();
            for (var i = 0; i < points.Count; i++)
            {
                var t = i / (double)(points.Count - 1);
                var hw = halfWidthStart + ((halfWidthEnd - halfWidthStart) * t);
                var offset = -hw + (2 * hw * fraction);
                offsetPoints.Add(points[i] + (RightAt(points, i) * offset) + new Vector3(0, LaneHeightAtFraction(fraction, CrownAt(t)), 0));
            }

            var isEdge = b == 0 || b == laneCount;
            lines.Add(new GuideLine(offsetPoints, NodeKind.Node, isEdge ? 0xffffffu : 0x888888u));
        }

        return lines;
    }

    private Vector3 RightAt(List<Vector3> points, int index)
    {
        if (index == 0)
        {
            return GetResolvedNodeBasis(0).Right;
        }

        if (index == points.Count - 1)
        {
            return GetResolvedNodeBasis(1).Right;
        }

        return (points[index + 1] - points[index - 1]).Normalized.Cross(Vector3.UnitY).Normalized;
    }

    private double GetEndCrown(int nodeIndex)
    {
        if (!Connections.TryGetValue(nodeIndex, out var connection))
        {
            return RoadCrown;
        }

        // Average both crowns so each road produces the same junction height.
        return connection.Element is Road other ? (RoadCrown + other.RoadCrown) / 2 : 0;
    }

    private double GetResolvedBridgeDeckThickness(int nodeIndex) =>
        Connections.TryGetValue(nodeIndex, out var connection) && connection.Element is Road { BridgeEnabled: true } other
            ? (BridgeDeckThickness + other.BridgeDeckThickness) / 2
            : BridgeDeckThickness;

    private UVTransform StoredUV(string group)
    {
        if (!_uvTransforms.TryGetValue(group, out var transform))
        {
            _uvTransforms[group] = transform = new UVTransform();
        }

        return transform;
    }

    private static UVTransform Sanitize(UVTransform transform) => new()
    {
        OffsetX = double.IsFinite(transform.OffsetX) ? transform.OffsetX : 0,
        OffsetY = double.IsFinite(transform.OffsetY) ? transform.OffsetY : 0,
        ScaleX = double.IsFinite(transform.ScaleX) ? Math.Max(0.1, transform.ScaleX) : 1,
        ScaleY = double.IsFinite(transform.ScaleY) ? Math.Max(0.1, transform.ScaleY) : 1,
    };

    private void ApplyUVTransform(string group, List<Triangle> triangles)
    {
        var t = StoredUV(group);
        if (Math.Abs(t.OffsetX) < 1e-8 && Math.Abs(t.OffsetY) < 1e-8 && Math.Abs(t.ScaleX - 1) < 1e-8 && Math.Abs(t.ScaleY - 1) < 1e-8)
        {
            return;
        }

        Vector2 Map(Vector2 uv) => new((uv.X * t.ScaleX) + t.OffsetX, (uv.Y * t.ScaleY) + t.OffsetY);
        foreach (var tri in triangles)
        {
            tri.UvA = Map(tri.UvA);
            tri.UvB = Map(tri.UvB);
            tri.UvC = Map(tri.UvC);
        }
    }

    public override IReadOnlyList<string> GetUVGroups()
    {
        var groups = new List<string> { "road" };
        if (EdgeType == "sidewalk")
        {
            groups.Add("sidewalk");
        }

        if (EdgeType == "bridge")
        {
            groups.Add("bridgeEdges");
        }

        if (BridgeEnabled)
        {
            groups.Add("bridgeDeck");
            groups.Add("bridgePillars");
        }

        return groups;
    }

    public override UVTransform GetUVTransform(string group) => _uvTransforms.ContainsKey(group) ? StoredUV(group).Clone() : base.GetUVTransform(group);

    public override void SetUVTransform(string group, UVTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (!UvGroupNames.Contains(group))
        {
            base.SetUVTransform(group, transform);
            return;
        }

        _uvTransforms[group] = Sanitize(transform);
        Update();
    }

    public void ConnectWith(int thisNodeIndex, Road otherRoad, int otherNodeIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(otherRoad);
        Connect(thisNodeIndex, otherRoad, otherNodeIndex);
        Update();
        otherRoad.Update();
    }

    public override IReadOnlyList<OccupiedTriangle> GetOccupiedArea()
    {
        if (BridgeEnabled)
        {
            return [];
        }

        // Real surface heights: sidewalk tops stay at curb height while an open road end stays at the road profile.
        var projected = new List<OccupiedTriangle>();
        foreach (var tri in GetGeometry().SelectMany(g => g.Triangles))
        {
            var area = Math.Abs(((tri.B.X - tri.A.X) * (tri.C.Z - tri.A.Z)) - ((tri.B.Z - tri.A.Z) * (tri.C.X - tri.A.X))) * 0.5;
            if (area >= 1e-8)
            {
                projected.Add(new OccupiedTriangle(tri.A, tri.B, tri.C));
            }
        }

        return projected;
    }

    public override ElementData Serialize(int id)
    {
        var (textures, rotations) = CollectTextureMaps();
        var data = new ElementData
        {
            Type = "road",
            Id = id,
            Textures = textures,
            TextureRotations = rotations,
            UvTransforms = UvGroupNames.ToDictionary(g => g, g => StoredUV(g).Clone(), StringComparer.Ordinal),
            Width = Width,
            Lanes = Lanes,
            EdgeType = EdgeType,
            SidewalkWidth = SidewalkWidth,
            CurbHeight = CurbHeight,
            RoadCrown = RoadCrown,
            BridgeEdgeStyle = BridgeEdgeStyle,
            BridgeEdgeHeight = BridgeEdgeHeight,
            BridgeEdgeWidth = BridgeEdgeWidth,
            BridgeEdgeDistance = BridgeEdgeDistance,
            BridgeEdgeLength = BridgeEdgeLength,
            BridgeEdgeCapEnabled = BridgeEdgeCapEnabled,
            BridgeEdgeCapHeight = BridgeEdgeCapHeight,
            BridgeEdgeCapWidth = BridgeEdgeCapWidth,
            BridgeEdgeCapJoined = BridgeEdgeCapJoined,
            BridgeEnabled = BridgeEnabled,
            BridgePillarShape = BridgePillarShape,
            BridgePillarSegments = BridgePillarSegments,
            BridgePillarDistance = BridgePillarDistance,
            BridgePillarCount = BridgePillarCount,
            BridgePillarWidth = BridgePillarWidth,
            BridgePillarInset = BridgePillarInset,
            BridgeDeckThickness = BridgeDeckThickness,
        };
        SerializeCurve(data);
        if (Divisions == 0)
        {
            data.CurvePointA = null;
            data.CurvePointB = null;
        }

        return data;
    }

    public static Road Deserialize(ElementData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var road = new Road(data.Nodes[0].ToVector3(), data.Nodes[1].ToVector3())
        {
            Width = data.Width ?? 3,
            Lanes = data.Lanes ?? 2,
            EdgeType = data.EdgeType ?? "none",
            SidewalkWidth = data.SidewalkWidth ?? 1,
            CurbHeight = data.CurbHeight ?? 0.15,
            RoadCrown = data.RoadCrown ?? 0,
            BridgeEdgeStyle = data.BridgeEdgeStyle ?? "solid",
            BridgeEdgeHeight = Math.Max(0.05, data.BridgeEdgeHeight ?? 0.8),
            BridgeEdgeWidth = Math.Max(0.02, data.BridgeEdgeWidth ?? 0.2),
            BridgeEdgeDistance = Math.Max(0.1, data.BridgeEdgeDistance ?? 3),
            BridgeEdgeLength = Math.Max(0.05, data.BridgeEdgeLength ?? 1),
            BridgeEdgeCapEnabled = data.BridgeEdgeCapEnabled ?? false,
            BridgeEdgeCapHeight = Math.Max(0.02, data.BridgeEdgeCapHeight ?? 0.12),
            BridgeEdgeCapWidth = Math.Max(0.02, data.BridgeEdgeCapWidth ?? ((data.BridgeEdgeWidth ?? 0.2) + (2 * (data.BridgeEdgeCapOverhang ?? 0.08)))),
            BridgeEdgeCapJoined = data.BridgeEdgeCapJoined ?? false,
            BridgeEnabled = data.BridgeEnabled ?? false,
            BridgePillarShape = data.BridgePillarShape ?? "box",
            BridgePillarSegments = Math.Max(3, data.BridgePillarSegments ?? 12),
            BridgePillarDistance = Math.Max(0.1, data.BridgePillarDistance ?? 8),
            BridgePillarCount = Math.Max(1, data.BridgePillarCount ?? 2),
            BridgePillarWidth = Math.Max(0.1, data.BridgePillarWidth ?? 0.8),
            BridgePillarInset = Math.Max(0, data.BridgePillarInset ?? 0.4),
            BridgeDeckThickness = Math.Max(0.05, data.BridgeDeckThickness ?? 0.5),
        };
        foreach (var (group, transform) in data.UvTransforms ?? [])
        {
            if (UvGroupNames.Contains(group))
            {
                road._uvTransforms[group] = Sanitize(transform);
            }
        }

        road.DeserializeCurve(data);
        return road;
    }

    public override PropertyDefinition GetProperties()
    {
        Vector3Property NodePosition(string label, WorldNode node) => new(label, () => node.Position, node.Update);
        PropertySection NodeSection(string label, WorldNode node, int nodeIndex)
        {
            var items = new List<PropertyItem> { NodePosition("Position", node) };
            if (IsConnected(nodeIndex))
            {
                items.Add(new ButtonProperty("Disconnect", () =>
                {
                    Disconnect(nodeIndex);
                    OnPropertiesChanged?.Invoke();
                }));
            }

            return new PropertySection(label, items);
        }

        void Set(Action change, bool refreshProperties = false)
        {
            change();
            Update();
            if (refreshProperties)
            {
                OnPropertiesChanged?.Invoke();
            }
        }

        var edges = new List<PropertyItem>
        {
            new SelectProperty("Type", [new("None", "none"), new("Sidewalk", "sidewalk"), new("Bridge", "bridge")], () => EdgeType, v => Set(() => EdgeType = v, true)),
        };
        if (EdgeType == "sidewalk")
        {
            edges.Add(new NumberProperty("Sidewalk Width", () => SidewalkWidth, v => Set(() => SidewalkWidth = Math.Max(0.1, v)), 0.1, null, 0.1));
            edges.Add(new NumberProperty("Curb Height", () => CurbHeight, v => Set(() => CurbHeight = Math.Max(0, v)), 0, null, 0.05));
        }

        if (EdgeType == "bridge")
        {
            edges.Add(new SelectProperty("Bridge Edge Style",
                [new("Two Solid Walls", "solid"), new("Two Double-Sided Planes", "plane"), new("Spaced Walls", "spaced"), new("Steel Truss", "truss")],
                () => BridgeEdgeStyle, v => Set(() => BridgeEdgeStyle = v, true)));
            edges.Add(new NumberProperty("Wall Height", () => BridgeEdgeHeight, v => Set(() => BridgeEdgeHeight = Math.Max(0.05, v)), 0.05, null, 0.05));
            if (BridgeEdgeStyle != "plane")
            {
                edges.Add(new NumberProperty("Wall Width", () => BridgeEdgeWidth, v => Set(() => BridgeEdgeWidth = Math.Max(0.02, v)), 0.02, null, 0.05));
            }

            if (BridgeEdgeStyle == "spaced")
            {
                edges.Add(new NumberProperty("Wall Distance", () => BridgeEdgeDistance, v => Set(() => BridgeEdgeDistance = Math.Max(0.1, v)), 0.1, null, 0.1));
                edges.Add(new NumberProperty("Wall Length", () => BridgeEdgeLength, v => Set(() => BridgeEdgeLength = Math.Max(0.05, v)), 0.05, null, 0.05));
                edges.Add(new BooleanProperty("Wall Cap", () => BridgeEdgeCapEnabled, v => Set(() => BridgeEdgeCapEnabled = v, true)));
                if (BridgeEdgeCapEnabled)
                {
                    edges.Add(new NumberProperty("Cap Height", () => BridgeEdgeCapHeight, v => Set(() => BridgeEdgeCapHeight = Math.Max(0.02, v)), 0.02, null, 0.02));
                    edges.Add(new NumberProperty("Cap Width", () => BridgeEdgeCapWidth, v => Set(() => BridgeEdgeCapWidth = Math.Max(0.02, v)), 0.02, null, 0.02));
                    edges.Add(new BooleanProperty("Joined Cap", () => BridgeEdgeCapJoined, v => Set(() => BridgeEdgeCapJoined = v)));
                }
            }

            if (BridgeEdgeStyle == "truss")
            {
                edges.Add(new NumberProperty("Truss Section Distance", () => BridgeEdgeDistance, v => Set(() => BridgeEdgeDistance = Math.Max(0.25, v)), 0.25, null, 0.25));
            }
        }

        var bridge = new List<PropertyItem> { new BooleanProperty("Enabled", () => BridgeEnabled, v => Set(() => BridgeEnabled = v, true)) };
        if (BridgeEnabled)
        {
            bridge.Add(new NumberProperty("Deck Thickness", () => BridgeDeckThickness, v => Set(() => BridgeDeckThickness = Math.Max(0.05, v)), 0.05, null, 0.05));
            bridge.Add(new SelectProperty("Pillar Shape", [new("Box", "box"), new("Circular", "circular")], () => BridgePillarShape,
                v => Set(() => BridgePillarShape = v == "circular" ? "circular" : "box", true)));
            bridge.Add(new NumberProperty("Pillar Distance", () => BridgePillarDistance, v => Set(() => BridgePillarDistance = Math.Max(0.1, v)), 0.1, null, 0.1));
            bridge.Add(new NumberProperty("Pillar Count", () => BridgePillarCount, v => Set(() => BridgePillarCount = Math.Max(1, (int)MathUtil.JsRound(v))), 1, null, 1));
            bridge.Add(new NumberProperty("Pillar Width", () => BridgePillarWidth, v => Set(() => BridgePillarWidth = Math.Max(0.1, v)), 0.1, null, 0.1));
            bridge.Add(new NumberProperty("Pillar Inset", () => BridgePillarInset, v => Set(() => BridgePillarInset = Math.Max(0, v)), 0, null, 0.1));
            if (BridgePillarShape == "circular")
            {
                bridge.Add(new NumberProperty("Pillar Segments", () => BridgePillarSegments, v => Set(() => BridgePillarSegments = Math.Max(3, (int)MathUtil.JsRound(v))), 3, null, 1));
            }
        }

        SelectOption[] rotations = [new("0°", "0"), new("90°", "90"), new("180°", "180"), new("270°", "270")];
        static string Degrees(double value) => Js.Num(value);
        var sections = new List<PropertySection>
        {
            NodeSection("Node A", NodeA, 0),
            NodeSection("Node B", NodeB, 1),
            new("Road",
            [
                new NumberProperty("Width", () => Width, v => Set(() => Width = v), 0.1, null, 0.1),
                new NumberProperty("Lanes", () => Lanes, v => Set(() => Lanes = Math.Max(1, (int)MathUtil.JsRound(v))), 1, null, 1),
                new NumberProperty("Divisions", () => Divisions, v => Set(() => Divisions = Math.Max(0, (int)MathUtil.JsRound(v))), 0, null, 1),
                new NumberProperty("Crown", () => RoadCrown, v => Set(() => RoadCrown = v), 0, null, 0.01),
            ]),
            new("Edges", edges),
            new("Bridge", bridge),
            new("Textures",
            [
                new SelectProperty("Road Rot.", rotations, () => Degrees(TextureRotations.GetValueOrDefault("road")), v => SetTextureRotation("road", double.Parse(v, System.Globalization.CultureInfo.InvariantCulture))),
                new NumberProperty("Road Stretch", () => RoadTexStretch, v => Set(() => RoadTexStretch = Math.Max(0.1, v)), 0.1, null, 0.1),
                new SelectProperty("Sidewalk Rot.", rotations, () => Degrees(TextureRotations.GetValueOrDefault("sidewalk")), v => SetTextureRotation("sidewalk", double.Parse(v, System.Globalization.CultureInfo.InvariantCulture))),
                new NumberProperty("Sidewalk Stretch", () => SidewalkTexStretch, v => Set(() => SidewalkTexStretch = Math.Max(0.1, v)), 0.1, null, 0.1),
            ]),
        };
        if (CurvePointA is { } a)
        {
            sections.Add(new PropertySection("Curve Point A", [NodePosition("Position", a)]));
        }

        if (CurvePointB is { } b)
        {
            sections.Add(new PropertySection("Curve Point B", [NodePosition("Position", b)]));
        }

        return new PropertyDefinition("Road", "☰", sections);
    }

    protected override List<GeometryGroup> GetGeometry()
    {
        var lod = World?.LodPreviewLevel ?? 0;
        return BuildGeometry(lod > 0 ? LodLevels.Divisions(Divisions, lod) : null);
    }

    /// <summary>Geometry at an export LOD level, without touching the live element.</summary>
    public List<GeometryGroup> GetExportGeometry(int lodIndex) => BuildGeometry(LodLevels.Divisions(Divisions, lodIndex));

    private List<GeometryGroup> BuildGeometry(int? divisionsOverride)
    {
        var edge = ComputeEdgeData(divisionsOverride);
        var groups = SweepGeometryLine(edge);
        if (EdgeType == "bridge" && BridgeEdgeStyle == "spaced" && GetSpacedBridgeEdgeTriangles(edge) is { Count: > 0 } spaced)
        {
            groups.Add(new GeometryGroup("bridgeEdges", spaced));
        }

        if (EdgeType == "bridge" && BridgeEdgeStyle == "truss" && GetTrussBridgeEdgeTriangles(edge) is { Count: > 0 } truss)
        {
            groups.Add(new GeometryGroup("bridgeEdges", truss));
        }

        if (!BridgeEnabled)
        {
            return groups;
        }

        if (GetBridgePillarTriangles(edge) is { Count: > 0 } pillars)
        {
            groups.Add(new GeometryGroup("bridgePillars", pillars));
        }

        return groups;
    }

    private double CrownAt(double t)
    {
        var crownA = GetEndCrown(0);
        var crownB = GetEndCrown(1);
        return t < 0.5 ? crownA + ((RoadCrown - crownA) * t * 2) : RoadCrown + ((crownB - RoadCrown) * (t - 0.5) * 2);
    }

    private static double LaneHeightAtFraction(double fraction, double crownHeight) => crownHeight * (1 - Math.Abs((2 * fraction) - 1));

    private double BridgeOuterHalfWidth(GeometryLineContext context) => context.HalfWidth + (EdgeType == "sidewalk" ? context.SidewalkWidth : 0);

    private double DeckTopHeightAtLateral(double lateral, GeometryLineContext context)
    {
        if (EdgeType == "sidewalk" && Math.Abs(lateral) > context.HalfWidth + 1e-6)
        {
            return context.CurbHeight;
        }

        if (context.HalfWidth <= 1e-6)
        {
            return LaneHeightAtFraction(0.5, context.CrownHeight);
        }

        return LaneHeightAtFraction(MathUtil.Clamp((lateral + context.HalfWidth) / (2 * context.HalfWidth), 0, 1), context.CrownHeight);
    }

    private static GeometryLineSegment Segment(string group, double l0, double h0, double u0, double v0, double l1, double h1, double u1, double v1) =>
        new(group, new GeometryLineVertex(l0, h0, u0, v0), new GeometryLineVertex(l1, h1, u1, v1));

    private List<GeometryLineSegment> GetGeometryLine(GeometryLineContext context)
    {
        var segments = new List<GeometryLineSegment>();
        var laneCount = Math.Max(1, Lanes);

        // Every vertex of a cross-section uses the same longitudinal coordinate: mixing edge distances shears UVs on bends.
        double LaneV(bool invert) => (invert ? context.CenterTotal - context.CenterDistance : context.CenterDistance) * RoadTexStretch;
        for (var lane = 0; lane < laneCount; lane++)
        {
            var laneLeft = lane / (double)laneCount;
            var laneRight = (lane + 1) / (double)laneCount;
            var invert = lane % 2 == 1;

            // The crown is a road profile: split the lane containing the centre so odd lane counts get an exact ridge.
            double[] fractions = laneLeft < 0.5 && laneRight > 0.5 ? [laneLeft, 0.5, laneRight] : [laneLeft, laneRight];
            for (var part = 0; part < fractions.Length - 1; part++)
            {
                var startU = (fractions[part] - laneLeft) * laneCount;
                var endU = (fractions[part + 1] - laneLeft) * laneCount;
                segments.Add(Segment("road",
                    -context.HalfWidth + (2 * context.HalfWidth * fractions[part]), LaneHeightAtFraction(fractions[part], context.CrownHeight), invert ? 1 - startU : startU, LaneV(invert),
                    -context.HalfWidth + (2 * context.HalfWidth * fractions[part + 1]), LaneHeightAtFraction(fractions[part + 1], context.CrownHeight), invert ? 1 - endU : endU, LaneV(invert)));
            }
        }

        if (EdgeType == "sidewalk")
        {
            var leftV = context.LeftDistance * SidewalkTexStretch;
            var rightV = context.RightDistance * SidewalkTexStretch;

            // World-size UVs across each cross-section, so curb heights and sidewalk widths keep their texel density.
            var curbU = Math.Max(0.001, context.CurbHeight) * SidewalkTexStretch;
            var sidewalkU = Math.Max(0.001, context.SidewalkWidth) * SidewalkTexStretch;
            var hw = context.HalfWidth;
            segments.Add(Segment("sidewalk", hw, 0, 0, rightV, hw, context.CurbHeight, curbU, rightV)); // right curb face
            segments.Add(Segment("sidewalk", hw, context.CurbHeight, 0, rightV, hw + context.SidewalkWidth, context.CurbHeight, sidewalkU, rightV)); // right top
            segments.Add(Segment("sidewalk", -hw, context.CurbHeight, curbU, leftV, -hw, 0, 0, leftV)); // left curb face (reversed: outward normals)
            segments.Add(Segment("sidewalk", -(hw + context.SidewalkWidth), context.CurbHeight, sidewalkU, leftV, -hw, context.CurbHeight, 0, leftV)); // left top
        }

        if (EdgeType == "bridge" && BridgeEdgeStyle is "solid" or "plane")
        {
            var wallHeight = Math.Max(0.05, BridgeEdgeHeight);
            var wallWidth = BridgeEdgeStyle == "plane" ? 0 : Math.Max(0.02, BridgeEdgeWidth);
            var hw = context.HalfWidth;
            var rightBase = DeckTopHeightAtLateral(hw, context);
            var leftBase = DeckTopHeightAtLateral(-hw, context);
            var rightV = context.RightDistance;
            var leftV = context.LeftDistance;
            if (BridgeEdgeStyle == "plane")
            {
                segments.Add(Segment("bridgeEdges", hw, rightBase, 0, rightV, hw, rightBase + wallHeight, 1, rightV));
                segments.Add(Segment("bridgeEdges", -hw, leftBase + wallHeight, 1, leftV, -hw, leftBase, 0, leftV));
            }
            else
            {
                var deckThickness = Math.Max(0.05, context.DeckThickness);
                var rightBottom = rightBase - deckThickness;
                var leftBottom = leftBase - deckThickness;

                // Solid parapets extend down to the deck underside and are closed there.
                segments.Add(Segment("bridgeEdges", hw, rightBottom, 0, rightV, hw, rightBase + wallHeight, 1, rightV));
                segments.Add(Segment("bridgeEdges", hw, rightBase + wallHeight, 0, rightV, hw + wallWidth, rightBase + wallHeight, 1, rightV));
                segments.Add(Segment("bridgeEdges", hw + wallWidth, rightBase + wallHeight, 1, rightV, hw + wallWidth, rightBottom, 0, rightV));
                segments.Add(Segment("bridgeEdges", hw + wallWidth, rightBottom, 1, rightV, hw, rightBottom, 0, rightV));
                segments.Add(Segment("bridgeEdges", -hw, leftBase + wallHeight, 1, leftV, -hw, leftBottom, 0, leftV));
                segments.Add(Segment("bridgeEdges", -(hw + wallWidth), leftBase + wallHeight, 0, leftV, -hw, leftBase + wallHeight, 1, leftV));
                segments.Add(Segment("bridgeEdges", -(hw + wallWidth), leftBottom, 0, leftV, -(hw + wallWidth), leftBase + wallHeight, 1, leftV));
                segments.Add(Segment("bridgeEdges", -hw, leftBottom, 1, leftV, -(hw + wallWidth), leftBottom, 0, leftV));
            }
        }

        if (BridgeEnabled)
        {
            var deckThickness = Math.Max(0.05, context.DeckThickness);
            var deckSegments = new List<GeometryLineSegment>();

            // Only the roadway/sidewalk surface belongs to the deck shell; edge walls are never mirrored into it.
            foreach (var segment in segments.Where(s => s.Group is "road" or "sidewalk"))
            {
                deckSegments.Add(new GeometryLineSegment("bridgeDeck",
                    segment.End with { Height = segment.End.Height - deckThickness },
                    segment.Start with { Height = segment.Start.Height - deckThickness }));
            }

            var outerHalfWidth = BridgeOuterHalfWidth(context);
            var rightTop = DeckTopHeightAtLateral(outerHalfWidth, context);
            var leftTop = DeckTopHeightAtLateral(-outerHalfWidth, context);
            var rightV = context.RightDistance * RoadTexStretch;
            var leftV = context.LeftDistance * RoadTexStretch;

            // Outer side walls close the top deck to its underside.
            deckSegments.Add(Segment("bridgeDeck", outerHalfWidth, rightTop, 1, rightV, outerHalfWidth, rightTop - deckThickness, 0, rightV));
            deckSegments.Add(Segment("bridgeDeck", -outerHalfWidth, leftTop - deckThickness, 0, leftV, -outerHalfWidth, leftTop, 1, leftV));
            segments.AddRange(deckSegments);
        }

        return segments;
    }

    private List<GeometryGroup> SweepGeometryLine(EdgeData edge)
    {
        var pointCount = edge.Points.Count;
        if (pointCount < 2)
        {
            return [new GeometryGroup("road", [])];
        }

        var (swStart, swEnd) = (GetResolvedSidewalkWidth(0), GetResolvedSidewalkWidth(1));
        var (chStart, chEnd) = (GetResolvedCurbHeight(0), GetResolvedCurbHeight(1));
        var (dtStart, dtEnd) = (GetResolvedBridgeDeckThickness(0), GetResolvedBridgeDeckThickness(1));
        var sections = new List<GeometryLineSection>();
        for (var i = 0; i < pointCount; i++)
        {
            var t = i / (double)(pointCount - 1);
            var context = new GeometryLineContext(
                edge.HalfWidths[i],
                swStart + ((swEnd - swStart) * t),
                chStart + ((chEnd - chStart) * t),
                dtStart + ((dtEnd - dtStart) * t),
                edge.LeftCumDist[i], edge.RightCumDist[i], edge.CenterCumDist[i], edge.CenterTotal,
                CrownAt(t));
            sections.Add(new GeometryLineSection(edge.Points[i], edge.RightVecs[i], GetGeometryLine(context)));
        }

        var swept = GeometrySweep.SweepSections(sections);
        var road = swept.FirstOrDefault(g => g.Name == "road");
        var sidewalk = swept.FirstOrDefault(g => g.Name == "sidewalk");
        var bridgeEdges = swept.FirstOrDefault(g => g.Name == "bridgeEdges");
        var bridgeDeck = swept.FirstOrDefault(g => g.Name == "bridgeDeck");
        if (road is not null)
        {
            ApplyUVTransform("road", road.Triangles);
        }

        if (sidewalk is not null)
        {
            ApplyUVTransform("sidewalk", sidewalk.Triangles);
        }

        if (bridgeEdges is not null)
        {
            if (BridgeEdgeStyle == "solid")
            {
                bridgeEdges.Triangles.AddRange(GetSolidBridgeEdgeEndCapTriangles(edge));
            }

            ApplyUVTransform("bridgeEdges", bridgeEdges.Triangles);
        }

        if (bridgeDeck is not null)
        {
            ApplyUVTransform("bridgeDeck", bridgeDeck.Triangles);
        }

        var groups = new List<GeometryGroup> { new("road", road?.Triangles ?? []) };
        foreach (var group in new[] { sidewalk, bridgeEdges, bridgeDeck })
        {
            if (group is { Triangles.Count: > 0 })
            {
                groups.Add(group);
            }
        }

        return groups;
    }

    private List<Triangle> GetSolidBridgeEdgeEndCapTriangles(EdgeData edge)
    {
        var triangles = new List<Triangle>();
        if (edge.Points.Count < 2)
        {
            return triangles;
        }

        var wallHeight = Math.Max(0.05, BridgeEdgeHeight);
        var wallWidth = Math.Max(0.02, BridgeEdgeWidth);
        var last = edge.Points.Count - 1;
        var ends = new[]
        {
            (Index: 0, NodeIndex: 0, Deck: GetResolvedBridgeDeckThickness(0), Outward: (edge.Points[0] - edge.Points[1]).Normalized),
            (Index: last, NodeIndex: 1, Deck: GetResolvedBridgeDeckThickness(1), Outward: (edge.Points[last] - edge.Points[last - 1]).Normalized),
        };
        foreach (var end in ends)
        {
            if (HasContinuousBridgeEdgeConnection(end.NodeIndex, "solid"))
            {
                continue;
            }

            var origin = edge.Points[end.Index];
            var right = edge.RightVecs[end.Index];
            var halfWidth = edge.HalfWidths[end.Index];
            var deckThickness = Math.Max(0.05, end.Deck);
            foreach (var side in new[] { -1, 1 })
            {
                var innerBottom = origin + (right * (side * halfWidth)) + new Vector3(0, -deckThickness, 0);
                var outerBottom = origin + (right * (side * (halfWidth + wallWidth))) + new Vector3(0, -deckThickness, 0);
                var innerTop = origin + (right * (side * halfWidth)) + new Vector3(0, wallHeight, 0);
                var outerTop = origin + (right * (side * (halfWidth + wallWidth))) + new Vector3(0, wallHeight, 0);
                var normal = (innerTop - innerBottom).Cross(outerTop - innerBottom);
                PushBridgeEdgeQuad(triangles, innerBottom, innerTop, outerTop, outerBottom, wallHeight + deckThickness, wallWidth, normal.Dot(end.Outward) < 0);
            }
        }

        return triangles;
    }

    private List<Triangle> GetTrussBridgeEdgeTriangles(EdgeData edge)
    {
        var triangles = new List<Triangle>();
        if (edge.CenterTotal <= 1e-6)
        {
            return triangles;
        }

        var beamWidth = Math.Max(0.02, BridgeEdgeWidth);
        var trussHeight = Math.Max(beamWidth * 2, BridgeEdgeHeight);
        var spacing = Math.Max(0.25, BridgeEdgeDistance);
        var sectionCount = (int)Math.Max(2, Math.Ceiling(edge.CenterTotal / spacing));
        var (deckStart, deckEnd) = (GetResolvedBridgeDeckThickness(0), GetResolvedBridgeDeckThickness(1));
        var topNodesBySide = new Dictionary<int, List<Vector3>>();
        var sectionRightAxes = new List<Vector3>();
        var jointKeys = new HashSet<(long, long, long)>();
        foreach (var side in new[] { -1, 1 })
        {
            var bottomNodes = new List<Vector3>();
            var topNodes = new List<Vector3>();
            var sideAxes = new List<Vector3>();
            for (var i = 0; i <= sectionCount; i++)
            {
                if (SampleEdgeAtDistance(edge, edge.CenterTotal * i / sectionCount) is not { } sample)
                {
                    continue;
                }

                var t = i / (double)sectionCount;
                var ramp = Math.Min(1, Math.Min(t / 0.2, (1 - t) / 0.2));
                var deckThickness = MathUtil.Lerp(deckStart, deckEnd, sample.T);
                var lateral = side * (sample.HalfWidth + (beamWidth * 0.5));
                var bottom = sample.Origin + (sample.Right * lateral) + new Vector3(0, -deckThickness + (beamWidth * 0.5), 0);
                bottomNodes.Add(bottom);
                topNodes.Add(bottom + new Vector3(0, trussHeight * ramp, 0));
                sideAxes.Add(sample.Right.Normalized);
            }

            for (var i = 0; i < bottomNodes.Count - 1; i++)
            {
                var sideAxis = sideAxes[i].LerpUnclamped(sideAxes[i + 1], 0.5).Normalized;
                AddBridgeBeamBox(triangles, bottomNodes[i], bottomNodes[i + 1], beamWidth, sideAxis);
                AddBridgeBeamBox(triangles, topNodes[i], topNodes[i + 1], beamWidth, sideAxis);
                if (i % 2 == 0)
                {
                    AddBridgeBeamBox(triangles, bottomNodes[i], topNodes[i + 1], beamWidth, sideAxis);
                }
                else
                {
                    AddBridgeBeamBox(triangles, topNodes[i], bottomNodes[i + 1], beamWidth, sideAxis);
                }
            }

            for (var i = 1; i < bottomNodes.Count - 1; i++)
            {
                AddBridgeBeamBox(triangles, bottomNodes[i], topNodes[i], beamWidth, sideAxes[i]);
            }

            for (var i = 0; i < bottomNodes.Count; i++)
            {
                AddTrussJointBox(triangles, bottomNodes[i], beamWidth, sideAxes[i], jointKeys);
                AddTrussJointBox(triangles, topNodes[i], beamWidth, sideAxes[i], jointKeys);
            }

            topNodesBySide[side] = topNodes;
            sectionRightAxes = sideAxes;
        }

        var leftTop = topNodesBySide.GetValueOrDefault(-1) ?? [];
        var rightTop = topNodesBySide.GetValueOrDefault(1) ?? [];
        var overheadCount = Math.Min(leftTop.Count, rightTop.Count);
        for (var i = 1; i < overheadCount - 1; i++)
        {
            AddBridgeBeamBox(triangles, leftTop[i], rightTop[i], beamWidth, Vector3.UnitY.Cross(sectionRightAxes[i]).Normalized);
        }

        for (var i = 1; i < overheadCount - 2; i++)
        {
            var forwardAxis = Vector3.UnitY.Cross(sectionRightAxes[i]).Normalized;
            AddBridgeBeamBox(triangles, leftTop[i], rightTop[i + 1], beamWidth, forwardAxis);
            AddBridgeBeamBox(triangles, rightTop[i], leftTop[i + 1], beamWidth, forwardAxis);
        }

        ApplyUVTransform("bridgeEdges", triangles);
        return triangles;
    }

    private bool HasContinuousBridgeEdgeConnection(int nodeIndex, string style) =>
        Connections.TryGetValue(nodeIndex, out var connection) && connection.Element is Road { EdgeType: "bridge" } other && other.BridgeEdgeStyle == style;

    private static void AddTrussJointBox(List<Triangle> triangles, Vector3 center, double beamWidth, Vector3 sideAxis, HashSet<(long, long, long)> jointKeys)
    {
        if (!jointKeys.Add(((long)MathUtil.JsRound(center.X * 10000), (long)MathUtil.JsRound(center.Y * 10000), (long)MathUtil.JsRound(center.Z * 10000))))
        {
            return;
        }

        // A slightly oversized closed cube acts as a steel gusset, covering the wedges where beams meet at angles.
        var size = beamWidth * 1.3;
        var half = size * 0.5;
        AddBridgeBeamBox(triangles, center + new Vector3(0, -half, 0), center + new Vector3(0, half, 0), size, sideAxis);
    }

    private static void AddBridgeBeamBox(List<Triangle> triangles, Vector3 start, Vector3 end, double width, Vector3 sideAxisHint)
    {
        var along = end - start;
        var length = along.Magnitude;
        if (length <= 1e-5)
        {
            return;
        }

        along /= length;
        var sideAxis = sideAxisHint + (along * -sideAxisHint.Dot(along));
        if (sideAxis.SqrMagnitude <= 1e-8)
        {
            sideAxis = Vector3.UnitY + (along * -along.Y);
        }

        sideAxis = sideAxis.Normalized;
        var otherAxis = along.Cross(sideAxis).Normalized;
        var half = width * 0.5;
        Vector3[] CornersAt(Vector3 c) =>
        [
            c + (sideAxis * -half) + (otherAxis * -half),
            c + (sideAxis * half) + (otherAxis * -half),
            c + (sideAxis * half) + (otherAxis * half),
            c + (sideAxis * -half) + (otherAxis * half),
        ];
        var s = CornersAt(start);
        var e = CornersAt(end);
        var boxCenter = start.LerpUnclamped(end, 0.5);
        Vector3[][] faces =
        [
            [s[0], s[1], s[2], s[3]], [e[3], e[2], e[1], e[0]], [s[0], e[0], e[1], s[1]],
            [s[1], e[1], e[2], s[2]], [s[2], e[2], e[3], s[3]], [s[3], e[3], e[0], s[0]],
        ];
        foreach (var face in faces)
        {
            var faceCenter = (face[0] + face[1] + face[2] + face[3]) * 0.25;
            var normal = (face[1] - face[0]).Cross(face[2] - face[0]);
            PushBridgeEdgeQuad(triangles, face[0], face[1], face[2], face[3], length, width, normal.Dot(faceCenter - boxCenter) < 0);
        }
    }

    private List<Triangle> GetSpacedBridgeEdgeTriangles(EdgeData edge)
    {
        var triangles = new List<Triangle>();
        if (edge.CenterTotal <= 1e-6)
        {
            return triangles;
        }

        var spacing = Math.Max(0.1, BridgeEdgeDistance);
        var wallLength = Math.Min(Math.Max(0.05, BridgeEdgeLength), edge.CenterTotal);
        var wallWidth = Math.Max(0.02, BridgeEdgeWidth);
        var wallHeight = Math.Max(0.05, BridgeEdgeHeight);
        var (deckStart, deckEnd) = (GetResolvedBridgeDeckThickness(0), GetResolvedBridgeDeckThickness(1));
        for (var center = spacing * 0.5; center < edge.CenterTotal + 1e-6; center += spacing)
        {
            var wallStart = Math.Max(0, center - (wallLength * 0.5));
            var wallEnd = Math.Min(edge.CenterTotal, center + (wallLength * 0.5));
            if (SampleEdgeAtDistance(edge, wallStart) is not { } start || SampleEdgeAtDistance(edge, wallEnd) is not { } end
                || start.Origin.DistanceToSquared(end.Origin) <= 1e-8)
            {
                continue;
            }

            var startDeck = MathUtil.Lerp(deckStart, deckEnd, start.T);
            var endDeck = MathUtil.Lerp(deckStart, deckEnd, end.T);
            foreach (var side in new[] { -1, 1 })
            {
                var up = new Vector3(0, wallHeight, 0);
                var startInnerTop = start.Origin + (start.Right * (side * start.HalfWidth)) + up;
                var endInnerTop = end.Origin + (end.Right * (side * end.HalfWidth)) + up;
                var startOuterTop = start.Origin + (start.Right * (side * (start.HalfWidth + wallWidth))) + up;
                var endOuterTop = end.Origin + (end.Right * (side * (end.HalfWidth + wallWidth))) + up;
                var startInnerBottom = startInnerTop + new Vector3(0, -(wallHeight + startDeck), 0);
                var endInnerBottom = endInnerTop + new Vector3(0, -(wallHeight + endDeck), 0);
                var startOuterBottom = startOuterTop + new Vector3(0, -(wallHeight + startDeck), 0);
                var endOuterBottom = endOuterTop + new Vector3(0, -(wallHeight + endDeck), 0);
                var reverse = side > 0;
                var totalStart = wallHeight + startDeck;
                var totalEnd = wallHeight + endDeck;
                PushBridgeEdgeQuad(triangles, startInnerBottom, endInnerBottom, endInnerTop, startInnerTop, wallLength, Math.Max(totalStart, totalEnd), reverse);
                PushBridgeEdgeQuad(triangles, endOuterBottom, startOuterBottom, startOuterTop, endOuterTop, wallLength, Math.Max(totalStart, totalEnd), reverse);
                PushBridgeEdgeQuad(triangles, startInnerTop, endInnerTop, endOuterTop, startOuterTop, wallLength, wallWidth, reverse);
                PushBridgeEdgeQuad(triangles, startOuterBottom, startInnerBottom, startInnerTop, startOuterTop, wallWidth, totalStart, reverse);
                PushBridgeEdgeQuad(triangles, endInnerBottom, endOuterBottom, endOuterTop, endInnerTop, wallWidth, totalEnd, reverse);
                PushBridgeEdgeQuad(triangles, startInnerBottom, startOuterBottom, endOuterBottom, endInnerBottom, wallWidth, wallLength, reverse);
                if (BridgeEdgeCapEnabled && !BridgeEdgeCapJoined)
                {
                    AddSpacedBridgeEdgeCapBox(triangles, edge, wallStart, wallEnd, side, wallHeight, wallWidth);
                }
            }
        }

        if (BridgeEdgeCapEnabled && BridgeEdgeCapJoined)
        {
            triangles.AddRange(GetJoinedSpacedBridgeEdgeCapTriangles(edge, wallHeight, wallWidth));
        }

        ApplyUVTransform("bridgeEdges", triangles);
        return triangles;
    }

    private void AddSpacedBridgeEdgeCapBox(List<Triangle> triangles, EdgeData edge, double startDistance, double endDistance, int side, double wallHeight, double wallWidth)
    {
        if (SampleEdgeAtDistance(edge, startDistance) is not { } start || SampleEdgeAtDistance(edge, endDistance) is not { } end)
        {
            return;
        }

        var capHeight = Math.Max(0.02, BridgeEdgeCapHeight);
        var capWidth = Math.Max(0.02, BridgeEdgeCapWidth);
        var startCenter = start.HalfWidth + (wallWidth * 0.5);
        var endCenter = end.HalfWidth + (wallWidth * 0.5);
        var up = new Vector3(0, wallHeight, 0);
        var capUp = new Vector3(0, capHeight, 0);
        var startInnerBottom = start.Origin + (start.Right * (side * Math.Max(0, startCenter - (capWidth * 0.5)))) + up;
        var endInnerBottom = end.Origin + (end.Right * (side * Math.Max(0, endCenter - (capWidth * 0.5)))) + up;
        var startOuterBottom = start.Origin + (start.Right * (side * (startCenter + (capWidth * 0.5)))) + up;
        var endOuterBottom = end.Origin + (end.Right * (side * (endCenter + (capWidth * 0.5)))) + up;
        var (startInnerTop, endInnerTop) = (startInnerBottom + capUp, endInnerBottom + capUp);
        var (startOuterTop, endOuterTop) = (startOuterBottom + capUp, endOuterBottom + capUp);
        var capLength = Math.Max(0.01, endDistance - startDistance);
        var reverse = side > 0;
        PushBridgeEdgeQuad(triangles, startInnerBottom, endInnerBottom, endInnerTop, startInnerTop, capLength, capHeight, reverse);
        PushBridgeEdgeQuad(triangles, endOuterBottom, startOuterBottom, startOuterTop, endOuterTop, capLength, capHeight, reverse);
        PushBridgeEdgeQuad(triangles, startInnerTop, endInnerTop, endOuterTop, startOuterTop, capLength, capWidth, reverse);
        PushBridgeEdgeQuad(triangles, startOuterBottom, startInnerBottom, startInnerTop, startOuterTop, capWidth, capHeight, reverse);
        PushBridgeEdgeQuad(triangles, endInnerBottom, endOuterBottom, endOuterTop, endInnerTop, capWidth, capHeight, reverse);
        PushBridgeEdgeQuad(triangles, startInnerBottom, startOuterBottom, endOuterBottom, endInnerBottom, capWidth, capLength, reverse);
    }

    private List<Triangle> GetJoinedSpacedBridgeEdgeCapTriangles(EdgeData edge, double wallHeight, double wallWidth)
    {
        var capHeight = Math.Max(0.02, BridgeEdgeCapHeight);
        var capWidth = Math.Max(0.02, BridgeEdgeCapWidth);
        var sections = new List<GeometryLineSection>();
        for (var i = 0; i < edge.Points.Count; i++)
        {
            var center = edge.HalfWidths[i] + (wallWidth * 0.5);
            var inner = Math.Max(0, center - (capWidth * 0.5));
            var outer = center + (capWidth * 0.5);
            var v = edge.CenterCumDist[i];
            var top = wallHeight + capHeight;
            sections.Add(new GeometryLineSection(edge.Points[i], edge.RightVecs[i],
            [
                Segment("bridgeEdges", inner, wallHeight, 0, v, inner, top, 1, v),
                Segment("bridgeEdges", inner, top, 0, v, outer, top, 1, v),
                Segment("bridgeEdges", outer, top, 1, v, outer, wallHeight, 0, v),
                Segment("bridgeEdges", outer, wallHeight, 1, v, inner, wallHeight, 0, v),
                Segment("bridgeEdges", -inner, top, 1, v, -inner, wallHeight, 0, v),
                Segment("bridgeEdges", -outer, top, 0, v, -inner, top, 1, v),
                Segment("bridgeEdges", -outer, wallHeight, 0, v, -outer, top, 1, v),
                Segment("bridgeEdges", -inner, wallHeight, 1, v, -outer, wallHeight, 0, v),
            ]));
        }

        var triangles = GeometrySweep.SweepSections(sections).FirstOrDefault(g => g.Name == "bridgeEdges")?.Triangles ?? [];
        var last = edge.Points.Count - 1;
        foreach (var (index, neighbour) in new[] { (0, 1), (last, last - 1) })
        {
            var origin = edge.Points[index];
            var right = edge.RightVecs[index];
            var center = edge.HalfWidths[index] + (wallWidth * 0.5);
            var inner = Math.Max(0, center - (capWidth * 0.5));
            var outer = center + (capWidth * 0.5);
            var outward = (origin - edge.Points[neighbour]).Normalized;
            foreach (var side in new[] { -1, 1 })
            {
                var innerBottom = origin + (right * (side * inner)) + new Vector3(0, wallHeight, 0);
                var outerBottom = origin + (right * (side * outer)) + new Vector3(0, wallHeight, 0);
                var innerTop = innerBottom + new Vector3(0, capHeight, 0);
                var outerTop = outerBottom + new Vector3(0, capHeight, 0);
                var normal = (innerTop - innerBottom).Cross(outerTop - innerBottom);
                PushBridgeEdgeQuad(triangles, innerBottom, innerTop, outerTop, outerBottom, capHeight, capWidth, normal.Dot(outward) < 0);
            }
        }

        return triangles;
    }

    private static void PushBridgeEdgeQuad(List<Triangle> output, Vector3 a, Vector3 b, Vector3 c, Vector3 d, double width, double height, bool reverse = false)
    {
        var uvA = new Vector2(0, 0);
        var uvB = new Vector2(Math.Max(0.01, width), 0);
        var uvC = new Vector2(Math.Max(0.01, width), Math.Max(0.01, height));
        var uvD = new Vector2(0, Math.Max(0.01, height));
        if (reverse)
        {
            output.Add(new Triangle(a, c, b, uvA, uvC, uvB));
            output.Add(new Triangle(a, d, c, uvA, uvD, uvC));
        }
        else
        {
            output.Add(new Triangle(a, b, c, uvA, uvB, uvC));
            output.Add(new Triangle(a, c, d, uvA, uvC, uvD));
        }
    }

    private List<Triangle> GetBridgePillarTriangles(EdgeData edge)
    {
        var triangles = new List<Triangle>();
        _terrainSampler = World is { } world ? SurfaceHeightSampler.ForTerrain(world) : null;
        var totalLength = edge.CenterTotal;
        if (totalLength <= 1e-6)
        {
            return triangles;
        }

        var spacing = Math.Max(0.1, BridgePillarDistance);
        var margin = Math.Min(spacing * 0.5, totalLength * 0.5);
        var rowDistances = new List<double>();
        for (var d = margin; d <= totalLength - margin + 1e-6; d += spacing)
        {
            rowDistances.Add(d);
        }

        if (rowDistances.Count == 0)
        {
            rowDistances.Add(totalLength * 0.5);
        }

        var (swStart, swEnd) = (GetResolvedSidewalkWidth(0), GetResolvedSidewalkWidth(1));
        var (chStart, chEnd) = (GetResolvedCurbHeight(0), GetResolvedCurbHeight(1));
        var (dtStart, dtEnd) = (GetResolvedBridgeDeckThickness(0), GetResolvedBridgeDeckThickness(1));
        var lateralFractions = GetBridgePillarLateralFractions();
        var halfPillarWidth = Math.Max(0.05, BridgePillarWidth * 0.5);
        foreach (var distance in rowDistances)
        {
            if (SampleEdgeAtDistance(edge, distance) is not { } sample)
            {
                continue;
            }

            var context = new GeometryLineContext(sample.HalfWidth,
                swStart + ((swEnd - swStart) * sample.T),
                chStart + ((chEnd - chStart) * sample.T),
                dtStart + ((dtEnd - dtStart) * sample.T),
                0, 0, 0, 1, CrownAt(sample.T));
            var maxOffset = Math.Max(0, BridgeOuterHalfWidth(context) - BridgePillarInset - halfPillarWidth);
            foreach (var lateralFraction in lateralFractions)
            {
                var offset = -maxOffset + (2 * maxOffset * lateralFraction);
                var pillarCenter = sample.Origin + (sample.Right * offset);
                var deckTopY = sample.Origin.Y + DeckTopHeightAtLateral(offset, context);
                var topY = deckTopY - Math.Max(0.05, context.DeckThickness);
                if (TerrainHeightAt(pillarCenter.X, pillarCenter.Z) is not { } terrainY || terrainY >= topY - 0.01)
                {
                    continue;
                }

                if (BridgePillarShape == "circular")
                {
                    AddCircularPillarTriangles(triangles, pillarCenter, topY, halfPillarWidth, Math.Max(3, BridgePillarSegments), terrainY);
                }
                else
                {
                    AddBoxPillarTriangles(triangles, pillarCenter, topY, halfPillarWidth, terrainY);
                }
            }
        }

        ApplyUVTransform("bridgePillars", triangles);
        return triangles;
    }

    private List<double> GetBridgePillarLateralFractions()
    {
        var count = Math.Max(1, BridgePillarCount);
        return count switch
        {
            1 => [0.5],
            2 => [0, 1],
            _ => [.. Enumerable.Range(0, count).Select(i => i / (double)(count - 1))],
        };
    }

    private static (Vector3 Origin, Vector3 Right, double HalfWidth, double T)? SampleEdgeAtDistance(EdgeData edge, double distance)
    {
        var pointCount = edge.Points.Count;
        if (pointCount < 2)
        {
            return null;
        }

        var target = MathUtil.Clamp(distance, 0, edge.CenterTotal);
        var segIndex = pointCount - 2;
        for (var i = 0; i < pointCount - 1; i++)
        {
            if (target <= edge.CenterCumDist[i + 1])
            {
                segIndex = i;
                break;
            }
        }

        var segStart = edge.CenterCumDist[segIndex];
        var segLength = edge.CenterCumDist[segIndex + 1] - segStart;
        var alpha = segLength > 1e-6 ? (target - segStart) / segLength : 0;
        var origin = edge.Points[segIndex].LerpUnclamped(edge.Points[segIndex + 1], alpha);
        var right = edge.RightVecs[segIndex].LerpUnclamped(edge.RightVecs[segIndex + 1], alpha);
        if (right.SqrMagnitude < 1e-8)
        {
            right = edge.RightVecs[segIndex];
        }

        return (origin, right.Normalized, MathUtil.Lerp(edge.HalfWidths[segIndex], edge.HalfWidths[segIndex + 1], alpha), (segIndex + alpha) / (pointCount - 1));
    }

    /// <summary>The terrain below (x, z), or null where there is none (a gap in the terrain, no terrain at all).</summary>
    private double? TerrainHeightAt(double x, double z) => _terrainSampler?.Sample(x, z);

    private double PillarBottomY(double x, double z, double fallbackY) => (TerrainHeightAt(x, z) ?? fallbackY) - PillarEmbedDepth;

    private void AddBoxPillarTriangles(List<Triangle> output, Vector3 center, double topY, double halfSize, double fallbackY)
    {
        var (x0, x1, z0, z1) = (center.X - halfSize, center.X + halfSize, center.Z - halfSize, center.Z + halfSize);
        var y0 = PillarBottomY(x0, z0, fallbackY);
        var y1 = PillarBottomY(x1, z0, fallbackY);
        var y2 = PillarBottomY(x1, z1, fallbackY);
        var y3 = PillarBottomY(x0, z1, fallbackY);
        Vector3 b0 = new(x0, y0, z0), b1 = new(x1, y1, z0), b2 = new(x1, y2, z1), b3 = new(x0, y3, z1);
        Vector3 t0 = new(x0, topY, z0), t1 = new(x1, topY, z0), t2 = new(x1, topY, z1), t3 = new(x0, topY, z1);
        PushPillarQuad(output, b0, b1, t1, t0, 0, 1, topY - Math.Min(y0, y1));
        PushPillarQuad(output, b1, b2, t2, t1, 0, 1, topY - Math.Min(y1, y2));
        PushPillarQuad(output, b2, b3, t3, t2, 0, 1, topY - Math.Min(y2, y3));
        PushPillarQuad(output, b3, b0, t0, t3, 0, 1, topY - Math.Min(y3, y0));
    }

    private void AddCircularPillarTriangles(List<Triangle> output, Vector3 center, double topY, double radius, int segments, double fallbackY)
    {
        for (var i = 0; i < segments; i++)
        {
            var a0 = i / (double)segments * Math.PI * 2;
            var a1 = (i + 1) / (double)segments * Math.PI * 2;
            var (x0, z0) = (center.X + (Math.Cos(a0) * radius), center.Z + (Math.Sin(a0) * radius));
            var (x1, z1) = (center.X + (Math.Cos(a1) * radius), center.Z + (Math.Sin(a1) * radius));
            var y0 = PillarBottomY(x0, z0, fallbackY);
            var y1 = PillarBottomY(x1, z1, fallbackY);
            PushPillarQuad(output, new Vector3(x0, y0, z0), new Vector3(x1, y1, z1), new Vector3(x1, topY, z1), new Vector3(x0, topY, z0),
                i / (double)segments, (i + 1) / (double)segments, topY - Math.Min(y0, y1));
        }
    }

    private static void PushPillarQuad(List<Triangle> output, Vector3 bottomA, Vector3 bottomB, Vector3 topB, Vector3 topA, double u0, double u1, double height)
    {
        var vBottom = Math.Max(0.01, height);
        output.Add(new Triangle(bottomA, topB, bottomB, new Vector2(u0, vBottom), new Vector2(u1, 0), new Vector2(u1, vBottom)));
        output.Add(new Triangle(bottomA, topA, topB, new Vector2(u0, vBottom), new Vector2(u0, 0), new Vector2(u1, 0)));
    }

    private EdgeData ComputeEdgeData(int? divisionsOverride)
    {
        var points = GetBezierCurvePoints(divisionsOverride);
        var halfWidthStart = GetResolvedHalfWidth(0);
        var halfWidthEnd = GetResolvedHalfWidth(1);
        var rightVecs = new List<Vector3>();
        var halfWidths = new List<double>();
        for (var i = 0; i < points.Count; i++)
        {
            var t = i / (double)(points.Count - 1);
            halfWidths.Add(halfWidthStart + ((halfWidthEnd - halfWidthStart) * t));
            rightVecs.Add(RightAt(points, i));
        }

        var leftEdge = points.Select((p, i) => p + (rightVecs[i] * -halfWidths[i])).ToList();
        var rightEdge = points.Select((p, i) => p + (rightVecs[i] * halfWidths[i])).ToList();
        var leftCum = new List<double> { 0 };
        var rightCum = new List<double> { 0 };
        var centerCum = new List<double> { 0 };
        for (var i = 1; i < points.Count; i++)
        {
            leftCum.Add(leftCum[i - 1] + leftEdge[i].DistanceTo(leftEdge[i - 1]));
            rightCum.Add(rightCum[i - 1] + rightEdge[i].DistanceTo(rightEdge[i - 1]));
            centerCum.Add(centerCum[i - 1] + points[i].DistanceTo(points[i - 1]));
        }

        // Normalize so both edges end at the shorter edge's length.
        var leftTotal = leftCum[^1];
        var rightTotal = rightCum[^1];
        var maxEdgeLength = Math.Min(leftTotal, rightTotal);
        for (var i = 0; i < leftCum.Count; i++)
        {
            leftCum[i] = leftTotal > 0 ? leftCum[i] / leftTotal * maxEdgeLength : 0;
            rightCum[i] = rightTotal > 0 ? rightCum[i] / rightTotal * maxEdgeLength : 0;
        }

        return new EdgeData(points, rightVecs, halfWidths, leftCum, rightCum, centerCum, centerCum[^1]);
    }
}
