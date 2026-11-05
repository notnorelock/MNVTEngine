using Microsoft.Extensions.Logging;
using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Meshing;

/// <summary>
/// The adaptive terrain mesher: the free surface (the footprint minus every cutter) is triangulated with poly2tri,
/// refined with Steiner points near roads and cut points within a triangle budget, sealed along cutter boundaries, and
/// given heights that blend from the painted base height toward roads, cut points and banks without exceeding the max
/// slope. The topology is cached by a signature of everything that changes it.
/// </summary>
public sealed partial class TerrainMesher(ILogger? logger = null)
{
    private const double Snap = 1e5;
    private const double BoundaryEpsilon = 2 / 1e5;
    private const double Epsilon = 1e-7;
    private TopologyCache? _topologyCache;

    private sealed record PolygonRegion(List<Vec2> Contour, List<List<Vec2>> Holes);

    private record Segment2(Vec2 A, Vec2 B);

    private sealed record HeightSegment(Vec2 A, Vec2 B, double HeightA, double HeightB, double MaxSlope, bool UsesTerrainSlope) : Segment2(A, B);

    private sealed record SamplingTier(double InnerRadius, double OuterRadius, double Spacing);

    private sealed record SteinerCandidate(Vec2 Point, double Spacing, int Tier, double Distance);

    private sealed record TopologyCache(string Signature, List<Vec2[]> Triangles, List<PolygonRegion> Regions, List<Segment2> RoadSegments, HashSet<string> ActiveCutPointKeys);

    public IReadOnlyList<OccupiedTriangle> Build(TerrainMesherInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var signature = GetTopologySignature(input);
        var topology = _topologyCache;
        if (topology is null || topology.Signature != signature)
        {
            try
            {
                topology = BuildTopology(input, signature);
            }
            catch (Exception ex) when (ex is Poly2TriException or InvalidOperationException or ArgumentException)
            {
                LogRemeshFailed(ex);
                try
                {
                    topology = BuildFallbackTopology(input, signature);
                    if (topology.Regions.Count > 0 && topology.Triangles.Count == 0)
                    {
                        throw new InvalidOperationException("Fallback triangulator returned an empty terrain mesh.");
                    }
                }
                catch (Exception fallbackError) when (fallbackError is InvalidOperationException or ArgumentException)
                {
                    LogFallbackFailed(fallbackError);
                    topology = BuildSolidFallbackTopology(input, signature);
                }
            }

            _topologyCache = topology;
        }

        return ApplyHeights(topology, input);
    }

    public void Invalidate() => _topologyCache = null;

    private void LogRemeshFailed(Exception ex)
    {
        if (logger is not null)
        {
            LogRemeshFailedMessage(logger, ex);
        }
    }

    private void LogFallbackFailed(Exception ex)
    {
        if (logger is not null)
        {
            LogFallbackFailedMessage(logger, ex);
        }
    }

    [LoggerMessage(EventId = 7100, Level = LogLevel.Warning, Message = "Terrain remesh failed; rebuilding the current footprint with the fallback triangulator")]
    private static partial void LogRemeshFailedMessage(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7101, Level = LogLevel.Warning, Message = "Terrain footprint fallback failed; using a sealed uncut surface")]
    private static partial void LogFallbackFailedMessage(ILogger logger, Exception exception);

    private static TopologyCache BuildTopology(TerrainMesherInput input, string signature)
    {
        var regions = BuildFreeRegions(input);
        if (regions.Count == 0)
        {
            return new TopologyCache(signature, [], [], [], []);
        }

        var roadBoundarySegments = CollectRoadSegments(regions, input);
        var sealedRoadSegments = SplitRoadSegmentsAtCutVertices(roadBoundarySegments, input.CutAreas);
        var roadSegments = MergeSegments(sealedRoadSegments, CollectInfluenceBoundarySegments(input.InfluenceAreas ?? input.CutAreas));
        var activeCutPoints = input.CutPoints
            .Where(point => DoesCutPointInfluenceRegions(point, regions, point.Radius) && !IsInsideCutArea(point.Position.Xz, input.CutAreas))
            .ToList();
        var activePaintPoints = GetActivePaintPoints(input, regions);
        var influencePoints = activeCutPoints.Concat(activePaintPoints).ToList();
        var activeCutPointKeys = activeCutPoints.Select(point => PointKey(point.Position.Xz)).ToHashSet();
        var mandatoryEstimate = regions.Sum(region => region.Contour.Count + region.Holes.Sum(hole => hole.Count))
            + influencePoints.Count + Math.Max(0, sealedRoadSegments.Count - roadBoundarySegments.Count);
        var maxSteiner = Math.Max(0, (int)Math.Floor((input.Settings.TriangleLimit - mandatoryEstimate) / 2.0));
        var steiner = GenerateSteinerPoints(input, regions, roadSegments, influencePoints, activeCutPoints, maxSteiner);

        var triangles = InsertRoadBoundaryKnots(TriangulateRegions(regions, steiner), roadBoundarySegments, input.CutAreas);
        for (var pass = 0; pass < 3; pass++)
        {
            var remaining = Math.Max(0, (int)Math.Floor((input.Settings.TriangleLimit - triangles.Count) / 2.0));
            if (remaining == 0 || roadSegments.Count + activeCutPoints.Count == 0)
            {
                break;
            }

            var candidates = CollectRefinementPoints(triangles, regions, roadSegments, activeCutPoints, input.Settings, remaining, steiner);
            if (candidates.Count == 0)
            {
                break;
            }

            steiner.AddRange(candidates);
            triangles = InsertRoadBoundaryKnots(TriangulateRegions(regions, steiner), roadBoundarySegments, input.CutAreas);
        }

        if (triangles.Count > input.Settings.TriangleLimit)
        {
            var essential = influencePoints.Select(point => point.Position.Xz).ToList();
            triangles = InsertRoadBoundaryKnots(TriangulateRegions(regions, essential), roadBoundarySegments, input.CutAreas);
        }

        AssertRoadBoundariesAreSealed(triangles, sealedRoadSegments);
        return new TopologyCache(signature, triangles, regions, roadSegments, activeCutPointKeys);
    }

    private static TopologyCache BuildFallbackTopology(TerrainMesherInput input, string signature)
    {
        var regions = BuildFreeRegions(input);
        var triangles = new List<Vec2[]>();
        foreach (var region in regions)
        {
            var contour = region.Contour.ToList();
            var holes = region.Holes.Select(hole => hole.ToList()).ToList();
            var faces = ShapeUtils.TriangulateShape(contour, holes);
            var points = region.Contour.Concat(region.Holes.SelectMany(h => h)).ToList();
            foreach (var face in faces)
            {
                triangles.Add([points[face[0]], points[face[1]], points[face[2]]]);
            }
        }

        var roadBoundarySegments = CollectRoadSegments(regions, input);
        var sealedRoadSegments = SplitRoadSegmentsAtCutVertices(roadBoundarySegments, input.CutAreas);
        var roadSegments = MergeSegments(sealedRoadSegments, CollectInfluenceBoundarySegments(input.InfluenceAreas ?? input.CutAreas));
        var withKnots = InsertRoadBoundaryKnots(triangles, roadBoundarySegments, input.CutAreas);
        AssertRoadBoundariesAreSealed(withKnots, sealedRoadSegments);
        var activeCutPointKeys = input.CutPoints
            .Where(point => DoesCutPointInfluenceRegions(point, regions, point.Radius))
            .Select(point => PointKey(point.Position.Xz))
            .ToHashSet();
        return new TopologyCache(signature, withKnots, regions, roadSegments, activeCutPointKeys);
    }

    private static void AssertRoadBoundariesAreSealed(List<Vec2[]> triangles, List<Segment2> roadSegments)
    {
        var meshEdges = new HashSet<string>();
        foreach (var triangle in triangles)
        {
            meshEdges.Add(EdgeKey(triangle[0], triangle[1]));
            meshEdges.Add(EdgeKey(triangle[1], triangle[2]));
            meshEdges.Add(EdgeKey(triangle[2], triangle[0]));
        }

        var missing = roadSegments.FirstOrDefault(segment => !meshEdges.Contains(EdgeKey(segment.A, segment.B)));
        if (missing is not null)
        {
            throw new InvalidOperationException($"Terrain mesh does not contain cutter boundary {EdgeKey(missing.A, missing.B)}.");
        }
    }

    private static TopologyCache BuildSolidFallbackTopology(TerrainMesherInput input, string signature)
    {
        var triangles = new List<Vec2[]>();
        var regions = new List<PolygonRegion>();
        foreach (var rect in GetFootprintRects(input))
        {
            var contour = RectRing(rect).Take(4).ToList();
            triangles.Add([contour[0], contour[1], contour[2]]);
            triangles.Add([contour[0], contour[2], contour[3]]);
            regions.Add(new PolygonRegion(contour, []));
        }

        return new TopologyCache(signature, triangles, regions, CollectInfluenceBoundarySegments(input.InfluenceAreas ?? input.CutAreas), []);
    }

    private static IReadOnlyList<TerrainRect> GetFootprintRects(TerrainMesherInput input)
    {
        if (input.Footprint is { Count: > 0 } footprint)
        {
            return footprint;
        }

        return [new TerrainRect(input.Center.X - (input.Width / 2), input.Center.X + (input.Width / 2), input.Center.Z - (input.Length / 2), input.Center.Z + (input.Length / 2))];
    }

    private static TerrainRect GetFootprintBounds(TerrainMesherInput input)
    {
        var rects = GetFootprintRects(input);
        return new TerrainRect(rects.Min(r => r.MinX), rects.Max(r => r.MaxX), rects.Min(r => r.MinZ), rects.Max(r => r.MaxZ));
    }

    private static List<Vec2> RectRing(TerrainRect rect) =>
        new Vec2[] { new(rect.MinX, rect.MinZ), new(rect.MaxX, rect.MinZ), new(rect.MaxX, rect.MaxZ), new(rect.MinX, rect.MaxZ), new(rect.MinX, rect.MinZ) }
            .Select(SnapPoint).ToList();

    private static List<PolygonRegion> BuildFreeRegions(TerrainMesherInput input)
    {
        var rects = GetFootprintRects(input);

        // Union first: the shared edges between touching tiles disappear here, which is exactly what makes the group a
        // single surface rather than several stitched ones.
        var surface = new List<List<List<Vec2>>> { new() { RectRing(rects[0]) } };
        if (rects.Count > 1)
        {
            var union = PolygonClipping.Union(rects.Select(rect => new List<List<List<Vec2>>> { new() { RectRing(rect) } }));
            if (union.Count > 0)
            {
                surface = union;
            }
        }

        var cutterPolygons = new List<List<List<List<Vec2>>>>();
        foreach (var tri in input.CutAreas)
        {
            var a = SnapPoint(tri.A.Xz);
            var b = SnapPoint(tri.B.Xz);
            var c = SnapPoint(tri.C.Xz);
            if (Math.Abs(SignedArea([a, b, c])) <= Epsilon)
            {
                continue;
            }

            cutterPolygons.Add([[[a, b, c, a]]]);
        }

        var free = surface;
        if (cutterPolygons.Count > 0)
        {
            var cutters = PolygonClipping.Union(cutterPolygons);
            if (cutters.Count > 0)
            {
                free = PolygonClipping.Difference(surface, cutters);
            }
        }

        var regions = new List<PolygonRegion>();
        foreach (var polygon in free)
        {
            if (polygon.Count == 0)
            {
                continue;
            }

            var contour = SanitizeRing(polygon[0], clockwise: false);
            if (contour.Count < 3)
            {
                continue;
            }

            var holes = polygon.Skip(1).Select(ring => SanitizeRing(ring, clockwise: true)).Where(ring => ring.Count >= 3).ToList();
            regions.Add(new PolygonRegion(contour, holes));
        }

        return regions;
    }

    private static List<Vec2> SanitizeRing(IEnumerable<Vec2> source, bool clockwise)
    {
        var ring = new List<Vec2>();
        foreach (var raw in source)
        {
            var point = SnapPoint(raw);
            if (ring.Count == 0 || PointKey(ring[^1]) != PointKey(point))
            {
                ring.Add(point);
            }
        }

        if (ring.Count > 1 && PointKey(ring[0]) == PointKey(ring[^1]))
        {
            ring.RemoveAt(ring.Count - 1);
        }

        var changed = true;
        while (changed && ring.Count > 3)
        {
            changed = false;
            for (var i = 0; i < ring.Count; i++)
            {
                var prev = ring[(i - 1 + ring.Count) % ring.Count];
                var curr = ring[i];
                var next = ring[(i + 1) % ring.Count];
                if (Math.Abs(SignedArea([prev, curr, next])) <= Epsilon)
                {
                    ring.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
        }

        var isClockwise = SignedArea(ring) < 0;
        if (isClockwise != clockwise)
        {
            ring.Reverse();
        }

        return ring;
    }

    private static List<Segment2> CollectRoadSegments(List<PolygonRegion> regions, TerrainMesherInput input)
    {
        var result = new List<Segment2>();
        var seen = new HashSet<string>();
        var rects = GetFootprintRects(input);
        foreach (var region in regions)
        {
            var rings = new List<List<Vec2>> { region.Contour };
            rings.AddRange(region.Holes);
            for (var ringIndex = 0; ringIndex < rings.Count; ringIndex++)
            {
                var ring = rings[ringIndex];
                for (var i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    var isHoleBoundary = ringIndex > 0;
                    if (!isHoleBoundary && IsTerrainOuterBoundarySegment(a, b, rects))
                    {
                        continue;
                    }

                    if (seen.Add(EdgeKey(a, b)))
                    {
                        result.Add(new Segment2(a, b));
                    }
                }
            }
        }

        return result;
    }

    private static List<Segment2> CollectInfluenceBoundarySegments(IReadOnlyList<OccupiedTriangle> areas)
    {
        var polygons = new List<List<List<List<Vec2>>>>();
        foreach (var triangle in areas)
        {
            var a = SnapPoint(triangle.A.Xz);
            var b = SnapPoint(triangle.B.Xz);
            var c = SnapPoint(triangle.C.Xz);
            if (Math.Abs(SignedArea([a, b, c])) <= Epsilon)
            {
                continue;
            }

            polygons.Add([[[a, b, c, a]]]);
        }

        if (polygons.Count == 0)
        {
            return [];
        }

        var union = polygons[0];
        for (var index = 1; index < polygons.Count; index += 32)
        {
            var chunk = polygons.Skip(index).Take(32).ToList();
            try
            {
                union = PolygonClipping.Union([union, .. chunk]);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                foreach (var polygon in chunk)
                {
                    try
                    {
                        union = PolygonClipping.Union(union, polygon);
                    }
                    catch (Exception sliver) when (sliver is InvalidOperationException or ArgumentException)
                    {
                        // An invalid sliver; valid neighbouring triangles still constrain the height.
                        _ = sliver;
                    }
                }
            }
        }

        var segments = new List<Segment2>();
        foreach (var polygon in union)
        {
            for (var ringIndex = 0; ringIndex < polygon.Count; ringIndex++)
            {
                var ring = SanitizeRing(polygon[ringIndex], ringIndex > 0);
                for (var index = 0; index < ring.Count; index++)
                {
                    var a = ring[index];
                    var b = ring[(index + 1) % ring.Count];
                    if (a.DistanceTo(b) > Epsilon)
                    {
                        segments.Add(new Segment2(a, b));
                    }
                }
            }
        }

        return segments;
    }

    private static List<Segment2> MergeSegments(params List<Segment2>[] collections)
    {
        // Map semantics: insertion order kept, a repeated key keeps its first position with the latest value.
        var order = new List<string>();
        var result = new Dictionary<string, Segment2>();
        foreach (var segments in collections)
        {
            foreach (var segment in segments)
            {
                var key = EdgeKey(segment.A, segment.B);
                if (!result.ContainsKey(key))
                {
                    order.Add(key);
                }

                result[key] = segment;
            }
        }

        return order.Select(key => result[key]).ToList();
    }

    private static List<Segment2> SplitRoadSegmentsAtCutVertices(List<Segment2> segments, IReadOnlyList<OccupiedTriangle> cutAreas)
    {
        var result = new List<Segment2>();
        foreach (var segment in segments)
        {
            var knots = GetRoadBoundaryKnots(segment, cutAreas);
            for (var i = 0; i < knots.Count - 1; i++)
            {
                if (knots[i].DistanceTo(knots[i + 1]) <= Epsilon)
                {
                    continue;
                }

                result.Add(new Segment2(knots[i], knots[i + 1]));
            }
        }

        return result;
    }

    private static List<Vec2[]> InsertRoadBoundaryKnots(List<Vec2[]> triangles, List<Segment2> boundarySegments, IReadOnlyList<OccupiedTriangle> cutAreas)
    {
        var result = triangles.ToList();
        foreach (var segment in boundarySegments)
        {
            var knots = GetRoadBoundaryKnots(segment, cutAreas);
            if (knots.Count <= 2)
            {
                continue;
            }

            var triangleIndex = -1;
            var edgeIndex = -1;
            var segmentKey = EdgeKey(segment.A, segment.B);
            for (var i = 0; i < result.Count && triangleIndex < 0; i++)
            {
                for (var edge = 0; edge < 3; edge++)
                {
                    if (EdgeKey(result[i][edge], result[i][(edge + 1) % 3]) == segmentKey)
                    {
                        triangleIndex = i;
                        edgeIndex = edge;
                        break;
                    }
                }
            }

            if (triangleIndex < 0 || edgeIndex < 0)
            {
                continue;
            }

            var triangle = result[triangleIndex];
            var start = triangle[edgeIndex];
            var third = triangle[(edgeIndex + 2) % 3];
            var ordered = PointKey(start) == PointKey(segment.A) ? knots : Enumerable.Reverse(knots).ToList();
            var replacements = new List<Vec2[]>();
            for (var i = 0; i < ordered.Count - 1; i++)
            {
                Vec2[] split = [ordered[i], ordered[i + 1], third];
                if (Math.Abs(SignedArea(split)) > Epsilon)
                {
                    replacements.Add(split);
                }
            }

            result.RemoveAt(triangleIndex);
            result.InsertRange(triangleIndex, replacements);
        }

        return result;
    }

    private static List<Vec2> GetRoadBoundaryKnots(Segment2 segment, IReadOnlyList<OccupiedTriangle> cutAreas)
    {
        var order = new List<string>();
        var candidates = new Dictionary<string, (Vec2 Point, double T)>();
        void Add(Vec2 point)
        {
            var snapped = SnapPoint(point);
            var projection = ProjectToSegment(snapped, segment.A, segment.B);
            if (projection.Distance > 5 / Snap || projection.T < -Epsilon || projection.T > 1 + Epsilon)
            {
                return;
            }

            var key = PointKey(snapped);
            if (!candidates.ContainsKey(key))
            {
                order.Add(key);
            }

            candidates[key] = (snapped, projection.T);
        }

        Add(segment.A);
        Add(segment.B);
        foreach (var triangle in cutAreas)
        {
            Add(triangle.A.Xz);
            Add(triangle.B.Xz);
            Add(triangle.C.Xz);
        }

        var list = order.Select(key => candidates[key]).ToList();
        Js.StableSort(list, (a, b) => a.T != b.T ? Js.Sign(a.T - b.T) : Js.LocaleCompare(PointKey(a.Point), PointKey(b.Point)));
        return list.Select(c => c.Point).ToList();
    }

    // A contour segment lying on one of the tile edge lines is the outer rim of the surface, not a cutter boundary, so
    // it must not pull heights toward a road. Edges shared between two tiles never reach here: the union dissolved them.
    private static bool IsTerrainOuterBoundarySegment(Vec2 a, Vec2 b, IReadOnlyList<TerrainRect> rects)
    {
        static bool OnSameBoundary(double valueA, double valueB, double boundary) =>
            Math.Abs(valueA - boundary) <= BoundaryEpsilon && Math.Abs(valueB - boundary) <= BoundaryEpsilon;
        return rects.Any(rect => OnSameBoundary(a.X, b.X, rect.MinX) || OnSameBoundary(a.X, b.X, rect.MaxX)
            || OnSameBoundary(a.Y, b.Y, rect.MinZ) || OnSameBoundary(a.Y, b.Y, rect.MaxZ));
    }

    private static List<Vec2> GenerateSteinerPoints(
        TerrainMesherInput input,
        List<PolygonRegion> regions,
        List<Segment2> roadSegments,
        List<TerrainCutPointInput> requiredPoints,
        List<TerrainCutPointInput> adaptivePoints,
        int limit)
    {
        var result = new List<Vec2>();
        var occupied = CollectBoundaryKeys(regions);
        var detail = Math.Max(0.05, input.Settings.MeshDetail);
        void AddExactPoint(Vec2 point)
        {
            if (result.Count >= limit)
            {
                return;
            }

            var snapped = SnapPoint(point);
            var key = PointKey(snapped);
            if (occupied.Contains(key) || !IsStrictlyInsideRegions(snapped, regions))
            {
                return;
            }

            occupied.Add(key);
            result.Add(snapped);
        }

        foreach (var point in requiredPoints)
        {
            AddExactPoint(point.Position.Xz);
        }

        if (result.Count >= limit || roadSegments.Count + adaptivePoints.Count == 0)
        {
            return result;
        }

        var bounds = GetFootprintBounds(input);
        var terrainDiagonal = MathUtil.Hypot(bounds.MaxX - bounds.MinX, bounds.MaxZ - bounds.MinZ);
        var baseRadius = Math.Max(detail * 2, input.Settings.SmoothingRadius);
        var tierCount = input.Settings.SmoothingEnabled ? 4 : 2;
        var tiers = new List<SamplingTier>();
        for (var tier = 0; tier < tierCount; tier++)
        {
            var innerRadius = tier == 0 ? 0 : baseRadius * Math.Pow(2, tier - 1);
            var outerRadius = Math.Min(terrainDiagonal, baseRadius * Math.Pow(2, tier));
            if (outerRadius <= innerRadius + Epsilon)
            {
                break;
            }

            tiers.Add(new SamplingTier(innerRadius, outerRadius, detail * Math.Pow(2, tier)));
        }

        var candidateOrder = new List<string>();
        var candidates = new Dictionary<string, SteinerCandidate>();
        var scanLimit = Math.Max(2000, limit * 24);
        var scanned = 0;
        for (var tierIndex = 0; tierIndex < tiers.Count && scanned < scanLimit; tierIndex++)
        {
            var tier = tiers[tierIndex];
            var visited = new HashSet<string>();
            void VisitBounds(double minX, double maxX, double minZ, double maxZ)
            {
                if (scanned >= scanLimit)
                {
                    return;
                }

                ForEachTriangularLatticePoint(minX, maxX, minZ, maxZ, tier.Spacing, point =>
                {
                    if (scanned >= scanLimit)
                    {
                        return false;
                    }

                    var key = PointKey(point);
                    if (!visited.Add(key))
                    {
                        return true;
                    }

                    scanned++;
                    if (occupied.Contains(key))
                    {
                        return true;
                    }

                    var region = FindContainingRegion(point, regions);
                    if (region is null)
                    {
                        return true;
                    }

                    var boundaryClearance = Math.Min(detail * 0.25, tier.Spacing * 0.2);
                    if (GetDistanceToRegionBoundary(point, region) < boundaryClearance)
                    {
                        return true;
                    }

                    var distance = GetNearestInfluenceDistance(point, roadSegments, adaptivePoints);
                    if (distance <= tier.InnerRadius + Epsilon || distance > tier.OuterRadius + Epsilon)
                    {
                        return true;
                    }

                    if (!candidates.TryGetValue(key, out var current) || tierIndex < current.Tier)
                    {
                        if (current is null)
                        {
                            candidateOrder.Add(key);
                        }

                        candidates[key] = new SteinerCandidate(SnapPoint(point), tier.Spacing, tierIndex, distance);
                    }

                    return true;
                });
            }

            foreach (var segment in roadSegments)
            {
                VisitBounds(
                    Math.Min(segment.A.X, segment.B.X) - tier.OuterRadius,
                    Math.Max(segment.A.X, segment.B.X) + tier.OuterRadius,
                    Math.Min(segment.A.Y, segment.B.Y) - tier.OuterRadius,
                    Math.Max(segment.A.Y, segment.B.Y) + tier.OuterRadius);
                if (scanned >= scanLimit)
                {
                    break;
                }
            }

            if (scanned < scanLimit)
            {
                foreach (var point in adaptivePoints)
                {
                    VisitBounds(
                        point.Position.X - tier.OuterRadius, point.Position.X + tier.OuterRadius,
                        point.Position.Z - tier.OuterRadius, point.Position.Z + tier.OuterRadius);
                    if (scanned >= scanLimit)
                    {
                        break;
                    }
                }
            }
        }

        var ordered = candidateOrder.Select(key => candidates[key]).ToList();
        Js.StableSort(ordered, (a, b) => a.Tier != b.Tier ? a.Tier - b.Tier
            : a.Distance != b.Distance ? Js.Sign(a.Distance - b.Distance)
            : Js.LocaleCompare(PointKey(a.Point), PointKey(b.Point)));
        foreach (var candidate in ordered)
        {
            if (result.Count >= limit)
            {
                break;
            }

            var minimumSeparation = candidate.Spacing * 0.55;
            if (result.Any(point => point.DistanceTo(candidate.Point) < minimumSeparation))
            {
                continue;
            }

            var key = PointKey(candidate.Point);
            if (!occupied.Add(key))
            {
                continue;
            }

            result.Add(candidate.Point);
        }

        return result;
    }

    private static List<Vec2> CollectRefinementPoints(
        List<Vec2[]> triangles,
        List<PolygonRegion> regions,
        List<Segment2> roadSegments,
        List<TerrainCutPointInput> cutPoints,
        TerrainMesherSettings settings,
        int limit,
        List<Vec2> existing)
    {
        var occupied = CollectBoundaryKeys(regions);
        foreach (var point in existing)
        {
            occupied.Add(PointKey(point));
        }

        var scored = new List<SteinerCandidate>();
        var detail = Math.Max(0.05, settings.MeshDetail);
        var baseRadius = Math.Max(detail * 2, settings.SmoothingRadius);
        var activeRadius = baseRadius * (settings.SmoothingEnabled ? 8 : 2);
        foreach (var tri in triangles)
        {
            var centroid = new Vec2((tri[0].X + tri[1].X + tri[2].X) / 3, (tri[0].Y + tri[1].Y + tri[2].Y) / 3);
            var influenceDistance = GetNearestInfluenceDistance(centroid, roadSegments, cutPoints);
            if (influenceDistance > activeRadius)
            {
                continue;
            }

            var desired = GetAdaptiveSpacing(influenceDistance, detail, baseRadius);
            var e0 = tri[0].DistanceTo(tri[1]);
            var e1 = tri[1].DistanceTo(tri[2]);
            var e2 = tri[2].DistanceTo(tri[0]);
            var longest = Math.Max(e0, Math.Max(e1, e2));
            var minAngle = GetMinimumAngle(e0, e1, e2);
            var tooLong = longest > desired * 2.4;
            var tooThin = minAngle < 11 && longest > desired * 1.35;
            if (!tooLong && !tooThin)
            {
                continue;
            }

            var circumcenter = GetCircumcenter(tri);
            var candidate = circumcenter is { } cc && IsPointInTriangle(cc, tri) && IsStrictlyInsideRegions(cc, regions) ? cc : centroid;
            var region = FindContainingRegion(candidate, regions);
            if (region is null || GetDistanceToRegionBoundary(candidate, region) < desired * 0.35)
            {
                continue;
            }

            var snapped = SnapPoint(candidate);
            if (occupied.Contains(PointKey(snapped)) || existing.Any(point => point.DistanceTo(snapped) < desired * 0.6))
            {
                continue;
            }

            var score = Math.Max(longest / desired, 11 / Math.Max(minAngle, 0.01));
            scored.Add(new SteinerCandidate(snapped, desired, 0, -score));
        }

        Js.StableSort(scored, (a, b) => a.Distance != b.Distance ? Js.Sign(a.Distance - b.Distance) : Js.LocaleCompare(PointKey(a.Point), PointKey(b.Point)));
        var result = new List<Vec2>();
        var passLimit = Math.Min(limit, Math.Max(4, (int)Math.Ceiling(triangles.Count * 0.08)));
        foreach (var item in scored)
        {
            var key = PointKey(item.Point);
            if (occupied.Contains(key) || result.Any(point => point.DistanceTo(item.Point) < item.Spacing * 0.6))
            {
                continue;
            }

            occupied.Add(key);
            result.Add(item.Point);
            if (result.Count >= passLimit)
            {
                break;
            }
        }

        return result;
    }

    private static List<Vec2[]> TriangulateRegions(List<PolygonRegion> regions, List<Vec2> steiner)
    {
        var result = new List<Vec2[]>();
        foreach (var region in regions)
        {
            var context = new P2TSweepContext(region.Contour.Select(p => new P2TPoint(p.X, p.Y)));
            foreach (var hole in region.Holes)
            {
                context.AddHole(hole.Select(p => new P2TPoint(p.X, p.Y)).ToList());
            }

            var regionPoints = steiner.Where(point => IsStrictlyInsideRegion(point, region)).ToList();
            if (regionPoints.Count > 0)
            {
                context.AddPoints(regionPoints.Select(p => new P2TPoint(p.X, p.Y)));
            }

            context.Triangulate();
            foreach (var triangle in context.Triangles)
            {
                Vec2[] points = [new(triangle.Points[0].X, triangle.Points[0].Y), new(triangle.Points[1].X, triangle.Points[1].Y), new(triangle.Points[2].X, triangle.Points[2].Y)];
                if (Math.Abs(SignedArea(points)) <= Epsilon)
                {
                    continue;
                }

                result.Add(points);
            }
        }

        return result;
    }

    private static List<TerrainCutPointInput> GetActivePaintPoints(TerrainMesherInput input, List<PolygonRegion> regions)
    {
        var paint = input.Paint;
        if (paint is null || paint.Samples.Count == 0)
        {
            return [];
        }

        var cellSize = Math.Max(0.05, paint.CellSize);
        var candidates = paint.Samples
            .Where(sample => Math.Abs(sample.Height) > Epsilon)
            .Select(sample => (Sample: sample, Position: new Vec3(
                MathUtil.JsRound(sample.GridX) * cellSize,
                (input.BaseHeight ?? input.Center.Y) + sample.Height,
                MathUtil.JsRound(sample.GridZ) * cellSize)))
            .Where(c => FindContainingRegion(c.Position.Xz, regions) is not null && !IsInsideCutArea(c.Position.Xz, input.CutAreas))
            .ToList();
        var maxInfluences = Math.Max(12, Math.Min(160, Math.Min(
            (int)Math.Floor(input.Settings.TriangleLimit / 6.0),
            (int)Math.Floor(240 / Math.Max(0.5, input.Settings.MeshDetail)))));
        if (candidates.Count <= maxInfluences)
        {
            return candidates.Select(c => new TerrainCutPointInput(c.Position, cellSize * 2)).ToList();
        }

        var values = new Dictionary<(double, double), double>();
        foreach (var sample in paint.Samples)
        {
            values[(MathUtil.JsRound(sample.GridX), MathUtil.JsRound(sample.GridZ))] = sample.Height;
        }

        double Importance(TerrainPaintHeightInput sample)
        {
            var x = MathUtil.JsRound(sample.GridX);
            var z = MathUtil.JsRound(sample.GridZ);
            var neighborSum = values.GetValueOrDefault((x - 1, z)) + values.GetValueOrDefault((x + 1, z))
                + values.GetValueOrDefault((x, z - 1)) + values.GetValueOrDefault((x, z + 1));
            return Math.Abs((sample.Height * 4) - neighborSum) + (Math.Abs(sample.Height) * 0.02);
        }

        var blockSize = Math.Max(2, (int)Math.Ceiling(Math.Sqrt(candidates.Count / (double)maxInfluences)));
        List<(TerrainPaintHeightInput Sample, Vec3 Position)> selected;
        do
        {
            var order = new List<(double, double)>();
            var blocks = new Dictionary<(double, double), (TerrainPaintHeightInput Sample, Vec3 Position)>();
            foreach (var candidate in candidates)
            {
                var key = (Math.Floor(candidate.Sample.GridX / blockSize), Math.Floor(candidate.Sample.GridZ / blockSize));
                if (!blocks.TryGetValue(key, out var current))
                {
                    order.Add(key);
                    blocks[key] = candidate;
                }
                else if (Importance(candidate.Sample) > Importance(current.Sample))
                {
                    blocks[key] = candidate;
                }
            }

            selected = order.Select(key => blocks[key]).ToList();
            blockSize++;
        }
        while (selected.Count > maxInfluences);

        Js.StableSort(selected, (a, b) => a.Sample.GridZ != b.Sample.GridZ ? Js.Sign(a.Sample.GridZ - b.Sample.GridZ) : Js.Sign(a.Sample.GridX - b.Sample.GridX));
        return selected.Select(c => new TerrainCutPointInput(c.Position, cellSize * 2)).ToList();
    }

    // How far a cut point actually reshapes the surface. Points carrying their own bank slope stop at their radius; the
    // rest fade out over the slope-derived width ApplyHeights uses, and both terrains sharing an edge must agree on this
    // number or one of them drops the point and leaves a wall on the seam.
    private static double GetCutPointInfluenceRadius(TerrainCutPointInput point, TerrainMesherInput input)
    {
        var radius = Math.Max(0, point.Radius);
        if (point.ProfileOnly || point.MaxSlopeDegrees is not null)
        {
            return radius;
        }

        var maxSlope = Math.Max(0.01, Math.Tan(MathUtil.DegToRad(MathUtil.Clamp(input.Settings.MaxSlopeDegrees, 1, 89))));
        var baseHeight = input.BaseHeight ?? input.Center.Y;
        return Math.Max(radius, 1.5 * Math.Abs(point.Position.Y - baseHeight) / maxSlope);
    }

    private static bool DoesCutPointInfluenceRegions(TerrainCutPointInput point, List<PolygonRegion> regions, double radius)
    {
        var position = point.Position.Xz;
        return regions.Any(region => IsInsideRegion(position, region) || GetDistanceToRegionBoundary(position, region) <= radius + Epsilon);
    }

    private static List<OccupiedTriangle> ApplyHeights(TopologyCache topology, TerrainMesherInput input)
    {
        var cutPointMap = new Dictionary<string, TerrainCutPointInput>();
        foreach (var point in input.CutPoints)
        {
            var key = PointKey(point.Position.Xz);
            if (topology.ActiveCutPointKeys.Contains(key))
            {
                cutPointMap[key] = point;
            }
        }

        var influencingCutPoints = input.CutPoints
            .Where(point => !point.ProfileOnly)
            .Select(point => (Point: point, Radius: GetCutPointInfluenceRadius(point, input)))
            .Where(c => DoesCutPointInfluenceRegions(c.Point, topology.Regions, c.Radius) && !IsInsideCutArea(c.Point.Position.Xz, input.CutAreas))
            .ToList();
        var paintCellSize = Math.Max(0.05, input.Paint?.CellSize ?? 1);
        var paintValues = new Dictionary<(double, double), double>();
        foreach (var sample in input.Paint?.Samples ?? [])
        {
            paintValues[(MathUtil.JsRound(sample.GridX), MathUtil.JsRound(sample.GridZ))] = sample.Height;
        }

        double PaintedHeightAt(Vec2 point)
        {
            if (paintValues.Count == 0)
            {
                return 0;
            }

            var gridX = point.X / paintCellSize;
            var gridZ = point.Y / paintCellSize;
            var x0 = Math.Floor(gridX);
            var z0 = Math.Floor(gridZ);
            var tx = gridX - x0;
            var tz = gridZ - z0;
            double Read(double x, double z) => paintValues.GetValueOrDefault((x, z));
            var bottom = MathUtil.Lerp(Read(x0, z0), Read(x0 + 1, z0), tx);
            var top = MathUtil.Lerp(Read(x0, z0 + 1), Read(x0 + 1, z0 + 1), tx);
            return MathUtil.Lerp(bottom, top, tz);
        }

        var roadHeightSegments = new List<HeightSegment>();
        var heightInfluenceAreas = input.InfluenceAreas ?? input.CutAreas;
        foreach (var segment in topology.RoadSegments)
        {
            var midpoint = new Vec2((segment.A.X + segment.B.X) / 2, (segment.A.Y + segment.B.Y) / 2);
            var fallback = SampleCutBoundary(midpoint, heightInfluenceAreas);
            var sampleA = SampleCutBoundary(segment.A, heightInfluenceAreas) ?? fallback;
            var sampleB = SampleCutBoundary(segment.B, heightInfluenceAreas) ?? fallback;
            if (sampleA is null || sampleB is null)
            {
                continue;
            }

            // A boundary triangle can override the terrain's global max slope (a river wanting a steep bank instead of
            // the wide, gentle blend a road would want).
            var slopeOverride = sampleA.Value.SlopeDegrees ?? sampleB.Value.SlopeDegrees;
            var slopeDegrees = slopeOverride ?? input.Settings.MaxSlopeDegrees;
            var segmentMaxSlope = Math.Max(0.01, Math.Tan(MathUtil.DegToRad(MathUtil.Clamp(slopeDegrees, 1, 89))));
            roadHeightSegments.Add(new HeightSegment(segment.A, segment.B, sampleA.Value.Height, sampleB.Value.Height, segmentMaxSlope, slopeOverride is null));
        }

        var heightCache = new Dictionary<string, double>();
        double HeightOf(Vec2 point)
        {
            var key = PointKey(point);
            if (heightCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (cutPointMap.TryGetValue(key, out var exactCutPoint))
            {
                heightCache[key] = exactCutPoint.Position.Y;
                return exactCutPoint.Position.Y;
            }

            var road = GetNearestRoadConstraint(point, roadHeightSegments);
            if (road is { } r && r.Distance <= Epsilon * 20)
            {
                heightCache[key] = r.Height;
                return r.Height;
            }

            var paintedBaseHeight = (input.BaseHeight ?? input.Center.Y) + PaintedHeightAt(point);
            if (!input.Settings.SmoothingEnabled)
            {
                heightCache[key] = paintedBaseHeight;
                return paintedBaseHeight;
            }

            var weightedDelta = 0.0;
            var weightSum = 0.0;
            if (road is { } near)
            {
                var width = Math.Max(input.Settings.SmoothingRadius, 1.5 * Math.Abs(near.Height - paintedBaseHeight) / near.MaxSlope);
                var weight = SmoothInfluence(near.Distance, width);
                weightedDelta += (near.Height - paintedBaseHeight) * weight;
                weightSum += weight;
            }

            // Cut points combine as a union of profiles, not as a sum: each point's radius is sized so its own profile
            // never exceeds the max slope, and taking the deepest (and the highest) profile keeps that limit intact
            // however many of them overlap.
            double? lowestProfile = null;
            double? highestProfile = null;
            foreach (var (cutPoint, radius) in influencingCutPoints)
            {
                var distance = MathUtil.Hypot(point.X - cutPoint.Position.X, point.Y - cutPoint.Position.Z);
                if (distance > radius)
                {
                    continue;
                }

                var t = radius <= Epsilon ? 1 : MathUtil.Clamp(distance / radius, 0, 1);
                var smoothT = MathUtil.Smoothstep(t);

                // A point carrying its own bank slope keeps its straight-to-rounded profile control; points on the
                // terrain-wide slope always use the rounded profile their radius was sized for.
                var smoothing = cutPoint.MaxSlopeDegrees is null ? 1 : MathUtil.Clamp(cutPoint.SlopeSmoothing ?? 0, 0, 1);
                var profileT = MathUtil.Lerp(t, smoothT, smoothing);
                var candidate = MathUtil.Lerp(cutPoint.Position.Y, paintedBaseHeight, profileT);
                if (candidate < paintedBaseHeight)
                {
                    lowestProfile = lowestProfile is null ? candidate : Math.Min(lowestProfile.Value, candidate);
                }
                else if (candidate > paintedBaseHeight)
                {
                    highestProfile = highestProfile is null ? candidate : Math.Max(highestProfile.Value, candidate);
                }
            }

            var height = paintedBaseHeight + (weightedDelta / Math.Max(1, weightSum));
            if (lowestProfile is { } low)
            {
                height = Math.Min(height, low);
            }

            if (highestProfile is { } high)
            {
                height = Math.Max(height, high);
            }

            heightCache[key] = height;
            return height;
        }

        var result = new List<OccupiedTriangle>(topology.Triangles.Count);
        foreach (var tri in topology.Triangles)
        {
            var a = new Vec3(tri[0].X, HeightOf(tri[0]), tri[0].Y);
            var b = new Vec3(tri[1].X, HeightOf(tri[1]), tri[1].Y);
            var c = new Vec3(tri[2].X, HeightOf(tri[2]), tri[2].Y);
            result.Add(SignedArea(tri) > 0 ? new OccupiedTriangle(a, c, b) : new OccupiedTriangle(a, b, c));
        }

        return result;
    }

    private static (double Distance, double Height, double MaxSlope, bool UsesTerrainSlope)? GetNearestRoadConstraint(Vec2 point, List<HeightSegment> segments)
    {
        (double Distance, double Height, double MaxSlope, bool UsesTerrainSlope)? best = null;
        foreach (var segment in segments)
        {
            var projection = ProjectToSegment(point, segment.A, segment.B);
            var height = segment.HeightA + ((segment.HeightB - segment.HeightA) * projection.T);
            if (best is null || projection.Distance < best.Value.Distance)
            {
                best = (projection.Distance, height, segment.MaxSlope, segment.UsesTerrainSlope);
            }
        }

        return best;
    }

    private static (double Height, double? SlopeDegrees)? SampleCutContainedBoundary(Vec2 point, IReadOnlyList<OccupiedTriangle> triangles)
    {
        (double Height, double? SlopeDegrees)? best = null;
        foreach (var tri in triangles)
        {
            if (GetBarycentric(point, tri) is not { } bary)
            {
                continue;
            }

            var height = (tri.A.Y * bary.U) + (tri.B.Y * bary.V) + (tri.C.Y * bary.W);
            if (best is null || height > best.Value.Height)
            {
                best = (height, tri.BankSlopeDegrees);
            }
        }

        return best;
    }

    private static (double Height, double? SlopeDegrees)? SampleCutBoundary(Vec2 point, IReadOnlyList<OccupiedTriangle> triangles)
    {
        if (SampleCutContainedBoundary(point, triangles) is { } contained)
        {
            return contained;
        }

        var bestDistance = double.PositiveInfinity;
        double? bestHeight = null;
        double? bestSlope = null;
        foreach (var tri in triangles)
        {
            Vec3[] vertices = [tri.A, tri.B, tri.C];
            for (var i = 0; i < 3; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % 3];
                var projection = ProjectToSegment(point, a.Xz, b.Xz);
                var height = a.Y + ((b.Y - a.Y) * projection.T);
                if (projection.Distance < bestDistance - Epsilon)
                {
                    bestDistance = projection.Distance;
                    bestHeight = height;
                    bestSlope = tri.BankSlopeDegrees;
                }
                else if (Math.Abs(projection.Distance - bestDistance) <= Epsilon && (bestHeight is null || height > bestHeight))
                {
                    bestHeight = height;
                    bestSlope = tri.BankSlopeDegrees;
                }
            }
        }

        return bestHeight is null ? null : (bestHeight.Value, bestSlope);
    }

    internal static (double U, double V, double W)? GetBarycentric(Vec2 point, OccupiedTriangle tri)
    {
        var denominator = ((tri.B.Z - tri.C.Z) * (tri.A.X - tri.C.X)) + ((tri.C.X - tri.B.X) * (tri.A.Z - tri.C.Z));
        if (Math.Abs(denominator) <= Epsilon)
        {
            return null;
        }

        var u = (((tri.B.Z - tri.C.Z) * (point.X - tri.C.X)) + ((tri.C.X - tri.B.X) * (point.Y - tri.C.Z))) / denominator;
        var v = (((tri.C.Z - tri.A.Z) * (point.X - tri.C.X)) + ((tri.A.X - tri.C.X) * (point.Y - tri.C.Z))) / denominator;
        var w = 1 - u - v;
        const double BaryEpsilon = 2e-5;
        return u >= -BaryEpsilon && v >= -BaryEpsilon && w >= -BaryEpsilon ? (u, v, w) : null;
    }

    private static bool IsInsideCutArea(Vec2 point, IReadOnlyList<OccupiedTriangle> triangles) => triangles.Any(tri => GetBarycentric(point, tri) is not null);

    private static double SmoothInfluence(double distance, double width)
    {
        if (width <= Epsilon || distance >= width)
        {
            return 0;
        }

        return 1 - MathUtil.Smoothstep(MathUtil.Clamp(distance / width, 0, 1));
    }

    private static string GetTopologySignature(TerrainMesherInput input)
    {
        static string TriangleKey(OccupiedTriangle tri)
        {
            var keys = new List<string> { PointKey(tri.A.Xz), PointKey(tri.B.Xz), PointKey(tri.C.Xz) };
            keys.Sort(string.CompareOrdinal); // Array.prototype.sort() without a comparator: UTF-16 order
            return string.Join(';', keys);
        }

        static List<string> Sorted(IEnumerable<string> values)
        {
            var list = values.ToList();
            list.Sort(string.CompareOrdinal);
            return list;
        }

        var cutterKeys = Sorted(input.CutAreas.Select(TriangleKey));
        var influenceAreaKeys = Sorted((input.InfluenceAreas ?? input.CutAreas).Select(TriangleKey));
        var pointKeys = Sorted(input.CutPoints.Select(point => $"{PointKey(point.Position.Xz)}@{Js.Num(SnapValue(point.Radius))}@{(point.ProfileOnly ? 1 : 0)}"));
        var paintKeys = Sorted((input.Paint?.Samples ?? [])
            .Where(sample => Math.Abs(sample.Height) > Epsilon)
            .Select(sample => $"{Js.Num(MathUtil.JsRound(sample.GridX))},{Js.Num(MathUtil.JsRound(sample.GridZ))}"));
        var footprintKeys = Sorted(GetFootprintRects(input)
            .Select(rect => string.Join(',', new[] { rect.MinX, rect.MaxX, rect.MinZ, rect.MaxZ }.Select(value => Js.Num(SnapValue(value))))));
        return string.Join('#',
            Js.Num(SnapValue(input.BaseHeight ?? input.Center.Y)),
            string.Join('|', footprintKeys),
            Js.Num(SnapValue(input.Settings.MeshDetail)),
            input.Settings.TriangleLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            input.Settings.SmoothingEnabled ? "1" : "0",
            Js.Num(SnapValue(input.Settings.SmoothingRadius)),
            string.Join('|', cutterKeys), string.Join('|', influenceAreaKeys), string.Join('|', pointKeys),
            Js.Num(SnapValue(input.Paint?.CellSize ?? 1)), string.Join('|', paintKeys));
    }

    private static void ForEachTriangularLatticePoint(double minX, double maxX, double minZ, double maxZ, double spacing, Func<Vec2, bool> visit)
    {
        var rowHeight = spacing * Math.Sqrt(3) / 2;
        var firstRow = (long)Math.Floor(minZ / rowHeight) - 1;
        var lastRow = (long)Math.Ceiling(maxZ / rowHeight) + 1;
        for (var row = firstRow; row <= lastRow; row++)
        {
            var z = row * rowHeight;
            if (z < minZ - Epsilon || z > maxZ + Epsilon)
            {
                continue;
            }

            var parity = ((row % 2) + 2) % 2;
            var offset = parity * spacing * 0.5;
            var firstColumn = (long)Math.Floor((minX - offset) / spacing) - 1;
            var lastColumn = (long)Math.Ceiling((maxX - offset) / spacing) + 1;
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var x = (column * spacing) + offset;
                if (x < minX - Epsilon || x > maxX + Epsilon)
                {
                    continue;
                }

                if (!visit(SnapPoint(new Vec2(x, z))))
                {
                    return;
                }
            }
        }
    }

    private static double GetAdaptiveSpacing(double distance, double detail, double baseRadius)
    {
        if (distance <= baseRadius)
        {
            return detail;
        }

        var tier = MathUtil.Clamp(Math.Ceiling(Math.Log2(distance / baseRadius)), 1, 3);
        return detail * Math.Pow(2, tier);
    }

    private static Vec2? GetCircumcenter(Vec2[] triangle)
    {
        var (a, b, c) = (triangle[0], triangle[1], triangle[2]);
        var denominator = 2 * ((a.X * (b.Y - c.Y)) + (b.X * (c.Y - a.Y)) + (c.X * (a.Y - b.Y)));
        if (Math.Abs(denominator) <= Epsilon)
        {
            return null;
        }

        var aLength = (a.X * a.X) + (a.Y * a.Y);
        var bLength = (b.X * b.X) + (b.Y * b.Y);
        var cLength = (c.X * c.X) + (c.Y * c.Y);
        return SnapPoint(new Vec2(
            ((aLength * (b.Y - c.Y)) + (bLength * (c.Y - a.Y)) + (cLength * (a.Y - b.Y))) / denominator,
            ((aLength * (c.X - b.X)) + (bLength * (a.X - c.X)) + (cLength * (b.X - a.X))) / denominator));
    }

    private static bool IsPointInTriangle(Vec2 point, Vec2[] triangle)
    {
        double Side(Vec2 a, Vec2 b) => ((point.X - b.X) * (a.Y - b.Y)) - ((a.X - b.X) * (point.Y - b.Y));
        var d1 = Side(triangle[0], triangle[1]);
        var d2 = Side(triangle[1], triangle[2]);
        var d3 = Side(triangle[2], triangle[0]);
        var hasNegative = d1 < -Epsilon || d2 < -Epsilon || d3 < -Epsilon;
        var hasPositive = d1 > Epsilon || d2 > Epsilon || d3 > Epsilon;
        return !(hasNegative && hasPositive);
    }

    private static HashSet<string> CollectBoundaryKeys(List<PolygonRegion> regions)
    {
        var result = new HashSet<string>();
        foreach (var region in regions)
        {
            foreach (var point in region.Contour.Concat(region.Holes.SelectMany(h => h)))
            {
                result.Add(PointKey(point));
            }
        }

        return result;
    }

    private static PolygonRegion? FindContainingRegion(Vec2 point, List<PolygonRegion> regions) => regions.FirstOrDefault(region => IsInsideRegion(point, region));

    private static bool IsInsideRegion(Vec2 point, PolygonRegion region) =>
        PointInRing(point, region.Contour) && !region.Holes.Any(hole => PointInRing(point, hole));

    private static bool IsStrictlyInsideRegion(Vec2 point, PolygonRegion region) =>
        IsInsideRegion(point, region) && GetDistanceToRegionBoundary(point, region) > Epsilon * 20;

    private static bool IsStrictlyInsideRegions(Vec2 point, List<PolygonRegion> regions) => regions.Any(region => IsStrictlyInsideRegion(point, region));

    internal static bool PointInRing(Vec2 point, IReadOnlyList<Vec2> ring)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            var dy = b.Y - a.Y;
            var intersects = ((a.Y > point.Y) != (b.Y > point.Y))
                && point.X < ((b.X - a.X) * (point.Y - a.Y) / (dy != 0 ? dy : 1e-12)) + a.X;
            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double GetDistanceToRegionBoundary(Vec2 point, PolygonRegion region)
    {
        var best = double.PositiveInfinity;
        foreach (var ring in region.Holes.Prepend(region.Contour))
        {
            for (var i = 0; i < ring.Count; i++)
            {
                best = Math.Min(best, ProjectToSegment(point, ring[i], ring[(i + 1) % ring.Count]).Distance);
            }
        }

        return best;
    }

    private static double GetNearestInfluenceDistance(Vec2 point, List<Segment2> roads, IReadOnlyList<TerrainCutPointInput> cutPoints)
    {
        var best = double.PositiveInfinity;
        foreach (var segment in roads)
        {
            best = Math.Min(best, ProjectToSegment(point, segment.A, segment.B).Distance);
        }

        foreach (var cutPoint in cutPoints)
        {
            best = Math.Min(best, MathUtil.Hypot(point.X - cutPoint.Position.X, point.Y - cutPoint.Position.Z));
        }

        return double.IsFinite(best) ? best : 0;
    }

    internal static (Vec2 Point, double Distance, double T) ProjectToSegment(Vec2 point, Vec2 a, Vec2 b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSq = (dx * dx) + (dy * dy);
        var t = lengthSq <= 1e-12 ? 0 : MathUtil.Clamp((((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSq, 0, 1);
        var projected = new Vec2(a.X + (dx * t), a.Y + (dy * t));
        return (projected, point.DistanceTo(projected), t);
    }

    private static double GetMinimumAngle(double a, double b, double c)
    {
        static double Angle(double opposite, double sideA, double sideB)
        {
            var denominator = Math.Max(1e-12, 2 * sideA * sideB);
            return MathUtil.RadToDeg(Math.Acos(MathUtil.Clamp(((sideA * sideA) + (sideB * sideB) - (opposite * opposite)) / denominator, -1, 1)));
        }

        return Math.Min(Angle(a, b, c), Math.Min(Angle(b, c, a), Angle(c, a, b)));
    }

    private static Vec2 SnapPoint(Vec2 point) => new(SnapValue(point.X), SnapValue(point.Y));

    private static double SnapValue(double value) => MathUtil.JsRound(value * Snap) / Snap;

    internal static string PointKey(Vec2 point) => $"{Js.Num(SnapValue(point.X))},{Js.Num(SnapValue(point.Y))}";

    private static string EdgeKey(Vec2 a, Vec2 b)
    {
        var ka = PointKey(a);
        var kb = PointKey(b);
        return string.CompareOrdinal(ka, kb) < 0 ? $"{ka}|{kb}" : $"{kb}|{ka}";
    }

    internal static double SignedArea(IReadOnlyList<Vec2> ring)
    {
        var area = 0.0;
        for (var i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        return area * 0.5;
    }
}
