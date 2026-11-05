using System.Globalization;
using Microsoft.Extensions.Logging;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Meshing;

/// <summary>
/// Meshes a whole group of touching terrain tiles as one surface: the union of the group is triangulated once, every
/// vertex height comes from a single evaluation of a single field, and the triangles are then cut along the tile borders
/// so each tile still draws with its own texture. Seams cannot disagree: there is nothing to match.
/// </summary>
public sealed class TerrainGroupMesher(TerrainWorld world, ILogger? logger = null)
{
    private const double Epsilon = 1e-5;
    private readonly Dictionary<Elements.Terrain, TerrainMesher> _meshers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Elements.Terrain, (string Signature, Dictionary<Elements.Terrain, List<OccupiedTriangle>> Slices)> _caches = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(Elements.Terrain, int), TerrainMesher> _lodMeshers = [];
    private readonly Dictionary<(Elements.Terrain, int), (string Signature, Dictionary<Elements.Terrain, List<OccupiedTriangle>> Slices)> _lodCaches = [];

    public IReadOnlyList<OccupiedTriangle> GetSurfaceFor(Elements.Terrain terrain) => BuildLodSurface(terrain, 0);

    public void Invalidate()
    {
        _caches.Clear();
        _lodCaches.Clear();
        foreach (var mesher in _meshers.Values.Concat(_lodMeshers.Values))
        {
            mesher.Invalidate();
        }
    }

    /// <summary>
    /// One tile's slice of its group at an LOD level (0: the live surface) with its own mesher and cache per level, so
    /// switching the LOD preview or exporting every level does not refire the mesher.
    /// </summary>
    public IReadOnlyList<OccupiedTriangle> BuildLodSurface(Elements.Terrain terrain, int lodIndex)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        var elements = world.Elements;
        var terrains = elements.OfType<Elements.Terrain>().ToList();
        var groups = GetGroups(terrains);
        var group = groups.FirstOrDefault(members => members.Contains(terrain));
        if (group is null)
        {
            return [];
        }

        var authority = group[0];
        PruneCaches(new HashSet<object>(groups.Select(members => (object)members[0]), ReferenceEqualityComparer.Instance));
        var settings = lodIndex > 0 ? LodLevels.ApplyTerrain(GetGroupSettings(group), lodIndex) : GetGroupSettings(group);
        var input = BuildInput(group, terrains, elements, settings, lodIndex);
        var signature = GetSignature(group, input);
        var cached = lodIndex <= 0 ? _caches.GetValueOrDefault(authority) : _lodCaches.GetValueOrDefault((authority, lodIndex));
        if (cached.Signature == signature && cached.Slices is not null)
        {
            return cached.Slices.GetValueOrDefault(terrain) ?? [];
        }

        TerrainMesher mesher;
        if (lodIndex <= 0)
        {
            if (!_meshers.TryGetValue(authority, out mesher!))
            {
                _meshers[authority] = mesher = new TerrainMesher(logger);
            }
        }
        else if (!_lodMeshers.TryGetValue((authority, lodIndex), out mesher!))
        {
            _lodMeshers[(authority, lodIndex)] = mesher = new TerrainMesher(logger);
        }

        var slices = SplitByTile(mesher.Build(input), group);
        if (lodIndex <= 0)
        {
            _caches[authority] = (signature, slices);
        }
        else
        {
            _lodCaches[(authority, lodIndex)] = (signature, slices);
        }

        return slices.GetValueOrDefault(terrain) ?? [];
    }

    /// <summary>Tiles touching along an edge (or a corner, or overlapping) belong to one surface.</summary>
    private static List<List<Elements.Terrain>> GetGroups(List<Elements.Terrain> terrains)
    {
        var groups = new List<List<Elements.Terrain>>();
        var remaining = new HashSet<Elements.Terrain>(terrains, ReferenceEqualityComparer.Instance);
        foreach (var seed in terrains)
        {
            if (!remaining.Remove(seed))
            {
                continue;
            }

            var members = new List<Elements.Terrain> { seed };
            var queue = new Queue<Elements.Terrain>([seed]);
            while (queue.TryDequeue(out var current))
            {
                foreach (var candidate in terrains)
                {
                    if (remaining.Contains(candidate) && AreNeighbours(current, candidate))
                    {
                        remaining.Remove(candidate);
                        members.Add(candidate);
                        queue.Enqueue(candidate);
                    }
                }
            }

            // Sorted so the group's identity and base height never depend on element order.
            Js.StableSort(members, (a, b) =>
            {
                var (first, second) = (a.GetBounds(), b.GetBounds());
                return first.MinX != second.MinX ? Js.Sign(first.MinX - second.MinX) : Js.Sign(first.MinZ - second.MinZ);
            });
            groups.Add(members);
        }

        return groups;
    }

    private static bool AreNeighbours(Elements.Terrain a, Elements.Terrain b)
    {
        var (first, second) = (a.GetBounds(), b.GetBounds());
        var overlapX = Math.Min(first.MaxX, second.MaxX) - Math.Max(first.MinX, second.MinX);
        var overlapZ = Math.Min(first.MaxZ, second.MaxZ) - Math.Max(first.MinZ, second.MinZ);
        var touchesX = Math.Abs(first.MaxX - second.MinX) <= Epsilon || Math.Abs(first.MinX - second.MaxX) <= Epsilon;
        var touchesZ = Math.Abs(first.MaxZ - second.MinZ) <= Epsilon || Math.Abs(first.MinZ - second.MaxZ) <= Epsilon;

        // Overlapping tiles are one surface too, otherwise the overlap would be meshed twice at two heights.
        var overlaps = overlapX > Epsilon && overlapZ > Epsilon;
        return overlaps || (touchesX && overlapZ >= -Epsilon) || (touchesZ && overlapX >= -Epsilon);
    }

    /// <summary>One surface needs one set of settings, reduced over the group so every tile's values count.</summary>
    private static TerrainMesherSettings GetGroupSettings(List<Elements.Terrain> group) => new(
        group.Min(tile => tile.MeshDetail),
        group.Sum(tile => tile.TriangleLimit),
        group.Any(tile => tile.SmoothingEnabled),
        group.Max(tile => tile.SmoothingRadius),
        group.Min(tile => tile.MaxSlopeDegrees));

    private TerrainMesherInput BuildInput(List<Elements.Terrain> group, List<Elements.Terrain> terrains, IReadOnlyList<WorldElement> elements, TerrainMesherSettings settings, int lodIndex)
    {
        var authority = group[0];
        var footprint = group.Select(tile => tile.GetBounds()).ToList();
        var cutAreaOrder = new List<string>();
        var cutAreas = new Dictionary<string, OccupiedTriangle>();
        foreach (var tile in group)
        {
            foreach (var triangle in TerrainBooleans.GetTerrainCutAreas(tile, elements))
            {
                var key = TriangleKey(triangle);
                if (!cutAreas.ContainsKey(key))
                {
                    cutAreaOrder.Add(key);
                }

                cutAreas[key] = triangle;
            }
        }

        var cutPoints = new List<TerrainCutPointInput>(world.CutPoints.GetPointsWithRadius());
        foreach (var element in elements)
        {
            if (element is TerrainCutSpline spline)
            {
                cutPoints.AddRange(spline.GetSampledCutPoints(lodIndex));
            }

            if (element is RiverSpline river)
            {
                cutPoints.AddRange(river.GetSampledTerrainPoints(Elements.Terrain.GetSharedRiverSurfaceHeight(river, terrains), lodIndex));
            }
        }

        return new TerrainMesherInput
        {
            Center = authority.Center,
            BaseHeight = authority.Center.Y,
            Width = authority.Width,
            Length = authority.Length,
            Footprint = footprint,
            CutAreas = cutAreaOrder.Select(key => cutAreas[key]).ToList(),
            InfluenceAreas = GetInfluenceAreas(footprint, authority, settings, elements),
            CutPoints = cutPoints,
            Paint = new TerrainPaintInput(Elements.Terrain.PaintCellSize, Elements.Terrain.CollectPaintSamples(terrains, footprint)),
            Settings = settings,
        };
    }

    /// <summary>An element shapes the surface as far as its slope needs to run back to the base height.</summary>
    private static List<OccupiedTriangle> GetInfluenceAreas(List<TerrainRect> footprint, Elements.Terrain authority, TerrainMesherSettings settings, IReadOnlyList<WorldElement> elements)
    {
        var maxSlope = Math.Max(0.01, Math.Tan(MathUtil.DegToRad(MathUtil.Clamp(settings.MaxSlopeDegrees, 1, 89))));
        var result = new List<OccupiedTriangle>();
        foreach (var element in elements)
        {
            if (element is Elements.Terrain || !element.CutsTerrainSurface())
            {
                continue;
            }

            var occupied = element.GetOccupiedArea();
            if (occupied.Count == 0)
            {
                continue;
            }

            var bounds = TerrainBooleans.GetBounds(occupied);
            var heightDifference = occupied.SelectMany(t => new[] { t.A, t.B, t.C }).Max(p => Math.Abs(p.Y - authority.Center.Y));
            var padding = Math.Max(settings.SmoothingRadius, 1.5 * heightDifference / maxSlope);
            if (footprint.Any(rect => RectsOverlap(bounds, rect, padding)))
            {
                result.AddRange(occupied);
            }
        }

        return result;
    }

    private static bool RectsOverlap(TerrainRect a, TerrainRect b, double padding) =>
        a.MaxX + padding >= b.MinX - Epsilon && a.MinX - padding <= b.MaxX + Epsilon
        && a.MaxZ + padding >= b.MinZ - Epsilon && a.MinZ - padding <= b.MaxZ + Epsilon;

    private static string F4(double value) => value.ToString("F4", CultureInfo.InvariantCulture);

    private static string TriangleKey(OccupiedTriangle triangle)
    {
        var keys = new[] { triangle.A, triangle.B, triangle.C }.Select(p => $"{F4(p.X)},{F4(p.Y)},{F4(p.Z)}").ToList();
        keys.Sort(string.CompareOrdinal);
        return string.Join('|', keys);
    }

    private static string GetSignature(List<Elements.Terrain> group, TerrainMesherInput input)
    {
        static string Sorted(IEnumerable<string> values)
        {
            var list = values.ToList();
            list.Sort(string.CompareOrdinal);
            return string.Join('|', list);
        }

        return string.Join('#',
            string.Join(';', group.Select(tile =>
            {
                var b = tile.GetBounds();
                return string.Join(',', new[] { b.MinX, b.MaxX, b.MinZ, b.MaxZ }.Select(F4));
            })),
            F4(input.BaseHeight ?? 0),
            string.Join(',', Js.Num(input.Settings.MeshDetail), input.Settings.TriangleLimit.ToString(CultureInfo.InvariantCulture),
                input.Settings.SmoothingEnabled ? "1" : "0", Js.Num(input.Settings.SmoothingRadius), Js.Num(input.Settings.MaxSlopeDegrees)),
            Sorted(input.CutAreas.Select(TriangleKey)),
            Sorted((input.InfluenceAreas ?? []).Select(TriangleKey)),
            Sorted(input.CutPoints.Select(p => string.Join(',',
                F4(p.Position.X), F4(p.Position.Y), F4(p.Position.Z), F4(p.Radius),
                p.MaxSlopeDegrees is null ? "-" : F4(p.MaxSlopeDegrees.Value),
                F4(p.SlopeSmoothing ?? 0), p.ProfileOnly ? "1" : "0"))),
            Sorted((input.Paint?.Samples ?? []).Select(s => $"{Js.Num(s.GridX)},{Js.Num(s.GridZ)},{F4(s.Height)}")));
    }

    private void PruneCaches(HashSet<object> authorities)
    {
        foreach (var key in _caches.Keys.Where(k => !authorities.Contains(k)).ToList())
        {
            _caches.Remove(key);
        }

        foreach (var key in _meshers.Keys.Where(k => !authorities.Contains(k)).ToList())
        {
            _meshers.Remove(key);
        }

        foreach (var key in _lodCaches.Keys.Where(k => !authorities.Contains(k.Item1)).ToList())
        {
            _lodCaches.Remove(key);
        }

        foreach (var key in _lodMeshers.Keys.Where(k => !authorities.Contains(k.Item1)).ToList())
        {
            _lodMeshers.Remove(key);
        }
    }

    /// <summary>
    /// Cuts the group surface along the tile borders and hands each tile its share. Splitting only adds vertices that
    /// lie on the triangle being split: bookkeeping, not geometry.
    /// </summary>
    private static Dictionary<Elements.Terrain, List<OccupiedTriangle>> SplitByTile(IReadOnlyList<OccupiedTriangle> triangles, List<Elements.Terrain> group)
    {
        var slices = new Dictionary<Elements.Terrain, List<OccupiedTriangle>>(ReferenceEqualityComparer.Instance);
        foreach (var tile in group)
        {
            slices[tile] = [];
        }

        if (group.Count == 1)
        {
            slices[group[0]] = [.. triangles];
            return slices;
        }

        var bounds = group.Select(tile => tile.GetBounds()).ToList();
        var xLines = InteriorLines(bounds.Select(r => r.MinX).Concat(bounds.Select(r => r.MaxX)));
        var zLines = InteriorLines(bounds.Select(r => r.MinZ).Concat(bounds.Select(r => r.MaxZ)));
        foreach (var triangle in triangles)
        {
            var polygons = new List<List<Vec3>> { new() { triangle.A, triangle.B, triangle.C } };
            foreach (var (useX, lines) in new[] { (true, xLines), (false, zLines) })
            {
                foreach (var line in lines)
                {
                    var next = new List<List<Vec3>>();
                    foreach (var polygon in polygons)
                    {
                        var values = polygon.Select(p => useX ? p.X : p.Z).ToList();
                        if (values.Max() <= line + Epsilon || values.Min() >= line - Epsilon)
                        {
                            next.Add(polygon);
                            continue;
                        }

                        foreach (var half in new[] { Clip(polygon, useX, line, true), Clip(polygon, useX, line, false) })
                        {
                            if (half.Count >= 3)
                            {
                                next.Add(half);
                            }
                        }
                    }

                    polygons = next;
                }
            }

            foreach (var polygon in polygons)
            {
                if (FindOwner(polygon, group, bounds) is not { } owner)
                {
                    continue;
                }

                var target = slices[owner];
                for (var index = 1; index + 1 < polygon.Count; index++)
                {
                    var (a, b, c) = (polygon[0], polygon[index], polygon[index + 1]);
                    if (Math.Abs(((b.X - a.X) * (c.Z - a.Z)) - ((b.Z - a.Z) * (c.X - a.X))) > 1e-9)
                    {
                        target.Add(new OccupiedTriangle(a, b, c));
                    }
                }
            }
        }

        return slices;
    }

    private static List<double> InteriorLines(IEnumerable<double> values)
    {
        var unique = new List<double>();
        foreach (var value in values.Order())
        {
            if (unique.Count == 0 || Math.Abs(unique[^1] - value) > Epsilon)
            {
                unique.Add(value);
            }
        }

        // The outermost lines bound the whole group: nothing crosses them.
        return unique.Count <= 2 ? [] : unique.GetRange(1, unique.Count - 2);
    }

    private static List<Vec3> Clip(List<Vec3> polygon, bool useX, double line, bool keepBelow)
    {
        double Axis(Vec3 p) => useX ? p.X : p.Z;
        bool Inside(Vec3 p) => keepBelow ? Axis(p) <= line + Epsilon : Axis(p) >= line - Epsilon;
        var result = new List<Vec3>();
        for (var index = 0; index < polygon.Count; index++)
        {
            var a = polygon[index];
            var b = polygon[(index + 1) % polygon.Count];
            var aInside = Inside(a);
            if (aInside)
            {
                result.Add(a);
            }

            if (aInside == Inside(b))
            {
                continue;
            }

            var span = Axis(b) - Axis(a);
            if (Math.Abs(span) < 1e-12)
            {
                continue;
            }

            result.Add(a.Lerp(b, MathUtil.Clamp((line - Axis(a)) / span, 0, 1)));
        }

        return result;
    }

    private static Elements.Terrain? FindOwner(List<Vec3> polygon, List<Elements.Terrain> group, List<TerrainRect> bounds)
    {
        var x = polygon.Average(p => p.X);
        var z = polygon.Average(p => p.Z);
        for (var index = 0; index < group.Count; index++)
        {
            var r = bounds[index];
            if (x >= r.MinX - Epsilon && x <= r.MaxX + Epsilon && z >= r.MinZ - Epsilon && z <= r.MaxZ + Epsilon)
            {
                return group[index];
            }
        }

        // Overlapping or floating-point-marginal pieces still have to belong somewhere, or the surface would have holes.
        Elements.Terrain? best = null;
        var bestDistance = double.PositiveInfinity;
        for (var index = 0; index < group.Count; index++)
        {
            var r = bounds[index];
            var distance = MathUtil.Hypot(x - MathUtil.Clamp(x, r.MinX, r.MaxX), z - MathUtil.Clamp(z, r.MinZ, r.MaxZ));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = group[index];
            }
        }

        return best;
    }
}
