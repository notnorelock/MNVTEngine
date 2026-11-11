using Microsoft.Extensions.Logging;
using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Editing;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Foliage;
using MNVTEngine.Terrain.Meshes;
using MNVTEngine.Terrain.Meshing;
using MNVTEngine.Terrain.Project;
using TerrainTile = MNVTEngine.Terrain.Elements.Terrain;

namespace MNVTEngine.Editor.Services;

/// <summary>What a left drag in the Scene view does while a terrain world is being edited.</summary>
public enum TerrainTool
{
    /// <summary>Click a node or surface to select it; drag a node to move it; Ctrl + drag an element to move it whole.</summary>
    Select,
    Road,
    Intersection,
    Tile,
    Polygon,
    River,
    CutSpline,
    CutPoint,
    Fence,
    Stairs,
    Height,
    Texture,
    Foliage,
    Props,
}

public enum HeightBrushMode
{
    Raise,
    Lower,
    Smooth,
}

/// <summary>Modifier keys of a Scene view pointer event.</summary>
[Flags]
public enum TerrainModifiers
{
    None = 0,

    /// <summary>Inverts a brush (lower, erase); starts a curved spline; moves a whole element in Select.</summary>
    Ctrl = 1,

    /// <summary>Snaps a spline end to 45° steps; moves a node vertically in Select.</summary>
    Shift = 2,
}

/// <summary>
/// Edits a <see cref="WorldTerrain"/>'s world in the Scene view, as TerrainHeaven's tools do: placing elements (spline
/// elements by drag, snapping and connecting to free nodes), selecting and dragging nodes, the height, texture,
/// foliage and prop brushes. UI-free: the Scene view passes rays in world space and a projection for node picking.
/// Every finished action is recorded in <see cref="History"/> and saved to the <c>.mnvtterrain</c> file at once, so
/// the asset on disk always matches what is shown.
/// </summary>
public sealed partial class TerrainEditor(ILogger logger)
{
    /// <summary>Pixels from a node's screen position that still pick it.</summary>
    public const float NodePickPixels = 10f;

    private const double MinElementLength = 0.1;
    private const double SnapNodeDistance = 1.5;
    private const int DefaultCurveDivisions = 12;
    private readonly Random _random = new();
    private Matrix4x4 _toLocal = Matrix4x4.Identity;
    private Matrix4x4 _toWorld = Matrix4x4.Identity;
    private Vector3? _dragStart;
    private WorldNode? _dragStartNode;
    private Vector3? _lastDab;
    private Vector3? _dragPrevious;
    private string? _actionLabel;
    private readonly List<Vector3> _polygon = [];

    public WorldTerrain? Target { get; private set; }

    public TerrainProject? Project { get; private set; }

    public TerrainHistory? History { get; private set; }

    /// <summary>The <c>.mnvtterrain</c> file being edited.</summary>
    public string? FilePath { get; private set; }

    public TerrainTool Tool
    {
        get;
        set
        {
            Cancel();
            field = value;
        }
    }

    public WorldElement? SelectedElement { get; private set; }

    public WorldNode? SelectedNode { get; private set; }

    public TerrainClipboard Clipboard { get; } = new();

    public ElementPresets Presets { get; } = new();

    public double HeightRadius { get; set; } = 3;

    public double HeightStrength { get; set; } = 0.25;

    public HeightBrushMode HeightMode { get; set; }

    public double TextureRadius { get; set; } = 4;

    public double TextureOpacity { get; set; } = 0.35;

    public double TextureHardness { get; set; } = 0.45;

    /// <summary>Dab spacing as a fraction of the radius.</summary>
    public double TextureSpacing { get; set; } = 0.15;

    public double TextureRotation { get; set; }

    public TextureBrushShape TextureShape { get; set; }

    public int TextureLayer { get; set; }

    public double FoliageRadius { get; set; } = 2;

    public int FoliageDensity { get; set; } = 25;

    public double FoliageSpacing { get; set; } = 0.3;

    public double FoliageMaxHeightDifference { get; set; } = 1.5;

    /// <summary>Foliage presets the brush scatters (indices into the foliage store).</summary>
    public HashSet<int> FoliageTypes { get; } = [];

    /// <summary>The prop asset the props brush places (index into the mesh store), or -1.</summary>
    public int PropAsset { get; set; } = -1;

    public double PropRadius { get; set; } = 2;

    public double PropSpacing { get; set; } = 2;

    public bool PropRandomRotation { get; set; } = true;

    /// <summary>Points of the polygon being placed (local space); Enter finishes it.</summary>
    public IReadOnlyList<Vector3> PolygonPoints => _polygon;

    /// <summary>The spline being dragged out (local space), for the preview line.</summary>
    public (Vector3 A, Vector3 B)? Placement { get; private set; }

    /// <summary>Where the brush is (local space), or null.</summary>
    public Vector3? BrushPoint { get; private set; }

    /// <summary>The brush radius of the current tool, or 0 for tools without a brush.</summary>
    public double BrushRadius => Tool switch
    {
        TerrainTool.Height => HeightRadius,
        TerrainTool.Texture => TextureRadius,
        TerrainTool.Foliage => FoliageRadius,
        TerrainTool.Props => PropRadius,
        _ => 0,
    };

    public bool IsEditing(WorldTerrain terrain) => terrain is not null && ReferenceEquals(Target, terrain);

    /// <summary>Starts editing a terrain's world from its file (the asset as saved, not the component's cached copy).</summary>
    /// <returns>False (logged) when the file cannot be read.</returns>
    public bool Begin(WorldTerrain target, string filePath)
    {
        ArgumentNullException.ThrowIfNull(target);
        End();
        var project = new TerrainProject(logger);
        try
        {
            project.Load(File.ReadAllText(filePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LogOpenFailed(logger, ex, filePath);
            return false;
        }

        project.World.LodPreviewLevel = target.LodLevel;
        project.World.Update();
        (Target, Project, FilePath) = (target, project, filePath);
        History = new TerrainHistory(project);
        History.Reset("Open");
        History.Restored += () =>
        {
            ClearSelection();
            target.MarkChanged();
            Save();
        };
        target.SetData(project);
        return true;
    }

    /// <summary>Stops editing; the component reloads the saved file.</summary>
    public void End()
    {
        Cancel();
        Target?.Reload();
        (Target, Project, History, FilePath) = (null, null, null, null);
        ClearSelection();
    }

    public void ClearSelection() => (SelectedElement, SelectedNode) = (null, null);

    /// <summary>Records the change for undo, saves the file and refreshes the component.</summary>
    public void Commit(string label)
    {
        if (Project is null || Target is null)
        {
            return;
        }

        Project.World.FlushDirty();
        Target.MarkChanged();
        History!.Record(label);
        Save();
    }

    public void Save()
    {
        if (Project is null || FilePath is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(FilePath, Project.Save());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(logger, ex, FilePath);
        }
    }

    public bool Undo() => History?.Undo() ?? false;

    public bool Redo() => History?.Redo() ?? false;

    /// <summary>Esc: drops a half-placed element or polygon.</summary>
    public void Cancel()
    {
        (Placement, _dragStart, _dragStartNode, _lastDab, _dragPrevious, _actionLabel) = (null, null, null, null, null, null);
        _polygon.Clear();
        History?.EndAction();
    }

    /// <summary>Enter: finishes the polygon being placed (three points or more).</summary>
    public void Finish()
    {
        if (Tool == TerrainTool.Polygon && _polygon.Count >= 3 && Project is not null)
        {
            var polygon = new PolygonTerrain(_polygon);
            Add(polygon, "Add Terrain Polygon");
        }

        _polygon.Clear();
    }

    public void DeleteSelection()
    {
        if (Project is null)
        {
            return;
        }

        if (SelectedNode is { } node && Project.World.CutPoints.IsCutPoint(node))
        {
            Project.World.CutPoints.RemovePoint(node);
            ClearSelection();
            Commit("Delete Cut Point");
        }
        else if (SelectedElement is { } element)
        {
            Project.World.Remove(element);
            ClearSelection();
            Commit($"Delete {element.GetProperties().Title}");
        }
    }

    public void Copy()
    {
        if (SelectedElement is { } element)
        {
            Clipboard.CopyElement(element);
        }
    }

    /// <summary>Pastes the copied element in front of the camera (world-space camera position and forward).</summary>
    public void Paste(Vector3 cameraPosition, Vector3 cameraForward)
    {
        if (Project is null)
        {
            return;
        }

        var (position, forward) = (ToLocal(cameraPosition), ToLocalDirection(cameraForward));
        if (Clipboard.CanPasteElement() && Clipboard.PasteElement(position, forward, Project.World) is { } pasted)
        {
            SelectedElement = pasted;
            Commit("Paste Element");
        }
        else if (SelectedElement is { } target && Clipboard.CanPastePropertiesOnto(target))
        {
            Clipboard.PastePropertiesOnto(target);
            Commit("Paste Properties");
        }
    }

    /// <summary>A left press in the Scene view.</summary>
    /// <param name="ray">The mouse ray in world space.</param>
    /// <param name="toScreen">World point to view pixels, or null when behind the camera (for node picking).</param>
    /// <returns>
    /// False when the click is not for the terrain: the Select tool hit no node and no surface of this world, or a
    /// placing tool found no ground. The Scene view then selects whatever else is under the mouse, as usual.
    /// </returns>
    public bool PointerDown(Ray ray, Vector2 mouse, Func<Vector3, Vector2?> toScreen, TerrainModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(toScreen);
        if (Project is null || Target is null)
        {
            return false;
        }

        UpdateTransform();
        var (origin, direction) = LocalRay(ray);
        var node = PickNode(mouse, toScreen);
        var ground = GroundPoint(origin, direction);
        switch (Tool)
        {
            case TerrainTool.Select:
                if (node is null && Raycast(origin, direction) is null)
                {
                    ClearSelection();
                    return false;
                }

                SelectedNode = node;
                SelectedElement = node?.Parent ?? (node is null ? Raycast(origin, direction)?.Element : null);
                _dragPrevious = node?.Position ?? Raycast(origin, direction)?.Point;
                _actionLabel = node is not null ? "Move Node" : modifiers.HasFlag(TerrainModifiers.Ctrl) && SelectedElement is not null ? "Move Element" : null;
                if (_actionLabel is not null)
                {
                    History!.BeginAction(_actionLabel);
                }

                break;
            case TerrainTool.Road or TerrainTool.River or TerrainTool.CutSpline or TerrainTool.Fence or TerrainTool.Stairs:
                var start = node?.Position ?? ground;
                if (start is not { } a)
                {
                    return false;
                }

                (_dragStart, _dragStartNode, Placement) = (a, node, (a, a));
                break;
            case TerrainTool.Intersection when ground is { } at:
                Add(new Intersection(at, 4), "Add Intersection");
                break;
            case TerrainTool.Tile when ground is { } at:
                Add(new TerrainTile(at, 30, 30), "Add Terrain");
                break;
            case TerrainTool.Polygon when ground is { } at:
                _polygon.Add(at);
                break;
            case TerrainTool.CutPoint when ground is { } at:
                SelectedNode = Project.World.CutPoints.AddPoint(at);
                SelectedElement = null;
                Commit("Add Cut Point");
                break;
            case TerrainTool.Height or TerrainTool.Texture or TerrainTool.Foliage or TerrainTool.Props:
                if (Raycast(origin, direction) is not { } hit)
                {
                    return false;
                }

                _actionLabel = BrushLabel(modifiers);
                History!.BeginAction(_actionLabel);
                _lastDab = hit.Point;
                Dab(hit.Point, modifiers);
                Target.MarkChanged();
                break;
            default:
                return false; // a placing tool with no ground under the mouse
        }

        return true;
    }

    /// <summary>The mouse moved (button held or not): previews, brushes and drags.</summary>
    public void PointerMove(Ray ray, bool leftDown, TerrainModifiers modifiers)
    {
        if (Project is null || Target is null)
        {
            return;
        }

        UpdateTransform();
        var (origin, direction) = LocalRay(ray);
        var hit = Raycast(origin, direction);
        BrushPoint = BrushRadius > 0 ? hit?.Point : null;
        if (!leftDown)
        {
            return;
        }

        if (_dragStart is { } start && Placement is not null && GroundPoint(origin, direction) is { } end)
        {
            Placement = (start, modifiers.HasFlag(TerrainModifiers.Shift) ? SnapAngle(start, end) : end);
            return;
        }

        if (_lastDab is { } last && hit is { } dab)
        {
            // Dabs at a fixed spacing along the stroke, as TerrainHeaven's brushes.
            var spacing = Tool == TerrainTool.Texture ? Math.Max(0.05, TextureRadius * TextureSpacing) : Math.Max(0.1, BrushRadius * 0.15);
            var delta = dab.Point - last;
            delta = delta.WithY(0);
            double distance = delta.Magnitude;
            if (Tool == TerrainTool.Props)
            {
                spacing = Math.Max(0.1, PropSpacing);
            }

            if (distance >= spacing)
            {
                var step = delta.Normalized * spacing;
                for (; distance >= spacing; distance -= spacing)
                {
                    last += step;
                    Dab(last, modifiers);
                }

                _lastDab = last;
                Project.World.FlushDirty();
                Target.MarkChanged();
            }

            return;
        }

        if (Tool == TerrainTool.Select && _actionLabel is not null && _dragPrevious is { } previous)
        {
            var moving = SelectedNode?.Position ?? previous;
            var target = modifiers.HasFlag(TerrainModifiers.Shift)
                ? VerticalPoint(origin, direction, moving)
                : PlanePoint(origin, direction, moving.Y);
            if (target is not { } to)
            {
                return;
            }

            if (SelectedNode is { } node)
            {
                MoveNode(node, to);
            }
            else if (SelectedElement is { } element)
            {
                element.Translate(to - previous);
            }

            _dragPrevious = to;
            Project.World.FlushDirty();
            Target.MarkChanged();
        }
    }

    /// <summary>The left button was released: finishes a placement, stroke or drag.</summary>
    public void PointerUp(Vector2 mouse, Func<Vector3, Vector2?> toScreen, TerrainModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(toScreen);
        if (Project is null)
        {
            return;
        }

        if (Placement is { } placement)
        {
            var endNode = PickNode(mouse, toScreen, exclude: _dragStartNode);
            var end = endNode?.Position ?? placement.B;
            if (placement.A.DistanceTo(end) >= MinElementLength)
            {
                PlaceSpline(placement.A, end, _dragStartNode, endNode, modifiers.HasFlag(TerrainModifiers.Ctrl));
            }

            (Placement, _dragStart, _dragStartNode) = (null, null, null);
            return;
        }

        if (_actionLabel is not null)
        {
            Project.World.FlushDirty();
            Target!.MarkChanged();
            History!.EndAction(_actionLabel);
            Save();
        }

        (_lastDab, _dragPrevious, _actionLabel) = (null, null, null);
    }

    /// <summary>The surface under a world ray (element triangles, local space), or null.</summary>
    public (WorldElement Element, Vector3 Point)? Raycast(Ray worldRay)
    {
        UpdateTransform();
        var (origin, direction) = LocalRay(worldRay);
        return Raycast(origin, direction);
    }

    public Vector3 ToWorld(Vector3 local) => Transform(_toWorld, local, 1f);

    private void UpdateTransform()
    {
        if (Target?.GameObject is { } owner)
        {
            _toWorld = owner.Transform.LocalToWorldMatrix;
            _toLocal = Matrix4x4.TryInvert(_toWorld, out var inverse) ? inverse : Matrix4x4.Identity;
        }
    }

    private Vector3 ToLocal(Vector3 world) => Transform(_toLocal, world, 1f);

    private Vector3 ToLocalDirection(Vector3 world) => Transform(_toLocal, world, 0f);

    private (Vector3 Origin, Vector3 Direction) LocalRay(Ray ray) => (ToLocal(ray.Origin), ToLocalDirection(ray.Direction).Normalized);

    private static Vector3 Transform(Matrix4x4 m, Vector3 v, float w)
    {
        var r = m * new Vector4(v.X, v.Y, v.Z, w);
        return new Vector3(r.X, r.Y, r.Z);
    }


    /// <summary>Every draggable node: element nodes, curve handles and cut points.</summary>
    public IEnumerable<WorldNode> Nodes() => Project is null ? []
        : Project.World.Elements.SelectMany(e => Enumerable.Range(0, e.NodeCount).Select(e.GetNode).Concat(e.GetChildWorldNodes())).Concat(Project.World.CutPoints.Points).Distinct();

    private WorldNode? PickNode(Vector2 mouse, Func<Vector3, Vector2?> toScreen, WorldNode? exclude = null)
    {
        WorldNode? best = null;
        var bestDistance = NodePickPixels;
        foreach (var node in Nodes())
        {
            if (node != exclude && toScreen(ToWorld(node.Position)) is { } screen && Vector2.Distance(screen, mouse) is var d && d <= bestDistance)
            {
                (best, bestDistance) = (node, d);
            }
        }

        return best;
    }

    private (WorldElement Element, Vector3 Point)? Raycast(Vector3 origin, Vector3 direction)
    {
        (WorldElement, Vector3)? best = null;
        var bestT = double.MaxValue;
        foreach (var element in Project!.World.Elements)
        {
            foreach (var group in element.Geometry)
            {
                foreach (var t in group.Triangles)
                {
                    if (Terrain.Geometry.RayCasting.IntersectTriangle(origin, direction, t.A, t.B, t.C) is { } hit && hit < bestT)
                    {
                        (bestT, best) = (hit, (element, origin + (direction * hit)));
                    }
                }
            }
        }

        return best;
    }

    /// <summary>Placement plane: local y = 0, as TerrainHeaven's tools place on the ground plane.</summary>
    private static Vector3? GroundPoint(Vector3 origin, Vector3 direction) => PlanePoint(origin, direction, 0);

    private static Vector3? PlanePoint(Vector3 origin, Vector3 direction, double y)
    {
        if (Math.Abs(direction.Y) < 1e-9)
        {
            return null;
        }

        var t = (y - origin.Y) / direction.Y;
        return t < 0 || t > 5000 ? null : origin + (direction * t);
    }

    /// <summary>The point on the vertical line through <paramref name="at"/> nearest the ray.</summary>
    private static Vector3? VerticalPoint(Vector3 origin, Vector3 direction, Vector3 at)
    {
        var toCamera = new Vector3(origin.X - at.X, 0, origin.Z - at.Z);
        if (toCamera.Magnitude < 1e-9)
        {
            return null;
        }

        // Intersect with the vertical plane through the point, facing the camera.
        var normal = toCamera.Normalized;
        var denominator = direction.Dot(normal);
        if (Math.Abs(denominator) < 1e-9)
        {
            return null;
        }

        var hit = origin + (direction * ((at - origin).Dot(normal) / denominator));
        return at.WithY(hit.Y);
    }

    private static Vector3 SnapAngle(Vector3 start, Vector3 end)
    {
        var (dx, dz) = (end.X - start.X, end.Z - start.Z);
        var length = Math.Sqrt((dx * dx) + (dz * dz));
        if (length < 1e-8)
        {
            return end;
        }

        var angle = Math.Round(Math.Atan2(dz, dx) / (Math.PI / 4)) * (Math.PI / 4);
        return new Vector3(start.X + (Math.Cos(angle) * length), end.Y, start.Z + (Math.Sin(angle) * length));
    }

    private void MoveNode(WorldNode node, Vector3 to)
    {
        if (Project!.World.CutPoints.IsCutPoint(node))
        {
            Project.World.CutPoints.Move(node, to);
            return;
        }

        node.Update(to);
        foreach (var element in node.Parent?.GetAffectedElementsForNode(node) ?? [])
        {
            element.Update();
        }
    }

    private void PlaceSpline(Vector3 a, Vector3 b, WorldNode? startNode, WorldNode? endNode, bool curved)
    {
        WorldElement element = Tool switch
        {
            TerrainTool.Road => new Road(a, b),
            TerrainTool.River => new RiverSpline(a, b),
            TerrainTool.CutSpline => new TerrainCutSpline(a, b),
            TerrainTool.Fence => new Fence(a, b),
            _ => new Stairs(a, b + new Vector3(0, 3, 0)),
        };
        Presets.ApplyDefault(element);
        if (curved && element is SplineElement spline)
        {
            if (spline.Divisions <= 0)
            {
                spline.Divisions = DefaultCurveDivisions;
            }

            spline.SetCurvePointA(a.LerpUnclamped(b, 1.0 / 3));
            spline.SetCurvePointB(a.LerpUnclamped(b, 2.0 / 3));
        }

        Project!.World.Add(element);
        TryConnect(element, 0, startNode);
        TryConnect(element, 1, endNode);
        SelectedElement = element;
        SelectedNode = null;
        Commit($"Add {element.GetProperties().Title}");
    }

    /// <summary>Connects an end of a new element to a free node it was snapped to (roads and intersections join).</summary>
    private static void TryConnect(WorldElement element, int index, WorldNode? node)
    {
        if (node?.Parent is { } parent && parent != element && element is not Stairs && parent.GetNodeIndex(node) is var other and >= 0
            && other < parent.NodeCount && !parent.IsConnected(other) && element.GetNode(index).Position.DistanceTo(node.Position) < SnapNodeDistance)
        {
            element.Connect(index, parent, other);
        }
    }

    private void Add(WorldElement element, string label)
    {
        Presets.ApplyDefault(element);
        Project!.World.Add(element);
        (SelectedElement, SelectedNode) = (element, null);
        Commit(label);
    }

    private string BrushLabel(TerrainModifiers modifiers) => Tool switch
    {
        TerrainTool.Height when HeightMode == HeightBrushMode.Smooth => "Smooth Terrain",
        TerrainTool.Height => HeightDirection(modifiers) > 0 ? "Raise Terrain" : "Lower Terrain",
        TerrainTool.Texture => "Paint Texture",
        TerrainTool.Foliage => modifiers.HasFlag(TerrainModifiers.Ctrl) ? "Erase Foliage" : "Paint Foliage",
        _ => modifiers.HasFlag(TerrainModifiers.Ctrl) ? "Erase Props" : "Place Props",
    };

    private int HeightDirection(TerrainModifiers modifiers) =>
        (HeightMode == HeightBrushMode.Lower ? -1 : 1) * (modifiers.HasFlag(TerrainModifiers.Ctrl) ? -1 : 1);

    private void Dab(Vector3 point, TerrainModifiers modifiers)
    {
        var erase = modifiers.HasFlag(TerrainModifiers.Ctrl);
        var tiles = Project!.World.Terrains.ToList();
        switch (Tool)
        {
            case TerrainTool.Height when HeightMode == HeightBrushMode.Smooth:
                TerrainTile.SmoothHeight(point, HeightRadius, HeightStrength, tiles);
                tiles.ForEach(t => t.Update());
                break;
            case TerrainTool.Height:
                foreach (var tile in tiles.Where(t => t.IntersectsPaintBrush(point, HeightRadius)))
                {
                    if (tile.PaintHeight(point, HeightRadius, HeightStrength * HeightDirection(modifiers)))
                    {
                        tile.Update();
                    }
                }

                break;
            case TerrainTool.Texture:
                foreach (var tile in tiles)
                {
                    if (tile.PaintTexture(point, TextureRadius, TextureLayer, TextureOpacity, TextureHardness, TextureShape, TextureRotation, _random.Next()))
                    {
                        tile.Update();
                    }
                }

                break;
            case TerrainTool.Foliage when erase:
                Project.Foliage.RemoveInstancesNear(point, FoliageRadius);
                break;
            case TerrainTool.Foliage:
                ScatterFoliage(point);
                break;
            case TerrainTool.Props when erase:
                Project.Meshes.RemoveInstancesNear(point, PropRadius);
                break;
            case TerrainTool.Props when PropAsset >= 0 && PropAsset < Project.Meshes.Assets.Count && !Project.Meshes.IsTooClose(PropAsset, point, PropSpacing):
                Project.Meshes.AddInstance(PropAsset, new MeshInstanceData
                {
                    Position = PointData.From(point),
                    RotationY = PropRandomRotation ? _random.NextDouble() * 360 : 0,
                });
                break;
            default:
                break;
        }
    }

    /// <summary>TerrainHeaven's foliage brush: random points in the disc dropped onto the surface below, skipping steps and crowding.</summary>
    private void ScatterFoliage(Vector3 center)
    {
        var types = FoliageTypes.Where(t => t >= 0 && t < Project!.Foliage.Types.Count).ToList();
        if (types.Count == 0)
        {
            return;
        }

        for (var attempt = 0; attempt < FoliageDensity; attempt++)
        {
            var angle = _random.NextDouble() * Math.PI * 2;
            var radius = Math.Sqrt(_random.NextDouble()) * FoliageRadius;
            var top = new Vector3(center.X + (Math.Cos(angle) * radius), center.Y + 50, center.Z + (Math.Sin(angle) * radius));
            if (Raycast(top, new Vector3(0, -1, 0)) is not { } hit || Math.Abs(hit.Point.Y - center.Y) > FoliageMaxHeightDifference)
            {
                continue;
            }

            var type = types[_random.Next(types.Count)];
            if (!Project!.Foliage.IsTooClose(type, hit.Point, FoliageSpacing))
            {
                Project.Foliage.AddInstance(type, new FoliageInstanceData
                {
                    Position = PointData.From(hit.Point),
                    RotationY = _random.NextDouble() * 360,
                    ScaleT = _random.NextDouble(),
                    ColorT = _random.NextDouble(),
                });
            }
        }
    }

    [LoggerMessage(EventId = 7140, Level = LogLevel.Error, Message = "Terrain editor: '{Path}' could not be opened")]
    private static partial void LogOpenFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(EventId = 7141, Level = LogLevel.Error, Message = "Terrain editor: '{Path}' could not be saved")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception, string path);
}
