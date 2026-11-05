using Newtonsoft.Json;

namespace MNVTEngine.Terrain.Elements;

/// <summary>A point as TerrainHeaven's project files store it.</summary>
public sealed class PointData
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public static PointData From(Geometry.Vec3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    public Geometry.Vec3 ToVec3() => new(X, Y, Z);
}

public sealed class UVTransform
{
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    public double ScaleX { get; set; } = 1;

    public double ScaleY { get; set; } = 1;

    public UVTransform Clone() => (UVTransform)MemberwiseClone();
}

public sealed class HeightPaintSampleData
{
    public double GridX { get; set; }

    public double GridZ { get; set; }

    public double Height { get; set; }
}

public sealed class TextureLayerData
{
    public string TexturePath { get; set; } = "";

    public double Tiling { get; set; } = 8;
}

public sealed class BuildingSegmentData
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public double Width { get; set; }

    public double Depth { get; set; }

    public double? WallHeight { get; set; }

    public double? RoofRidgeHeight { get; set; }

    public double? RoofOverhang { get; set; }

    public string? RoofDirection { get; set; }

    public string? RoofType { get; set; }

    public double? HipRidgeRatio { get; set; }

    public int? GambrelSegments { get; set; }

    public double? GambrelRoundness { get; set; }

    public bool? RailingEnabled { get; set; }

    public double? RailingHeight { get; set; }

    public double? RailingThickness { get; set; }
}

public sealed class BuildingOpeningData
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public string Type { get; set; } = "window";

    public double Width { get; set; }

    public double Height { get; set; }

    public double Depth { get; set; }
}

public sealed class BuildingRoofWindowData
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }

    public double? Width { get; set; }

    public double? Depth { get; set; }

    public double? WallHeight { get; set; }

    public double? RidgeHeight { get; set; }
}

/// <summary>
/// One element of a TerrainHeaven project file, field for field: the element type, its nodes and textures, and the
/// type-specific settings (absent fields keep the element's defaults, so older files still load).
/// </summary>
public sealed class ElementData
{
    /// <summary>road, intersection, terrain, terrainCutSpline, river, fence, stairs, terrainPolygon or building.</summary>
    public string Type { get; set; } = "";

    public int Id { get; set; }

    public List<PointData> Nodes { get; set; } = [];

    public Dictionary<string, string> Textures { get; set; } = [];

    public Dictionary<string, double> TextureRotations { get; set; } = [];

    public Dictionary<string, UVTransform>? UvTransforms { get; set; }

    // Road
    public double? Width { get; set; }

    public int? Lanes { get; set; }

    public int? Divisions { get; set; }

    public string? EdgeType { get; set; }

    public double? SidewalkWidth { get; set; }

    public double? CurbHeight { get; set; }

    public double? RoadCrown { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public PointData? CurvePointA { get; set; }

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public PointData? CurvePointB { get; set; }

    public bool? BridgeEnabled { get; set; }

    public string? BridgePillarShape { get; set; }

    public int? BridgePillarSegments { get; set; }

    public double? BridgePillarDistance { get; set; }

    public int? BridgePillarCount { get; set; }

    public double? BridgePillarWidth { get; set; }

    public double? BridgePillarInset { get; set; }

    public double? BridgeDeckThickness { get; set; }

    public string? BridgeEdgeStyle { get; set; }

    public double? BridgeEdgeHeight { get; set; }

    public double? BridgeEdgeWidth { get; set; }

    public double? BridgeEdgeDistance { get; set; }

    public double? BridgeEdgeLength { get; set; }

    public bool? BridgeEdgeCapEnabled { get; set; }

    public double? BridgeEdgeCapHeight { get; set; }

    public double? BridgeEdgeCapWidth { get; set; }

    public bool? BridgeEdgeCapJoined { get; set; }

    /// <summary>Legacy: migrated to <see cref="BridgeEdgeCapWidth"/> when loading older project files.</summary>
    public double? BridgeEdgeCapOverhang { get; set; }

    // Intersection
    public double? Length { get; set; }

    public double? Rotation { get; set; }

    public int? NodeCount { get; set; }

    public double? OutletWidth { get; set; }

    public double? OutletLength { get; set; }

    public double? RoadTexWidth { get; set; }

    public double? RoadTexHeight { get; set; }

    public double? RoadTexOffsetX { get; set; }

    public double? RoadTexOffsetY { get; set; }

    public double? SidewalkTexWidth { get; set; }

    public double? SidewalkTexHeight { get; set; }

    public double? SidewalkTexOffsetX { get; set; }

    public double? SidewalkTexOffsetY { get; set; }

    // Terrain
    public double? TerrainWidth { get; set; }

    public double? TerrainLength { get; set; }

    public bool? TerrainGridEnabled { get; set; }

    public double? TerrainGridSize { get; set; }

    public double? TerrainMeshDetail { get; set; }

    public double? TerrainTriangleLimit { get; set; }

    public bool? TerrainSmoothingEnabled { get; set; }

    public double? TerrainSmoothingRadius { get; set; }

    public double? TerrainMaxSlope { get; set; }

    public List<HeightPaintSampleData>? TerrainHeightPaint { get; set; }

    /// <summary>"local" is the legacy centre-relative paint lattice; "world" is the shared one.</summary>
    public string? TerrainHeightPaintSpace { get; set; }

    public List<TextureLayerData>? TerrainTextureLayers { get; set; }

    public int? TerrainTextureControlResolution { get; set; }

    public string? TerrainTextureControlRle { get; set; }

    // Terrain cut spline
    public double? TerrainCutDistance { get; set; }

    // River
    public double? RiverBankSlope { get; set; }

    public double? RiverBankSmoothing { get; set; }

    public double? RiverIrregularityLevel { get; set; }

    public double? RiverDetailLevel { get; set; }

    // Fence
    public string? FenceStyle { get; set; }

    public double? FenceHeight { get; set; }

    public double? FenceThickness { get; set; }

    public double? FencePostSpacing { get; set; }

    public double? FencePostHeight { get; set; }

    public double? FencePostWidth { get; set; }

    public string? FencePostShape { get; set; }

    public int? FencePostSides { get; set; }

    public double? FenceMaxAngleStep { get; set; }

    // Stairs
    public double? StairsWidth { get; set; }

    public int? StairsStepCount { get; set; }

    public double? StairsFoundationDepth { get; set; }

    public string? StairsRailingSide { get; set; }

    public double? StairsRailingHeight { get; set; }

    public double? StairsRailingThickness { get; set; }

    public double? StairsRailingPostSpacing { get; set; }

    public bool? StairsMidRail { get; set; }

    public bool? StairsCutTerrain { get; set; }

    // Building
    public List<BuildingSegmentData>? BuildingSegments { get; set; }

    public double? BuildingWallHeight { get; set; }

    public string? BuildingRoofType { get; set; }

    public double? BuildingRoofOverhang { get; set; }

    public double? BuildingRoofRidgeHeight { get; set; }

    public double? BuildingRoofThickness { get; set; }

    public double? BuildingRailingHeight { get; set; }

    public double? BuildingRailingThickness { get; set; }

    public bool? BuildingCutInGround { get; set; }

    public List<BuildingOpeningData>? BuildingOpenings { get; set; }

    public List<BuildingRoofWindowData>? BuildingRoofWindows { get; set; }
}
