using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// Free editor camera over the active scene, in 2D or 3D (toolbar toggle; 3D is chosen for scenes with meshes or
/// lights, see <see cref="SceneView3D"/> for its controls). Laid out like Unity's Scene view: a toolbar (2D/3D,
/// Global/Local, snapping, grid, gizmos, camera settings) and the tool palette (hand, move, rotate, scale) floating
/// over the view's top-left corner. In 2D: left click selects or drags gizmo handles; right/middle drag pans;
/// wheel zooms at the cursor; W/E/R pick the tool, F frames the selection, Delete removes it. Dropping a texture
/// from the Project panel creates a sprite at the drop point. With a Tilemap selected and painting on (B), left drag
/// paints the palette tile and Ctrl + left drag erases.
/// </summary>
internal sealed class SceneViewPanel : IDisposable
{
    private static readonly Color Background = new(0.18f, 0.19f, 0.21f);

    private readonly EditorContext _context;
    private readonly ViewTarget _view;
    private readonly Camera2D _camera = new(1, 1) { OrthographicSize = 6f };
    private readonly TransformGizmo _gizmo = new();
    private readonly SceneView3D _view3D;
    private GizmoHandle _hovered;
    private Vector2? _lastPaint;
    private Vector2 _mouseWorld;
    private bool _hand;

    public SceneViewPanel(EditorContext context)
    {
        _context = context;
        _view = new ViewTarget(context, "SceneView");
        _view3D = new SceneView3D(context);
    }

    /// <summary>The project's profile decides the Scene view: 3D projects edit in the 3D view, 2D projects in the 2D view.</summary>
    private bool Is3D => _context.Session.Project.Profile == Modules.ProjectProfile.ThreeD;

    public void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, System.Numerics.Vector2.Zero);
        var visible = ImGui.Begin(EditorWindows.Title("Scene"));
        ImGui.PopStyleVar();
        if (visible)
        {
            DrawToolbar();
            var size = ImGui.GetContentRegionAvail();
            var width = Math.Max(1, (int)size.X);
            var height = Math.Max(1, (int)size.Y);
            _view.Request(width, height);
            _camera.SetViewportSize(width, height);
            _view3D.SetViewport(width, height);

            var origin = ImGui.GetCursorScreenPos();
            Gui.Image(_view.Id, _view.Target, new System.Numerics.Vector2(width, height));
            ImGui.SetCursorScreenPos(origin);
            ImGui.SetNextItemAllowOverlap(); // the tool palette and navigation gizmo sit on top of the view
            ImGui.InvisibleButton("##scene_canvas", new System.Numerics.Vector2(width, height), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
            if (Is3D)
            {
                (_view3D.Tool, _view3D.HandTool) = (_gizmo.Tool, _hand);
                _view3D.HandleInput(origin.ToEngine(), ImGui.IsItemHovered(), ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows));
                (_gizmo.Tool, _hand) = (_view3D.Tool, _view3D.HandTool);
                HandleDrop(origin.ToEngine());
                _view3D.DrawOverlay(origin.ToEngine());
                _view3D.DrawNavigation(origin.ToEngine(), new Vector2(width, height));
            }
            else
            {
                HandleInput(origin.ToEngine(), ImGui.IsItemHovered(), ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows));
                HandleDrop(origin.ToEngine());
            }

            DrawToolPalette(origin);
        }

        ImGui.End();

        // Scene audio follows the Scene view camera, even while the panel is behind another tab.
        var listener = Is3D ? _view3D.Camera.Position : new Vector3(_camera.Position, 0f);
        _context.AudioPreview.Update(_context.ActiveScene, _context.PlayMode.IsPlaying, listener, ImGui.GetIO().DeltaTime);
    }

    /// <summary>Renders the scene and overlays. Call during the render phase.</summary>
    public void Render()
    {
        _view.EnsureSize();
        var scene = _context.ActiveScene;
        if (Is3D)
        {
            _view3D.Render(scene, _view.Target, Background);
            return;
        }

        _context.SceneRenderer.RenderView(scene, _camera, Background, _view.Target, options: new Runtime.SceneViewOptions { IsVisible = _context.Visibility.IsVisible });

        var batch = _context.Renderer.Begin(_camera, SpriteSortMode.BackToFront, target: _view.Target);
        var upp = _camera.UnitsPerPixel;
        DrawGrid(batch, upp);
        foreach (var camera in scene.FindObjectsOfType<Camera>())
        {
            var half = camera.HalfExtents;
            DrawQuad(batch, [.. new[] { new Vector2(-half.X, -half.Y), new Vector2(half.X, -half.Y), new Vector2(half.X, half.Y), new Vector2(-half.X, half.Y) }
                .Select(p => (Vector2)camera.Transform.Position + Vector2.Rotate(p, camera.Transform.Rotation2D))], new Color(1f, 1f, 1f, 0.35f), upp);
        }

        if (PaintTarget() is { } tilemap)
        {
            var (x, y) = tilemap.WorldToCell(_mouseWorld);
            DrawQuad(batch, [tilemap.CellToWorld(x, y), tilemap.CellToWorld(x + 1, y), tilemap.CellToWorld(x + 1, y + 1), tilemap.CellToWorld(x, y + 1)], new Color(0.3f, 0.9f, 1f), upp);
        }
        else if (_context.Selected is { } selected)
        {
            if (selected.GetComponent<SpriteRenderer>() is { } sprite)
            {
                DrawQuad(batch, SceneEditing.SpriteCorners(sprite), new Color(1f, 0.6f, 0.1f), upp);
            }

            if (!_hand)
            {
                _gizmo.Draw(batch, selected.Transform, upp, _hovered);
            }
        }

        _context.Renderer.End();
    }

    public void Dispose()
    {
        _view3D.Dispose();
        _view.Dispose();
    }

    /// <summary>Unity's tool palette: a vertical strip floating over the view's top-left corner.</summary>
    private void DrawToolPalette(System.Numerics.Vector2 origin)
    {
        var list = ImGui.GetWindowDrawList();
        var size = ImGui.GetFrameHeight();
        var top = origin + new System.Numerics.Vector2(8f, 8f);
        list.AddRectFilled(top - new System.Numerics.Vector2(4f), top + new System.Numerics.Vector2(size + 4f, (4 * (size + 2f)) + 2f), 0xE0202226, 5f);
        var tools = new (bool Active, string Icon, string Label, Action Choose)[]
        {
            (_hand, Icons.Hand, "Hand: left drag pans (Q)", () => _hand = true),
            (!_hand && _gizmo.Tool == GizmoTool.Move, Icons.Move, "Move (W)", () => (_gizmo.Tool, _hand) = (GizmoTool.Move, false)),
            (!_hand && _gizmo.Tool == GizmoTool.Rotate, Icons.RotateCw, "Rotate (E)", () => (_gizmo.Tool, _hand) = (GizmoTool.Rotate, false)),
            (!_hand && _gizmo.Tool == GizmoTool.Scale, Icons.Scale3D, "Scale (R)", () => (_gizmo.Tool, _hand) = (GizmoTool.Scale, false)),
        };
        for (var i = 0; i < tools.Length; i++)
        {
            ImGui.SetCursorScreenPos(top + new System.Numerics.Vector2(0f, i * (size + 2f)));
            if (Gui.IconButton(tools[i].Icon, tools[i].Label, tools[i].Active))
            {
                tools[i].Choose();
            }
        }
    }

    private void DrawToolbar()
    {
        ImGui.SetCursorPos(ImGui.GetCursorPos() + new System.Numerics.Vector2(6f, 4f));
        var is3D = Is3D;
        if (is3D)
        {
            if (Gui.IconButton(_view3D.PivotCenter ? Icons.Crosshair : Icons.CircleDot, _view3D.PivotCenter ? "Center: handles at the selection's bounds center (click for Pivot)" : "Pivot: handles at the object's pivot (click for Center)"))
            {
                _view3D.PivotCenter = !_view3D.PivotCenter;
            }

            ImGui.SameLine(0f, 2f);
            var gizmo = _view3D.Gizmo;
            if (Gui.IconButton(gizmo.LocalSpace ? Icons.Cuboid : Icons.Globe, gizmo.LocalSpace ? "Local: handles follow the object's rotation (click for Global)" : "Global: handles along the world axes (click for Local)"))
            {
                gizmo.LocalSpace = !gizmo.LocalSpace;
            }

            ImGui.SameLine(0f, 12f);
        }

        if (is3D)
        {
            DrawToolbar3D();
        }

        ImGui.SameLine(0f, 12f);
        Gui.Muted(is3D ? "Alt+drag orbit, middle pan, right look + WASD, F frame" : $"zoom {_camera.Zoom:0.##}x");
        ImGui.Spacing();
    }

    /// <summary>Unity's Scene toolbar for 3D: handle orientation, snapping, grid, gizmos and the camera.</summary>
    private void DrawToolbar3D()
    {
        var gizmo = _view3D.Gizmo;
        ImGui.SameLine(0f, 12f);
        if (Gui.IconButton(Icons.Magnet, _view3D.AlwaysSnap ? "Snapping on (Ctrl moves freely)" : "Snapping off (hold Ctrl to snap)", _view3D.AlwaysSnap))
        {
            _view3D.AlwaysSnap = !_view3D.AlwaysSnap;
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            ImGui.OpenPopup("##snap_settings");
        }

        ImGui.SameLine(0f, 0f);
        if (ImGui.ArrowButton("##snap_menu", ImGuiDir.Down))
        {
            ImGui.OpenPopup("##snap_settings");
        }

        if (ImGui.BeginPopup("##snap_settings"))
        {
            ImGui.SeparatorText("Increment snapping");
            var (move, rotate, scale) = (gizmo.MoveSnap, gizmo.RotateSnap, gizmo.ScaleSnap);
            ImGui.SetNextItemWidth(120f);
            if (ImGui.DragFloat("Move", ref move, 0.05f, 0.01f, 100f, "%.2f"))
            {
                gizmo.MoveSnap = MathF.Max(0.01f, move);
            }

            ImGui.SetNextItemWidth(120f);
            if (ImGui.DragFloat("Rotate (degrees)", ref rotate, 0.5f, 1f, 180f, "%.0f"))
            {
                gizmo.RotateSnap = Math.Clamp(rotate, 1f, 180f);
            }

            ImGui.SetNextItemWidth(120f);
            if (ImGui.DragFloat("Scale", ref scale, 0.01f, 0.01f, 10f, "%.2f"))
            {
                gizmo.ScaleSnap = MathF.Max(0.01f, scale);
            }

            ImGui.EndPopup();
        }

        ImGui.SameLine(0f, 8f);
        if (Gui.IconButton(Icons.Sun, _view3D.SceneLighting ? "Scene lighting (click for a light from the camera)" : "Camera light: the scene's lights are off in this view", _view3D.SceneLighting))
        {
            _view3D.SceneLighting = !_view3D.SceneLighting;
        }

        ImGui.SameLine(0f, 2f);
        var audio = _context.AudioPreview;
        ImGui.BeginDisabled(!audio.Available);
        if (Gui.IconButton(Icons.Volume2, "Scene audio: play looping, play-on-start sources here, heard from this camera", audio.Enabled))
        {
            audio.Enabled = !audio.Enabled;
        }

        ImGui.EndDisabled();
        ImGui.SameLine(0f, 2f);
        if (Gui.IconButton(Icons.Sparkles, "Effects: skybox and animated materials", _view3D.Skybox && _view3D.AnimatedMaterials))
        {
            var on = !(_view3D.Skybox && _view3D.AnimatedMaterials);
            (_view3D.Skybox, _view3D.AnimatedMaterials) = (on, on);
        }

        ImGui.SameLine(0f, 0f);
        if (ImGui.ArrowButton("##effects_menu", ImGuiDir.Down))
        {
            ImGui.OpenPopup("##effects");
        }

        if (ImGui.BeginPopup("##effects"))
        {
            var (sky, animated) = (_view3D.Skybox, _view3D.AnimatedMaterials);
            if (ImGui.Checkbox("Skybox", ref sky))
            {
                _view3D.Skybox = sky;
            }

            if (ImGui.Checkbox("Animated materials", ref animated))
            {
                _view3D.AnimatedMaterials = animated;
            }

            ImGui.EndPopup();
        }

        ImGui.SameLine(0f, 2f);
        var visibility = _context.Visibility;
        if (Gui.IconButton(Icons.EyeOff, visibility.HiddenCount == 0 ? "No hidden objects (hide them with the eye in the Hierarchy)" : visibility.ShowHidden ? $"Showing {visibility.HiddenCount} hidden objects (click to hide them again)" : $"{visibility.HiddenCount} hidden objects (click to show them)", visibility.ShowHidden))
        {
            visibility.ShowHidden = !visibility.ShowHidden;
        }

        ImGui.SameLine(0f, 8f);
        if (Gui.IconButton(Icons.Grid3x3, "Grid", _view3D.ShowGrid))
        {
            _view3D.ShowGrid = !_view3D.ShowGrid;
        }

        ImGui.SameLine(0f, 2f);
        if (Gui.IconButton(_view3D.ShowGizmos ? Icons.Eye : Icons.EyeOff, "Gizmos: light and camera icons, selection bounds", _view3D.ShowGizmos))
        {
            _view3D.ShowGizmos = !_view3D.ShowGizmos;
        }

        ImGui.SameLine(0f, 2f);
        if (Gui.IconButton(Icons.Video, "Scene camera settings"))
        {
            ImGui.OpenPopup("##camera_settings");
        }

        if (ImGui.BeginPopup("##camera_settings"))
        {
            var camera = _view3D.Camera;
            ImGui.SeparatorText("Scene camera");
            var fov = camera.FieldOfView;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Field of view", ref fov, 10f, 120f, "%.0f"))
            {
                camera.FieldOfView = fov;
            }

            var speed = camera.FlySpeed;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.SliderFloat("Fly speed", ref speed, 0.1f, 10f, "%.1fx"))
            {
                camera.FlySpeed = speed;
            }

            var orthographic = camera.Orthographic;
            if (ImGui.Checkbox("Orthographic", ref orthographic))
            {
                camera.Orthographic = orthographic;
            }

            ImGui.EndPopup();
        }
    }

    /// <summary>The selected tilemap while tile painting is on.</summary>
    private Tilemap? PaintTarget() => _context.Tiles.Active ? _context.Selected?.GetComponent<Tilemap>() : null;

    private void HandleInput(Vector2 origin, bool hovered, bool focused)
    {
        var io = ImGui.GetIO();
        var mouseWorld = _camera.ScreenToWorld(io.MousePos.ToEngine() - origin);
        _mouseWorld = mouseWorld;
        var upp = _camera.UnitsPerPixel;
        var scene = _context.ActiveScene;
        var selected = _context.Selected;

        if (focused && !io.WantTextInput)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Q, false))
            {
                _hand = true;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.W, false))
            {
                (_gizmo.Tool, _hand) = (GizmoTool.Move, false);
            }

            if (ImGui.IsKeyPressed(ImGuiKey.E, false))
            {
                (_gizmo.Tool, _hand) = (GizmoTool.Rotate, false);
            }

            if (ImGui.IsKeyPressed(ImGuiKey.R, false))
            {
                (_gizmo.Tool, _hand) = (GizmoTool.Scale, false);
            }

            if (ImGui.IsKeyPressed(ImGuiKey.B, false))
            {
                _context.Tiles.Active = !_context.Tiles.Active;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.F, false) && selected is not null)
            {
                _camera.Position = (Vector2)selected.Transform.Position;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.Delete, false) && selected is not null)
            {
                _context.Delete(selected);
                selected = null;
            }
        }

        if (hovered && io.MouseWheel != 0f)
        {
            // Zoom towards the cursor: keep the world point under it fixed.
            var before = mouseWorld;
            _camera.Zoom = Mathf.Clamp(_camera.Zoom * MathF.Pow(1.15f, io.MouseWheel), 0.02f, 50f);
            _camera.Position += before - _camera.ScreenToWorld(io.MousePos.ToEngine() - origin);
        }

        if (ImGui.IsItemActive() && (ImGui.IsMouseDragging(ImGuiMouseButton.Right) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle) || (_hand && ImGui.IsMouseDragging(ImGuiMouseButton.Left))))
        {
            var delta = io.MouseDelta;
            _camera.Position -= Vector2.Rotate(new Vector2(delta.X, -delta.Y) * upp, _camera.Rotation);
        }

        if (PaintTarget() is { } tilemap)
        {
            Paint(tilemap, mouseWorld, hovered, io.KeyCtrl);
            return;
        }

        if (_hand)
        {
            _hovered = GizmoHandle.None;
            return;
        }

        _hovered = selected is not null && hovered && !_gizmo.IsDragging ? _gizmo.HitTest(selected.Transform, mouseWorld, upp) : GizmoHandle.None;

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (selected is not null && _hovered != GizmoHandle.None)
            {
                _gizmo.BeginDrag(selected.Transform, _hovered, mouseWorld, upp);
            }
            else
            {
                _context.Session.Selection.Select(SceneEditing.Pick(scene, mouseWorld, 8f * upp, _context.Visibility.IsVisible));
            }
        }

        if (_gizmo.IsDragging)
        {
            if (selected is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (_gizmo.Drag(selected.Transform, mouseWorld))
                {
                    _context.Changed("Transform");
                }
            }
            else
            {
                _gizmo.EndDrag();
            }
        }
    }

    private void Paint(Tilemap tilemap, Vector2 mouseWorld, bool hovered, bool erase)
    {
        _hovered = GizmoHandle.None;
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _lastPaint = mouseWorld;
        }

        if (_lastPaint is { } from && ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (TilePainter.Stroke(tilemap, from, mouseWorld, erase ? -1 : _context.Tiles.Tile) > 0)
            {
                _context.Changed("Paint Tiles");
            }

            _lastPaint = mouseWorld;
        }
        else
        {
            _lastPaint = null;
        }
    }

    private void HandleDrop(Vector2 origin)
    {
        if (!ImGui.BeginDragDropTarget())
        {
            return;
        }

        if (Gui.AcceptStringPayload(Gui.AssetPayload) is { } path && _context.ResolveAsset(path) is { } asset)
        {
            var mouse = ImGui.GetIO().MousePos.ToEngine() - origin;
            var world = _camera.ScreenToWorld(mouse);
            var world3D = Is3D ? _view3D.DropPoint(mouse) : new Mathematics.Vector3(world.X, world.Y, 0f);
            if (asset.Type == Serialization.AssetTypes.Prefab)
            {
                var instance = _context.InstantiatePrefab(asset.Guid, null, world);
                if (instance is not null && Is3D)
                {
                    instance.Transform.Position = world3D;
                }
            }
            else if (asset.Type == Serialization.AssetTypes.Model)
            {
                _context.InstantiateModel(asset.Guid, null, world3D);
            }
            else if (asset.Type == Serialization.AssetTypes.Texture)
            {
                CreateSprite(path, asset.Guid, world);
            }
        }

        ImGui.EndDragDropTarget();
    }

    private void DrawGrid(SpriteBatch batch, float upp)
    {
        // Spacing: the smallest power of ten that keeps lines at least 40 px apart.
        var spacing = MathF.Pow(10f, MathF.Ceiling(MathF.Log10(40f * upp)));
        var bounds = _camera.VisibleWorldBounds;
        var thickness = upp;
        var minor = new Color(1f, 1f, 1f, 0.05f);
        var axisX = new Color(0.9f, 0.3f, 0.3f, 0.5f);
        var axisY = new Color(0.4f, 0.9f, 0.4f, 0.5f);

        for (var x = MathF.Floor(bounds.Min.X / spacing) * spacing; x <= bounds.Max.X; x += spacing)
        {
            batch.DrawLine(new Vector2(x, bounds.Min.Y), new Vector2(x, bounds.Max.Y), MathF.Abs(x) < spacing * 0.01f ? axisY : minor, thickness, depth: 100f);
        }

        for (var y = MathF.Floor(bounds.Min.Y / spacing) * spacing; y <= bounds.Max.Y; y += spacing)
        {
            batch.DrawLine(new Vector2(bounds.Min.X, y), new Vector2(bounds.Max.X, y), MathF.Abs(y) < spacing * 0.01f ? axisX : minor, thickness, depth: 100f);
        }
    }

    private static void DrawQuad(SpriteBatch batch, Vector2[] corners, Color color, float upp)
    {
        for (var i = 0; i < 4; i++)
        {
            batch.DrawLine(corners[i], corners[(i + 1) % 4], color, 1.5f * upp, depth: -50f);
        }
    }

    /// <summary>Sprite sized from the texture's pixel size and its pixels-per-unit import setting.</summary>
    private void CreateSprite(string path, Guid texture, Vector2 world)
    {
        var assets = _context.Session.Assets;
        var record = assets.Get(texture)!;
        var ppu = assets.GetTextureSettings(texture).PixelsPerUnit;
        var size = record.Properties.TryGetValue("width", out var w) && record.Properties.TryGetValue("height", out var h)
            ? new Vector2(float.Parse(w, System.Globalization.CultureInfo.InvariantCulture), float.Parse(h, System.Globalization.CultureInfo.InvariantCulture)) / ppu
            : Vector2.One;

        var gameObject = _context.CreateGameObject(Path.GetFileNameWithoutExtension(path));
        gameObject.Transform.Position = world;
        var sprite = gameObject.AddComponent<SpriteRenderer>();
        sprite.Texture = new AssetRef(texture);
        sprite.Size = size;
    }
}
