using ImGuiNET;
using MNVTEngine.Editor.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// Free editor camera over the active scene. Left click selects or drags gizmo handles; right/middle drag pans;
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
    private GizmoHandle _hovered;
    private Vector2? _lastPaint;
    private Vector2 _mouseWorld;

    public SceneViewPanel(EditorContext context)
    {
        _context = context;
        _view = new ViewTarget(context, "SceneView");
    }

    public void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, System.Numerics.Vector2.Zero);
        var visible = ImGui.Begin("Scene");
        ImGui.PopStyleVar();
        if (visible)
        {
            DrawToolbar();
            var size = ImGui.GetContentRegionAvail();
            var width = Math.Max(1, (int)size.X);
            var height = Math.Max(1, (int)size.Y);
            _view.Request(width, height);
            _camera.SetViewportSize(width, height);

            var origin = ImGui.GetCursorScreenPos();
            Gui.Image(_view.Id, _view.Target, new System.Numerics.Vector2(width, height));
            ImGui.SetCursorScreenPos(origin);
            ImGui.InvisibleButton("##scene_canvas", new System.Numerics.Vector2(width, height), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
            HandleInput(origin.ToEngine(), ImGui.IsItemHovered(), ImGui.IsWindowFocused());
            HandleDrop(origin.ToEngine());
        }

        ImGui.End();
    }

    /// <summary>Renders the scene and overlays. Call during the render phase.</summary>
    public void Render()
    {
        _view.EnsureSize();
        var scene = _context.ActiveScene;
        _context.SceneRenderer.RenderView(scene, _camera, Background, _view.Target);

        var batch = _context.Renderer.Begin(_camera, SpriteSortMode.BackToFront, target: _view.Target);
        var upp = _camera.UnitsPerPixel;
        DrawGrid(batch, upp);
        foreach (var camera in scene.FindObjectsOfType<Camera>())
        {
            var half = camera.HalfExtents;
            DrawQuad(batch, [.. new[] { new Vector2(-half.X, -half.Y), new Vector2(half.X, -half.Y), new Vector2(half.X, half.Y), new Vector2(-half.X, half.Y) }
                .Select(p => camera.Transform.Position + Vector2.Rotate(p, camera.Transform.Rotation))], new Color(1f, 1f, 1f, 0.35f), upp);
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

            _gizmo.Draw(batch, selected.Transform, upp, _hovered);
        }

        _context.Renderer.End();
    }

    public void Dispose() => _view.Dispose();

    private void DrawToolbar()
    {
        ImGui.SetCursorPos(ImGui.GetCursorPos() + new System.Numerics.Vector2(6f, 4f));
        foreach (var (tool, label) in new[] { (GizmoTool.Move, "Move (W)"), (GizmoTool.Rotate, "Rotate (E)"), (GizmoTool.Scale, "Scale (R)") })
        {
            if (ImGui.RadioButton(label, _gizmo.Tool == tool))
            {
                _gizmo.Tool = tool;
            }

            ImGui.SameLine();
        }

        Gui.Muted($"zoom {_camera.Zoom:0.##}x");
        ImGui.Spacing();
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
            if (ImGui.IsKeyPressed(ImGuiKey.W, false))
            {
                _gizmo.Tool = GizmoTool.Move;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.E, false))
            {
                _gizmo.Tool = GizmoTool.Rotate;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.R, false))
            {
                _gizmo.Tool = GizmoTool.Scale;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.B, false))
            {
                _context.Tiles.Active = !_context.Tiles.Active;
            }

            if (ImGui.IsKeyPressed(ImGuiKey.F, false) && selected is not null)
            {
                _camera.Position = selected.Transform.Position;
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

        if (ImGui.IsItemActive() && (ImGui.IsMouseDragging(ImGuiMouseButton.Right) || ImGui.IsMouseDragging(ImGuiMouseButton.Middle)))
        {
            var delta = io.MouseDelta;
            _camera.Position -= Vector2.Rotate(new Vector2(delta.X, -delta.Y) * upp, _camera.Rotation);
        }

        if (PaintTarget() is { } tilemap)
        {
            Paint(tilemap, mouseWorld, hovered, io.KeyCtrl);
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
                _context.Session.Selection.Select(SceneEditing.Pick(scene, mouseWorld, 8f * upp));
            }
        }

        if (_gizmo.IsDragging)
        {
            if (selected is not null && ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (_gizmo.Drag(selected.Transform, mouseWorld))
                {
                    _context.Changed();
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
                _context.Changed();
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
            var world = _camera.ScreenToWorld(ImGui.GetIO().MousePos.ToEngine() - origin);
            if (asset.Type == Serialization.AssetTypes.Prefab)
            {
                _context.InstantiatePrefab(asset.Guid, null, world);
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
