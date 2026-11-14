using System.Globalization;

namespace MNVTEngine.Diagnostics;

/// <summary>Whether a value was measured. Unsupported and unavailable values are never shown or budgeted as zero.</summary>
public enum MetricStatus
{
    /// <summary>Nothing reported this value (yet), or its provider is disabled.</summary>
    Unavailable,

    /// <summary>The platform, driver or backend cannot measure it (e.g. GPU memory without a vendor extension).</summary>
    Unsupported,

    Measured,

    /// <summary>Derived from other data, not read from the hardware or driver (e.g. texture memory from sizes). Shown with "~".</summary>
    Estimated,
}

public enum MetricUnit
{
    Count,
    Milliseconds,
    Seconds,
    Bytes,
    PerMinute,
}

public readonly record struct MetricValue(MetricStatus Status, double Value)
{
    public static MetricValue Unavailable => default;

    public static MetricValue Unsupported => new(MetricStatus.Unsupported, 0);

    public bool IsMeasured => Status == MetricStatus.Measured;

    /// <summary>Measured or estimated: there is a number to show and compare.</summary>
    public bool HasValue => Status is MetricStatus.Measured or MetricStatus.Estimated;

    public static MetricValue Measured(double value) => new(MetricStatus.Measured, value);

    public static MetricValue Estimated(double value) => new(MetricStatus.Estimated, value);
}

/// <summary>A metric's identity and how to present it.</summary>
public sealed record MetricDefinition(string Id, string Name, string Subsystem, MetricUnit Unit, string Description);

/// <summary>
/// Identifiers of the engine's built-in metrics. Providers may add their own ids; <see cref="Definitions"/> lists the
/// built-in ones with units and subsystems for panels and budgets.
/// </summary>
public static class Metrics
{
    public const string Fps = "time.fps";
    public const string FrameTime = "time.frame";
    public const string FrameTimeAverage = "time.frameAverage";
    public const string CpuTime = "time.cpu";
    public const string GpuTime = "time.gpu";
    public const string Uptime = "time.uptime";
    public const string FrameNumber = "time.frameNumber";

    public const string ProcessMemory = "memory.process";
    public const string ManagedHeap = "memory.managed";
    public const string AllocatedPerFrame = "memory.allocatedPerFrame";
    public const string GpuMemoryUsed = "memory.gpuUsed";
    public const string GpuMemoryTotal = "memory.gpuTotal";
    public const string TextureMemory = "memory.textures";
    public const string AudioMemory = "memory.audio";
    public const string AssetMemory = "memory.assets";
    public const string Gen0Collections = "gc.gen0";
    public const string Gen1Collections = "gc.gen1";
    public const string Gen2Collections = "gc.gen2";
    public const string CollectionsPerMinute = "gc.perMinute";

    public const string DrawCalls = "render.drawCalls";
    public const string Vertices = "render.vertices";
    public const string Indices = "render.indices";
    public const string Triangles = "render.triangles";
    public const string TextureSwitches = "render.textureSwitches";
    public const string ShaderSwitches = "render.shaderSwitches";
    public const string RenderTargetSwitches = "render.targetSwitches";
    public const string PipelineSwitches = "render.pipelineSwitches";
    public const string UIElements = "ui.elements";
    public const string UIVisibleElements = "ui.visible";
    public const string UIDrawCalls = "ui.drawCalls";
    public const string UIQuads = "ui.quads";
    public const string UILayoutPasses = "ui.layoutPasses";
    public const string UIGeometryRebuilds = "ui.rebuilds";
    public const string UIGlyphs = "ui.glyphs";
    public const string UIAtlasPages = "ui.atlasPages";
    public const string UITextLayouts = "ui.textLayouts";
    public const string DescriptorWrites = "render.descriptorWrites";
    public const string Barriers = "render.barriers";
    public const string Submits = "render.submits";
    public const string GpuAllocated = "memory.gpuAllocated";
    public const string GpuAllocations = "memory.gpuAllocations";
    public const string GpuAllocationFailures = "memory.gpuAllocationFailures";
    public const string BufferMemory = "memory.buffers";
    public const string RenderTargets = "render.targets";
    public const string Textures = "render.textures";
    public const string Shaders = "render.shaders";
    public const string Sprites = "render.sprites";
    public const string SpritesCulled = "render.spritesCulled";
    public const string SpriteBatches = "render.batches";

    public const string Entities = "scene.entities";
    public const string Components = "scene.components";
    public const string Systems = "scene.systems";
    public const string PhysicsBodies = "physics.bodies";
    public const string PhysicsColliders = "physics.colliders";
    public const string ParticleSystems = "particles.systems";
    public const string ParticlesAlive = "particles.alive";
    public const string ParticleCapacity = "particles.capacity";
    public const string ParticlesEmitted = "particles.emitted";
    public const string ParticlesRecycled = "particles.recycled";
    public const string ParticleModules = "particles.modules";
    public const string ParticleCollisionChecks = "particles.collisionChecks";
    public const string ParticleMemory = "particles.memory";
    public const string ParticleSimulationTime = "particles.simulationTime";
    public const string ParticleGpuSimulationTime = "particles.gpuSimulationTime";
    public const string ParticlesDrawn = "render.particles";
    public const string ParticleDrawCalls = "render.particleDrawCalls";
    public const string ParticleVertices = "render.particleVertices";
    public const string ParticleTextureSwitches = "render.particleTextureSwitches";
    public const string ParticleRenderTime = "render.particleTime";
    public const string BrowserInstances = "browser.instances";
    public const string BrowserFramesPainted = "browser.framesPainted";
    public const string BrowserFramesUploaded = "browser.framesUploaded";
    public const string BrowserUploadBytes = "browser.uploadBytes";
    public const string BrowserUploadTime = "browser.uploadTime";
    public const string BrowserTextureMemory = "browser.textureMemory";
    public const string BrowserHostMemory = "browser.hostMemory";
    public const string BrowserGpuZeroCopy = "browser.gpuZeroCopy";
    public const string AudioSources = "audio.voices";
    public const string AudioStreams = "audio.streams";
    public const string AudioUnderruns = "audio.underruns";
    public const string CSharpScripts = "scripts.csharp";
    public const string ScriptVms = "scripts.vms";
    public const string ScriptFibers = "scripts.fibers";
    public const string AssetsLoaded = "assets.loaded";
    public const string AssetsFailed = "assets.failed";
    public const string AssetLoadTime = "assets.loadTime";
    public const string AssetLoadTimeMax = "assets.loadTimeMax";
    public const string AssetsUnloaded = "assets.unloaded";
    public const string SceneLoadTime = "scene.loadTime";
    public const string TextureProxies = "textures.proxies";
    public const string TexturesStreamedIn = "textures.fullResolution";
    public const string TexturesReduced = "textures.reduced";
    public const string TexturesPending = "textures.pending";
    public const string TextureDowngrades = "textures.downgrades";
    public const string StreamedTextureMemory = "memory.streamedTextures";
    public const string DebugDrawCommands = "debug.drawCommands";
    public const string DebugDrawDropped = "debug.drawDropped";
    public const string ContentMounts = "content.mounts";
    public const string ContentPackages = "content.packages";
    public const string ContentPackageBytes = "content.packageBytes";
    public const string ContentFiles = "content.files";
    public const string ContentActiveStreams = "content.activeStreams";
    public const string ContentPendingReads = "content.pendingReads";
    public const string ContentBytesRead = "content.bytesRead";
    public const string ContentChunksDecoded = "content.chunksDecoded";
    public const string ContentChunkCacheHits = "content.chunkCacheHits";
    public const string ContentVerifyTime = "content.verifyTime";
    public const string ContentDecryptTime = "content.decryptTime";
    public const string ContentDecompressTime = "content.decompressTime";
    public const string ContentPackageOpenTime = "content.packageOpenTime";
    public const string ContentFailedReads = "content.failedReads";
    public const string ContentCorruptedChunks = "content.corruptedChunks";

    public static IReadOnlyList<MetricDefinition> Definitions { get; } =
    [
        new(Fps, "FPS", "Time", MetricUnit.Count, "Frames per second (from the frame time)."),
        new(FrameTime, "Frame time", "Time", MetricUnit.Milliseconds, "Wall time between frames."),
        new(FrameTimeAverage, "Frame time (avg)", "Time", MetricUnit.Milliseconds, "Frame time averaged over the last 30 frames."),
        new(CpuTime, "CPU time", "Time", MetricUnit.Milliseconds, "CPU time spent updating and submitting the frame."),
        new(GpuTime, "GPU time", "Time", MetricUnit.Milliseconds, "GPU time of the frame, from timer queries (a few frames late)."),
        new(Uptime, "Uptime", "Time", MetricUnit.Seconds, "Time since the game started."),
        new(FrameNumber, "Frame", "Time", MetricUnit.Count, "Frames rendered since start."),
        new(ProcessMemory, "Process memory", "Memory", MetricUnit.Bytes, "Working set of the process."),
        new(ManagedHeap, "Managed heap", "Memory", MetricUnit.Bytes, "Bytes in use on the .NET heap."),
        new(AllocatedPerFrame, "Allocated / frame", "Memory", MetricUnit.Bytes, "Managed bytes allocated during the last frame (temporary allocations)."),
        new(GpuMemoryUsed, "GPU memory", "Memory", MetricUnit.Bytes, "Device-local memory in use on the adapter (all processes), from the driver's memory budget (VK_EXT_memory_budget)."),
        new(GpuAllocated, "GPU allocated", "Memory", MetricUnit.Bytes, "Device memory the engine reserved through its allocator (VMA blocks), exact."),
        new(GpuAllocations, "GPU allocations", "Memory", MetricUnit.Count, "Live GPU allocations (buffers and images) placed in those blocks."),
        new(GpuAllocationFailures, "GPU allocation failures", "Memory", MetricUnit.Count, "GPU allocations that failed since start."),
        new(BufferMemory, "Buffer memory", "Memory", MetricUnit.Bytes, "GPU buffers: static geometry, streaming and staging memory."),
        new(GpuMemoryTotal, "GPU memory total", "Memory", MetricUnit.Bytes, "Dedicated video memory of the adapter."),
        new(TextureMemory, "Texture memory", "Memory", MetricUnit.Bytes, "Textures and render targets: exact allocation sizes from the GPU allocator, or estimated from sizes (RGBA8, shown with ~) without one."),
        new(AudioMemory, "Audio memory", "Memory", MetricUnit.Bytes, "Decoded PCM held by audio buffers."),
        new(AssetMemory, "Asset memory", "Memory", MetricUnit.Bytes, "Textures plus audio buffers held for the current scene."),
        new(Gen0Collections, "GC gen0", "Memory", MetricUnit.Count, "Gen0 collections since start."),
        new(Gen1Collections, "GC gen1", "Memory", MetricUnit.Count, "Gen1 collections since start."),
        new(Gen2Collections, "GC gen2", "Memory", MetricUnit.Count, "Gen2 (full) collections since start."),
        new(CollectionsPerMinute, "GCs / minute", "Memory", MetricUnit.PerMinute, "Garbage collections (all generations) over the last minute."),
        new(DrawCalls, "Draw calls", "Renderer", MetricUnit.Count, "Draw calls submitted last frame."),
        new(Vertices, "Vertices", "Renderer", MetricUnit.Count, "Vertices uploaded to dynamic buffers last frame."),
        new(Indices, "Indices", "Renderer", MetricUnit.Count, "Indices drawn last frame."),
        new(Triangles, "Triangles", "Renderer", MetricUnit.Count, "Triangles drawn last frame."),
        new(TextureSwitches, "Texture switches", "Renderer", MetricUnit.Count, "Texture binding changes last frame."),
        new(ShaderSwitches, "Shader switches", "Renderer", MetricUnit.Count, "Shader program changes last frame."),
        new(RenderTargetSwitches, "Target switches", "Renderer", MetricUnit.Count, "Render target changes last frame."),
        new(PipelineSwitches, "Pipeline switches", "Renderer", MetricUnit.Count, "Graphics pipeline binds (shader + blend/topology state) last frame."),
        new(UIElements, "UI elements", "UI", MetricUnit.Count, "Elements in all UI contexts of the scene."),
        new(UIVisibleElements, "UI visible", "UI", MetricUnit.Count, "Elements drawn (visible ones)."),
        new(UIDrawCalls, "UI draw calls", "UI", MetricUnit.Count, "Draw calls of the game UI last frame (one per texture run)."),
        new(UIQuads, "UI quads", "UI", MetricUnit.Count, "Quads in the cached UI geometry (4 vertices, 6 indices each)."),
        new(UILayoutPasses, "UI layout passes", "UI", MetricUnit.Count, "Layout passes since start (only when something changed)."),
        new(UIGeometryRebuilds, "UI rebuilds", "UI", MetricUnit.Count, "Geometry rebuilds since start (only when something visible changed)."),
        new(UIGlyphs, "Glyphs cached", "UI", MetricUnit.Count, "Glyphs rasterized into the atlas (per font, glyph and size)."),
        new(UIAtlasPages, "Atlas pages", "UI", MetricUnit.Count, "1024x1024 glyph atlas textures."),
        new(UITextLayouts, "Text layouts built", "UI", MetricUnit.Count, "Text layouts built since start (cache misses)."),
        new(DescriptorWrites, "Descriptor writes", "Renderer", MetricUnit.Count, "Push-descriptor writes (texture and uniform bindings) last frame."),
        new(Barriers, "Barriers", "Renderer", MetricUnit.Count, "Pipeline barriers (image layout transitions) recorded last frame."),
        new(Submits, "Queue submits", "Renderer", MetricUnit.Count, "Command buffer submissions to the GPU queue last frame."),
        new(RenderTargets, "Render targets", "Renderer", MetricUnit.Count, "Live render targets."),
        new(Textures, "Textures", "Renderer", MetricUnit.Count, "Live textures (render targets included)."),
        new(Shaders, "Shaders", "Renderer", MetricUnit.Count, "Live shader programs."),
        new(Sprites, "Sprites", "Renderer", MetricUnit.Count, "Sprites drawn last frame."),
        new(SpritesCulled, "Sprites culled", "Renderer", MetricUnit.Count, "Sprites skipped by camera culling."),
        new(SpriteBatches, "Batches", "Renderer", MetricUnit.Count, "Sprite batch flushes last frame."),
        new(Entities, "Entities", "Runtime", MetricUnit.Count, "GameObjects in the scene."),
        new(Components, "Components", "Runtime", MetricUnit.Count, "Components on those GameObjects (Transform excluded)."),
        new(Systems, "Systems", "Runtime", MetricUnit.Count, "Scene systems (physics, audio...)."),
        new(PhysicsBodies, "Physics bodies", "Physics", MetricUnit.Count, "Box2D bodies."),
        new(PhysicsColliders, "Colliders", "Physics", MetricUnit.Count, "Collider components."),
        new(ParticleSystems, "Particle systems", "Particles", MetricUnit.Count, "Particle systems playing or with particles alive."),
        new(ParticlesAlive, "Particles", "Particles", MetricUnit.Count, "Particles alive in every system."),
        new(ParticleCapacity, "Particle capacity", "Particles", MetricUnit.Count, "Sum of the active systems' maximum particles."),
        new(ParticlesEmitted, "Particles emitted", "Particles", MetricUnit.Count, "Particles emitted last frame."),
        new(ParticlesRecycled, "Particles recycled", "Particles", MetricUnit.Count, "Particles that died last frame (their slots are reused)."),
        new(ParticleModules, "Particle modules", "Particles", MetricUnit.Count, "Optional modules enabled across the active systems."),
        new(ParticleCollisionChecks, "Particle collision checks", "Particles", MetricUnit.Count, "Particle-plane tests last frame."),
        new(ParticleMemory, "Particle buffers", "Particles", MetricUnit.Bytes, "Memory of the particle buffers."),
        new(ParticleSimulationTime, "Particle simulation", "Particles", MetricUnit.Milliseconds, "CPU time simulating particles last frame."),
        new(ParticleGpuSimulationTime, "Particle GPU simulation", "Particles", MetricUnit.Milliseconds, "Unsupported: particles are simulated on the CPU."),
        new(ParticlesDrawn, "Particles drawn", "Renderer", MetricUnit.Count, "Particles drawn last frame (after culling)."),
        new(ParticleDrawCalls, "Particle draw calls", "Renderer", MetricUnit.Count, "3D particle draw calls (2D particles batch with sprites)."),
        new(ParticleVertices, "Particle vertices", "Renderer", MetricUnit.Count, "Vertices of the particle quads drawn."),
        new(ParticleTextureSwitches, "Particle texture switches", "Renderer", MetricUnit.Count, "3D particle runs that changed texture or blending."),
        new(ParticleRenderTime, "Particle rendering", "Renderer", MetricUnit.Milliseconds, "CPU time building and submitting particle geometry."),
        new(BrowserInstances, "Browsers", "Browser", MetricUnit.Count, "Embedded browsers open (created or loading)."),
        new(BrowserFramesPainted, "Browser paints", "Browser", MetricUnit.Count, "Paints Chromium delivered since the last frame (several merge into one upload)."),
        new(BrowserFramesUploaded, "Browser uploads", "Browser", MetricUnit.Count, "Browser textures updated last frame."),
        new(BrowserUploadBytes, "Browser upload size", "Browser", MetricUnit.Bytes, "Dirty pixels copied to the GPU last frame."),
        new(BrowserUploadTime, "Browser upload", "Browser", MetricUnit.Milliseconds, "CPU time copying browser pixels from shared memory and queuing uploads last frame."),
        new(BrowserTextureMemory, "Browser textures", "Browser", MetricUnit.Bytes, "GPU memory of the browser textures."),
        new(BrowserHostMemory, "Browser host memory", "Browser", MetricUnit.Bytes, "Working set of MNVTBrowserHost.exe (Chromium's renderer and GPU processes are not counted)."),
        new(BrowserGpuZeroCopy, "Browser GPU zero-copy", "Browser", MetricUnit.Count, "Unsupported: pages reach the GPU through CPU pixel buffers."),
        new(AudioSources, "Audio voices", "Audio", MetricUnit.Count, "Voices playing or paused."),
        new(AudioStreams, "Streamed voices", "Audio", MetricUnit.Count, "Voices playing a streamed clip (decoded while playing)."),
        new(AudioUnderruns, "Stream underruns", "Audio", MetricUnit.Count, "Blocks of streamed audio played silent because decoding fell behind, since start."),
        new(CSharpScripts, "C# scripts", "Scripting", MetricUnit.Count, "Components from game assemblies."),
        new(ScriptVms, "Script VMs", "Scripting", MetricUnit.Count, "Running .mnvts VMs (MissionRunners)."),
        new(ScriptFibers, "Script coroutines", "Scripting", MetricUnit.Count, "Live .mnvts coroutines (running or waiting)."),
        new(AssetsLoaded, "Assets loaded", "Assets", MetricUnit.Count, "Textures and clips loaded by the scene."),
        new(AssetsFailed, "Asset problems", "Assets", MetricUnit.Count, "Assets with import or load errors."),
        new(AssetLoadTime, "Texture load time", "Assets", MetricUnit.Milliseconds, "Mean of the last 32 texture loads: read, decode and upload (streamed ones from request to swap-in)."),
        new(AssetLoadTimeMax, "Texture load time (max)", "Assets", MetricUnit.Milliseconds, "Longest of the last 32 texture loads."),
        new(AssetsUnloaded, "Textures unloaded", "Assets", MetricUnit.Count, "Textures unloaded as unused after scene changes, since start."),
        new(SceneLoadTime, "Scene load time", "Assets", MetricUnit.Milliseconds, "Duration of the last scene load, from request to start."),
        new(TextureProxies, "Texture proxies", "Assets", MetricUnit.Count, "Streamed textures showing their low-resolution proxy now."),
        new(TexturesStreamedIn, "Full-resolution textures", "Assets", MetricUnit.Count, "Streamed textures showing their full image."),
        new(TexturesReduced, "Reduced textures", "Assets", MetricUnit.Count, "Streamed textures showing a reduced level: drawn small enough not to need their full image."),
        new(TexturesPending, "Textures streaming in", "Assets", MetricUnit.Count, "Texture levels being read and decoded in the background."),
        new(TextureDowngrades, "Texture downgrades", "Assets", MetricUnit.Count, "Full images released for budget or memory pressure, since start."),
        new(StreamedTextureMemory, "Streamed texture memory", "Memory", MetricUnit.Bytes, "GPU memory of full-resolution streamed textures (estimated from sizes, with mipmaps)."),
        new(DebugDrawCommands, "Debug draws", "Diagnostics", MetricUnit.Count, "Debug draw commands queued this frame."),
        new(DebugDrawDropped, "Debug draws dropped", "Diagnostics", MetricUnit.Count, "Debug draw commands rejected because the buffer was full."),
        new(ContentMounts, "Mounted sources", "Content", MetricUnit.Count, "Sources mounted in the content file system (packages, or the project's folders in development)."),
        new(ContentPackages, "Packages", "Content", MetricUnit.Count, "Mounted .mnvtpack packages."),
        new(ContentPackageBytes, "Package content", "Content", MetricUnit.Bytes, "Uncompressed size of the files in the mounted packages."),
        new(ContentFiles, "Content files", "Content", MetricUnit.Count, "Files in the mounted sources; unsupported when a folder is mounted (counting would scan it)."),
        new(ContentActiveStreams, "Open streams", "Content", MetricUnit.Count, "Content streams open now."),
        new(ContentPendingReads, "Pending reads", "Content", MetricUnit.Count, "Asynchronous content reads in flight."),
        new(ContentBytesRead, "Content read", "Content", MetricUnit.Bytes, "Bytes read from the content since the last frame (any thread)."),
        new(ContentChunksDecoded, "Chunks decoded", "Content", MetricUnit.Count, "Package chunks verified and decoded since the last frame."),
        new(ContentChunkCacheHits, "Chunk cache hits", "Content", MetricUnit.Count, "Reads served from a stream's decoded chunk, since start."),
        new(ContentVerifyTime, "Verification", "Content", MetricUnit.Milliseconds, "Time hashing chunks and checking signatures since the last frame (any thread)."),
        new(ContentDecryptTime, "Decryption", "Content", MetricUnit.Milliseconds, "AES-256-GCM time since the last frame (any thread)."),
        new(ContentDecompressTime, "Decompression", "Content", MetricUnit.Milliseconds, "Decompression time since the last frame (any thread)."),
        new(ContentPackageOpenTime, "Package opening", "Content", MetricUnit.Milliseconds, "Total time opening packages (header, index, signature)."),
        new(ContentFailedReads, "Failed reads", "Content", MetricUnit.Count, "Content reads that failed (missing, damaged or tampered files) since start."),
        new(ContentCorruptedChunks, "Corrupted chunks", "Content", MetricUnit.Count, "Package chunks that failed their hash or authentication since start."),
    ];

    private static readonly Dictionary<string, MetricDefinition> ById = Definitions.ToDictionary(d => d.Id, StringComparer.Ordinal);

    public static MetricDefinition? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>"8.33 ms", "1.24 GB", "n/a (unsupported)"... Unmeasured values never print as zero.</summary>
    public static string Format(MetricValue value, MetricUnit unit) => value.Status switch
    {
        MetricStatus.Unsupported => "n/a (unsupported)",
        MetricStatus.Unavailable => "n/a",
        MetricStatus.Estimated => "~" + Format(MetricValue.Measured(value.Value), unit),
        _ => unit switch
        {
            MetricUnit.Milliseconds => string.Create(CultureInfo.InvariantCulture, $"{value.Value:0.00} ms"),
            MetricUnit.Seconds => FormatDuration(value.Value),
            MetricUnit.Bytes => FormatBytes(value.Value),
            MetricUnit.PerMinute => string.Create(CultureInfo.InvariantCulture, $"{value.Value:0.#}/min"),
            _ => value.Value.ToString(Math.Abs(value.Value % 1) < 1e-9 ? "N0" : "N1", CultureInfo.InvariantCulture),
        },
    };

    public static string Format(string id, MetricValue value) => Format(value, Find(id)?.Unit ?? MetricUnit.Count);

    public static string FormatBytes(double bytes) => bytes switch
    {
        >= 1024d * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024 * 1024):0.00} GB"),
        >= 1024d * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024):0.0} MB"),
        >= 1024d => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:0.0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes:0} B"),
    };

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}:{span.Seconds:00}");
    }
}

/// <summary>
/// The metric values of one frame. Reused across frames (no allocation once every id was seen); values not set this
/// frame read as <see cref="MetricStatus.Unavailable"/>.
/// </summary>
public sealed class MetricsSnapshot
{
    private readonly Dictionary<string, (long Frame, MetricValue Value)> _values = new(StringComparer.Ordinal);

    /// <summary>Frame these values belong to; set by the collector before providers run.</summary>
    public long Frame { get; set; }

    public void Set(string id, double value) => _values[id] = (Frame, MetricValue.Measured(value));

    public void Set(string id, MetricValue value) => _values[id] = (Frame, value);

    public void SetUnsupported(string id) => _values[id] = (Frame, MetricValue.Unsupported);

    public MetricValue Get(string id) => _values.TryGetValue(id, out var entry) && entry.Frame == Frame ? entry.Value : MetricValue.Unavailable;

    /// <summary>Ids that have a value this frame.</summary>
    public IEnumerable<string> Ids => _values.Where(p => p.Value.Frame == Frame).Select(p => p.Key);
}

/// <summary>Supplies metrics each frame. Providers must be cheap; expensive ones sample less often and keep their last value.</summary>
public interface IMetricsProvider
{
    string Name { get; }

    void Collect(MetricsSnapshot metrics);
}

/// <summary>Runs providers in order; any provider can be switched off by name (its metrics then read as unavailable).</summary>
public sealed class MetricsCollector
{
    private readonly List<IMetricsProvider> _providers = [];
    private readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IMetricsProvider> Providers => _providers;

    public MetricsCollector Add(IMetricsProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers.Add(provider);
        return this;
    }

    public bool IsEnabled(string provider) => !_disabled.Contains(provider);

    public void SetEnabled(string provider, bool enabled)
    {
        if (enabled)
        {
            _disabled.Remove(provider);
        }
        else
        {
            _disabled.Add(provider);
        }
    }

    public void Collect(MetricsSnapshot metrics, long frame)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        metrics.Frame = frame;
        foreach (var provider in _providers)
        {
            if (!_disabled.Contains(provider.Name))
            {
                using var sample = Core.Diagnostics.Profiler.Sample(provider.Name); // diagnostics cost is itself profiled
                provider.Collect(metrics);
            }
        }
    }
}

/// <summary>Fixed-size history of chosen metrics for graphs (no allocation per frame; unmeasured frames record NaN).</summary>
public sealed class MetricsHistory(int capacity = 240)
{
    private readonly Dictionary<string, float[]> _series = new(StringComparer.Ordinal);
    private int _next;

    public int Capacity { get; } = capacity > 1 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    /// <summary>Frames recorded so far, up to <see cref="Capacity"/>.</summary>
    public int Count { get; private set; }

    public void Track(string id)
    {
        if (!_series.ContainsKey(id))
        {
            var values = new float[Capacity];
            Array.Fill(values, float.NaN);
            _series[id] = values;
        }
    }

    public void Record(MetricsSnapshot metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        foreach (var (id, values) in _series)
        {
            var value = metrics.Get(id);
            values[_next] = value.IsMeasured ? (float)value.Value : float.NaN;
        }

        _next = (_next + 1) % Capacity;
        Count = Math.Min(Count + 1, Capacity);
    }

    /// <summary>Copies the series oldest-first into <paramref name="destination"/> (length <see cref="Capacity"/>); false if not tracked.</summary>
    public bool CopyTo(string id, Span<float> destination)
    {
        if (!_series.TryGetValue(id, out var values))
        {
            return false;
        }

        values.AsSpan(_next).CopyTo(destination);
        values.AsSpan(0, _next).CopyTo(destination[(Capacity - _next)..]);
        return true;
    }
}
