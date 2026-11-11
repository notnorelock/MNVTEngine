using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Materials;
using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Graphics.Rendering3D;
using MNVTEngine.Mathematics;
using MNVTEngine.Runtime;
using SysVector2 = System.Numerics.Vector2;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// The Scene view in 3D: the scene through <see cref="EditorCamera3D"/> with the game's renderer (meshes, lights,
/// shadows, then sprites), a ground grid (depth-tested against the scene), and overlays drawn on top: light and camera
/// icons, the selection's bounds and the 3D transform gizmo.
/// <list type="bullet">
/// <item>Left click: select (nearest mesh triangle, or an icon), or drag a gizmo handle (Ctrl snaps).</item>
/// <item>Alt + left drag: orbit. Middle drag: pan. Right drag: look around, with WASD/QE to fly (Shift faster). Wheel: zoom.</item>
/// <item>F: frame the selection.</item>
/// <item>While a terrain world is edited (Terrain window), left click and drag use the terrain tool instead; Esc cancels
/// a placement, Enter finishes a polygon, Delete removes the selected element, Ctrl+C/Ctrl+V copy and paste it.</item>
/// </list>
/// </summary>
internal sealed class SceneView3D(EditorContext context) : IDisposable
{
    private static readonly uint[] AxisColors = [0xFF4D4DE6, 0xFF4DD94D, 0xFFE6804D]; // ABGR: red X, green Y, blue Z
    private const uint Highlight = 0xFF33E6FF;
    private readonly EditorCamera3D _camera = new();
    private readonly TransformGizmo3D _gizmo = new();
    private Gizmo3DHandle _hovered;
    private Renderer3D? _gridRenderer;
    private GpuMeshCache? _meshes;
    private Material? _gridMaterial;
    private Mesh? _grid;
    private float _gridSpacing;
    private bool _terrainDragging;

    public GizmoTool Tool { get => _gizmo.Tool; set => _gizmo.Tool = value; }

    public EditorCamera3D Camera => _camera;

    public void SetViewport(int width, int height) => _camera.SetViewport(width, height);

    public void HandleInput(Vector2 origin, bool hovered, bool focused)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos.ToEngine() - origin;
        var selected = context.Selected;
        var looking = ImGui.IsItemActive() && ImGui.IsMouseDown(ImGuiMouseButton.Right);
        var terrain = context.TerrainEditor.Target is not null && !context.PlayMode.IsPlaying ? context.TerrainEditor : null;

        if (focused && !io.WantTextInput && !looking && terrain is null)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.W, false))
            {
                Tool = GizmoTool.Move;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.E, false))
            {
                Tool = GizmoTool.Rotate;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.R, false))
            {
                Tool = GizmoTool.Scale;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.F, false) && selected is not null)
            {
                _camera.Frame(BoundsOf(selected));
            }

            if (ImGui.IsKeyPressed(ImGuiKey.Delete, false) && selected is not null)
            {
                context.Delete(selected);
                selected = null;
            }
        }

        if (hovered && io.MouseWheel != 0f)
        {
            _camera.Zoom(io.MouseWheel);
        }

        var delta = io.MouseDelta.ToEngine();
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
        {
            _camera.Pan(delta);
        }

        if (looking)
        {
            _camera.Look(delta);
            var fly = new Vector3(
                (ImGui.IsKeyDown(ImGuiKey.D) ? 1f : 0f) - (ImGui.IsKeyDown(ImGuiKey.A) ? 1f : 0f),
                (ImGui.IsKeyDown(ImGuiKey.E) ? 1f : 0f) - (ImGui.IsKeyDown(ImGuiKey.Q) ? 1f : 0f),
                (ImGui.IsKeyDown(ImGuiKey.W) ? 1f : 0f) - (ImGui.IsKeyDown(ImGuiKey.S) ? 1f : 0f));
            _camera.Fly(fly, io.DeltaTime, io.KeyShift);
            return;
        }

        if (io.KeyAlt && ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            _camera.Orbit(delta);
            return;
        }

        if (terrain is not null)
        {
            HandleTerrainInput(terrain, mouse, hovered, focused);
            return;
        }

        _hovered = selected is not null && hovered && !_gizmo.IsDragging ? _gizmo.HitTest(selected.Transform, _camera, mouse) : Gizmo3DHandle.None;
        if (hovered && !io.KeyAlt && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (selected is not null && _hovered != Gizmo3DHandle.None)
            {
                _gizmo.BeginDrag(selected.Transform, _hovered, _camera, mouse);
            }
            else
            {
                context.Session.Selection.Select(ScenePicker3D.Pick(context.ActiveScene, _camera.ScreenRay(mouse), MeshOf, _camera.UnitsPerPixel));
            }
        }

        if (_gizmo.IsDragging)
        {
            if (selected is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (_gizmo.Drag(selected.Transform, _camera, mouse, io.KeyCtrl))
                {
                    context.Changed("Transform");
                }
            }
            else
            {
                _gizmo.EndDrag();
            }
        }
    }

    private void HandleTerrainInput(TerrainEditor terrain, Vector2 mouse, bool hovered, bool focused)
    {
        var io = ImGui.GetIO();
        var modifiers = (io.KeyCtrl ? TerrainModifiers.Ctrl : TerrainModifiers.None) | (io.KeyShift ? TerrainModifiers.Shift : TerrainModifiers.None);
        Vector2? ToScreen(Vector3 world) => _camera.WorldToScreen(world) is { Z: > 0f } s ? new Vector2(s.X, s.Y) : null;
        var ray = _camera.ScreenRay(mouse);
        if (focused && !io.WantTextInput)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            {
                terrain.Cancel();
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false))
            {
                terrain.Finish();
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.Delete, false))
            {
                terrain.DeleteSelection();
            }
            else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.C, false))
            {
                terrain.Copy();
            }
            else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.V, false))
            {
                terrain.Paste(_camera.Position, (_camera.Pivot - _camera.Position).Normalized);
            }
        }

        if (hovered || _terrainDragging)
        {
            terrain.PointerMove(ray, _terrainDragging && ImGui.IsMouseDown(ImGuiMouseButton.Left), modifiers);
        }

        if (hovered && !io.KeyAlt && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            terrain.PointerDown(ray, mouse, ToScreen, modifiers);
            _terrainDragging = true;
        }

        if (_terrainDragging && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            terrain.PointerUp(mouse, ToScreen, modifiers);
            _terrainDragging = false;
        }
    }

    /// <summary>Nodes, curve guides, the placement preview and the brush of the terrain being edited.</summary>
    private void DrawTerrainOverlay(TerrainEditor terrain, Vector2 origin)
    {
        var list = ImGui.GetWindowDrawList();
        SysVector2? Screen(Vector3 local) => _camera.WorldToScreen(terrain.ToWorld(local)) is { Z: > 0f } s ? new SysVector2(origin.X + s.X, origin.Y + s.Y) : null;
        void Polyline(IReadOnlyList<Vector3> points, uint color, float thickness, bool closed = false)
        {
            for (var i = 0; i + 1 < points.Count + (closed ? 1 : 0); i++)
            {
                if (Screen(points[i]) is { } a && Screen(points[(i + 1) % points.Count]) is { } b)
                {
                    list.AddLine(a, b, color, thickness);
                }
            }
        }

        foreach (var spline in terrain.Project!.World.Elements.OfType<Terrain.Elements.SplineElement>())
        {
            foreach (var guide in spline.GetGuideLines())
            {
                var rgb = guide.Color ?? 0x8899AAu;
                Polyline(guide.Points, 0x80000000u | ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF), 1f);
            }
        }

        foreach (var node in terrain.Nodes())
        {
            if (Screen(node.Position) is not { } at)
            {
                continue;
            }

            var selected = node == terrain.SelectedNode || (node.Parent is not null && node.Parent == terrain.SelectedElement);
            var color = node.Kind switch
            {
                Terrain.Elements.NodeKind.CurveHandle => 0xFFE0A040u,
                Terrain.Elements.NodeKind.TerrainCut => 0xFF4080FFu,
                Terrain.Elements.NodeKind.River => 0xFFFFC040u,
                _ => 0xFFF0F0F0u,
            };
            list.AddCircleFilled(at, node == terrain.SelectedNode ? 7f : 5f, selected ? Highlight : color);
            list.AddCircle(at, node == terrain.SelectedNode ? 9f : 7f, 0xFF202020, 0, 1.5f);
        }

        if (terrain.Placement is { } placement)
        {
            Polyline([placement.A, placement.B], Highlight, 3f);
        }

        if (terrain.PolygonPoints.Count > 0)
        {
            Polyline(terrain.PolygonPoints, Highlight, 2f, closed: terrain.PolygonPoints.Count > 2);
            foreach (var point in terrain.PolygonPoints)
            {
                if (Screen(point) is { } at)
                {
                    list.AddCircleFilled(at, 4f, Highlight);
                }
            }
        }

        if (terrain.BrushPoint is { } center && terrain.BrushRadius > 0)
        {
            const int Segments = 48;
            var ring = Enumerable.Range(0, Segments).Select(i => center + new Vector3(Math.Cos(i * Math.PI * 2 / Segments) * terrain.BrushRadius, 0.05, Math.Sin(i * Math.PI * 2 / Segments) * terrain.BrushRadius)).ToList();
            Polyline(ring, ImGui.GetIO().KeyCtrl ? 0xFF5555FFu : 0xFF77EE55u, 2f, closed: true);
        }
    }

    /// <summary>Where a drop lands: the mouse ray on the ground plane (y = 0), or 10 units in front of the camera.</summary>
    public Vector3 DropPoint(Vector2 mouse)
    {
        var ray = _camera.ScreenRay(mouse);
        return TransformGizmo3D.OnPlane(ray, Vector3.Zero, Vector3.UnitY) is { } ground && Vector3.Distance(ground, ray.Origin) < 500f ? ground : ray.GetPoint(10f);
    }

    /// <summary>Renders the scene and the grid into the view's target (render phase).</summary>
    public void Render(Scene scene, RenderTarget2D target, Color background)
    {
        var size = _camera.Camera.ViewportSize;
        context.SceneRenderer.RenderView(scene, SceneRenderer.CreateView(_camera.Camera, (int)size.X, (int)size.Y), background, target, _camera.Camera);
        var device = context.Device;
        device.SetRenderTarget(target);
        _gridRenderer ??= new Renderer3D(device);
        _meshes ??= new GpuMeshCache(device);
        var white = context.Resources.GetDefaultTexture(DefaultTexture.White);
        _gridMaterial ??= GridMaterial();
        var spacing = MathF.Pow(10f, MathF.Floor(MathF.Log10(MathF.Max(_camera.Distance, 1f) / 4f)));
        if (_grid is null || spacing != _gridSpacing)
        {
            if (_grid is not null)
            {
                _meshes.Remove(_grid);
            }

            (_grid, _gridSpacing) = (GridMesh(spacing), spacing);
        }

        var pivot = _camera.Pivot;
        var snapped = Matrix4x4.Translate(new Vector3(MathF.Round(pivot.X / (spacing * 10f)) * spacing * 10f, 0f, MathF.Round(pivot.Z / (spacing * 10f)) * spacing * 10f));
        var view = new View3D(_camera.Camera.ViewMatrix, _camera.Camera.ProjectionMatrix, _camera.Position);
        _gridRenderer.Render(view, [new MeshDrawItem(_meshes.Get(_grid), 0, _gridMaterial, white, snapped, CullMode.None)]);
        device.SetRenderTarget(null);
    }

    /// <summary>Icons, selection bounds and the gizmo, over the view image (ImGui phase).</summary>
    public void DrawOverlay(Vector2 origin)
    {
        var list = ImGui.GetWindowDrawList();
        SysVector2? Screen(Vector3 world) => _camera.WorldToScreen(world) is { Z: > 0f } s ? new SysVector2(origin.X + s.X, origin.Y + s.Y) : null;
        void Line(Vector3 a, Vector3 b, uint color, float thickness)
        {
            if (Screen(a) is { } p && Screen(b) is { } q)
            {
                list.AddLine(p, q, color, thickness);
            }
        }

        foreach (var gameObject in context.ActiveScene.GameObjects.Where(g => g.ActiveInHierarchy && ScenePicker3D.HasIcon(g)))
        {
            if (Screen(gameObject.Transform.Position) is not { } at)
            {
                continue;
            }

            var color = gameObject.GetComponent<Light>() is { } light ? 0xFF40D0FF : gameObject.GetComponent<AmbientLight>() is not null ? 0xFFA0A0A0 : 0xFFE0E0E0;
            list.AddCircleFilled(at, 6f, color);
            list.AddCircle(at, 9f, 0xFF202020, 0, 1.5f);
            if (gameObject.GetComponent<Light>() is { Type: not LightType.Point } directed)
            {
                Line(directed.Transform.Position, directed.Transform.Position + (directed.Transform.Forward * (40f * _camera.UnitsPerPixel(directed.Transform.Position))), color, 2f);
            }
            else if (gameObject.GetComponent<Camera>() is { } camera)
            {
                Line(camera.Transform.Position, camera.Transform.Position + (camera.Transform.Forward * (40f * _camera.UnitsPerPixel(camera.Transform.Position))), color, 2f);
            }
        }

        if (context.TerrainEditor.Target is not null && !context.PlayMode.IsPlaying)
        {
            DrawTerrainOverlay(context.TerrainEditor, origin);
            return;
        }

        if (context.Selected is not { } selected)
        {
            return;
        }

        var bounds = BoundsOf(selected);
        var (min, max) = (bounds.Min, bounds.Max);
        Vector3 Corner(int i) => new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
        foreach (var (a, b) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
        {
            Line(Corner(a), Corner(b), 0xFF1A99FF, 1f);
        }

        foreach (var (handle, from, to) in _gizmo.Lines(selected.Transform, _camera))
        {
            var active = handle == _hovered || handle == _gizmo.ActiveHandle;
            var axis = handle switch { Gizmo3DHandle.X or Gizmo3DHandle.YZ => 0, Gizmo3DHandle.Y or Gizmo3DHandle.XZ => 1, _ => 2 };
            Line(from, to, active ? Highlight : AxisColors[axis], active ? 3.5f : 2.5f);
        }

        if (Tool == GizmoTool.Scale && Screen(selected.Transform.Position) is { } center)
        {
            var half = TransformGizmo3D.UniformHandlePixels;
            list.AddRectFilled(center - new SysVector2(half), center + new SysVector2(half), _hovered == Gizmo3DHandle.Uniform || _gizmo.ActiveHandle == Gizmo3DHandle.Uniform ? Highlight : 0xFFE0E0E0);
        }
    }

    public void Dispose() => _meshes?.Dispose();

    /// <summary>The CPU mesh a renderer draws, for picking (skinned meshes by their bind pose).</summary>
    private Mesh? MeshOf(MeshRenderer renderer) =>
        !renderer.Mesh.IsNone ? context.Resources.GetModelMesh(renderer.Mesh)?.Mesh
        : renderer.Primitive != PrimitiveMesh.None ? SceneRenderer.GetPrimitive(renderer.Primitive) : null;

    /// <summary>World box of a GameObject: its mesh and its children's meshes, or a 1-unit box at its position.</summary>
    private Bounds BoundsOf(GameObject gameObject)
    {
        Bounds? total = null;
        foreach (var renderer in Descendants(gameObject.Transform).Select(t => t.GameObject.GetComponent<MeshRenderer>()).OfType<MeshRenderer>())
        {
            if (MeshOf(renderer) is { } mesh)
            {
                var box = Renderer3D.TransformBounds(mesh.Bounds, renderer.Transform.LocalToWorldMatrix);
                total = total is { } t ? Bounds.FromMinMax(Vector3.Min(t.Min, box.Min), Vector3.Max(t.Max, box.Max)) : box;
            }
        }

        return total ?? new Bounds(gameObject.Transform.Position, Vector3.One);

        static IEnumerable<Transform> Descendants(Transform root)
        {
            yield return root;
            foreach (var child in root.Children.SelectMany(Descendants))
            {
                yield return child;
            }
        }
    }

    private Material GridMaterial()
    {
        var material = new Material(context.Resources.GetBuiltinShader(Serialization.MaterialDefinition.BuiltinUnlit), BlendMode.Alpha, "Scene grid")
        {
            RenderQueue = Material.TransparentQueue,
            DepthWrite = false,
            Cull = CullMode.None,
        };
        material.SetColor("_BaseColor", Color.White);
        MaterialShaders.ApplyDefaults(material, MaterialShaders.UnlitProperties, context.Resources.GetDefaultTexture);
        return material;
    }

    /// <summary>A 100 × 100 cell grid on the XZ plane, every tenth line brighter, the axes colored (thin quads with vertex colors).</summary>
    private static Mesh GridMesh(float spacing)
    {
        const int Half = 50;
        var positions = new List<Vector3>();
        var colors = new List<Color>();
        var indices = new List<uint>();
        void Quad(Vector3 a, Vector3 b, Vector3 widthDirection, float width, Color color)
        {
            var offset = widthDirection * (width * 0.5f);
            var start = (uint)positions.Count;
            positions.AddRange([a - offset, a + offset, b + offset, b - offset]);
            colors.AddRange([color, color, color, color]);
            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }

        var extent = Half * spacing;
        for (var i = -Half; i <= Half; i++)
        {
            var major = i % 10 == 0;
            var p = i * spacing;
            var width = spacing * (major ? 0.04f : 0.025f);
            Quad(new Vector3(p, 0f, -extent), new Vector3(p, 0f, extent), Vector3.UnitX, width, i == 0 ? new Color(0.35f, 0.5f, 1f, 0.8f) : new Color(1f, 1f, 1f, major ? 0.2f : 0.05f));
            Quad(new Vector3(-extent, 0f, p), new Vector3(extent, 0f, p), Vector3.UnitZ, width, i == 0 ? new Color(1f, 0.35f, 0.35f, 0.8f) : new Color(1f, 1f, 1f, major ? 0.2f : 0.05f));
        }

        var mesh = new Mesh("Scene grid") { Positions = [.. positions], Colors = [.. colors] };
        mesh.SetIndices([.. indices], [new SubMesh(0, indices.Count)]);
        mesh.RecalculateNormals();
        return mesh;
    }
}
