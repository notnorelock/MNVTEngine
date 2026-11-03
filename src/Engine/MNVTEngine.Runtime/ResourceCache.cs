using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Runtime;

/// <summary>
/// Loads textures by asset GUID on first use, with the sampler from each texture's import settings, and keeps them
/// until the asset changes or the cache is disposed. A missing or broken texture logs one error and renders as a
/// magenta checkerboard instead of crashing the game.
/// </summary>
public sealed partial class ResourceCache : IDisposable
{
    private readonly IGraphicsDevice _device;
    private readonly AssetDatabase _assets;
    private readonly ILogger _logger;
    private readonly Dictionary<Guid, Texture2D> _textures = [];
    private Texture2D? _missing;

    public ResourceCache(IGraphicsDevice device, AssetDatabase assets, ILogger logger)
    {
        _device = device;
        _assets = assets;
        _logger = logger;
        _assets.AssetsChanged += Evict;
    }

    public int LoadedTextureCount => _textures.Values.Count(t => t != _missing);

    /// <summary>Raised after cached textures were released because their assets changed (e.g. new import settings).</summary>
    public event Action<IReadOnlyCollection<Guid>>? TexturesEvicted;

    public Texture2D GetTexture(AssetRef asset)
    {
        if (_textures.TryGetValue(asset.Guid, out var cached))
        {
            return cached;
        }

        Texture2D texture;
        try
        {
            if (_assets.GetAssetType(asset.Guid) != AssetTypes.Texture)
            {
                throw new InvalidDataException($"{asset} is not a texture asset in this project.");
            }

            texture = _device.CreateTexture(Image.Load(_assets.FullPath(asset.Guid)), _assets.GetTextureSettings(asset.Guid).ToSampler(), _assets.Get(asset.Guid)!.Path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
        {
            LogTextureFailed(_logger, ex, asset.ToString());
            texture = MissingTexture;
        }

        _textures.Add(asset.Guid, texture);
        return texture;
    }

    /// <summary>Texture by path relative to Assets (editor thumbnails); the placeholder if the path is not an indexed asset.</summary>
    public Texture2D GetTexture(string relativePath) =>
        _assets.TryGetGuid(relativePath, out var guid) ? GetTexture(new AssetRef(guid)) : MissingTexture;

    public Texture2D MissingTexture => _missing ??= _device.CreateTexture(
        2, 2, [255, 0, 255, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 0, 255, 255], SamplerState.PointRepeat, "Missing");

    public void Dispose()
    {
        _assets.AssetsChanged -= Evict;
        foreach (var texture in _textures.Values.Distinct())
        {
            texture.Dispose();
        }

        _missing?.Dispose();
        _textures.Clear();
    }

    private void Evict(IReadOnlyCollection<Guid> changed)
    {
        var evicted = new List<Guid>();
        foreach (var guid in changed)
        {
            if (_textures.Remove(guid, out var texture))
            {
                if (texture != _missing)
                {
                    texture.Dispose();
                }

                evicted.Add(guid);
            }
        }

        if (evicted.Count > 0)
        {
            TexturesEvicted?.Invoke(evicted);
        }
    }

    [LoggerMessage(EventId = 3000, Level = LogLevel.Error, Message = "Texture '{Asset}' could not be loaded; using the missing-texture placeholder")]
    private static partial void LogTextureFailed(ILogger logger, Exception exception, string asset);
}

/// <summary>Creates prefab instances from prefab assets; caches prefab text until the asset changes.</summary>
public sealed partial class PrefabProvider : IPrefabProvider
{
    private readonly SceneSerializer _serializer;
    private readonly AssetDatabase _assets;
    private readonly ILogger _logger;
    private readonly Dictionary<Guid, string> _json = [];

    public PrefabProvider(SceneSerializer serializer, AssetDatabase assets, ILogger logger)
    {
        _serializer = serializer;
        _assets = assets;
        _logger = logger;
        assets.AssetsChanged += changed =>
        {
            foreach (var guid in changed)
            {
                _json.Remove(guid);
            }
        };
    }

    public GameObject Instantiate(Scene scene, AssetRef prefab, Transform? parent)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (_assets.GetAssetType(prefab.Guid) != AssetTypes.Prefab)
        {
            throw new InvalidOperationException($"{prefab} is not a prefab asset in this project.");
        }

        var path = _assets.Get(prefab.Guid)!.Path;
        try
        {
            if (!_json.TryGetValue(prefab.Guid, out var json))
            {
                json = File.ReadAllText(_assets.FullPath(prefab.Guid));
                _json[prefab.Guid] = json;
            }

            var instance = _serializer.InstantiatePrefab(json, scene, parent, path);
            foreach (var diagnostic in instance.Diagnostics)
            {
                LogDiagnostic(_logger, path, diagnostic.Path, diagnostic.Message);
            }

            return instance.Root;
        }
        catch (Exception ex) when (ex is IOException or SerializationException)
        {
            throw new InvalidOperationException($"Prefab '{path}' could not be instantiated: {ex.Message}", ex);
        }
    }

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "Prefab {Prefab}: {Path}: {Message}")]
    private static partial void LogDiagnostic(ILogger logger, string prefab, string path, string message);
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
            if (sprite.Texture.IsNone || !sprite.IsActiveAndEnabled)
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
