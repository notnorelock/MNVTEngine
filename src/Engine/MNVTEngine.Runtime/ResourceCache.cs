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
    private readonly Dictionary<Guid, LoadedMaterial?> _materials = [];
    private readonly Dictionary<string, Shader> _builtinShaders = new(StringComparer.Ordinal);
    private readonly Dictionary<Graphics.Materials.DefaultTexture, Texture2D> _defaultTextures = [];
    private Texture2D? _missing;

    public ResourceCache(IGraphicsDevice device, AssetDatabase assets, ILogger logger)
    {
        _device = device;
        _assets = assets;
        _logger = logger;
        _assets.AssetsChanged += Evict;
    }

    public int LoadedTextureCount => _textures.Values.Count(t => t != _missing);

    /// <summary>Textures that failed to load and show the missing-texture placeholder.</summary>
    public int FailedTextureCount => _missing is null ? 0 : _textures.Values.Count(t => t == _missing);

    /// <summary>Loaded textures with their asset ids (for the asset inspector; walks the cache).</summary>
    public IEnumerable<(Guid Asset, Texture2D Texture)> LoadedTextures => _textures.Where(p => p.Value != _missing).Select(p => (p.Key, p.Value));

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

            texture = _device.CreateTexture(Image.Decode(_assets.ReadAllBytes(asset.Guid), _assets.Get(asset.Guid)!.Path), _assets.GetTextureSettings(asset.Guid).ToSampler(), _assets.Get(asset.Guid)!.Path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
        {
            LogTextureFailed(_logger, ex, asset.ToString());
            texture = MissingTexture;
        }

        _textures.Add(asset.Guid, texture);
        return texture;
    }

    /// <summary>True when the texture is already on the GPU (or failed and uses the placeholder).</summary>
    public bool HasTexture(Guid guid) => _textures.ContainsKey(guid);

    /// <summary>
    /// Creates the GPU texture of an asset from an image decoded elsewhere (a loading worker), so loading a scene only
    /// uploads on the graphics thread. Does nothing when the texture is already cached.
    /// </summary>
    public void AddDecodedTexture(Guid guid, Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (_textures.ContainsKey(guid))
        {
            return;
        }

        var path = _assets.Get(guid)?.Path ?? guid.ToString();
        _textures.Add(guid, _device.CreateTexture(image, _assets.GetTextureSettings(guid).ToSampler(), path));
    }

    /// <summary>Texture by path relative to Assets (editor thumbnails); the placeholder if the path is not an indexed asset.</summary>
    public Texture2D GetTexture(string relativePath) =>
        _assets.TryGetGuid(relativePath, out var guid) ? GetTexture(new AssetRef(guid)) : MissingTexture;

    /// <summary>
    /// The material of a <c>.mnvtmat</c> asset, created on first use; null for none or (logged once) when the asset is
    /// invalid or its shader does not compile, so the caller draws with the default material. Reloaded when the asset,
    /// its shaders or its textures change.
    /// </summary>
    public Material? GetMaterial(AssetRef asset)
    {
        if (asset.IsNone)
        {
            return null;
        }

        if (_materials.TryGetValue(asset.Guid, out var cached))
        {
            return cached?.Material;
        }

        LoadedMaterial? loaded = null;
        var name = _assets.Get(asset.Guid)?.Path ?? asset.ToString();
        try
        {
            if (_assets.GetAssetType(asset.Guid) != AssetTypes.Material)
            {
                throw new InvalidDataException($"{asset} is not a material asset in this project.");
            }

            var definition = MaterialDefinition.Parse(System.Text.Encoding.UTF8.GetString(_assets.ReadAllBytes(asset.Guid)), name);
            var material = BuildMaterial(definition, name, out var owned);
            loaded = new LoadedMaterial(material, owned, [.. definition.Dependencies]);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SerializationException or ShaderCompilationException or KeyNotFoundException or ArgumentException)
        {
            LogMaterialFailed(_logger, ex, name);
        }

        _materials.Add(asset.Guid, loaded);
        return loaded?.Material;
    }

    /// <summary>
    /// A material from a definition (assets, editor previews): its shader (built-in, or a custom one this call compiles
    /// and the caller then owns), render settings with the defaults of its shader and blend mode, its values and
    /// textures, and defaults for everything else.
    /// </summary>
    /// <exception cref="ShaderCompilationException">A custom shader does not compile.</exception>
    public Material BuildMaterial(MaterialDefinition definition, string name, out Shader? ownedShader)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ownedShader = null;
        Shader shader;
        if (definition.Shader is { } fragmentAsset)
        {
            var fragment = System.Text.Encoding.UTF8.GetString(_assets.ReadAllBytes(fragmentAsset));
            var vertex = definition.VertexShader is { } vertexAsset ? System.Text.Encoding.UTF8.GetString(_assets.ReadAllBytes(vertexAsset)) : BuiltinShaders.Sprite.VertexGlsl;
            shader = ownedShader = _device.CreateShader(new ShaderSource(name, vertex, fragment));
        }
        else
        {
            shader = GetBuiltinShader(definition.Builtin ?? MaterialDefinition.BuiltinSprite);
        }

        var blend = Enum.Parse<BlendMode>(definition.Blend);
        var mesh = definition.Builtin is MaterialDefinition.BuiltinStandard or MaterialDefinition.BuiltinUnlit || definition.VertexShader is not null;
        var cutoff = definition.Properties.TryGetValue("_AlphaCutoff", out var c) && c.Numbers is { } n ? n[0] : 0f;
        var material = new Material(shader, blend, name)
        {
            Cull = definition.Cull is { } cull ? Enum.Parse<CullMode>(cull) : mesh ? CullMode.Back : CullMode.None,
            RenderQueue = definition.RenderQueue ?? (blend != BlendMode.Opaque ? Material.TransparentQueue : cutoff > 0f ? Material.AlphaTestQueue : Material.GeometryQueue),
            DepthTest = definition.DepthTest ?? true,
        };
        material.DepthWrite = definition.DepthWrite ?? !material.IsTransparent;
        foreach (var (property, value) in definition.Properties)
        {
            if (value.Texture is { } texture)
            {
                material.SetTexture(property, GetTexture(new AssetRef(texture)));
            }
            else
            {
                var v = value.Numbers!;
                _ = v.Length switch
                {
                    1 => material.SetFloat(property, v[0]),
                    2 => material.SetVector(property, new Vector2(v[0], v[1])),
                    _ => material.SetVector(property, new Vector4(v[0], v[1], v[2], v[3])),
                };
            }
        }

        Graphics.Materials.MaterialShaders.ApplyDefaults(material, Graphics.Materials.MaterialShaders.PropertiesOf(definition.Builtin), GetDefaultTexture);
        return material;
    }

    /// <summary>A built-in shader by id (<c>builtin:standard</c>, <c>builtin:unlit</c>, <c>builtin:sprite</c>), compiled once.</summary>
    /// <exception cref="ArgumentException">Unknown id.</exception>
    public Shader GetBuiltinShader(string id)
    {
        if (!_builtinShaders.TryGetValue(id, out var shader))
        {
            shader = _device.CreateShader(id switch
            {
                MaterialDefinition.BuiltinStandard => Graphics.Materials.MaterialShaders.Standard,
                MaterialDefinition.BuiltinUnlit => Graphics.Materials.MaterialShaders.Unlit,
                MaterialDefinition.BuiltinSprite => BuiltinShaders.Sprite,
                Graphics.Materials.MaterialShaders.ShadowCasterId => Graphics.Materials.MaterialShaders.ShadowCaster,
                Graphics.Materials.MaterialShaders.StandardSkinnedId => Graphics.Materials.MaterialShaders.StandardSkinned,
                Graphics.Materials.MaterialShaders.UnlitSkinnedId => Graphics.Materials.MaterialShaders.UnlitSkinned,
                Graphics.Materials.MaterialShaders.ShadowCasterSkinnedId => Graphics.Materials.MaterialShaders.ShadowCasterSkinned,
                Graphics.Materials.MaterialShaders.SkyId => Graphics.Materials.MaterialShaders.Sky,
                _ => throw new ArgumentException($"Unknown built-in shader '{id}'.", nameof(id)),
            });
            _builtinShaders.Add(id, shader);
        }

        return shader;
    }

    /// <summary>The 1×1 texture an unassigned material slot uses.</summary>
    public Texture2D GetDefaultTexture(Graphics.Materials.DefaultTexture kind)
    {
        if (!_defaultTextures.TryGetValue(kind, out var texture))
        {
            byte[] rgba = kind switch
            {
                Graphics.Materials.DefaultTexture.Black => [0, 0, 0, 255],
                Graphics.Materials.DefaultTexture.FlatNormal => [128, 128, 255, 255],
                _ => [255, 255, 255, 255],
            };
            texture = _device.CreateTexture(1, 1, rgba, SamplerState.PointRepeat, $"Default {kind}");
            _defaultTextures.Add(kind, texture);
        }

        return texture;
    }

    /// <summary>Sets <c>uTime</c> (seconds) on every loaded material whose shader uses it.</summary>
    public void SetTime(float seconds)
    {
        foreach (var loaded in _materials.Values)
        {
            if (loaded is not null && loaded.Material.Shader.HasUniform("uTime"))
            {
                loaded.Material.SetFloat("uTime", seconds);
            }
        }
    }

    public Texture2D MissingTexture => _missing ??= _device.CreateTexture(
        2, 2, [255, 0, 255, 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 0, 255, 255], SamplerState.PointRepeat, "Missing");

    public void Dispose()
    {
        _assets.AssetsChanged -= Evict;
        DisposeModels();
        DisposeEnvironments();
        foreach (var texture in _textures.Values.Distinct())
        {
            texture.Dispose();
        }

        _missing?.Dispose();
        _textures.Clear();
        foreach (var loaded in _materials.Values)
        {
            loaded?.OwnedShader?.Dispose();
        }

        _materials.Clear();
        foreach (var shader in _builtinShaders.Values)
        {
            shader.Dispose();
        }

        _builtinShaders.Clear();
        foreach (var texture in _defaultTextures.Values)
        {
            texture.Dispose();
        }

        _defaultTextures.Clear();
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

        // A material is stale when its own asset, a shader or a texture it uses changed (its textures were just released).
        foreach (var (guid, loaded) in _materials.Where(m => changed.Contains(m.Key) || (m.Value?.Dependencies.Any(changed.Contains) ?? false)).ToList())
        {
            loaded?.OwnedShader?.Dispose();
            _materials.Remove(guid);
        }

        EvictModels(changed);
        EvictEnvironments(changed);
        if (evicted.Count > 0)
        {
            TexturesEvicted?.Invoke(evicted);
        }
    }

    private sealed record LoadedMaterial(Material Material, Shader? OwnedShader, IReadOnlyCollection<Guid> Dependencies);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Error, Message = "Material '{Asset}' could not be loaded; using the default material")]
    private static partial void LogMaterialFailed(ILogger logger, Exception exception, string asset);

    [LoggerMessage(EventId = 3000, Level = LogLevel.Error, Message = "Texture '{Asset}' could not be loaded; using the missing-texture placeholder")]
    private static partial void LogTextureFailed(ILogger logger, Exception exception, string asset);
}

/// <summary>
/// Creates prefab instances from prefab assets and reads text assets; caches prefab text until the asset changes.
/// In exported games compiled assets (script bytecode) come from the content archive.
/// </summary>
public sealed partial class PrefabProvider : IPrefabProvider, ITextAssetReader
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
                json = System.Text.Encoding.UTF8.GetString(_assets.ReadAllBytes(prefab.Guid));
                _json[prefab.Guid] = json;
            }

            var instance = _serializer.InstantiatePrefab(json, scene, parent, path);
            foreach (var diagnostic in instance.Diagnostics)
            {
                LogDiagnostic(_logger, path, diagnostic.Path, diagnostic.Message);
            }

            return instance.Root;
        }
        catch (Exception ex) when (ex is IOException or SerializationException or InvalidDataException)
        {
            throw new InvalidOperationException($"Prefab '{path}' could not be instantiated: {ex.Message}", ex);
        }
    }

    public string ReadText(AssetRef asset, string assetType)
    {
        if (asset.IsNone || _assets.GetAssetType(asset.Guid) != assetType)
        {
            throw new InvalidOperationException($"{asset} is not a {assetType} asset in this project.");
        }

        try
        {
            return System.Text.Encoding.UTF8.GetString(_assets.ReadAllBytes(asset.Guid));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw new InvalidOperationException($"'{_assets.Get(asset.Guid)!.Path}' could not be read: {ex.Message}", ex);
        }
    }

    public byte[]? ReadCompiled(AssetRef asset, string assetType)
    {
        if (!_assets.IsPackaged)
        {
            return null;
        }

        if (asset.IsNone || _assets.GetAssetType(asset.Guid) != assetType)
        {
            throw new InvalidOperationException($"{asset} is not a {assetType} asset in this game.");
        }

        try
        {
            return _assets.ReadCompiled(asset.Guid);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw new InvalidOperationException($"Compiled form of '{_assets.Get(asset.Guid)!.Path}' could not be read ({ex.Message}); rebuild the game.", ex);
        }
    }

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "Prefab {Prefab}: {Path}: {Message}")]
    private static partial void LogDiagnostic(ILogger logger, string prefab, string path, string message);
}

/// <summary>
/// Draws a scene's sprites and tilemaps through its main camera, with their materials. Scenes with an enabled
/// <see cref="Light2D"/> are lit: a light buffer is cleared to the ambient color (<see cref="GlobalLight2D"/>), each
/// light adds a soft disc, and the buffer is multiplied over the rendered scene.
/// </summary>
public sealed partial class SceneRenderer(Renderer2D renderer, ResourceCache resources, ILogger logger) : IDisposable
{
    private static readonly Color DefaultAmbient = new(0.15f, 0.15f, 0.2f);
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private bool _warnedNoCamera;
    private readonly Dictionary<(int Width, int Height), RenderTarget2D> _lightBuffers = []; // one per view size (editor: scene + game view)
    private Texture2D? _lightDisc;
    private Material? _additive;
    private Material? _multiply;

    public int SpritesDrawn { get; private set; }

    public int SpritesCulled { get; private set; }

    /// <summary>Sprite batch flushes (draw calls) of the last <see cref="RenderView"/>.</summary>
    public int BatchesDrawn { get; private set; }

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
        RenderView(scene, CreateView(camera, width, height), camera.BackgroundColor, target, camera);
    }

    public static Camera2D CreateView(Camera camera, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return new Camera2D(width, height)
        {
            Position = (Vector2)camera.Transform.Position,
            Rotation = camera.Transform.Rotation2D,
            OrthographicSize = camera.OrthographicSize,
        };
    }

    /// <summary>
    /// Renders the scene through an arbitrary 2D view (e.g. the editor's scene camera): with <paramref name="camera3D"/>,
    /// first its meshes (3D pass, with depth), then sprites, tilemaps and 2D lighting on top.
    /// </summary>
    public void RenderView(Scene scene, Camera2D view, Color background, RenderTarget2D? target = null, Camera? camera3D = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(view);
        SpritesDrawn = SpritesCulled = 0;
        var visible = view.VisibleWorldBounds;

        resources.SetTime((float)_clock.Elapsed.TotalSeconds);
        var drawCalls = renderer.Batch.DrawCallCount;
        var sprites = Core.Diagnostics.Profiler.Sample("Render.Sprites");
        var batch = renderer.Begin(view, SpriteSortMode.BackToFront, target: target);
        renderer.Device.Clear(background);
        if (camera3D is not null)
        {
            Render3D(scene, camera3D, target); // sprites are drawn when the batch ends, on top
        }
        foreach (var sprite in scene.World.Pool<SpriteRenderer>().Components)
        {
            if (sprite.Texture.IsNone || !sprite.IsActiveAndEnabled)
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

            var source = sprite.SourceRect is { Width: > 0f, Height: > 0f } rect ? rect : (Rect?)null;
            batch.Draw(resources.GetTexture(sprite.Texture), position, size, sprite.Color, transform.Rotation2D, sprite.Pivot, source, flip, -sprite.SortingOrder, resources.GetMaterial(sprite.Material));
            SpritesDrawn++;
        }

        foreach (var tilemap in scene.World.Pool<Tilemap>().Components)
        {
            if (!tilemap.Texture.IsNone && tilemap.IsActiveAndEnabled && tilemap.Count > 0)
            {
                DrawTilemap(batch, tilemap, visible);
            }
        }

        renderer.End();
        sprites.Dispose();
        using (Core.Diagnostics.Profiler.Sample("Render.Lighting"))
        {
            ApplyLighting(scene, view, target);
        }

        Core.Diagnostics.Profiler.Count("Sprites drawn", SpritesDrawn);
        Core.Diagnostics.Profiler.Count("Sprites culled", SpritesCulled);
        Core.Diagnostics.Profiler.Count("Lights", LightsDrawn);
        BatchesDrawn = renderer.Batch.DrawCallCount - drawCalls;
        Core.Diagnostics.Profiler.Count("Draw calls", BatchesDrawn);
    }

    public int LightsDrawn { get; private set; }

    public void Dispose()
    {
        foreach (var buffer in _lightBuffers.Values)
        {
            buffer.Dispose();
        }

        _lightBuffers.Clear();
        _lightDisc?.Dispose();
        _lightDisc = null;
        Dispose3D();
    }

    private void ApplyLighting(Scene scene, Camera2D view, RenderTarget2D? target)
    {
        LightsDrawn = 0;
        var lights = new List<Light2D>();
        foreach (var light in scene.World.Pool<Light2D>().Components)
        {
            if (light.IsActiveAndEnabled)
            {
                lights.Add(light);
            }
        }

        if (lights.Count == 0)
        {
            return;
        }

        var device = renderer.Device;
        var width = target?.Width ?? device.BackBufferWidth;
        var height = target?.Height ?? device.BackBufferHeight;
        if (!_lightBuffers.TryGetValue((width, height), out var lightBuffer))
        {
            if (_lightBuffers.Count >= 4)
            {
                Dispose(); // views were resized: start over rather than keep stale sizes
            }

            lightBuffer = device.CreateRenderTarget(width, height, SamplerState.LinearClamp, "Lighting.Buffer");
            _lightBuffers[(width, height)] = lightBuffer;
        }

        _lightDisc ??= CreateLightDisc(device);
        _additive ??= new Material(renderer.DefaultMaterial.Shader, BlendMode.Additive, "Lighting/Additive");
        _multiply ??= new Material(renderer.DefaultMaterial.Shader, BlendMode.Multiply, "Lighting/Multiply");

        var global = scene.FindObjectsOfType<GlobalLight2D>().FirstOrDefault(g => g.IsActiveAndEnabled);
        var ambient = global is null ? DefaultAmbient : global.Color * global.Intensity;
        var lightsBatch = renderer.Begin(view, SpriteSortMode.Deferred, _additive, lightBuffer);
        device.Clear(new Color(ambient.R, ambient.G, ambient.B, 1f));
        foreach (var light in lights)
        {
            var scale = light.Transform.LossyScale;
            var diameter = 2f * light.Radius * MathF.Max(MathF.Abs(scale.X), MathF.Abs(scale.Y));
            var color = light.Color * light.Intensity;
            lightsBatch.Draw(_lightDisc, (Vector2)light.Transform.Position, new Vector2(diameter, diameter), new Color(color.R, color.G, color.B, 1f));
            LightsDrawn++;
        }

        renderer.End();

        var screen = new Camera2D(width, height) { Position = new Vector2(width * 0.5f, height * 0.5f) };
        screen.UsePixelUnits();
        var composite = renderer.Begin(screen, SpriteSortMode.Deferred, _multiply, target);
        composite.Draw(lightBuffer, new Vector2(width * 0.5f, height * 0.5f), new Vector2(width, height), Color.White);
        renderer.End();
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
    private void DrawTilemap(SpriteBatch batch, Tilemap tilemap, Rect visible)
    {
        var texture = resources.GetTexture(tilemap.Texture);
        var material = resources.GetMaterial(tilemap.Material);
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
                SpritesCulled++;
                continue;
            }

            batch.Draw(texture, corner, size, tilemap.Color, transform.Rotation2D, Vector2.Zero, tilemap.SourceRect(cell.Tile), flip, -tilemap.SortingOrder, material);
            SpritesDrawn++;
        }
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Scene '{Scene}' has no active Camera; nothing is rendered")]
    private static partial void LogNoCamera(ILogger logger, string scene);
}
