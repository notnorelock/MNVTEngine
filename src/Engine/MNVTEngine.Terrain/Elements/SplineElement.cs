using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Elements;

/// <summary>A line the editor draws over an element (a spline, or a curve handle from an end to its control point).</summary>
/// <param name="Color">Overrides the kind's colour (0xRRGGBB): road lane edges are white, lane dividers grey.</param>
public sealed record GuideLine(IReadOnlyList<Vec3> Points, NodeKind Kind, uint? Color = null);

/// <summary>How a geometry group is drawn: an untextured colour (sRGB, 0xRRGGBB), transparency and sidedness.</summary>
public sealed record MaterialHint(uint? Color = null, double Opacity = 1, bool DoubleSided = false, bool DepthWrite = true, double Roughness = 1, double Metalness = 0);

/// <summary>
/// An element between two end nodes with an optional cubic curve: Divisions above 0 adds two curve handles (at 1/3 and
/// 2/3 of the way) that bend it. Roads, rivers and terrain cut splines.
/// </summary>
public abstract class SplineElement : WorldElement
{
    private int _divisions;

    protected SplineElement(Vec3 positionA, Vec3 positionB, NodeKind endKind)
    {
        SetNode(0, new WorldNode(positionA, endKind));
        SetNode(1, new WorldNode(positionB, endKind));
    }

    public WorldNode NodeA => Nodes[0];

    public WorldNode NodeB => Nodes[1];

    public WorldNode? CurvePointA { get; private set; }

    public WorldNode? CurvePointB { get; private set; }

    public int Divisions
    {
        get => _divisions;
        set
        {
            _divisions = Math.Max(0, (int)MathUtil.JsRound(value));
            if (_divisions > 0)
            {
                if (CurvePointA is null)
                {
                    SetCurvePointA(NodeA.Position.Lerp(NodeB.Position, 1.0 / 3));
                }

                if (CurvePointB is null)
                {
                    SetCurvePointB(NodeA.Position.Lerp(NodeB.Position, 2.0 / 3));
                }
            }
            else
            {
                SetCurvePointA(null);
                SetCurvePointB(null);
            }

            Update();
        }
    }

    /// <summary>The divisions field without the side effects of <see cref="Divisions"/> (for subclasses' own rules).</summary>
    protected int RawDivisions
    {
        get => _divisions;
        set => _divisions = value;
    }

    public void SetCurvePointA(Vec3? point) => CurvePointA = SetCurvePoint(CurvePointA, point);

    public void SetCurvePointB(Vec3? point) => CurvePointB = SetCurvePoint(CurvePointB, point);

    private WorldNode? SetCurvePoint(WorldNode? current, Vec3? point)
    {
        if (point is { } position)
        {
            if (current is null)
            {
                current = new WorldNode(position, NodeKind.CurveHandle) { Parent = this };
                Attach(current);
            }
            else
            {
                current.Parent = this;
                current.Update(position);
            }

            return current;
        }

        if (current is not null)
        {
            Detach(current);
        }

        return null;
    }

    /// <summary>The fewest divisions the curve is sampled with (roads keep a middle cross-section for their crown).</summary>
    protected virtual int MinimumCurveDivisions => 0;

    /// <summary>The curve sampled at the current (or the given) divisions: <c>divisions + 2</c> points.</summary>
    protected List<Vec3> GetBezierCurvePoints(int? divisionsOverride = null) => Bezier.SampleCubic(
        NodeA.Position,
        CurvePointA?.Position ?? NodeA.Position,
        CurvePointB?.Position ?? NodeB.Position,
        NodeB.Position,
        Math.Max(MinimumCurveDivisions, divisionsOverride ?? _divisions));

    /// <summary>The spline and its handles, for the editor.</summary>
    public virtual IReadOnlyList<GuideLine> GetGuideLines()
    {
        var lines = new List<GuideLine>();
        if (CurvePointA is { } a && CurvePointB is { } b)
        {
            lines.Add(new GuideLine([NodeA.Position, a.Position], NodeKind.CurveHandle));
            lines.Add(new GuideLine([NodeB.Position, b.Position], NodeKind.CurveHandle));
        }

        return lines;
    }

    /// <summary>The basis at either end from the curve's first or last segment.</summary>
    public override NodeBasis GetNodeBasis(int index)
    {
        var points = GetBezierCurvePoints();
        var up = Vec3.UnitY;
        var forward = index == 0 ? (points[1] - points[0]).Normalized() : (points[^1] - points[^2]).Normalized();
        var right = forward.Cross(up).Normalized();
        return new NodeBasis(forward, right, up);
    }

    protected void SerializeCurve(ElementData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.Nodes = [PointData.From(NodeA.Position), PointData.From(NodeB.Position)];
        data.Divisions = Divisions;
        data.CurvePointA = CurvePointA is { } a ? PointData.From(a.Position) : null;
        data.CurvePointB = CurvePointB is { } b ? PointData.From(b.Position) : null;
    }

    /// <summary>TerrainHeaven's deserialize: Divisions creates the handles, saved handle positions then move them.</summary>
    protected void DeserializeCurve(ElementData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Divisions is > 0 and var divisions)
        {
            Divisions = divisions;
            if (data.CurvePointA is { } a)
            {
                SetCurvePointA(a.ToVec3());
            }

            if (data.CurvePointB is { } b)
            {
                SetCurvePointB(b.ToVec3());
            }
        }
    }

    protected static (Vec3 A, Vec3 B) EndPositions(ElementData data, Vec3 defaultB)
    {
        ArgumentNullException.ThrowIfNull(data);
        var a = data.Nodes.Count > 0 ? data.Nodes[0].ToVec3() : Vec3.Zero;
        var b = data.Nodes.Count > 1 ? data.Nodes[1].ToVec3() : defaultB;
        return (a, b);
    }
}
