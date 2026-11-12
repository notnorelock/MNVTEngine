using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Graphics;
using MNVTEngine.Scenes;

namespace MNVTEngine.Runtime;

/// <summary>Rendering switches of a host (games use the defaults; tests and diagnostics turn features off).</summary>
public sealed record RenderSettings(bool GpuSkinning = true, bool GpuInstancing = true);

/// <summary>What a module's scene pass draws with; owned by the host.</summary>
/// <param name="Modules">The project's started modules, for passes that combine several (the 3D pass draws terrain).</param>
public sealed record RenderServices(Renderer2D Renderer, ResourceCache Resources, ILoggerFactory LoggerFactory, IReadOnlyList<IEngineModule> Modules, RenderSettings Settings);

/// <summary>A module with resources of its own: built-in shaders, caches (<see cref="IResourceExtension"/>).</summary>
public interface IResourceIntegration
{
    /// <summary>
    /// Every built-in shader the module may create at run time. The build precompiles them for the project's modules:
    /// exported games have no shader compiler, so a shader missing here fails when it is first used.
    /// </summary>
    IReadOnlyList<ShaderSource> BuiltinShaders { get; }

    /// <summary>Adds the module's resources to a cache. May be called again for the same cache: then it does nothing.</summary>
    void RegisterResources(ResourceCache resources);
}

/// <summary>
/// A module that draws scene content in a pass of its own (sprites, meshes). The host creates one pass per renderer
/// from each module of the project that implements this, and owns (disposes) the passes.
/// </summary>
public interface IRenderIntegration
{
    IScenePass CreatePass(RenderServices services);
}

/// <summary>What a screen layer draws with and reads input from; owned by the host.</summary>
public sealed record ScreenServices(Platform.GameWindow Window, Renderer2D Renderer, ResourceCache Resources, AssetDatabase Assets, Graphics.Text.Font DefaultFont);

/// <summary>A module drawn over the scene in screen space (game UI). The host creates one layer and owns it.</summary>
public interface IScreenIntegration
{
    IScreenLayer CreateScreenLayer(ScreenServices services);
}

/// <summary>A module's screen-space layer: prepares scenes, takes input before gameplay, draws over the scene.</summary>
public interface IScreenLayer : IDisposable
{
    void SceneLoaded(Scene scene);

    /// <summary>Before gameplay each frame: input the layer takes reads as released for scripts.</summary>
    void Update(Scene scene, float deltaSeconds);

    /// <summary>After the scene, on the back buffer.</summary>
    void Render(Scene scene, int framebufferWidth, int framebufferHeight);
}

/// <summary>The host side of module integration: one place that applies a project's modules to a cache, a renderer and scenes.</summary>
public static class ModuleIntegration
{
    /// <summary>Registers every module's resources, then creates the rendering modules' passes.</summary>
    public static IReadOnlyList<IScenePass> CreatePasses(IReadOnlyList<IEngineModule> modules, Renderer2D renderer, ResourceCache resources, ILoggerFactory loggerFactory, RenderSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(modules);
        foreach (var module in modules.OfType<IResourceIntegration>())
        {
            module.RegisterResources(resources);
        }

        var services = new RenderServices(renderer, resources, loggerFactory, modules, settings ?? new RenderSettings());
        return [.. modules.OfType<IRenderIntegration>().Select(m => m.CreatePass(services))];
    }

    /// <summary>Lets every module prepare a scene the host is about to activate.</summary>
    public static void SceneLoaded(IReadOnlyList<IEngineModule> modules, Scene scene)
    {
        ArgumentNullException.ThrowIfNull(modules);
        foreach (var module in modules.OfType<ISceneIntegration>())
        {
            module.SceneLoaded(scene);
        }
    }

    /// <summary>Creates the screen layers of the modules that draw over the scene.</summary>
    public static IReadOnlyList<IScreenLayer> CreateScreenLayers(IReadOnlyList<IEngineModule> modules, ScreenServices services)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return [.. modules.OfType<IScreenIntegration>().Select(m => m.CreateScreenLayer(services))];
    }

    /// <summary>Adds the metric providers and console commands of the modules with diagnostics.</summary>
    public static void AddDiagnostics(IReadOnlyList<IEngineModule> modules, Diagnostics.MetricsCollector collector, Diagnostics.DebugCommandRegistry commands, Func<Scene?> scene)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(collector);
        foreach (var module in modules.OfType<IDiagnosticsIntegration>())
        {
            foreach (var provider in module.CreateMetricsProviders(scene))
            {
                collector.Add(provider);
            }

            module.RegisterCommands(commands, scene);
        }
    }
}

/// <summary>
/// One part of drawing a scene. Per view: every pass's <see cref="Draw"/> runs, in <see cref="Order"/>, while the
/// view's sprite batch is open (sprites queued there are drawn when it ends, on top of anything a pass drew directly);
/// then every pass's <see cref="Composite"/> runs after the batch has ended (full-screen effects such as 2D lighting).
/// </summary>
public interface IScenePass : IDisposable
{
    /// <summary>Lower first: 3D content is 100, 2D sprites 200.</summary>
    int Order { get; }

    void Draw(ScenePassContext context);

    void Composite(ScenePassContext context)
    {
    }
}

/// <summary>Well-known statistics of the built-in passes (<see cref="SceneRenderer.Statistics"/>); absent means the pass is not there.</summary>
public static class RenderStatistics
{
    /// <summary>Particle systems drawn, particles drawn and culled, by the 2D or 3D particle pass.</summary>
    public const string ParticleSystems = "particles.systems", ParticlesDrawn = "particles.drawn", ParticlesCulled = "particles.culled";

    /// <summary>3D particle pass only: draw calls, and runs that changed texture or blending (2D particles batch with sprites).</summary>
    public const string ParticleDrawCalls = "particles.drawCalls", ParticleTextureSwitches = "particles.textureSwitches";

    /// <summary>CPU time of the particle passes, in microseconds.</summary>
    public const string ParticleRenderMicroseconds = "particles.renderMicroseconds";

    public const string SpritesDrawn = "sprites.drawn";
    public const string SpritesCulled = "sprites.culled";
    public const string Lights2D = "lights2d.drawn";
    public const string MeshesDrawn = "meshes.drawn";
    public const string MeshesCulled = "meshes.culled";
    public const string MeshTriangles = "meshes.triangles";
    public const string GpuSkinnedMeshes = "meshes.gpu-skinned";
    public const string InstancedMeshes = "meshes.instanced";
    public const string MeshDrawCalls = "meshes.draw-calls";
}

/// <summary>One view being drawn: the scene, cameras, target, open batch and Scene view options.</summary>
public sealed class ScenePassContext
{
    private readonly Dictionary<string, int> _statistics;

    internal ScenePassContext(Scene scene, Camera2D view, Camera? camera3D, RenderTarget2D? target, SpriteBatch batch, SceneViewOptions options, Dictionary<string, int> statistics)
    {
        (Scene, View, Camera3D, Target, Batch, Options, _statistics) = (scene, view, camera3D, target, batch, options, statistics);
    }

    public Scene Scene { get; }

    /// <summary>The 2D view (sprites, culling bounds).</summary>
    public Camera2D View { get; }

    /// <summary>The 3D camera, when the view has one.</summary>
    public Camera? Camera3D { get; }

    public RenderTarget2D? Target { get; }

    /// <summary>The view's sprite batch; open during <see cref="IScenePass.Draw"/>, ended during <see cref="IScenePass.Composite"/>.</summary>
    public SpriteBatch Batch { get; }

    public SceneViewOptions Options { get; }

    /// <summary>Hidden in the Scene view (the Hierarchy's eye); games draw everything.</summary>
    public bool IsHidden(GameObject gameObject) => Options.IsVisible is { } visible && !visible(gameObject);

    /// <summary>Adds to a statistic of this view (<see cref="RenderStatistics"/>).</summary>
    public void Count(string statistic, int value) => _statistics[statistic] = _statistics.GetValueOrDefault(statistic) + value;
}
