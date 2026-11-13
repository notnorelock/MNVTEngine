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
    private readonly Dictionary<Guid, Texture2D> _runtimeTextures = [];
    private readonly Dictionary<Guid, LoadedMaterial?> _materials = [];
    private readonly Dictionary<string, Shader> _builtinShaders = new(StringComparer.Ordinal);
    private readonly Dictionary<Graphics.Materials.DefaultTexture, Texture2D> _defaultTextures = [];
    private readonly Dictionary<string, (ShaderSource Source, IReadOnlyList<Graphics.Materials.MaterialPropertyInfo> Properties)> _builtinSources = new(StringComparer.Ordinal)
    {
        [MaterialDefinition.BuiltinSprite] = (BuiltinShaders.Sprite, []),
    };

    private readonly Dictionary<Type, IResourceExtension> _extensions = [];
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

    /// <summary>
    /// Shows a texture made at run time (a browser page, a video frame) under an id that is no asset, so sprites and
    /// materials referencing <c>asset:&lt;id&gt;</c> draw it. The caller keeps owning (and disposing) the texture; null
    /// removes it.
    /// </summary>
    public void SetRuntimeTexture(Guid id, Texture2D? texture)
    {
        if (texture is null)
        {
            _runtimeTextures.Remove(id);
        }
        else
        {
            _runtimeTextures[id] = texture;
        }
    }

    public Texture2D GetTexture(AssetRef asset)
    {
        if (_runtimeTextures.TryGetValue(asset.Guid, out var runtime))
        {
            return runtime;
        }

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

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            if (TryStartStreaming(asset.Guid) is { } streamed)
            {
                texture = streamed; // the proxy now, the full image when it is decoded (Update)
            }
            else
            {
                var settings = _assets.GetTextureSettings(asset.Guid);
                texture = _device.CreateTexture(Image.Decode(_assets.ReadAllBytes(asset.Guid), _assets.Get(asset.Guid)!.Path, settings.SvgScale), settings.ToSampler(), _assets.Get(asset.Guid)!.Path);
                RecordLoad(System.Diagnostics.Stopwatch.GetElapsedTime(started));
            }

            _loadedFrame[asset.Guid] = _device.FrameNumber;
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
        _loadedFrame[guid] = _device.FrameNumber;
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

        var properties = definition.Builtin is { } builtin && _builtinSources.TryGetValue(builtin, out var registered) ? registered.Properties : [];
        Graphics.Materials.MaterialProperties.ApplyDefaults(material, properties, GetDefaultTexture);
        return material;
    }

    /// <summary>
    /// A built-in shader by id, compiled once: <c>builtin:sprite</c>, and the ids the project's modules registered
    /// (world.3d: <c>builtin:standard</c>, <c>builtin:unlit</c>, ...).
    /// </summary>
    /// <exception cref="ArgumentException">Unknown id, or one of a module the project does not have.</exception>
    public Shader GetBuiltinShader(string id)
    {
        if (!_builtinShaders.TryGetValue(id, out var shader))
        {
            if (!_builtinSources.TryGetValue(id, out var source))
            {
                throw new ArgumentException($"Unknown built-in shader '{id}', or its module is not part of this project.", nameof(id));
            }

            shader = _device.CreateShader(source.Source);
            _builtinShaders.Add(id, shader);
        }

        return shader;
    }

    /// <summary>Makes a module's built-in shader available by id, with the documented properties its materials default to.</summary>
    /// <exception cref="ArgumentException">The id is already registered.</exception>
    public void RegisterBuiltinShader(string id, ShaderSource source, IReadOnlyList<Graphics.Materials.MaterialPropertyInfo>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!_builtinSources.TryAdd(id, (source, properties ?? [])))
        {
            throw new ArgumentException($"Built-in shader '{id}' is already registered.", nameof(id));
        }
    }

    public bool HasBuiltinShader(string id) => _builtinSources.ContainsKey(id);

    /// <summary>The graphics device resources are created on.</summary>
    public IGraphicsDevice Device => _device;

    /// <summary>The asset database resources are loaded from.</summary>
    public AssetDatabase Assets => _assets;

    /// <summary>The cache's logger, for extensions.</summary>
    public ILogger Logger => _logger;

    /// <summary>
    /// Adds a module's cache (world.3d: models and environments). The cache owns it from then on: evicts it when assets
    /// change and disposes it with itself.
    /// </summary>
    /// <exception cref="InvalidOperationException">An extension of this type is already added.</exception>
    public void AddExtension<T>(T extension)
        where T : class, IResourceExtension
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (!_extensions.TryAdd(typeof(T), extension))
        {
            throw new InvalidOperationException($"{typeof(T).Name} is already added to the resource cache.");
        }
    }

    /// <exception cref="InvalidOperationException">The module that adds it is not part of the project.</exception>
    public T GetExtension<T>()
        where T : class, IResourceExtension =>
        _extensions.TryGetValue(typeof(T), out var extension) ? (T)extension
            : throw new InvalidOperationException($"No {typeof(T).Name} in the resource cache: the module that provides it is not part of this project.");

    public bool TryGetExtension<T>(out T extension)
        where T : class, IResourceExtension
    {
        var found = _extensions.TryGetValue(typeof(T), out var value);
        extension = found ? (T)value! : null!;
        return found;
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
        foreach (var extension in _extensions.Values)
        {
            extension.Dispose();
        }

        _extensions.Clear();
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

                ForgetStream(guid);
                evicted.Add(guid);
            }
        }

        // A material is stale when its own asset, a shader or a texture it uses changed (its textures were just released).
        foreach (var (guid, loaded) in _materials.Where(m => changed.Contains(m.Key) || (m.Value?.Dependencies.Any(changed.Contains) ?? false)).ToList())
        {
            loaded?.OwnedShader?.Dispose();
            _materials.Remove(guid);
        }

        foreach (var extension in _extensions.Values)
        {
            extension.Evict(changed);
        }
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
/// What a Scene view shows (the editor's Unity-style Scene toolbar): games render with <see cref="Default"/>.
/// </summary>
public sealed record SceneViewOptions
{
    public static SceneViewOptions Default { get; } = new();

    /// <summary>Off: the scene's lights are replaced by a light from the camera (Unity's Lighting toggle).</summary>
    public bool SceneLighting { get; init; } = true;

    /// <summary>Off: no skybox is drawn (environment lighting stays).</summary>
    public bool Skybox { get; init; } = true;

    /// <summary>Off: materials that read <c>uTime</c> stay at time 0.</summary>
    public bool AnimatedMaterials { get; init; } = true;

    /// <summary>Whether a GameObject is drawn (scene visibility, the Hierarchy's eye); null draws everything.</summary>
    public Func<GameObject, bool>? IsVisible { get; init; }
}

/// <summary>
/// Draws a scene through a view: clears, then runs the passes of the project's modules (<see cref="IScenePass"/>: 2D
/// sprites, 3D meshes, terrain, ...) in order. With no passes, a view is just cleared to its background.
/// </summary>
public sealed partial class SceneRenderer(Renderer2D renderer, ResourceCache resources, ILogger logger, IEnumerable<IScenePass>? passes = null) : IDisposable
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly IReadOnlyList<IScenePass> _passes = [.. (passes ?? []).OrderBy(p => p.Order)];
    private readonly Dictionary<string, int> _statistics = new(StringComparer.Ordinal);
    private bool _warnedNoCamera;
    private SceneViewOptions _options = SceneViewOptions.Default;

    /// <summary>The passes, in drawing order.</summary>
    public IReadOnlyList<IScenePass> Passes => _passes;

    /// <summary>Statistics of the last <see cref="RenderView"/> (<see cref="RenderStatistics"/>); a pass that is not there reports nothing.</summary>
    public IReadOnlyDictionary<string, int> Statistics => _statistics;

    /// <summary>Sprite batch flushes (draw calls) of the last <see cref="RenderView"/>.</summary>
    public int BatchesDrawn { get; private set; }

    /// <summary>Renders through the scene's main camera into <paramref name="target"/> (or the back buffer).</summary>
    public void Render(Scene scene, int width, int height, RenderTarget2D? target = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        _statistics.Clear();

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
    /// Renders the scene through an arbitrary 2D view (e.g. the editor's scene camera) and, for 3D passes,
    /// <paramref name="camera3D"/>: the passes draw in their order (3D meshes first, sprites on top).
    /// </summary>
    /// <param name="options">Scene view toggles; null renders as games do.</param>
    public void RenderView(Scene scene, Camera2D view, Color background, RenderTarget2D? target = null, Camera? camera3D = null, SceneViewOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(view);
        _options = options ?? SceneViewOptions.Default;
        _statistics.Clear();

        resources.SetTime(_options.AnimatedMaterials ? (float)_clock.Elapsed.TotalSeconds : 0f);
        var drawCalls = renderer.Batch.DrawCallCount;
        var batch = renderer.Begin(view, SpriteSortMode.BackToFront, target: target);
        renderer.Device.Clear(background);
        var context = new ScenePassContext(scene, view, camera3D, target, batch, _options, _statistics);
        foreach (var pass in _passes)
        {
            pass.Draw(context);
        }

        renderer.End();
        foreach (var pass in _passes)
        {
            pass.Composite(context);
        }

        foreach (var (name, value) in _statistics)
        {
            Core.Diagnostics.Profiler.Count(name, value);
        }

        BatchesDrawn = renderer.Batch.DrawCallCount - drawCalls;
        Core.Diagnostics.Profiler.Count("Draw calls", BatchesDrawn);
    }

    public void Dispose()
    {
        foreach (var pass in _passes)
        {
            pass.Dispose();
        }

    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Scene '{Scene}' has no active Camera; nothing is rendered")]
    private static partial void LogNoCamera(ILogger logger, string scene);
}
