using MNVTEngine.Mathematics;
using System.Globalization;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshing;
using TerrainTile = MNVTEngine.Terrain.Elements.Terrain;

namespace MNVTEngine.Terrain.Tests;

/// <summary>Ported from TerrainHeaven's Terrain.test.ts, TerrainGroupMesher.test.ts, TerrainTexturePaint.test.ts and RiverSpline.test.ts.</summary>
public sealed class TerrainElementTests
{
    private static List<Triangle> Triangles(WorldElement element) => element.Geometry.SelectMany(g => g.Triangles).ToList();

    private static IEnumerable<Vector3> Vertices(WorldElement element) => Triangles(element).SelectMany(t => new[] { t.A, t.B, t.C });

    private static double SteepestSlope(IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> triangles)
    {
        var worst = 0.0;
        foreach (var (a0, b0, c0) in triangles)
        {
            foreach (var (a, b) in new[] { (a0, b0), (b0, c0), (c0, a0) })
            {
                var run = MathUtil.Hypot(b.X - a.X, b.Z - a.Z);
                if (run >= 1e-6)
                {
                    worst = Math.Max(worst, Math.Abs(b.Y - a.Y) / run);
                }
            }
        }

        return worst;
    }

    private static double SteepestSlope(TerrainTile terrain) => SteepestSlope(Triangles(terrain).Select(t => (t.A, t.B, t.C)));

    private static double Area(TerrainTile terrain) => Triangles(terrain).Sum(t => Math.Abs(((t.B.X - t.A.X) * (t.C.Z - t.A.Z)) - ((t.B.Z - t.A.Z) * (t.C.X - t.A.X))) / 2);

    /// <summary>The spline from the report the group-mesher suite exists for: across the shared edge of two tiles.</summary>
    private static TerrainCutSpline CrossingSpline() => new(
        new Vector3(2.465868690316727, -4.235237081011675, -19.754201782271124),
        new Vector3(2.0766671505637273, -4.2352370810116735, -9.470413082974028))
    {
        Distance = 4,
        Divisions = 5,
    };

    // ---------------------------------------------------------------- Terrain.smoothHeight

    [Fact]
    public void SmoothHeight_PullsASharpSpikeTowardItsNeighbours()
    {
        var world = new TerrainWorld();
        var terrain = new TerrainTile(Vector3.Zero, 20, 20);
        world.Add(terrain);
        terrain.PaintHeight(Vector3.Zero, 0.5, 10);
        var before = TerrainTile.GetPaintedHeightOffsetAt([terrain], 0, 0);
        Assert.Equal(10, before, 3);
        Assert.True(TerrainTile.SmoothHeight(Vector3.Zero, 2, 1, [terrain]));
        var after = TerrainTile.GetPaintedHeightOffsetAt([terrain], 0, 0);
        Assert.True(after < before);
        Assert.Equal(10.0 / 9, after, 2); // averaged with its all-zero 3x3 neighbourhood at full strength
    }

    [Fact]
    public void SmoothHeight_LeavesFlatTerrainUntouched()
    {
        var terrain = new TerrainTile(Vector3.Zero, 20, 20);
        new TerrainWorld().Add(terrain);
        Assert.False(TerrainTile.SmoothHeight(Vector3.Zero, 3, 1, [terrain]));
    }

    [Fact]
    public void SmoothHeight_WorksAcrossASeam_IndependentOfTileOrder()
    {
        var world = new TerrainWorld();
        var left = new TerrainTile(new Vector3(-10, 0, 0), 20, 20);
        var right = new TerrainTile(new Vector3(10, 0, 0), 20, 20);
        world.Add(left);
        world.Add(right);
        foreach (var terrain in new[] { left, right })
        {
            if (terrain.IntersectsPaintBrush(Vector3.Zero, 0.5))
            {
                terrain.PaintHeight(Vector3.Zero, 0.5, 10);
            }
        }

        TerrainTile.SmoothHeight(Vector3.Zero, 2, 1, [left, right]);
        var resolved = TerrainTile.GetPaintedHeightOffsetAt([left, right], 0, 0);
        Assert.True(resolved < 10);
        Assert.Equal(resolved, TerrainTile.GetPaintedHeightOffsetAt([right, left], 0, 0), 5);
    }

    // ---------------------------------------------------------------- TerrainGroupMesher

    [Fact]
    public void GroupMesher_KeepsACutSplineCrossingASharedEdgeWithinMaxSlope()
    {
        var world = new TerrainWorld();
        var near = new TerrainTile(Vector3.Zero, 30, 30);
        var far = new TerrainTile(new Vector3(0, 0, -30), 30, 30);
        world.Add(near);
        world.Add(far);
        world.Add(CrossingSpline());
        near.Update();
        far.Update();
        var limit = Math.Tan(MathUtil.DegToRad(near.MaxSlopeDegrees + 2));
        Assert.True(SteepestSlope(near) < limit);
        Assert.True(SteepestSlope(far) < limit);
    }

    [Fact]
    public void GroupMesher_GivesAPairOfTilesTheSameSurfaceASingleTileWouldGet()
    {
        var world = new TerrainWorld();
        var near = new TerrainTile(Vector3.Zero, 30, 30);
        var far = new TerrainTile(new Vector3(0, 0, -30), 30, 30);
        world.Add(near);
        world.Add(far);
        world.Add(CrossingSpline());
        near.Update();
        far.Update();
        var paired = Math.Max(SteepestSlope(near), SteepestSlope(far));

        var singleWorld = new TerrainWorld();
        var single = new TerrainTile(new Vector3(0, 0, -15), 30, 60);
        singleWorld.Add(single);
        singleWorld.Add(CrossingSpline());
        single.Update();

        // The shared edge must not make the surface any steeper than one tile would be.
        Assert.True(paired < (SteepestSlope(single) * 1.15) + 0.05);
    }

    [Fact]
    public void GroupMesher_HandsEveryPartOfTheSurfaceToExactlyOneTile()
    {
        var world = new TerrainWorld();
        TerrainTile[] tiles =
        [
            new(new Vector3(-10, 0, -10), 20, 20), new(new Vector3(10, 0, -10), 20, 20),
            new(new Vector3(-10, 0, 10), 20, 20), new(new Vector3(10, 0, 10), 20, 20),
        ];
        foreach (var tile in tiles)
        {
            world.Add(tile);
        }

        foreach (var tile in tiles)
        {
            tile.Update();
        }

        Assert.Equal(4 * 20 * 20, tiles.Sum(Area), 3);
        foreach (var tile in tiles)
        {
            var bounds = tile.GetBounds();
            Assert.All(Vertices(tile), v =>
            {
                Assert.InRange(v.X, bounds.MinX - 1e-4, bounds.MaxX + 1e-4);
                Assert.InRange(v.Z, bounds.MinZ - 1e-4, bounds.MaxZ + 1e-4);
            });
        }
    }

    [Fact]
    public void GroupMesher_DoesNotDependOnTheOrderTilesWereAdded()
    {
        string Build(bool reversed)
        {
            var world = new TerrainWorld();
            var near = new TerrainTile(Vector3.Zero, 30, 30) { MeshDetail = 4 };
            var far = new TerrainTile(new Vector3(0, 0, -30), 30, 30) { MeshDetail = 2 };
            foreach (var tile in reversed ? new[] { far, near } : [near, far])
            {
                world.Add(tile);
            }

            world.Add(CrossingSpline());
            near.Update();
            far.Update();
            static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);
            return string.Join('#', new[] { near, far }.Select(tile =>
                string.Join(';', Vertices(tile).Select(v => $"{F(v.X)},{F(v.Y)},{F(v.Z)}").Order(StringComparer.Ordinal))));
        }

        Assert.Equal(Build(false), Build(true));
    }

    [Fact]
    public void BuildLodSurface_ThinsShapingPointsWithTheMesh()
    {
        var world = new TerrainWorld();
        var terrain = new TerrainTile(Vector3.Zero, 40, 40) { MeshDetail = 2, TriangleLimit = 1500 };
        world.Add(terrain);
        var river = new RiverSpline(new Vector3(-18, 0, -3), new Vector3(18, 0, 3)) { DetailLevel = 2 };
        river.Divisions = 4;
        world.Add(river);
        terrain.Update();
        var counts = Enumerable.Range(0, 4).Select(lod => world.GroupMesher.BuildLodSurface(terrain, lod).Count).ToList();
        for (var i = 1; i < counts.Count; i++)
        {
            Assert.True(counts[i] < counts[i - 1], $"LOD {i}: {counts[i]} vs {counts[i - 1]}");
        }
    }

    [Fact]
    public void BuildLodSurface_KeepsEveryLevelFreeOfEdgesFarSteeperThanTheBank()
    {
        var world = new TerrainWorld();
        var terrain = new TerrainTile(Vector3.Zero, 40, 40);
        world.Add(terrain);
        var river = new RiverSpline(new Vector3(-18, 0, -3), new Vector3(18, 0, 3)) { DetailLevel = 2 };
        river.Divisions = 4;
        world.Add(river);
        terrain.Update();
        var limit = Math.Tan(MathUtil.DegToRad(85));
        foreach (var lod in new[] { 0, 1, 2, 3 })
        {
            Assert.True(SteepestSlope(world.GroupMesher.BuildLodSurface(terrain, lod).Select(t => (t.A, t.B, t.C))) < limit, $"LOD {lod}");
        }
    }

    // ---------------------------------------------------------------- TerrainTexturePaint

    private static void AssertNormalized(TerrainTexturePaint map)
    {
        for (var offset = 0; offset < map.Data.Length; offset += 4)
        {
            Assert.Equal(255, map.Data[offset] + map.Data[offset + 1] + map.Data[offset + 2] + map.Data[offset + 3]);
        }
    }

    [Fact]
    public void TexturePaint_KeepsNormalizedByteWeights()
    {
        var map = new TerrainTexturePaint(16);
        Assert.True(map.Paint(new TexturePaintStroke(0.5, 0.5, 0.25, 0.25, 2, 0.5, 0.5, 0, TextureBrushShape.SoftRound)));
        AssertNormalized(map);
        Assert.Contains(Enumerable.Range(0, map.Data.Length), i => i % 4 == 2 && map.Data[i] > 0);
    }

    [Fact]
    public void TexturePaint_RoundTripsCompactRle()
    {
        var map = new TerrainTexturePaint(16);
        map.Paint(new TexturePaintStroke(0.4, 0.6, 0.2, 0.2, 1, 0.8, 0.2, 0.3, TextureBrushShape.Noise, 7));
        Assert.Equal(map.Data, TerrainTexturePaint.DecodeRle(16, map.EncodeRle()).Data);
        Assert.True(map.EncodeRle().Length < map.Data.Length * 2);
    }

    [Fact]
    public void TexturePaint_SupportsEveryBrushMask()
    {
        foreach (var (shape, index) in Enum.GetValues<TextureBrushShape>().Select((s, i) => (s, i)))
        {
            var map = new TerrainTexturePaint(16);
            Assert.True(map.Paint(new TexturePaintStroke(0.5, 0.5, 0.35, 0.35, 1, 0.8, 0.35, index * 0.2, shape, 19)), shape.ToString());
            AssertNormalized(map);
        }
    }

    // ---------------------------------------------------------------- RiverSpline

    private static void AssertFinite(RiverSpline river)
    {
        Assert.NotEmpty(Vertices(river));
        Assert.All(Vertices(river), v => Assert.True(double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z)));
    }

    [Fact]
    public void River_KeepsTerrainSealed_SculptsTheChannel_AndRendersWater()
    {
        var river = new RiverSpline(Vector3.Zero, new Vector3(5, 0, 0));
        river.Update();
        Assert.False(river.CutsTerrainSurface());
        var material = river.GetMaterialHint("water")!;
        Assert.True(material.Opacity < 1);
        Assert.False(material.DepthWrite);
        AssertFinite(river);

        var samples = river.GetSampledTerrainPoints(0);
        Assert.True(samples.Count > 6);
        Assert.All(samples.Where(s => !s.ProfileOnly), s => Assert.True(s.Position.Y < -0.3));
        var profile = samples.Where(s => s.ProfileOnly).ToList();
        Assert.NotEmpty(profile);
        Assert.Contains(profile, s => Math.Abs(s.Position.Y) < 1e-5);
        var terrain = new TerrainMesher().Build(new TerrainMesherInput
        {
            Center = new Vector3(0, 3, 0),
            Width = 20,
            Length = 20,
            CutAreas = [],
            CutPoints = samples,
            Settings = new TerrainMesherSettings(2, 1200, true, 4, 35),
        });
        Assert.True(terrain.Count > 2);
        Assert.Equal(400, terrain.Sum(t => Math.Abs(((t.B.X - t.A.X) * (t.C.Z - t.A.Z)) - ((t.B.Z - t.A.Z) * (t.C.X - t.A.X))) * 0.5), 3);
        Assert.Contains(terrain.SelectMany(t => new[] { t.A, t.B, t.C }), p => p.Y < -0.3);
    }

    [Fact]
    public void River_UsesBankSlopeAsTheActualBankAngle()
    {
        var river = new RiverSpline(Vector3.Zero, new Vector3(5, 0, 0)) { BankSlope = 20 };
        var shallow = river.GetSampledTerrainPoints(0);
        river.BankSlope = 75;
        var steep = river.GetSampledTerrainPoints(0);
        Assert.Equal(shallow[0].Position.Y, steep[0].Position.Y, 5);
        Assert.True(shallow[0].Radius > steep[0].Radius * 5);
        Assert.Equal(20, shallow[0].MaxSlopeDegrees);
        Assert.Equal(75, steep[0].MaxSlopeDegrees);
    }

    [Fact]
    public void River_RoundsAndRefinesTheBankProfileWithSlopeSmoothing()
    {
        var river = new RiverSpline(Vector3.Zero, new Vector3(5, 0, 0)) { BankSlope = 45, BankSmoothing = 0 };
        var linear = river.GetSampledTerrainPoints(0);
        river.BankSmoothing = 1;
        var smooth = river.GetSampledTerrainPoints(0);
        var linearBed = linear.First(s => !s.ProfileOnly);
        var smoothBed = smooth.First(s => !s.ProfileOnly);
        var smoothProfile = smooth.Where(s => s.ProfileOnly).ToList();
        Assert.Equal(linearBed.Radius * 1.5, smoothBed.Radius, 5);
        Assert.Equal(1, smoothBed.SlopeSmoothing);
        Assert.NotEmpty(smoothProfile);
        Assert.True(smoothProfile.Select(s => s.Position.Y.ToString("F5", CultureInfo.InvariantCulture)).Distinct().Count() > 2);
        Assert.Equal(1, RiverSpline.Deserialize(river.Serialize(1)).BankSmoothing);
    }

    [Fact]
    public void River_AddsDeterministicIrregularity_AndRemeshesAtHigherDetail()
    {
        var river = new RiverSpline(Vector3.Zero, new Vector3(12, 0, 0)) { Width = 4, BankSlope = 45, BankSmoothing = 0.65, DetailLevel = 0.1 };
        var regularLowDetail = river.GetSampledTerrainPoints(0);
        river.DetailLevel = 4;
        river.IrregularityLevel = 1;
        var irregularHighDetail = river.GetSampledTerrainPoints(0);
        var repeated = river.GetSampledTerrainPoints(0);
        Assert.True(irregularHighDetail.Count > regularLowDetail.Count * 10);
        Assert.Equal(irregularHighDetail.Select(s => s.Position), repeated.Select(s => s.Position));
        var outerBankWidths = irregularHighDetail
            .Where(s => s.ProfileOnly && Math.Abs(s.Position.Y) < 1e-6)
            .Select(s => Math.Abs(s.Position.Z).ToString("F4", CultureInfo.InvariantCulture))
            .Distinct().Count();
        Assert.True(outerBankWidths > 4);
        var restored = RiverSpline.Deserialize(river.Serialize(1));
        Assert.Equal((1.0, 4.0), (restored.IrregularityLevel, restored.DetailLevel));
    }

    [Fact]
    public void River_AcceptsFractionalDetail_AndPersistsIt()
    {
        var river = new RiverSpline(Vector3.Zero, new Vector3(12, 0, 0)) { DetailLevel = 0.1 };
        var sparse = river.GetSampledTerrainPoints(0);
        river.DetailLevel = 1;
        var baseline = river.GetSampledTerrainPoints(0);
        Assert.True(sparse.Count < baseline.Count / 2.0);
        river.DetailLevel = 0.3;
        Assert.Equal(0.3, RiverSpline.Deserialize(river.Serialize(1)).DetailLevel, 5);
    }

    [Fact]
    public void River_DivisionsAddToBothWaterAndTerrainRemeshing()
    {
        var river = new RiverSpline(Vector3.Zero, new Vector3(12, 0, 0)) { DetailLevel = 1 };
        river.Update();
        var waterBefore = Vertices(river).Count();
        var terrainBefore = river.GetSampledTerrainPoints(0).Count;
        river.Divisions = 5;
        Assert.True(Vertices(river).Count() > waterBefore);
        Assert.True(river.GetSampledTerrainPoints(0).Count > terrainBefore);
    }

    [Fact]
    public void River_KeepsBothMeshesValidWhenConnected()
    {
        var first = new RiverSpline(Vector3.Zero, new Vector3(5, 0, 0));
        var second = new RiverSpline(new Vector3(5, 0, 0), new Vector3(9, 0, 4));
        first.Divisions = 4;
        second.Divisions = 4;
        Assert.True(second.Connect(0, first, 1));
        AssertFinite(first);
        AssertFinite(second);
        Assert.Same(second, first.Connections[1].Element);
        Assert.Same(first, second.Connections[0].Element);
    }

    private static double CrossSectionSpan(RiverSpline river, double x)
    {
        var bed = river.GetSampledTerrainPoints(0).Where(s => !s.ProfileOnly).ToList();
        var nearestX = bed.Select(s => s.Position.X).Aggregate((best, value) => Math.Abs(value - x) < Math.Abs(best - x) ? value : best);
        var zs = bed.Where(s => Math.Abs(s.Position.X - nearestX) < 1e-6).Select(s => s.Position.Z).ToList();
        return zs.Max() - zs.Min();
    }

    [Fact]
    public void Irregularity_AboveOne_KeepsTheWaterEdgeMatchedToTheBed()
    {
        foreach (var level in new[] { 0.0, 1, 2, 3 })
        {
            var river = new RiverSpline(Vector3.Zero, new Vector3(40, 0, 0)) { Width = 4, DetailLevel = 2, IrregularityLevel = level };
            river.Divisions = 3;
            river.Update();
            Assert.Equal(level, river.IrregularityLevel);
            var bed = river.GetSampledTerrainPoints(0).Where(s => !s.ProfileOnly).ToList();
            var nearestX = bed.Select(s => s.Position.X).Aggregate((best, value) => Math.Abs(value - 20) < Math.Abs(best - 20) ? value : best);
            var midZs = Vertices(river).Where(v => Math.Abs(v.X - nearestX) < 1e-3).Select(v => v.Z).ToList();
            Assert.True(Math.Abs(midZs.Max() - midZs.Min() - CrossSectionSpan(river, 20)) < 0.05, $"level {level}");
        }
    }

    [Fact]
    public void Irregularity_HigherLevelsAreMoreJagged()
    {
        double EdgeVariance(double level)
        {
            var river = new RiverSpline(Vector3.Zero, new Vector3(60, 0, 0)) { Width = 4, DetailLevel = 2, IrregularityLevel = level };
            river.Divisions = 4;
            var spans = new[] { 10.0, 20, 30, 40, 50 }.Select(x => CrossSectionSpan(river, x)).ToList();
            var mean = spans.Average();
            return spans.Sum(s => (s - mean) * (s - mean)) / spans.Count;
        }

        Assert.True(EdgeVariance(2.5) > EdgeVariance(0.3) * 4);
    }

    [Fact]
    public void Irregularity_NeverPinchesTheChannelShut()
    {
        var river = new RiverSpline(Vector3.Zero, new Vector3(40, 0, 0)) { Width = 4, IrregularityLevel = 3 };
        river.Divisions = 3;
        Assert.True(new[] { 10.0, 15, 20, 25, 30 }.Min(x => CrossSectionSpan(river, x)) > 0.3);
    }
}
