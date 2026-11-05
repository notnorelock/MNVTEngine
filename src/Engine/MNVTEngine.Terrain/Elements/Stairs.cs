using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshing;

namespace MNVTEngine.Terrain.Elements;

/// <summary>A compact closed stair prism between a bottom and a top node, with optional low-poly handrails.</summary>
public sealed class Stairs : WorldElement
{
    private const double MinRun = 0.1;
    private const double MinRise = 0.1;
    private static readonly int[] LodStepDivisors = [1, 2, 4, 8];
    private static readonly double[] LodPostMultipliers = [1, 1.5, 2.5, 4];
    private readonly Dictionary<string, UVTransform> _uvTransforms = new(StringComparer.Ordinal) { ["steps"] = new(), ["railings"] = new() };

    private sealed record StairFrame(Vec3 Bottom, Vec3 Top, Vec3 Forward, Vec3 Right, double Run, double Rise);

    public Stairs(Vec3 bottom, Vec3 top)
    {
        var safeTop = top.Y < bottom.Y + MinRise ? top.WithY(bottom.Y + 3) : top;
        SetNode(0, new WorldNode(bottom));
        SetNode(1, new WorldNode(safeTop));
    }

    public WorldNode NodeA => Nodes[0];

    public WorldNode NodeB => Nodes[1];

    public double Width { get; set; } = 3;

    public int StepCount { get; set; } = 12;

    public double FoundationDepth { get; set; } = 0.15;

    /// <summary>none, left, right or both.</summary>
    public string RailingSide { get; set; } = "both";

    public double RailingHeight { get; set; } = 1.05;

    public double RailingThickness { get; set; } = 0.08;

    public double RailingPostSpacing { get; set; } = 1.5;

    public bool MidRail { get; set; } = true;

    public bool CutTerrain { get; set; } = true;

    public override bool CastsShadows => true;

    /// <summary>Untextured stairs are stone-coloured with dark railings.</summary>
    public override MaterialHint? GetMaterialHint(string groupName) => groupName switch
    {
        "steps" => new MaterialHint(0x8b8377),
        "railings" => new MaterialHint(0x30353a),
        _ => null,
    };

    public override void Update()
    {
        Width = Math.Max(0.2, Width);
        StepCount = Math.Clamp(StepCount, 1, 128);
        FoundationDepth = Math.Max(0.02, FoundationDepth);
        RailingHeight = Math.Max(0.2, RailingHeight);
        RailingThickness = MathUtil.Clamp(RailingThickness, 0.02, Math.Max(0.02, Width * 0.25));
        RailingPostSpacing = Math.Max(0.25, RailingPostSpacing);
        if (NodeB.Position.Y < NodeA.Position.Y + MinRise)
        {
            NodeB.Position = NodeB.Position.WithY(NodeA.Position.Y + MinRise);
        }

        base.Update();
    }

    protected override List<GeometryGroup> GetGeometry() => BuildGeometry(World?.LodPreviewLevel ?? 0);

    public List<GeometryGroup> GetExportGeometry(int lodIndex) => BuildGeometry(lodIndex);

    private List<GeometryGroup> BuildGeometry(int lodIndex)
    {
        if (GetFrame() is not { } frame)
        {
            return [new GeometryGroup("steps", []), new GeometryGroup("railings", [])];
        }

        var lod = Math.Clamp(lodIndex, 0, LodStepDivisors.Length - 1);
        var steps = (int)Math.Max(1, Math.Ceiling(StepCount / (double)LodStepDivisors[lod]));
        var stepTriangles = new List<Triangle>();
        var railTriangles = new List<Triangle>();
        var baseY = frame.Bottom.Y - FoundationDepth;
        var halfWidth = Width / 2;
        var stepDepth = frame.Run / steps;
        var stepRise = frame.Rise / steps;
        for (var index = 0; index < steps; index++)
        {
            var d0 = index * stepDepth;
            var d1 = (index + 1) * stepDepth;
            var topY = frame.Bottom.Y + ((index + 1) * stepRise);
            var previousY = index == 0 ? baseY : frame.Bottom.Y + (index * stepRise);
            var startLeft = FramePoint(frame, d0, -halfWidth, topY);
            var startRight = FramePoint(frame, d0, halfWidth, topY);
            var endLeft = FramePoint(frame, d1, -halfWidth, topY);
            var endRight = FramePoint(frame, d1, halfWidth, topY);
            AddQuadFacing(stepTriangles, startLeft, startRight, endRight, endLeft, Vec3.UnitY, Width, stepDepth);
            AddQuadFacing(stepTriangles, FramePoint(frame, d0, -halfWidth, previousY), FramePoint(frame, d0, halfWidth, previousY), startRight, startLeft,
                -frame.Forward, Width, Math.Max(0.001, topY - previousY));
            AddQuadFacing(stepTriangles, FramePoint(frame, d1, -halfWidth, baseY), FramePoint(frame, d0, -halfWidth, baseY), startLeft, endLeft,
                -frame.Right, stepDepth, topY - baseY);
            AddQuadFacing(stepTriangles, FramePoint(frame, d0, halfWidth, baseY), FramePoint(frame, d1, halfWidth, baseY), endRight, startRight,
                frame.Right, stepDepth, topY - baseY);
        }

        AddQuadFacing(stepTriangles,
            FramePoint(frame, frame.Run, -halfWidth, baseY), FramePoint(frame, frame.Run, -halfWidth, frame.Top.Y),
            FramePoint(frame, frame.Run, halfWidth, frame.Top.Y), FramePoint(frame, frame.Run, halfWidth, baseY),
            frame.Forward, Width, frame.Top.Y - baseY);
        AddQuadFacing(stepTriangles,
            FramePoint(frame, 0, -halfWidth, baseY), FramePoint(frame, frame.Run, -halfWidth, baseY),
            FramePoint(frame, frame.Run, halfWidth, baseY), FramePoint(frame, 0, halfWidth, baseY),
            -Vec3.UnitY, frame.Run, Width);
        if (RailingSide is "left" or "both")
        {
            AddRailing(railTriangles, frame, -halfWidth + (RailingThickness * 0.5), steps, lod);
        }

        if (RailingSide is "right" or "both")
        {
            AddRailing(railTriangles, frame, halfWidth - (RailingThickness * 0.5), steps, lod);
        }

        ApplyUV("steps", stepTriangles);
        ApplyUV("railings", railTriangles);
        return [new GeometryGroup("steps", stepTriangles), new GeometryGroup("railings", railTriangles)];
    }

    private void AddRailing(List<Triangle> output, StairFrame frame, double side, int steps, int lod)
    {
        var spacing = RailingPostSpacing * LodPostMultipliers[lod];
        var segmentCount = (int)Math.Max(1, Math.Min(256, Math.Ceiling(frame.Run / spacing)));
        var firstTreadY = frame.Bottom.Y + (frame.Rise / steps);
        var railStart = FramePoint(frame, 0, side, firstTreadY + RailingHeight);
        var railEnd = FramePoint(frame, frame.Run, side, frame.Top.Y + RailingHeight);
        for (var index = 0; index <= segmentCount; index++)
        {
            var distance = frame.Run * index / segmentCount;
            var stepIndex = Math.Min(steps - 1, (int)Math.Floor(distance / frame.Run * steps));
            var surfaceY = frame.Bottom.Y + ((stepIndex + 1) * frame.Rise / steps);
            var railY = MathUtil.Lerp(railStart.Y, railEnd.Y, distance / frame.Run);
            AddBeam(output, FramePoint(frame, distance, side, surfaceY), FramePoint(frame, distance, side, railY), RailingThickness, frame.Right);
        }

        AddBeam(output, railStart, railEnd, RailingThickness, frame.Right);
        if (MidRail && lod < 3)
        {
            var offset = new Vec3(0, -RailingHeight * 0.48, 0);
            AddBeam(output, railStart + offset, railEnd + offset, RailingThickness * 0.8, frame.Right);
        }
    }

    private static void AddBeam(List<Triangle> output, Vec3 start, Vec3 end, double size, Vec3 lateralHint)
    {
        var axis = end - start;
        var length = axis.Length;
        if (length < 1e-6)
        {
            return;
        }

        axis /= length;
        var side = lateralHint + (axis * -lateralHint.Dot(axis));
        if (side.LengthSquared < 1e-8)
        {
            side = Vec3.UnitX + (axis * -axis.X);
        }

        side = side.Normalized();
        var normal = axis.Cross(side).Normalized();
        var half = size / 2;
        Vec3 Corner(Vec3 center, double sideSign, double normalSign) => center + (side * (sideSign * half)) + (normal * (normalSign * half));
        var (a0, a1, a2, a3) = (Corner(start, -1, -1), Corner(start, 1, -1), Corner(start, 1, 1), Corner(start, -1, 1));
        var (b0, b1, b2, b3) = (Corner(end, -1, -1), Corner(end, 1, -1), Corner(end, 1, 1), Corner(end, -1, 1));
        AddQuadFacing(output, a0, a3, a2, a1, -axis, size, size);
        AddQuadFacing(output, b0, b1, b2, b3, axis, size, size);
        AddQuadFacing(output, a0, a1, b1, b0, -normal, length, size);
        AddQuadFacing(output, a1, a2, b2, b1, side, length, size);
        AddQuadFacing(output, a2, a3, b3, b2, normal, length, size);
        AddQuadFacing(output, a3, a0, b0, b3, -side, length, size);
    }

    private static void AddQuadFacing(List<Triangle> output, Vec3 a, Vec3 b, Vec3 c, Vec3 d, Vec3 outward, double width, double height)
    {
        var normal = (b - a).Cross(c - a);
        Vec3[] points = normal.Dot(outward) < 0 ? [a, d, c, b] : [a, b, c, d];
        output.Add(new Triangle(points[0], points[1], points[2], new Vec2(0, 0), new Vec2(width, 0), new Vec2(width, height)));
        output.Add(new Triangle(points[0], points[2], points[3], new Vec2(0, 0), new Vec2(width, height), new Vec2(0, height)));
    }

    private StairFrame? GetFrame()
    {
        var bottom = NodeA.Position;
        var top = NodeB.Position;
        var horizontal = new Vec3(top.X - bottom.X, 0, top.Z - bottom.Z);
        var run = horizontal.Length;
        if (run < MinRun)
        {
            return null;
        }

        var forward = horizontal / run;
        return new StairFrame(bottom, top, forward, forward.Cross(Vec3.UnitY).Normalized(), run, Math.Max(MinRise, top.Y - bottom.Y));
    }

    private static Vec3 FramePoint(StairFrame frame, double distance, double side, double y) =>
        (frame.Bottom + (frame.Forward * distance) + (frame.Right * side)).WithY(y);

    private void ApplyUV(string group, List<Triangle> triangles)
    {
        var t = _uvTransforms[group];
        Vec2 Map(Vec2 uv) => new((uv.X * t.ScaleX) + t.OffsetX, (uv.Y * t.ScaleY) + t.OffsetY);
        foreach (var triangle in triangles)
        {
            triangle.UvA = Map(triangle.UvA);
            triangle.UvB = Map(triangle.UvB);
            triangle.UvC = Map(triangle.UvC);
        }
    }

    public override NodeBasis GetNodeBasis(int index) =>
        GetFrame() is { } frame ? new NodeBasis(frame.Forward, frame.Right, Vec3.UnitY) : new NodeBasis(Vec3.UnitX, Vec3.UnitZ, Vec3.UnitY);

    public override double GetWidth() => Width;

    public override ConnectionProfile GetFixedConnectionProfile(int index) => new(Width / 2, 0, 0);

    public override bool CutsTerrainSurface() => CutTerrain;

    public override IReadOnlyList<OccupiedTriangle> GetOccupiedArea()
    {
        if (GetFrame() is not { } frame)
        {
            return [];
        }

        var half = Width / 2;
        var bottomLeft = FramePoint(frame, 0, -half, frame.Bottom.Y);
        var bottomRight = FramePoint(frame, 0, half, frame.Bottom.Y);
        var topLeft = FramePoint(frame, frame.Run, -half, frame.Top.Y);
        var topRight = FramePoint(frame, frame.Run, half, frame.Top.Y);
        return [new(bottomLeft, bottomRight, topRight, 70), new(bottomLeft, topRight, topLeft, 70)];
    }

    public override IReadOnlyList<string> GetUVGroups() => ["steps", "railings"];

    public override UVTransform GetUVTransform(string group) => _uvTransforms.TryGetValue(group, out var t) ? t.Clone() : base.GetUVTransform(group);

    public override void SetUVTransform(string group, UVTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (!_uvTransforms.ContainsKey(group))
        {
            return;
        }

        _uvTransforms[group] = new UVTransform
        {
            OffsetX = double.IsFinite(transform.OffsetX) ? transform.OffsetX : 0,
            OffsetY = double.IsFinite(transform.OffsetY) ? transform.OffsetY : 0,
            ScaleX = double.IsFinite(transform.ScaleX) ? Math.Max(0.05, transform.ScaleX) : 1,
            ScaleY = double.IsFinite(transform.ScaleY) ? Math.Max(0.05, transform.ScaleY) : 1,
        };
        Update();
    }

    public override PropertyDefinition GetProperties()
    {
        PropertySection NodeSection(string label, WorldNode node, int index)
        {
            var items = new List<PropertyItem>
            {
                new Vector3Property("Position", () => node.Position, v =>
                {
                    node.Update(v);
                    Update();
                }),
            };
            if (IsConnected(index))
            {
                items.Add(new ButtonProperty("Disconnect", () =>
                {
                    Disconnect(index);
                    OnPropertiesChanged?.Invoke();
                }));
            }

            return new PropertySection(label, items);
        }

        void Set(Action change)
        {
            change();
            Update();
        }

        var railings = new List<PropertyItem>
        {
            new SelectProperty("Sides", [new("None", "none"), new("Left", "left"), new("Right", "right"), new("Both", "both")], () => RailingSide, v =>
            {
                RailingSide = IsRailingSide(v) ? v : "both";
                Update();
                OnPropertiesChanged?.Invoke();
            }),
        };
        if (RailingSide != "none")
        {
            railings.Add(new NumberProperty("Height", () => RailingHeight, v => Set(() => RailingHeight = Math.Max(0.2, v)), 0.2, null, 0.05));
            railings.Add(new NumberProperty("Thickness", () => RailingThickness, v => Set(() => RailingThickness = Math.Max(0.02, v)), 0.02, null, 0.01));
            railings.Add(new NumberProperty("Post Spacing", () => RailingPostSpacing, v => Set(() => RailingPostSpacing = Math.Max(0.25, v)), 0.25, null, 0.1));
            railings.Add(new BooleanProperty("Mid Rail", () => MidRail, v => Set(() => MidRail = v)));
        }

        return new PropertyDefinition("Stairs", "◩",
        [
            NodeSection("Bottom", NodeA, 0),
            NodeSection("Top", NodeB, 1),
            new PropertySection("Stairs",
            [
                new NumberProperty("Width", () => Width, v => Set(() => Width = Math.Max(0.2, v)), 0.2, null, 0.1),
                new NumberProperty("Rise", () => NodeB.Position.Y - NodeA.Position.Y, v => Set(() => NodeB.Position = NodeB.Position.WithY(NodeA.Position.Y + Math.Max(MinRise, v))), MinRise, null, 0.1),
                new NumberProperty("Run Length", () => GetFrame()?.Run ?? 0, SetRunLength, MinRun, null, 0.1),
                new NumberProperty("Step Count", () => StepCount, v => Set(() => StepCount = Math.Max(1, (int)MathUtil.JsRound(v))), 1, 128, 1),
                new NumberProperty("Foundation Depth", () => FoundationDepth, v => Set(() => FoundationDepth = Math.Max(0.02, v)), 0.02, null, 0.05),
                new BooleanProperty("Cut Terrain", () => CutTerrain, v => Set(() => CutTerrain = v)),
                new ButtonProperty("Flip Direction", FlipDirection),
            ]),
            new PropertySection("Railings", railings),
        ]);
    }

    private void SetRunLength(double value)
    {
        var direction = GetFrame()?.Forward ?? Vec3.UnitX;
        var length = Math.Max(MinRun, value);
        NodeB.Position = new Vec3(NodeA.Position.X + (direction.X * length), NodeB.Position.Y, NodeA.Position.Z + (direction.Z * length));
        Update();
    }

    public void FlipDirection()
    {
        var bottom = NodeA.Position;
        var top = NodeB.Position;
        var rise = top.Y - bottom.Y;
        NodeA.Position = new Vec3(top.X, bottom.Y, top.Z);
        NodeB.Position = new Vec3(bottom.X, bottom.Y + rise, bottom.Z);
        Update();
    }

    public override ElementData Serialize(int id)
    {
        var (textures, rotations) = CollectTextureMaps();
        return new ElementData
        {
            Type = "stairs",
            Id = id,
            Nodes = [PointData.From(NodeA.Position), PointData.From(NodeB.Position)],
            Textures = textures,
            TextureRotations = rotations,
            UvTransforms = _uvTransforms.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal),
            StairsWidth = Width,
            StairsStepCount = StepCount,
            StairsFoundationDepth = FoundationDepth,
            StairsRailingSide = RailingSide,
            StairsRailingHeight = RailingHeight,
            StairsRailingThickness = RailingThickness,
            StairsRailingPostSpacing = RailingPostSpacing,
            StairsMidRail = MidRail,
            StairsCutTerrain = CutTerrain,
        };
    }

    public static Stairs Deserialize(ElementData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var bottom = data.Nodes.Count > 0 ? data.Nodes[0].ToVec3() : Vec3.Zero;
        var top = data.Nodes.Count > 1 ? data.Nodes[1].ToVec3() : new Vec3(4, 3, 0);
        var stairs = new Stairs(bottom, top)
        {
            Width = Math.Max(0.2, data.StairsWidth ?? 3),
            StepCount = Math.Clamp(data.StairsStepCount ?? 12, 1, 128),
            FoundationDepth = Math.Max(0.02, data.StairsFoundationDepth ?? 0.15),
            RailingSide = IsRailingSide(data.StairsRailingSide) ? data.StairsRailingSide! : "both",
            RailingHeight = Math.Max(0.2, data.StairsRailingHeight ?? 1.05),
            RailingThickness = Math.Max(0.02, data.StairsRailingThickness ?? 0.08),
            RailingPostSpacing = Math.Max(0.25, data.StairsRailingPostSpacing ?? 1.5),
            MidRail = data.StairsMidRail ?? true,
            CutTerrain = data.StairsCutTerrain ?? true,
        };
        foreach (var (group, transform) in data.UvTransforms ?? [])
        {
            if (stairs._uvTransforms.ContainsKey(group))
            {
                stairs._uvTransforms[group] = transform.Clone();
            }
        }

        return stairs;
    }

    private static bool IsRailingSide(string? value) => value is "none" or "left" or "right" or "both";
}
