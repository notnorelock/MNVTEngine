using Microsoft.Extensions.Logging;
using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Runtime;

/// <summary>
/// Loads textures by asset path on first use and keeps them until disposed. A missing or broken file
/// logs one error and renders as a magenta checkerboard instead of crashing the game.
/// </summary>
public sealed partial class ResourceCache(IGraphicsDevice device, string assetsDirectory, ILogger logger) : IDisposable
{
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.Ordinal);
    private Texture2D? _missing;

    public int LoadedTextureCount => _textures.Values.Count(t => t != _missing);

    // shortcut: every texture uses point filtering; per-asset import settings arrive with the asset pipeline (Milestone 4).
    public Texture2D GetTexture(string relativePath)
    {
        if (_textures.TryGetValue(relativePath, out var cached))
        {
            return cached;
        }

        Texture2D texture;
        try
        {
            var full = AssetPaths.Resolve(assetsDirectory, relativePath);
            texture = device.CreateTexture(Image.Load(full), SamplerState.PointClamp, relativePath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            LogTextureFailed(logger, ex, relativePath);
            texture = MissingTexture;
        }

        _textures.Add(relativePath, texture);
        return texture;
    }

    public Texture2D MissingTexture => _missing ??= device.CreateTexture(
        2, 2, [255, 0, 255, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 0, 255, 255], SamplerState.PointRepeat, "Missing");

    public void Dispose()
    {
        foreach (var texture in _textures.Values.Distinct())
        {
            texture.Dispose();
        }

        _missing?.Dispose();
        _textures.Clear();
    }

    [LoggerMessage(EventId = 3000, Level = LogLevel.Error, Message = "Texture '{Path}' could not be loaded; using the missing-texture placeholder")]
    private static partial void LogTextureFailed(ILogger logger, Exception exception, string path);
}

/// <summary>Draws a scene's sprites through its main camera.</summary>
public sealed partial class SceneRenderer(Renderer2D renderer, ResourceCache resources, ILogger logger)
{
    private bool _warnedNoCamera;

    public int SpritesDrawn { get; private set; }

    public int SpritesCulled { get; private set; }

    /// <summary>Renders through the scene's main camera into <paramref name="target"/> (or the back buffer).</summary>
    public void Render(Scene scene, int width, int height, RenderTarget2D? target = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        SpritesDrawn = SpritesCulled = 0;

        if (scene.MainCamera is not { } camera)
        {
            renderer.Device.SetRenderTarget(target);
            renderer.Device.Clear(Color.Black);
            if (!_warnedNoCamera)
            {
                LogNoCamera(logger, scene.Name);
                _warnedNoCamera = true;
            }

            return;
        }

        camera.ViewportSize = new Vector2(width, height);
        RenderView(scene, CreateView(camera, width, height), camera.BackgroundColor, target);
    }

    public static Camera2D CreateView(Camera camera, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return new Camera2D(width, height)
        {
            Position = camera.Transform.Position,
            Rotation = camera.Transform.Rotation,
            OrthographicSize = camera.OrthographicSize,
        };
    }

    /// <summary>Renders the scene's sprites through an arbitrary view (e.g. the editor's scene camera).</summary>
    public void RenderView(Scene scene, Camera2D view, Color background, RenderTarget2D? target = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(view);
        SpritesDrawn = SpritesCulled = 0;
        var visible = view.VisibleWorldBounds;

        var batch = renderer.Begin(view, SpriteSortMode.BackToFront, target: target);
        renderer.Device.Clear(background);
        foreach (var sprite in scene.World.Pool<SpriteRenderer>().Components)
        {
            if (sprite.Texture is null || !sprite.IsActiveAndEnabled)
            {
                continue;
            }

            var transform = sprite.Transform;
            var scale = transform.LossyScale;
            var size = Vector2.Scale(sprite.Size, Vector2.Abs(scale));
            var position = transform.Position;

            // Conservative bounding circle: covers any pivot and rotation.
            var radius = size.Magnitude;
            if (position.X + radius < visible.Min.X || position.X - radius > visible.Max.X
                || position.Y + radius < visible.Min.Y || position.Y - radius > visible.Max.Y)
            {
                SpritesCulled++;
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

            batch.Draw(resources.GetTexture(sprite.Texture), position, size, sprite.Color, transform.Rotation, sprite.Pivot, flip: flip, depth: -sprite.SortingOrder);
            SpritesDrawn++;
        }

        renderer.End();
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Scene '{Scene}' has no active Camera; nothing is rendered")]
    private static partial void LogNoCamera(ILogger logger, string scene);
}
