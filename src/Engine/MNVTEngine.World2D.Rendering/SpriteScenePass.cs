using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;
using MNVTEngine.Runtime;

namespace MNVTEngine.World2D.Rendering;

/// <summary>
/// Draws a scene's sprites and tilemaps with their materials. Scenes with an enabled <see cref="Light2D"/> are lit: a
/// light buffer is cleared to the ambient color (<see cref="GlobalLight2D"/>), each light adds a soft disc, and the
/// buffer is multiplied over the rendered view.
/// </summary>
public sealed class SpriteScenePass(RenderServices services) : IScenePass
{
    private static readonly Color DefaultAmbient = new(0.15f, 0.15f, 0.2f);
    private readonly Dictionary<(int Width, int Height), RenderTarget2D> _lightBuffers = []; // one per view size (editor: scene + game view)
    private readonly List<Light2D> _lights = [];
    private Texture2D? _lightDisc;
    private Material? _additive;
    private Material? _multiply;

    public int Order => 200;

    public void Draw(ScenePassContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var sample = Core.Diagnostics.Profiler.Sample("Render.Sprites");
        var resources = services.Resources;
        var batch = context.Batch;
        var visible = context.View.VisibleWorldBounds;
        int drawn = 0, culled = 0;
        foreach (var sprite in context.Scene.World.Pool<SpriteRenderer>().Components)
        {
            if (sprite.Texture.IsNone || !sprite.IsActiveAndEnabled || context.IsHidden(sprite.GameObject))
            {
                continue;
            }

            var transform = sprite.Transform;
            var scale = transform.LossyScale;
            var size = Vector2.Scale(sprite.Size, Vector2.Abs((Vector2)scale));
            var position = (Vector2)transform.Position;

            // Conservative bounding circle: covers any pivot and rotation.
            var radius = size.Magnitude;
            if (position.X + radius < visible.Min.X || position.X - radius > visible.Max.X
                || position.Y + radius < visible.Min.Y || position.Y - radius > visible.Max.Y)
            {
                culled++;
                continue;
            }

            var flip = SpriteFlip.None;
            if (sprite.FlipX ^ (scale.X < 0f))
            {
                flip |= SpriteFlip.Horizontal;
            }

            if (sprite.FlipY ^ (scale.Y < 0f))
            {
                flip |= SpriteFlip.Vertical;
            }

            var source = sprite.SourceRect is { Width: > 0f, Height: > 0f } rect ? rect : (Rect?)null;
            batch.Draw(resources.GetTexture(sprite.Texture), position, size, sprite.Color, transform.Rotation2D, sprite.Pivot, source, flip, -sprite.SortingOrder, resources.GetMaterial(sprite.Material));
            drawn++;
        }

        foreach (var tilemap in context.Scene.World.Pool<Tilemap>().Components)
        {
            if (!tilemap.Texture.IsNone && tilemap.IsActiveAndEnabled && tilemap.Count > 0 && !context.IsHidden(tilemap.GameObject))
            {
                DrawTilemap(batch, tilemap, visible, ref drawn, ref culled);
            }
        }

        context.Count(RenderStatistics.SpritesDrawn, drawn);
        context.Count(RenderStatistics.SpritesCulled, culled);
    }

    /// <summary>2D lighting: multiplies the light buffer over what the view drew.</summary>
    public void Composite(ScenePassContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var sample = Core.Diagnostics.Profiler.Sample("Render.Lighting");
        _lights.Clear();
        foreach (var light in context.Scene.World.Pool<Light2D>().Components)
        {
            if (light.IsActiveAndEnabled)
            {
                _lights.Add(light);
            }
        }

        context.Count(RenderStatistics.Lights2D, _lights.Count);
        if (_lights.Count == 0)
        {
            return;
        }

        var renderer = services.Renderer;
        var device = renderer.Device;
        var target = context.Target;
        var width = target?.Width ?? device.BackBufferWidth;
        var height = target?.Height ?? device.BackBufferHeight;
        if (!_lightBuffers.TryGetValue((width, height), out var lightBuffer))
        {
            if (_lightBuffers.Count >= 4)
            {
                DisposeBuffers(); // views were resized: start over rather than keep stale sizes
            }

            lightBuffer = device.CreateRenderTarget(width, height, SamplerState.LinearClamp, "Lighting.Buffer");
            _lightBuffers[(width, height)] = lightBuffer;
        }

        _lightDisc ??= CreateLightDisc(device);
        _additive ??= new Material(renderer.DefaultMaterial.Shader, BlendMode.Additive, "Lighting/Additive");
        _multiply ??= new Material(renderer.DefaultMaterial.Shader, BlendMode.Multiply, "Lighting/Multiply");

        var global = context.Scene.FindObjectsOfType<GlobalLight2D>().FirstOrDefault(g => g.IsActiveAndEnabled);
        var ambient = global is null ? DefaultAmbient : global.Color * global.Intensity;
        var lightsBatch = renderer.Begin(context.View, SpriteSortMode.Deferred, _additive, lightBuffer);
        device.Clear(new Color(ambient.R, ambient.G, ambient.B, 1f));
        foreach (var light in _lights)
        {
            var scale = light.Transform.LossyScale;
            var diameter = 2f * light.Radius * MathF.Max(MathF.Abs(scale.X), MathF.Abs(scale.Y));
            var color = light.Color * light.Intensity;
            lightsBatch.Draw(_lightDisc, (Vector2)light.Transform.Position, new Vector2(diameter, diameter), new Color(color.R, color.G, color.B, 1f));
        }

        renderer.End();

        var screen = new Camera2D(width, height) { Position = new Vector2(width * 0.5f, height * 0.5f) };
        screen.UsePixelUnits();
        var composite = renderer.Begin(screen, SpriteSortMode.Deferred, _multiply, target);
        composite.Draw(lightBuffer, new Vector2(width * 0.5f, height * 0.5f), new Vector2(width, height), Color.White);
        renderer.End();
    }

    public void Dispose()
    {
        DisposeBuffers();
        _lightDisc?.Dispose();
        _lightDisc = null;
    }

    private void DisposeBuffers()
    {
        foreach (var buffer in _lightBuffers.Values)
        {
            buffer.Dispose();
        }

        _lightBuffers.Clear();
    }

    /// <summary>White disc fading to black at the edge with a smooth (squared) falloff.</summary>
    private static Texture2D CreateLightDisc(IGraphicsDevice device)
    {
        const int Size = 128;
        var pixels = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var dx = ((x + 0.5f) / Size * 2f) - 1f;
                var dy = ((y + 0.5f) / Size * 2f) - 1f;
                var falloff = MathF.Max(0f, 1f - MathF.Sqrt((dx * dx) + (dy * dy)));
                var value = (byte)(falloff * falloff * 255f);
                var i = ((y * Size) + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (value, value, value, 255);
            }
        }

        return device.CreateTexture(Size, Size, pixels, SamplerState.LinearClamp, "Lighting.Disc");
    }

    /// <summary>Draws the painted cells that overlap the view; each cell is culled by a bounding circle like sprites.</summary>
    private void DrawTilemap(SpriteBatch batch, Tilemap tilemap, Rect visible, ref int drawn, ref int culled)
    {
        var texture = services.Resources.GetTexture(tilemap.Texture);
        var material = services.Resources.GetMaterial(tilemap.Material);
        var transform = tilemap.Transform;
        var scale = transform.LossyScale;
        var size = Vector2.Scale(tilemap.CellSize, Vector2.Abs((Vector2)scale));
        var radius = size.Magnitude;
        var flip = (scale.X < 0f ? SpriteFlip.Horizontal : SpriteFlip.None) | (scale.Y < 0f ? SpriteFlip.Vertical : SpriteFlip.None);
        foreach (var cell in tilemap.EnumerateTiles())
        {
            var corner = tilemap.CellToWorld(cell.X, cell.Y);
            if (corner.X + radius < visible.Min.X || corner.X - radius > visible.Max.X || corner.Y + radius < visible.Min.Y || corner.Y - radius > visible.Max.Y)
            {
                culled++;
                continue;
            }

            batch.Draw(texture, corner, size, tilemap.Color, transform.Rotation2D, Vector2.Zero, tilemap.SourceRect(cell.Tile), flip, -tilemap.SortingOrder, material);
            drawn++;
        }
    }
}
