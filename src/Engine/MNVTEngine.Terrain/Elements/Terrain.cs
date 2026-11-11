using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Geometry;
using MNVTEngine.Terrain.Meshing;

namespace MNVTEngine.Terrain.Elements;

/// <summary>One of the four splat layers a terrain tile blends (control map channels r, g, b, a).</summary>
public sealed class TerrainTextureLayer
{
    public string TexturePath { get; set; } = "";

    public double Tiling { get; set; } = 8;
}

/// <summary>
/// A rectangular terrain tile. Touching tiles are meshed as one surface by <see cref="TerrainGroupMesher"/> (cut around
/// roads, sculpted by cut points, rivers and painted heights), then each tile draws its share with its own UVs and
/// four-layer texture painting. Painted heights live on one world-aligned lattice shared by every tile.
/// </summary>
public sealed class Terrain : WorldElement
{
    public const double PaintCellSize = 1;

    /// <summary>One ring of cells past every edge, so a bilinear read on a seam never falls back to zero on one side.</summary>
    private const double PaintEdgeMargin = 1;
    private const double CellEpsilon = 1e-5;
    private readonly Dictionary<(long X, long Z), TerrainPaintHeightInput> _paintedHeights = [];
    private UVTransform _terrainUV = new();

    public Terrain(Vector3 center, double width = 20, double length = 20)
    {
        Center = center;
        Width = width;
        Length = length;
    }

    public Vector3 Center { get; set; }

    public double Width { get; set; }

    public double Length { get; set; }

    public double MeshDetail { get; set; } = 2;

    public int TriangleLimit { get; set; } = 1500;

    public bool SmoothingEnabled { get; set; } = true;

    public double SmoothingRadius { get; set; } = 4;

    public double MaxSlopeDegrees { get; set; } = 35;

    public TerrainTexturePaint TexturePaint { get; set; } = new(TerrainTexturePaint.DefaultResolution);

    public IReadOnlyList<TerrainTextureLayer> TextureLayers { get; } = [new(), new(), new(), new()];

    public override bool IsTerrainSurface() => true;

    public override bool DoubleSided => true;

    /// <summary>One low-poly terrain caster gives hills self-shadowing without rendering every road into the shadow map.</summary>
    public override bool CastsShadows => true;

    /// <summary>Painted heights (for tools and tests).</summary>
    public IReadOnlyCollection<TerrainPaintHeightInput> PaintedHeights => _paintedHeights.Values;

    public void SetTextureLayerTiling(int index, double tiling)
    {
        if (index >= 0 && index < TextureLayers.Count)
        {
            TextureLayers[index].Tiling = MathUtil.Clamp(tiling, 0.1, 100);
        }
    }

    /// <summary>The path a layer actually draws with: layer 0 falls back to the tile's own "terrain" texture.</summary>
    public string EffectiveLayerTexture(int index) =>
        TextureLayers[index].TexturePath is { Length: > 0 } path ? path
        : index == 0 && GetGroupTextureReferences().TryGetValue("terrain", out var own) ? own : "";

    public bool PaintTexture(Vector3 worldPosition, double radius, int layer, double opacity, double hardness, TextureBrushShape shape, double rotation, int seed = 0)
    {
        var bounds = GetBounds();
        return TexturePaint.Paint(new TexturePaintStroke(
            (worldPosition.X - bounds.MinX) / Math.Max(0.0001, Width),
            (worldPosition.Z - bounds.MinZ) / Math.Max(0.0001, Length),
            radius / Math.Max(0.0001, Width),
            radius / Math.Max(0.0001, Length),
            layer, opacity, hardness, rotation, shape, seed));
    }

    public void ClearTexturePaint() => TexturePaint.Clear(0);

    public override void Translate(Vector3 delta)
    {
        Center += delta;
        ShiftPaintedHeights(delta);
        Update();
    }

    public bool PaintHeight(Vector3 worldPosition, double radius, double amount)
    {
        var bounds = GetPaintBounds();
        var safeRadius = Math.Max(PaintCellSize * 0.5, radius);
        var firstX = (long)Math.Max(Math.Ceiling(bounds.MinX / PaintCellSize), Math.Floor((worldPosition.X - safeRadius) / PaintCellSize));
        var lastX = (long)Math.Min(Math.Floor(bounds.MaxX / PaintCellSize), Math.Ceiling((worldPosition.X + safeRadius) / PaintCellSize));
        var firstZ = (long)Math.Max(Math.Ceiling(bounds.MinZ / PaintCellSize), Math.Floor((worldPosition.Z - safeRadius) / PaintCellSize));
        var lastZ = (long)Math.Min(Math.Floor(bounds.MaxZ / PaintCellSize), Math.Ceiling((worldPosition.Z + safeRadius) / PaintCellSize));
        var changed = false;
        for (var gridX = firstX; gridX <= lastX; gridX++)
        {
            for (var gridZ = firstZ; gridZ <= lastZ; gridZ++)
            {
                var distance = MathUtil.Hypot((gridX * PaintCellSize) - worldPosition.X, (gridZ * PaintCellSize) - worldPosition.Z);
                if (distance > safeRadius)
                {
                    continue;
                }

                var falloff = 1 - MathUtil.Smoothstep(MathUtil.Clamp(distance / safeRadius, 0, 1));
                var previous = _paintedHeights.TryGetValue((gridX, gridZ), out var stored) ? stored.Height : 0;
                var height = MathUtil.Clamp(previous + (amount * falloff), -100, 100);
                if (Math.Abs(height - previous) <= 1e-6)
                {
                    continue;
                }

                SetCell(gridX, gridZ, height);
                changed = true;
            }
        }

        return changed;
    }

    private void SetCell(long gridX, long gridZ, double height)
    {
        if (Math.Abs(height) <= 1e-5)
        {
            _paintedHeights.Remove((gridX, gridZ));
        }
        else
        {
            _paintedHeights[(gridX, gridZ)] = new TerrainPaintHeightInput(gridX, gridZ, height);
        }
    }

    /// <summary>
    /// Blends each cell toward the average of its 3x3 neighbourhood. Computed for every affected tile from one snapshot
    /// of the field, then applied to all of them, so a seam cell's result never depends on tile order.
    /// </summary>
    public static bool SmoothHeight(Vector3 worldPosition, double radius, double strength, IReadOnlyList<Terrain> terrains)
    {
        ArgumentNullException.ThrowIfNull(terrains);
        var safeRadius = Math.Max(PaintCellSize * 0.5, radius);
        var blendStrength = MathUtil.Clamp(strength, 0, 1);
        var affected = terrains.Where(t => t.IntersectsPaintBrush(worldPosition, safeRadius)).ToList();
        if (affected.Count == 0)
        {
            return false;
        }

        // The combined range across every affected tile's padded footprint: a seam cell is considered once.
        double firstX = double.PositiveInfinity, lastX = double.NegativeInfinity, firstZ = double.PositiveInfinity, lastZ = double.NegativeInfinity;
        foreach (var terrain in affected)
        {
            var bounds = terrain.GetPaintBounds();
            firstX = Math.Min(firstX, Math.Max(Math.Ceiling(bounds.MinX / PaintCellSize), Math.Floor((worldPosition.X - safeRadius) / PaintCellSize)));
            lastX = Math.Max(lastX, Math.Min(Math.Floor(bounds.MaxX / PaintCellSize), Math.Ceiling((worldPosition.X + safeRadius) / PaintCellSize)));
            firstZ = Math.Min(firstZ, Math.Max(Math.Ceiling(bounds.MinZ / PaintCellSize), Math.Floor((worldPosition.Z - safeRadius) / PaintCellSize)));
            lastZ = Math.Max(lastZ, Math.Min(Math.Floor(bounds.MaxZ / PaintCellSize), Math.Ceiling((worldPosition.Z + safeRadius) / PaintCellSize)));
        }

        var updates = new List<((long X, long Z) Cell, double Height)>();
        for (var gridX = (long)firstX; gridX <= (long)lastX; gridX++)
        {
            for (var gridZ = (long)firstZ; gridZ <= (long)lastZ; gridZ++)
            {
                var distance = MathUtil.Hypot((gridX * PaintCellSize) - worldPosition.X, (gridZ * PaintCellSize) - worldPosition.Z);
                if (distance > safeRadius)
                {
                    continue;
                }

                var falloff = 1 - MathUtil.Smoothstep(MathUtil.Clamp(distance / safeRadius, 0, 1));
                var sum = 0.0;
                for (var ox = -1; ox <= 1; ox++)
                {
                    for (var oz = -1; oz <= 1; oz++)
                    {
                        sum += ReadPaintCell(terrains, gridX + ox, gridZ + oz);
                    }
                }

                var current = ReadPaintCell(terrains, gridX, gridZ);
                var next = MathUtil.Clamp(MathUtil.Lerp(current, sum / 9, blendStrength * falloff), -100, 100);
                if (Math.Abs(next - current) > 1e-6)
                {
                    updates.Add(((gridX, gridZ), next));
                }
            }
        }

        if (updates.Count == 0)
        {
            return false;
        }

        // A cell on a shared edge is stored by every bordering tile: write the same value into each so they stay equal.
        var margin = PaintEdgeMargin * PaintCellSize;
        foreach (var ((gridX, gridZ), height) in updates)
        {
            foreach (var terrain in affected)
            {
                if (IsWithinBounds(terrain.GetBounds(), gridX * PaintCellSize, gridZ * PaintCellSize, margin))
                {
                    terrain.SetCell(gridX, gridZ, height);
                }
            }
        }

        return true;
    }

    public bool ClearPaintedHeight()
    {
        if (_paintedHeights.Count == 0)
        {
            return false;
        }

        _paintedHeights.Clear();
        Update();
        return true;
    }

    /// <summary>Mirrors the padded footprint PaintHeight writes into, so a brush on the margin paints the neighbour too.</summary>
    public bool IntersectsPaintBrush(Vector3 worldPosition, double radius)
    {
        var bounds = GetPaintBounds();
        var closestX = MathUtil.Clamp(worldPosition.X, bounds.MinX, bounds.MaxX);
        var closestZ = MathUtil.Clamp(worldPosition.Z, bounds.MinZ, bounds.MaxZ);
        return MathUtil.Hypot(worldPosition.X - closestX, worldPosition.Z - closestZ) <= radius;
    }

    /// <summary>The painted offset: one field over every tile, so a point on a shared edge reads the same everywhere.</summary>
    public static double GetPaintedHeightOffsetAt(IReadOnlyList<Terrain> terrains, double worldX, double worldZ)
    {
        var gridX = worldX / PaintCellSize;
        var gridZ = worldZ / PaintCellSize;
        var x0 = (long)Math.Floor(gridX);
        var z0 = (long)Math.Floor(gridZ);
        var tx = gridX - x0;
        var tz = gridZ - z0;
        var bottom = MathUtil.Lerp(ReadPaintCell(terrains, x0, z0), ReadPaintCell(terrains, x0 + 1, z0), tx);
        var top = MathUtil.Lerp(ReadPaintCell(terrains, x0, z0 + 1), ReadPaintCell(terrains, x0 + 1, z0 + 1), tx);
        return MathUtil.Lerp(bottom, top, tz);
    }

    private TerrainRect GetPaintBounds()
    {
        var bounds = GetBounds();
        var margin = PaintEdgeMargin * PaintCellSize;
        return new TerrainRect(bounds.MinX - margin, bounds.MaxX + margin, bounds.MinZ - margin, bounds.MaxZ + margin);
    }

    private static bool IsWithinBounds(TerrainRect bounds, double x, double z, double margin)
    {
        var slack = margin + CellEpsilon;
        return x >= bounds.MinX - slack && x <= bounds.MaxX + slack && z >= bounds.MinZ - slack && z <= bounds.MaxZ + slack;
    }

    // Whichever tile holds the cell answers for it, the tiles actually covering it first; a stale margin cell cannot
    // outvote the tile the cell really sits on, and cells left outside a tile by a move or resize are ignored.
    private static double ReadPaintCell(IReadOnlyList<Terrain> terrains, long gridX, long gridZ)
    {
        var x = gridX * PaintCellSize;
        var z = gridZ * PaintCellSize;
        var margin = PaintEdgeMargin * PaintCellSize;
        double? borrowed = null;
        foreach (var terrain in terrains)
        {
            if (!terrain._paintedHeights.TryGetValue((gridX, gridZ), out var stored))
            {
                continue;
            }

            var bounds = terrain.GetBounds();
            if (!IsWithinBounds(bounds, x, z, margin))
            {
                continue;
            }

            if (IsWithinBounds(bounds, x, z, 0))
            {
                return stored.Height;
            }

            borrowed ??= stored.Height;
        }

        return borrowed ?? 0;
    }

    /// <summary>The resolved field over a group's footprint, owner first, exactly what <see cref="ReadPaintCell"/> says.</summary>
    public static List<TerrainPaintHeightInput> CollectPaintSamples(IReadOnlyList<Terrain> terrains, IReadOnlyList<TerrainRect> footprint)
    {
        ArgumentNullException.ThrowIfNull(terrains);
        var margin = PaintEdgeMargin * PaintCellSize;
        var ownedOrder = new List<(long, long)>();
        var owned = new Dictionary<(long, long), TerrainPaintHeightInput>();
        var borrowedOrder = new List<(long, long)>();
        var borrowed = new Dictionary<(long, long), TerrainPaintHeightInput>();
        foreach (var terrain in terrains)
        {
            var bounds = terrain.GetBounds();
            foreach (var (key, sample) in terrain._paintedHeights)
            {
                var x = sample.GridX * PaintCellSize;
                var z = sample.GridZ * PaintCellSize;
                if (!footprint.Any(rect => IsWithinBounds(rect, x, z, margin)) || !IsWithinBounds(bounds, x, z, margin))
                {
                    continue;
                }

                var (targetOrder, target) = IsWithinBounds(bounds, x, z, 0) ? (ownedOrder, owned) : (borrowedOrder, borrowed);
                if (target.TryAdd(key, sample with { }))
                {
                    targetOrder.Add(key);
                }
            }
        }

        foreach (var key in borrowedOrder)
        {
            if (owned.TryAdd(key, borrowed[key]))
            {
                ownedOrder.Add(key);
            }
        }

        return ownedOrder.Select(key => owned[key]).ToList();
    }

    /// <summary>The lattice is world-aligned: a moved tile carries its samples to the cells now under it.</summary>
    private void ShiftPaintedHeights(Vector3 delta)
    {
        var shiftX = (long)MathUtil.JsRound(delta.X / PaintCellSize);
        var shiftZ = (long)MathUtil.JsRound(delta.Z / PaintCellSize);
        if (_paintedHeights.Count == 0 || (shiftX == 0 && shiftZ == 0))
        {
            return;
        }

        var moved = _paintedHeights.Values.Select(s => new TerrainPaintHeightInput(s.GridX + shiftX, s.GridZ + shiftZ, s.Height)).ToList();
        _paintedHeights.Clear();
        foreach (var sample in moved)
        {
            _paintedHeights[((long)sample.GridX, (long)sample.GridZ)] = sample;
        }
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

    public override IReadOnlyList<OccupiedTriangle> GetOccupiedArea()
    {
        var halfWidth = Width / 2;
        var halfLength = Length / 2;
        var bl = new Vector3(Center.X - halfWidth, Center.Y, Center.Z - halfLength);
        var br = new Vector3(Center.X + halfWidth, Center.Y, Center.Z - halfLength);
        var tr = new Vector3(Center.X + halfWidth, Center.Y, Center.Z + halfLength);
        var tl = new Vector3(Center.X - halfWidth, Center.Y, Center.Z + halfLength);
        return [new(bl, br, tr), new(bl, tr, tl)];
    }

    public override ElementData Serialize(int id)
    {
        var (textures, rotations) = CollectTextureMaps();
        return new ElementData
        {
            Type = "terrain",
            Id = id,
            Nodes = [PointData.From(Center)],
            Textures = textures,
            TextureRotations = rotations,
            UvTransforms = new Dictionary<string, UVTransform> { ["terrain"] = _terrainUV.Clone() },
            TerrainWidth = Width,
            TerrainLength = Length,
            TerrainMeshDetail = MeshDetail,
            TerrainTriangleLimit = TriangleLimit,
            TerrainSmoothingEnabled = SmoothingEnabled,
            TerrainSmoothingRadius = SmoothingRadius,
            TerrainMaxSlope = MaxSlopeDegrees,
            TerrainHeightPaintSpace = "world",
            TerrainHeightPaint = [.. _paintedHeights.Values
                .OrderBy(s => s.GridZ).ThenBy(s => s.GridX)
                .Select(s => new HeightPaintSampleData { GridX = s.GridX, GridZ = s.GridZ, Height = s.Height })],
            TerrainTextureLayers = [.. TextureLayers.Select(l => new TextureLayerData { TexturePath = l.TexturePath, Tiling = l.Tiling })],
            TerrainTextureControlResolution = TexturePaint.Resolution,
            TerrainTextureControlRle = TexturePaint.EncodeRle(),
        };
    }

    /// <exception cref="FormatException">The texture control map is invalid (callers may keep the default map).</exception>
    public static Terrain Deserialize(ElementData data, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var node = data.Nodes.Count > 0 ? data.Nodes[0].ToVector3() : Vector3.Zero;
        var terrain = new Terrain(node, data.TerrainWidth ?? 20, data.TerrainLength ?? 20);
        var legacyDetail = data.TerrainGridEnabled == true ? data.TerrainGridSize : null;
        terrain.MeshDetail = MathUtil.Clamp(data.TerrainMeshDetail ?? legacyDetail ?? 2, 0.5, 5);
        terrain.TriangleLimit = (int)MathUtil.JsRound(MathUtil.Clamp(data.TerrainTriangleLimit ?? 1500, 100, 5000));
        terrain.SmoothingEnabled = data.TerrainSmoothingEnabled ?? true;
        terrain.SmoothingRadius = MathUtil.Clamp(data.TerrainSmoothingRadius ?? 4, 0.5, 20);
        terrain.MaxSlopeDegrees = MathUtil.Clamp(data.TerrainMaxSlope ?? 35, 1, 89);

        // Scenes saved before the lattice became world-aligned stored cells relative to the tile centre.
        var legacyPaint = (data.TerrainHeightPaintSpace ?? "local") != "world";
        var legacyShiftX = legacyPaint ? (long)MathUtil.JsRound(node.X / PaintCellSize) : 0;
        var legacyShiftZ = legacyPaint ? (long)MathUtil.JsRound(node.Z / PaintCellSize) : 0;
        foreach (var sample in data.TerrainHeightPaint ?? [])
        {
            if (!double.IsFinite(sample.GridX) || !double.IsFinite(sample.GridZ) || !double.IsFinite(sample.Height))
            {
                continue;
            }

            var gridX = (long)MathUtil.JsRound(sample.GridX) + legacyShiftX;
            var gridZ = (long)MathUtil.JsRound(sample.GridZ) + legacyShiftZ;
            terrain.SetCell(gridX, gridZ, MathUtil.Clamp(sample.Height, -100, 100));
        }

        var layers = data.TerrainTextureLayers ?? [];
        for (var index = 0; index < Math.Min(4, layers.Count); index++)
        {
            terrain.TextureLayers[index].TexturePath = layers[index].TexturePath ?? "";
            terrain.TextureLayers[index].Tiling = MathUtil.Clamp(layers[index].Tiling, 0.1, 100);
        }

        if (data.TerrainTextureControlRle is { Length: > 0 } rle)
        {
            try
            {
                terrain.TexturePaint = TerrainTexturePaint.DecodeRle(data.TerrainTextureControlResolution ?? TerrainTexturePaint.DefaultResolution, rle);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
            {
                warn?.Invoke($"Ignoring invalid terrain texture control map: {ex.Message}");
            }
        }

        if (data.UvTransforms?.GetValueOrDefault("terrain") is { } uv)
        {
            terrain._terrainUV = uv.Clone();
        }

        return terrain;
    }

    public override PropertyDefinition GetProperties()
    {
        var terrainSection = new List<PropertyItem>
        {
            new NumberProperty("Width", () => Width, v => Set(() => Width = Math.Max(0.1, v)), 10, 500, 0.1),
            new NumberProperty("Length", () => Length, v => Set(() => Length = Math.Max(0.1, v)), 10, 500, 0.1),
            new NumberProperty("UV Size", () => _terrainUV.ScaleX, v =>
            {
                var size = MathUtil.Clamp(v, 0.1, 100);
                _terrainUV.ScaleX = size;
                _terrainUV.ScaleY = size;
                Update();
            }, 0.1, 100, 0.1),
            new NumberProperty("Mesh Detail", () => MeshDetail, v => Set(() => MeshDetail = MathUtil.Clamp(v, 0.5, 5)), 0.5, 5, 0.25),
            new NumberProperty("Triangle Limit", () => TriangleLimit, v => Set(() => TriangleLimit = (int)MathUtil.JsRound(MathUtil.Clamp(v, 100, 5000))), 100, 5000, 100),
            new BooleanProperty("Terrain Smoothing", () => SmoothingEnabled, v =>
            {
                SmoothingEnabled = v;
                Update();
                OnPropertiesChanged?.Invoke();
            }),
        };
        if (SmoothingEnabled)
        {
            terrainSection.Add(new NumberProperty("Smoothing Radius", () => SmoothingRadius, v => Set(() => SmoothingRadius = MathUtil.Clamp(v, 0.5, 20)), 0.5, 20, 0.5));
            terrainSection.Add(new NumberProperty("Max Slope", () => MaxSlopeDegrees, v => Set(() => MaxSlopeDegrees = MathUtil.Clamp(v, 1, 89)), 1, 89, 1));
        }

        return new PropertyDefinition("Terrain", "□",
        [
            new PropertySection("Transform", [new Vector3Property("Center", () => Center, v => Set(() => Center = v))]),
            new PropertySection("Terrain", terrainSection),
        ]);
    }

    private void Set(Action change)
    {
        change();
        Update();
    }

    protected override List<GeometryGroup> GetGeometry() => BuildGeometry(World?.LodPreviewLevel ?? 0);

    /// <summary>Geometry at an export LOD level, without touching the live element or the LOD preview.</summary>
    public List<GeometryGroup> GetExportGeometry(int lodIndex) => BuildGeometry(lodIndex);

    private List<GeometryGroup> BuildGeometry(int lodIndex)
    {
        // The whole group of touching tiles is meshed as one surface and cut along the tile borders: this tile skins
        // its own share of it.
        var area = World?.GroupMesher.BuildLodSurface(this, lodIndex) ?? [];
        var width = Math.Max(0.0001, Width);
        var length = Math.Max(0.0001, Length);
        var uv = _terrainUV;
        Vector2 UvFor(Vector3 point) => new(
            ((((point.X - Center.X) / width) + 0.5) * uv.ScaleX) + uv.OffsetX,
            ((((point.Z - Center.Z) / length) + 0.5) * uv.ScaleY) + uv.OffsetY);
        var triangles = area.Select(tri => new Triangle(tri.A, tri.B, tri.C, UvFor(tri.A), UvFor(tri.B), UvFor(tri.C))).ToList();
        return [new GeometryGroup("terrain", triangles)];
    }

    /// <summary>Every tile a river touches agrees on one water surface, derived from the river's own footprint.</summary>
    public static double GetSharedRiverSurfaceHeight(RiverSpline river, IReadOnlyList<Terrain> terrains)
    {
        ArgumentNullException.ThrowIfNull(river);
        ArgumentNullException.ThrowIfNull(terrains);
        var occupied = river.GetOccupiedArea();
        if (occupied.Count == 0 || terrains.Count == 0)
        {
            return terrains.Count > 0 ? terrains[0].Center.Y : 0;
        }

        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;
        foreach (var triangle in occupied)
        {
            minX = Math.Min(minX, Math.Min(triangle.A.X, Math.Min(triangle.B.X, triangle.C.X)));
            maxX = Math.Max(maxX, Math.Max(triangle.A.X, Math.Max(triangle.B.X, triangle.C.X)));
            minZ = Math.Min(minZ, Math.Min(triangle.A.Z, Math.Min(triangle.B.Z, triangle.C.Z)));
            maxZ = Math.Max(maxZ, Math.Max(triangle.A.Z, Math.Max(triangle.B.Z, triangle.C.Z)));
        }

        var centerX = (minX + maxX) / 2;
        var centerZ = (minZ + maxZ) / 2;
        var authority = terrains.FirstOrDefault(terrain =>
        {
            var b = terrain.GetBounds();
            return maxX >= b.MinX && minX <= b.MaxX && maxZ >= b.MinZ && minZ <= b.MaxZ;
        }) ?? terrains[0];
        var bounds = authority.GetBounds();
        var x = MathUtil.Clamp(centerX, bounds.MinX, bounds.MaxX);
        var z = MathUtil.Clamp(centerZ, bounds.MinZ, bounds.MaxZ);
        return authority.Center.Y + GetPaintedHeightOffsetAt(terrains, x, z);
    }

    public TerrainRect GetBounds() => new(Center.X - (Width / 2), Center.X + (Width / 2), Center.Z - (Length / 2), Center.Z + (Length / 2));
}
