using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Diagnostics;

/// <summary>Keys of the runtime debug interface (names of <c>MNVTEngine.Key</c> values).</summary>
public sealed class DiagnosticsKeyBindings
{
    public string ToggleOverlay { get; set; } = "F1";

    public string ToggleProfiler { get; set; } = "F2";

    public string ToggleConsole { get; set; } = "F3";

    public string ToggleDebugDrawing { get; set; } = "F4";

    public string ToggleWarnings { get; set; } = "F5";

    /// <summary>Opens every debug window (and gives the UI the mouse).</summary>
    public string ToggleWindows { get; set; } = "F6";
}

public enum OverlayCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>What the HUD overlay shows and where.</summary>
public sealed class OverlaySettings
{
    /// <summary>Sections in display order: frame, memory, renderer, runtime, scripting, physics, assets, warnings.</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> Sections { get; set; } = ["frame", "memory", "renderer", "runtime", "warnings"];

    public OverlayCorner Corner { get; set; } = OverlayCorner.TopLeft;

    /// <summary>Background opacity, 0 to 1.</summary>
    public float Opacity { get; set; } = 0.55f;

    /// <summary>Text scale of the overlay and debug windows (1 = 100 %).</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Title line ("DEVELOPMENT BUILD"); empty for none.</summary>
    public string Title { get; set; } = "DEVELOPMENT BUILD";

    /// <summary>"dark", "light" or "classic".</summary>
    public string Theme { get; set; } = "dark";
}

/// <summary>
/// Limits checked by the budget monitor. Null switches a budget off. Memory limits are in MB, times in ms. The
/// defaults are starting points for a 60 Hz 2D game, not universal limits.
/// </summary>
public sealed class BudgetSettings
{
    public double? FrameTimeMs { get; set; } = 1000.0 / 60.0;

    public double? CpuFrameTimeMs { get; set; } = 10;

    public double? GpuFrameTimeMs { get; set; } = 10;

    public double? MaxDrawCalls { get; set; } = 1000;

    public double? MaxVertices { get; set; } = 1_000_000;

    public double? MaxTriangles { get; set; } = 500_000;

    public double? MaxTextureSwitches { get; set; } = 500;

    public double? MaxShaderSwitches { get; set; } = 100;

    public double? MaxRenderTargets { get; set; } = 16;

    public double? MaxDebugDrawCommands { get; set; } = 10_000;

    public double? MaxEntities { get; set; } = 10_000;

    public double? MaxComponents { get; set; } = 50_000;

    public double? MaxPhysicsBodies { get; set; } = 2_000;

    public double? MaxScriptCoroutines { get; set; } = 500;

    public double? ManagedMemoryMb { get; set; } = 1024;

    public double? ProcessMemoryMb { get; set; } = 2048;

    public double? GpuMemoryMb { get; set; }

    public double? TextureMemoryMb { get; set; } = 512;

    public double? AssetMemoryMb { get; set; } = 768;

    public double? AudioMemoryMb { get; set; } = 256;

    public double? AllocationKbPerFrame { get; set; } = 256;

    public double? GcPerMinute { get; set; } = 60;
}

/// <summary>
/// The <c>"diagnostics"</c> section of the .mnvtproj: runtime debug UI, debug drawing, profiler and budgets. Session
/// overrides (editor, console) change a copy and never the project file unless saved explicitly.
/// </summary>
public sealed class DiagnosticsSettings
{
    private const double Mb = 1024.0 * 1024.0;

    /// <summary>Master switch for development builds, the Player and editor Play Mode.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Release exports normally contain no debug UI at all; true ships it. Stored in the project as the devtools module
    /// switch (<c>"modules": { "devtools": true }</c>); older files with <c>includeInRelease</c> are read as that switch.
    /// </summary>
    public bool IncludeInRelease { get; set; }

    public bool ShowOverlayByDefault { get; set; }

    public bool EnableProfiler { get; set; } = true;

    /// <summary>GPU timer queries (asynchronous; results arrive a few frames late).</summary>
    public bool EnableGpuTiming { get; set; } = true;

    public bool EnableDebugDrawing { get; set; } = true;

    public bool EnablePerformanceWarnings { get; set; } = true;

    /// <summary>False ignores every debug shortcut (the console command 'overlay' still works when open).</summary>
    public bool ShortcutsEnabled { get; set; } = true;

    public int MaxDebugDrawCommands { get; set; } = 20_000;

    /// <summary>Frames after start (loading, JIT warm-up) during which budgets are not evaluated.</summary>
    public int WarmupFrames { get; set; } = 60;

    /// <summary>Seconds before the same budget warning repeats while it persists.</summary>
    public double WarningRepeatSeconds { get; set; } = 10;

    /// <summary>Debug windows not offered at all (e.g. "Physics", "Assets").</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> DisabledPanels { get; set; } = [];

    /// <summary>Metric providers to skip (e.g. "Scripting", "Physics").</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> DisabledProviders { get; set; } = [];

    public DiagnosticsKeyBindings Keys { get; set; } = new();

    public OverlaySettings Overlay { get; set; } = new();

    public BudgetSettings Budgets { get; set; } = new();

    public BudgetThresholds Thresholds { get; set; } = BudgetThresholds.Default;

    /// <summary>Fixes invalid values to their defaults; returns a message per fix.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var defaults = new DiagnosticsSettings();
        if (MaxDebugDrawCommands is < 1 or > 1_000_000)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"diagnostics.maxDebugDrawCommands {MaxDebugDrawCommands} is out of range 1..1000000; using {defaults.MaxDebugDrawCommands}."));
            MaxDebugDrawCommands = defaults.MaxDebugDrawCommands;
        }

        if (WarmupFrames is < 0 or > 100_000)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"diagnostics.warmupFrames {WarmupFrames} is out of range 0..100000; using {defaults.WarmupFrames}."));
            WarmupFrames = defaults.WarmupFrames;
        }

        if (!(WarningRepeatSeconds >= 0.5))
        {
            problems.Add($"diagnostics.warningRepeatSeconds must be at least 0.5; using {defaults.WarningRepeatSeconds}.");
            WarningRepeatSeconds = defaults.WarningRepeatSeconds;
        }

        try
        {
            (Thresholds ??= BudgetThresholds.Default).Validate();
        }
        catch (ArgumentException ex)
        {
            problems.Add($"diagnostics.thresholds: {ex.Message} Using the defaults.");
            Thresholds = BudgetThresholds.Default;
        }

        Overlay ??= new OverlaySettings();
        Overlay.Opacity = Math.Clamp(Overlay.Opacity, 0f, 1f);
        if (Overlay.Scale is < 0.5f or > 4f)
        {
            problems.Add("diagnostics.overlay.scale must be between 0.5 and 4; using 1.");
            Overlay.Scale = 1f;
        }

        Keys ??= new DiagnosticsKeyBindings();
        Budgets ??= new BudgetSettings();
        foreach (var property in typeof(BudgetSettings).GetProperties())
        {
            if (property.GetValue(Budgets) is double value && !(value > 0 && double.IsFinite(value)))
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"diagnostics.budgets.{char.ToLowerInvariant(property.Name[0])}{property.Name[1..]} must be positive; budget disabled."));
                property.SetValue(Budgets, null);
            }
        }

        return problems;
    }

    /// <summary>The configured budgets with their metrics, subsystems and investigation hints.</summary>
    public IReadOnlyList<PerformanceBudget> CreateBudgets()
    {
        var b = Budgets;
        var list = new List<PerformanceBudget>();
        void Add(string id, double? limit, string metric, string subsystem, string hint, double scale = 1, BudgetThresholds? thresholds = null)
        {
            if (limit is { } value)
            {
                list.Add(new PerformanceBudget(id, metric, value * scale, subsystem, hint, thresholds));
            }
        }

        // With VSync the frame time equals the refresh period, so a 16.67 ms limit at 60 Hz is always "at 100 %": the
        // frame budget looks at the 30-frame average and only warns once it is clearly above the limit.
        Add("frameTime", b.FrameTimeMs, Metrics.FrameTimeAverage, "Frame", "Open the profiler (F2) and look for the slowest sample.",
            thresholds: new BudgetThresholds(0.95, 1.03, 1.10, 1.5));
        Add("cpuFrameTime", b.CpuFrameTimeMs, Metrics.CpuTime, "Frame", "CPU-bound: check Update/FixedUpdate samples, scripts and physics.");
        Add("gpuFrameTime", b.GpuFrameTimeMs, Metrics.GpuTime, "Renderer", "GPU-bound: large sprites, overdraw, lights, post effects.");
        Add("drawCalls", b.MaxDrawCalls, Metrics.DrawCalls, "Renderer", "Use texture atlases and fewer materials so sprites batch.");
        Add("vertices", b.MaxVertices, Metrics.Vertices, "Renderer", "Check tilemaps and particle-like objects; cull off-screen content.");
        Add("triangles", b.MaxTriangles, Metrics.Triangles, "Renderer", "Check tilemaps and many overlapping sprites.");
        Add("textureSwitches", b.MaxTextureSwitches, Metrics.TextureSwitches, "Renderer", "Sort by texture or pack sprites into atlases.");
        Add("shaderSwitches", b.MaxShaderSwitches, Metrics.ShaderSwitches, "Renderer", "Fewer distinct materials; group objects by material.");
        Add("renderTargets", b.MaxRenderTargets, Metrics.RenderTargets, "Renderer", "Render targets that are created but never released.");
        Add("debugDrawCommands", b.MaxDebugDrawCommands, Metrics.DebugDrawCommands, "Diagnostics", "A loop issuing Debug.Draw* every frame, or persistent draws never removed.");
        Add("entities", b.MaxEntities, Metrics.Entities, "Runtime", "Objects spawned without being destroyed; check pooling.");
        Add("components", b.MaxComponents, Metrics.Components, "Runtime", "Objects spawned without being destroyed.");
        Add("physicsBodies", b.MaxPhysicsBodies, Metrics.PhysicsBodies, "Physics", "Bodies of objects off-screen; disable or pool them.");
        Add("scriptCoroutines", b.MaxScriptCoroutines, Metrics.ScriptFibers, "Scripting", "Mission scripts spawning coroutines in a loop.");
        Add("managedMemory", b.ManagedMemoryMb, Metrics.ManagedHeap, "Memory", "Memory panel: look for growing caches or lists.", Mb);
        Add("processMemory", b.ProcessMemoryMb, Metrics.ProcessMemory, "Memory", "Native memory: textures, audio, drivers.", Mb);
        Add("gpuMemory", b.GpuMemoryMb, Metrics.GpuMemoryUsed, "Memory", "Texture sizes and render targets.", Mb);
        Add("textureMemory", b.TextureMemoryMb, Metrics.TextureMemory, "Memory", "Asset panel: largest textures; reduce sizes or unload.", Mb);
        Add("assetMemory", b.AssetMemoryMb, Metrics.AssetMemory, "Memory", "Asset panel: what the scene keeps loaded.", Mb);
        Add("audioMemory", b.AudioMemoryMb, Metrics.AudioMemory, "Memory", "Long clips are fully decoded; shorten or compress music.", Mb);
        Add("allocationPerFrame", b.AllocationKbPerFrame, Metrics.AllocatedPerFrame, "Memory", "Allocations in Update (LINQ, strings, new lists) cause GC pauses.", 1024);
        Add("gcFrequency", b.GcPerMinute, Metrics.CollectionsPerMinute, "Memory", "Reduce per-frame allocations (see 'Allocated / frame').");
        return list;
    }

    public DiagnosticsSettings Clone() => JObject.FromObject(this, Serializer).ToObject<DiagnosticsSettings>(Serializer)!;

    /// <summary>Applies a partial JSON object (session overrides) on top of these settings.</summary>
    /// <exception cref="JsonException">Invalid override JSON.</exception>
    public void Apply(JObject overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        using var reader = overrides.CreateReader();
        Serializer.Populate(reader, this);
    }

    private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
        ObjectCreationHandling = ObjectCreationHandling.Replace,
    });
}
