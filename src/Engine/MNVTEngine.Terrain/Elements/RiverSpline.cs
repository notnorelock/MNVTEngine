using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshing;

namespace MNVTEngine.Terrain.Elements;

/// <summary>
/// A river works like a road (nodes, curve points, Divisions, connectable ends, width), but its water is an overlay:
/// instead of cutting a hole it forces the terrain to conform to a lowered bed with banks at the requested slope.
/// </summary>
public sealed class RiverSpline(Vec3 positionA, Vec3 positionB) : SplineElement(positionA, positionB, NodeKind.River)
{
    public const uint WaterColor = 0x2f6fa8;
    private const double WaterSurfaceOffset = 0.025;

    /// <summary>How far past 1 Irregularity can go: above 1 the bank noise reads as sharp corners rather than a wander.</summary>
    public const double MaxIrregularity = 3;

    public double Width { get; set; } = 4;

    /// <summary>How steeply the surrounding terrain drops to the water: higher is a narrow, steep-banked channel.</summary>
    public double BankSlope { get; set; } = 70;

    /// <summary>Rounds the transition at the bed and the terrain edge: 0 is a straight slope, 1 fully eased.</summary>
    public double BankSmoothing { get; set; } = 0.65;

    /// <summary>Deterministic variation of the bank outline and profile, derived from world position (no flicker).</summary>
    public double IrregularityLevel { get; set; }

    /// <summary>Local river terrain sampling, without raising the detail of the whole terrain.</summary>
    public double DetailLevel { get; set; } = 1;

    public override bool CutsTerrainSurface() => false;

    public override double GetWidth() => Width;

    public override MaterialHint? GetMaterialHint(string groupName) =>
        new(WaterColor, Opacity: 0.8, DoubleSided: true, DepthWrite: false, Roughness: 0.15, Metalness: 0.05);

    public override bool DoubleSided => true;

    // Two components: a slow "wander" that meanders the bank gently even at low irregularity, and a "corner" component
    // folded through sign(x)*|x|^0.4 so its waveform snaps toward plateaus instead of rounding off.
    private static double GetBankNoise(Vec3 position, double channel)
    {
        var wander = Math.Sin((position.X * 0.37) + (position.Z * 0.48) + (channel * 2.17));
        var cornerRaw = Math.Sin((position.X * 1.9) - (position.Z * 2.6) + (channel * 5.3))
            + (0.5 * Math.Sin((position.X * 4.1) + (position.Z * 3.4) + (channel * 8.7)));
        var cornerFold = Math.Sign(cornerRaw) * Math.Pow(Math.Min(1.5, Math.Abs(cornerRaw)) / 1.5, 0.4);
        return MathUtil.Clamp((wander * 0.5) + (cornerFold * 0.5), -1, 1);
    }

    /// <summary>The water's half-width on each side, perturbed by bank noise; the bed and banks are built from these.</summary>
    private (double Left, double Right) GetChannelHalfWidths(Vec3 position)
    {
        var nominal = Math.Max(0.05, Width / 2);
        var irregularity = MathUtil.Clamp(IrregularityLevel, 0, MaxIrregularity);
        if (irregularity <= 0)
        {
            return (nominal, nominal);
        }

        double Perturb(double channel)
        {
            var fraction = MathUtil.Clamp(irregularity * 0.3 * GetBankNoise(position, channel), -0.85, 1.5);
            return Math.Max(nominal * 0.15, nominal * (1 + fraction));
        }

        return (Perturb(-1), Perturb(1));
    }

    /// <summary>
    /// The centerline, sampled finely enough (Detail Level plus Divisions) for the bank noise to show. Overrides only
    /// coarsen the rendered water for an LOD; terrain shaping and the footprint always use full fidelity.
    /// </summary>
    private List<(Vec3 Point, Vec3 Right)> GetCenterlineSamples(double? detailOverride = null, int? divisionsOverride = null)
    {
        var p0 = NodeA.Position;
        var p1 = CurvePointA?.Position ?? p0;
        var p2 = CurvePointB?.Position ?? NodeB.Position;
        var p3 = NodeB.Position;
        var controlLength = p0.DistanceTo(p1) + p1.DistanceTo(p2) + p2.DistanceTo(p3);
        var detailLevel = MathUtil.Clamp(detailOverride ?? DetailLevel, 0.1, 4);
        var targetSpacing = 1.5 / detailLevel;

        // Detail gives automatic coverage for long channels, Divisions always adds explicit curve segments.
        var automaticDivisions = (int)Math.Min(64, Math.Max(0, Math.Ceiling(controlLength / targetSpacing) - 1));
        var points = Bezier.SampleCubic(p0, p1, p2, p3, automaticDivisions + (divisionsOverride ?? Divisions));
        return points.Select((point, index) =>
        {
            var previous = points[Math.Max(0, index - 1)];
            var next = points[Math.Min(points.Count - 1, index + 1)];
            var forward = (next - previous).Normalized();
            var right = forward.Cross(Vec3.UnitY).Normalized();
            if (right.LengthSquared < 1e-8)
            {
                right = GetRightAtIndex(points, index);
            }

            return (point, right);
        }).ToList();
    }

    /// <summary>
    /// The lowered bed and its banks as terrain constraints. Bank Slope is the real angle between bank and horizontal
    /// (run = depth / tan(angle)); a non-zero LOD thins rows and profiles together.
    /// </summary>
    public IReadOnlyList<TerrainCutPointInput> GetSampledTerrainPoints(double? terrainSurfaceY = null, int lodIndex = 0)
    {
        var surfaceY = terrainSurfaceY ?? NodeA.Position.Y;
        var (detailOverride, divisionsOverride) = GetLodOverrides(lodIndex);
        var samplePoints = GetCenterlineSamples(detailOverride, divisionsOverride);
        var detailLevel = MathUtil.Clamp(detailOverride ?? DetailLevel, 0.1, 4);
        var targetSpacing = 1.5 / detailLevel;
        var minimumCrossSteps = detailLevel < 0.5 ? 1 : 2;
        var crossSteps = (int)Math.Min(12, Math.Max(minimumCrossSteps, Math.Ceiling(Width / targetSpacing)));
        var bedDepth = MathUtil.Clamp(Width * 0.25, 0.35, 4);
        var bankSlopeDegrees = MathUtil.Clamp(BankSlope, 1, 89);
        var bankSmoothing = MathUtil.Clamp(BankSmoothing, 0, 1);
        var irregularity = MathUtil.Clamp(IrregularityLevel, 0, MaxIrregularity);
        var samples = new List<TerrainCutPointInput>();
        foreach (var (point, right) in samplePoints)
        {
            var (leftHalfWidth, rightHalfWidth) = GetChannelHalfWidths(point);
            var bedY = point.Y - bedDepth;
            var verticalDepth = Math.Max(0.01, surfaceY - bedY);
            var straightBankRun = verticalDepth / Math.Tan(MathUtil.DegToRad(bankSlopeDegrees));

            // Smoothstep's maximum derivative is 1.5: expanding the run by as much keeps smoothing from steepening it.
            var bankRun = straightBankRun * (1 + (0.5 * bankSmoothing));
            var leftNoise = GetBankNoise(point, -1);
            var rightNoise = GetBankNoise(point, 1);

            // Irregularity only widens the nominal profile, so it cannot make a bank steeper than Bank Slope.
            double IrregularRun(double noise) => bankRun * (1 + (irregularity * 0.35 * (noise + 1) * 0.5));
            var leftBankRun = IrregularRun(leftNoise);
            var rightBankRun = IrregularRun(rightNoise);
            for (var cross = 0; cross <= crossSteps; cross++)
            {
                var crossT = cross / (double)crossSteps;
                var offset = MathUtil.Lerp(-leftHalfWidth, rightHalfWidth, crossT);
                samples.Add(new TerrainCutPointInput((point + (right * offset)).WithY(bedY), MathUtil.Lerp(leftBankRun, rightBankRun, crossT), bankSlopeDegrees, bankSmoothing));
            }

            // Explicit bank rows keep a narrow river from becoming one huge triangle when the terrain is coarser.
            var profileSteps = (int)Math.Min(6, Math.Max(1, MathUtil.JsRound((detailLevel * 2) + 1)));
            foreach (var side in new[] { -1, 1 })
            {
                var sideNoise = side < 0 ? leftNoise : rightNoise;
                var sideBankRun = side < 0 ? leftBankRun : rightBankRun;
                var sideHalfWidth = side < 0 ? leftHalfWidth : rightHalfWidth;
                for (var step = 1; step <= profileSteps; step++)
                {
                    var t = step / (double)profileSteps;
                    var profileT = MathUtil.Lerp(t, MathUtil.Smoothstep(t), bankSmoothing);
                    var verticalVariation = verticalDepth * Math.Min(irregularity, 1.5) * 0.08 * sideNoise * Math.Sin(Math.PI * t);
                    var profileY = MathUtil.Clamp(MathUtil.Lerp(bedY, surfaceY, profileT) + verticalVariation, bedY, surfaceY);
                    samples.Add(new TerrainCutPointInput((point + (right * (side * (sideHalfWidth + (sideBankRun * t))))).WithY(profileY), 0, ProfileOnly: true));
                }
            }
        }

        return samples;
    }

    private Vec3 GetRightAtIndex(List<Vec3> points, int index)
    {
        if (index == 0)
        {
            return GetResolvedNodeBasis(0).Right;
        }

        if (index == points.Count - 1)
        {
            return GetResolvedNodeBasis(1).Right;
        }

        var direction = (points[index + 1] - points[index - 1]).Normalized();
        return direction.Cross(Vec3.UnitY).Normalized();
    }

    /// <summary>The water surface: the same centerline and half-widths the terrain sampling uses, so they meet exactly.</summary>
    private List<Triangle> BuildSurfaceTriangles(double? detailOverride = null, int? divisionsOverride = null)
    {
        var samples = GetCenterlineSamples(detailOverride, divisionsOverride);
        if (samples.Count < 2)
        {
            return [];
        }

        var triangles = new List<Triangle>();
        var lift = new Vec3(0, WaterSurfaceOffset, 0);
        for (var i = 0; i < samples.Count - 1; i++)
        {
            var (a, b) = (samples[i], samples[i + 1]);
            var widthsA = GetChannelHalfWidths(a.Point);
            var widthsB = GetChannelHalfWidths(b.Point);
            var aLeft = a.Point + (a.Right * -widthsA.Left) + lift;
            var aRight = a.Point + (a.Right * widthsA.Right) + lift;
            var bLeft = b.Point + (b.Right * -widthsB.Left) + lift;
            var bRight = b.Point + (b.Right * widthsB.Right) + lift;
            var uA = i / (double)(samples.Count - 1);
            var uB = (i + 1) / (double)(samples.Count - 1);
            triangles.Add(new Triangle(aLeft, aRight, bRight, new Vec2(0, uA), new Vec2(1, uA), new Vec2(1, uB)));
            triangles.Add(new Triangle(aLeft, bRight, bLeft, new Vec2(0, uA), new Vec2(1, uB), new Vec2(0, uB)));
        }

        return triangles;
    }

    protected override List<GeometryGroup> GetGeometry() => GetExportGeometry(World?.LodPreviewLevel ?? 0);

    /// <summary>The water at an export LOD level, without touching terrain shaping or the footprint.</summary>
    public List<GeometryGroup> GetExportGeometry(int lodIndex)
    {
        var (detail, divisions) = GetLodOverrides(lodIndex);
        return [new GeometryGroup("water", BuildSurfaceTriangles(detail, divisions))];
    }

    private (double? Detail, int? Divisions) GetLodOverrides(int lodIndex) =>
        lodIndex <= 0 ? (null, null) : (LodLevels.DetailLevel(DetailLevel, lodIndex), LodLevels.Divisions(Divisions, lodIndex));

    public override IReadOnlyList<OccupiedTriangle> GetOccupiedArea()
    {
        var bankSlopeDegrees = MathUtil.Clamp(BankSlope, 1, 89);
        var projected = new List<OccupiedTriangle>();
        foreach (var tri in BuildSurfaceTriangles())
        {
            var area = Math.Abs(((tri.B.X - tri.A.X) * (tri.C.Z - tri.A.Z)) - ((tri.B.Z - tri.A.Z) * (tri.C.X - tri.A.X))) * 0.5;
            if (area >= 1e-8)
            {
                projected.Add(new OccupiedTriangle(tri.A, tri.B, tri.C, bankSlopeDegrees));
            }
        }

        return projected;
    }

    public override PropertyDefinition GetProperties() => new("River Spline", "≈",
    [
        new PropertySection("River",
        [
            new NumberProperty("Width", () => Width, v => Set(() => Width = Math.Max(0.1, v)), 0.1, 100, 0.1),
            new NumberProperty("Divisions", () => Divisions, v => Divisions = Math.Max(0, (int)MathUtil.JsRound(v)), 0, 32, 1),
            new NumberProperty("Bank Slope", () => BankSlope, v => Set(() => BankSlope = MathUtil.Clamp(v, 1, 89)), 1, 89, 1),
            new NumberProperty("Slope Smoothing", () => BankSmoothing, v => Set(() => BankSmoothing = MathUtil.Clamp(v, 0, 1)), 0, 1, 0.05),
            new NumberProperty("Irregularity Level", () => IrregularityLevel, v => Set(() => IrregularityLevel = MathUtil.Clamp(v, 0, MaxIrregularity)), 0, MaxIrregularity, 0.05),
            new NumberProperty("Detail Level", () => DetailLevel, v => Set(() => DetailLevel = MathUtil.Clamp(v, 0.1, 4)), 0.1, 4, 0.1),
        ]),
    ]);

    private void Set(Action change)
    {
        change();
        Update();
    }

    public override ElementData Serialize(int id)
    {
        var data = new ElementData
        {
            Type = "river",
            Id = id,
            Width = Width,
            RiverBankSlope = BankSlope,
            RiverBankSmoothing = BankSmoothing,
            RiverIrregularityLevel = IrregularityLevel,
            RiverDetailLevel = DetailLevel,
        };
        SerializeCurve(data);
        return data;
    }

    public static RiverSpline Deserialize(ElementData data)
    {
        var (a, b) = EndPositions(data, new Vec3(1, 0, 0));
        var river = new RiverSpline(a, b)
        {
            Width = Math.Max(0.1, data.Width ?? 4),
            BankSlope = MathUtil.Clamp(data.RiverBankSlope ?? 70, 1, 89),
            BankSmoothing = MathUtil.Clamp(data.RiverBankSmoothing ?? 0.65, 0, 1),
            IrregularityLevel = MathUtil.Clamp(data.RiverIrregularityLevel ?? 0, 0, MaxIrregularity),
            DetailLevel = MathUtil.Clamp(data.RiverDetailLevel ?? 1, 0.1, 4),
        };
        river.DeserializeCurve(data);
        return river;
    }
}
