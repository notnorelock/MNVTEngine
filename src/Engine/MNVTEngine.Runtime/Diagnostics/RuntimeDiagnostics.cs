using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Core.Diagnostics;
using MNVTEngine.Core.Timing;
using MNVTEngine.Diagnostics;
using MNVTEngine.Graphics;
using MNVTEngine.Scenes;
using Debug = MNVTEngine.Diagnostics.Debug;
using DiagnosticMetrics = MNVTEngine.Diagnostics.Metrics;

namespace MNVTEngine.Runtime;

/// <summary>What diagnostics observe: the device, the running scene and its resources. Any part may be missing.</summary>
/// <param name="Modules">The running project's engine modules (inspection panels look up the ones they show).</param>
public sealed record DiagnosticsTarget(IGraphicsDevice Device, Func<Scene?> Scene, ResourceCache? Resources = null, AssetDatabase? Assets = null, SceneRenderer? SceneRenderer = null, IReadOnlyList<IEngineModule>? Modules = null);

/// <summary>
/// The diagnostics of one running game (standalone runtime, Player or editor Play Mode): profiler, GPU timer, metrics,
/// budgets, the debug draw buffer and console commands. Collection is separate from presentation: the debug UI only
/// reads <see cref="Metrics"/>, <see cref="Budgets"/>, <see cref="Profiler"/> and <see cref="History"/>.
/// <para>Per frame the host calls <see cref="BeginFrame"/> before the first update, <see cref="SceneRendered"/> after the
/// game's own rendering (so debug UI draws are not counted as game draws) and <see cref="EndFrame"/> last.</para>
/// </summary>
public sealed partial class RuntimeDiagnostics
{
    private readonly ILogger _logger;
    private readonly string _captureDirectory;
    private readonly double[] _recentFrames = new double[30];
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly bool _ownsProfilerFrames;
    private GraphicsFrameStatistics _sceneBaseline;
    private DiagnosticsTarget? _target;
    private long _cpuStart;
    private bool _inFrame;
    private int _recentIndex;
    private int _recentCount;

    /// <param name="profiler">An existing profiler (e.g. <c>--profile</c>); otherwise one is created when the settings enable it.</param>
    /// <param name="captureDirectory">Where 'profiler capture' writes reports.</param>
    /// <param name="ownsProfilerFrames">False when the host already brackets the profiler's frames (the editor shares its profiler).</param>
    public RuntimeDiagnostics(DiagnosticsSettings settings, ILogger logger, string captureDirectory, FrameProfiler? profiler = null, bool ownsProfilerFrames = true)
    {
        _ownsProfilerFrames = ownsProfilerFrames;
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        Settings = settings;
        _logger = logger;
        _captureDirectory = captureDirectory;
        Profiler = profiler ?? (settings.EnableProfiler ? new FrameProfiler(history: 600) : null);
        DrawBuffer = new DebugCommandBuffer(settings.MaxDebugDrawCommands);
        Budgets = new BudgetMonitor(settings.CreateBudgets(), settings.Thresholds)
        {
            RepeatInterval = TimeSpan.FromSeconds(settings.WarningRepeatSeconds),
            Enabled = settings.EnablePerformanceWarnings,
        };
        Budgets.WarningRaised += OnWarning;
        foreach (var id in (string[])[DiagnosticMetrics.FrameTime, DiagnosticMetrics.CpuTime, DiagnosticMetrics.GpuTime, DiagnosticMetrics.ManagedHeap, DiagnosticMetrics.ProcessMemory, DiagnosticMetrics.DrawCalls, DiagnosticMetrics.AllocatedPerFrame])
        {
            History.Track(id);
        }

        Collector
            .Add(new TimeProvider(this))
            .Add(new MemoryProvider(this))
            .Add(new RendererProvider(this))
            .Add(new SceneProvider(this))
            .Add(new AssetProvider(this));
        foreach (var name in settings.DisabledProviders)
        {
            Collector.SetEnabled(name, false);
        }

        RegisterCommands();
    }

    /// <summary>Session settings (a copy the debug UI may change; never written back to the project by itself).</summary>
    public DiagnosticsSettings Settings { get; }

    public FrameProfiler? Profiler { get; }

    public MetricsSnapshot Metrics { get; } = new();

    public MetricsCollector Collector { get; } = new();

    public MetricsHistory History { get; } = new(300);

    public BudgetMonitor Budgets { get; }

    public DebugCommandBuffer DrawBuffer { get; }

    public DebugCommandRegistry Commands { get; } = new();

    public DiagnosticsTarget? Target => _target;

    /// <summary>Frames completed since start.</summary>
    public long Frame { get; private set; }

    /// <summary>Game rendering of the last frame (without the debug UI).</summary>
    public GraphicsFrameStatistics SceneStatistics { get; private set; }

    /// <summary>Everything drawn last frame, debug UI included.</summary>
    public GraphicsFrameStatistics TotalStatistics { get; private set; }

    public IGpuTimer? GpuTimer => Settings.EnableGpuTiming ? _target?.Device.GpuTimer : null;

    /// <summary>
    /// Set each frame by whatever displays GPU memory. Reading it (GL_NVX_gpu_memory_info) makes the driver wait for the
    /// GPU (up to a whole frame), so it is sampled every 5 s and only while shown or budgeted.
    /// </summary>
    public bool GpuMemoryRequested { get; set; }

    /// <summary>Source of the last GPU memory reading (e.g. VK_EXT_memory_budget), or null.</summary>
    public string? GpuMemorySource { get; private set; }

    public void Attach(DiagnosticsTarget target) => _target = target ?? throw new ArgumentNullException(nameof(target));

    /// <summary>Makes this instance the target of <see cref="Debug"/> and <see cref="Core.Diagnostics.Profiler"/> on this thread.</summary>
    public void Install()
    {
        Debug.Buffer = DrawBuffer;
        Debug.Commands = Commands;
        Debug.DrawingEnabled = Settings.EnableDebugDrawing;
        if (_ownsProfilerFrames)
        {
            Core.Diagnostics.Profiler.Current = Profiler;
        }
    }

    public void Uninstall()
    {
        if (ReferenceEquals(Debug.Buffer, DrawBuffer))
        {
            Debug.Buffer = null;
            Debug.Commands = null;
        }

        if (_ownsProfilerFrames && ReferenceEquals(Core.Diagnostics.Profiler.Current, Profiler))
        {
            Core.Diagnostics.Profiler.Current = null;
        }
    }

    /// <summary>Starts a frame (idempotent until <see cref="EndFrame"/>).</summary>
    public void BeginFrame()
    {
        if (_inFrame)
        {
            return;
        }

        _inFrame = true;
        _cpuStart = Stopwatch.GetTimestamp();
        if (_ownsProfilerFrames)
        {
            Profiler?.BeginFrame();
        }

        GpuTimer?.BeginFrame();
    }

    /// <summary>Optional: marks where the game's rendering starts when the device also draws other things in the frame (the editor's views).</summary>
    public void SceneRendering() => _sceneBaseline = _target?.Device.Statistics.Current ?? default;

    public void SceneRendered()
    {
        if (_target is { } target)
        {
            var now = target.Device.Statistics.Current;
            var b = _sceneBaseline;
            SceneStatistics = new GraphicsFrameStatistics(now.DrawCalls - b.DrawCalls, now.Indices - b.Indices, now.Triangles - b.Triangles, now.Lines - b.Lines,
                now.VerticesUploaded - b.VerticesUploaded, now.TextureSwitches - b.TextureSwitches, now.ShaderSwitches - b.ShaderSwitches, now.RenderTargetSwitches - b.RenderTargetSwitches,
                now.PipelineSwitches - b.PipelineSwitches, now.DescriptorWrites - b.DescriptorWrites, now.Barriers - b.Barriers, now.Submits - b.Submits);
            _sceneBaseline = default;
        }
    }

    public void EndFrame(FrameTime time)
    {
        if (!_inFrame)
        {
            BeginFrame(); // a frame with no update (e.g. paused editor): still measured
        }

        var cpu = Stopwatch.GetElapsedTime(_cpuStart).TotalMilliseconds;
        GpuTimer?.EndFrame();
        using (Core.Diagnostics.Profiler.Sample("Diagnostics"))
        {
            if (_target is { } target)
            {
                target.Device.Statistics.EndFrame();
                TotalStatistics = target.Device.Statistics.LastFrame;
            }

            RecordFrame(time.UnscaledDeltaTime * 1000.0, cpu);
            Collector.Collect(Metrics, Frame);
            if (Frame >= Settings.WarmupFrames)
            {
                Budgets.Evaluate(Metrics);
            }

            GpuMemoryRequested = false;
            History.Record(Metrics);
            DrawBuffer.EndFrame(time.UnscaledDeltaTime);
        }

        if (_ownsProfilerFrames)
        {
            Profiler?.EndFrame();
        }

        _inFrame = false;
        Frame++;
    }

    private double _frameMs;
    private double _cpuMs;

    private void RecordFrame(double frameMs, double cpuMs)
    {
        (_frameMs, _cpuMs) = (frameMs, cpuMs);
        if (frameMs > 0)
        {
            _recentFrames[_recentIndex] = frameMs;
            _recentIndex = (_recentIndex + 1) % _recentFrames.Length;
            _recentCount = Math.Min(_recentCount + 1, _recentFrames.Length);
        }
    }

    private void OnWarning(BudgetWarning warning)
    {
        var level = warning.Severity switch
        {
            BudgetSeverity.Critical => LogLevel.Critical,
            BudgetSeverity.Error => LogLevel.Error,
            BudgetSeverity.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        LogBudget(_logger, level, warning.Budget.Id, warning.Message);
    }

    [LoggerMessage(EventId = 9000, Message = "[budget {Budget}] {Message}")]
    private static partial void LogBudget(ILogger logger, LogLevel level, string budget, string message);

    // ------------------------------------------------------------------ providers

    private sealed class TimeProvider(RuntimeDiagnostics owner) : IMetricsProvider
    {
        public string Name => "Time";

        public void Collect(MetricsSnapshot metrics)
        {
            metrics.Set(DiagnosticMetrics.FrameTime, owner._frameMs);
            metrics.Set(DiagnosticMetrics.CpuTime, owner._cpuMs);
            if (owner._recentCount > 0)
            {
                var sum = 0.0;
                for (var i = 0; i < owner._recentCount; i++)
                {
                    sum += owner._recentFrames[i];
                }

                metrics.Set(DiagnosticMetrics.Fps, 1000.0 / (sum / owner._recentCount));
                metrics.Set(DiagnosticMetrics.FrameTimeAverage, sum / owner._recentCount);
            }

            var gpu = owner._target?.Device.GpuTimer;
            metrics.Set(DiagnosticMetrics.GpuTime, gpu is null ? MetricValue.Unsupported
                : !owner.Settings.EnableGpuTiming || double.IsNaN(gpu.LastFrameMilliseconds) ? MetricValue.Unavailable
                : MetricValue.Measured(gpu.LastFrameMilliseconds));
            metrics.Set(DiagnosticMetrics.Uptime, owner._uptime.Elapsed.TotalSeconds);
            metrics.Set(DiagnosticMetrics.FrameNumber, owner.Frame);
        }
    }

    /// <summary>Cheap values every frame; process and GPU memory (driver/OS calls) a few times per second.</summary>
    private sealed class MemoryProvider(RuntimeDiagnostics owner) : IMetricsProvider
    {
        private readonly Queue<(double Seconds, int Collections)> _gcSamples = new();
        private long _allocated = GC.GetTotalAllocatedBytes();
        private double _lastSample = double.NegativeInfinity;
        private double _lastGpuSample = double.NegativeInfinity;
        private MetricValue _process;
        private MetricValue _gpuUsed = MetricValue.Unavailable;
        private MetricValue _gpuTotal = MetricValue.Unavailable;
        private MetricValue _textures;
        private GpuAllocationStatistics? _allocation;

        public string Name => "Memory";

        public void Collect(MetricsSnapshot metrics)
        {
            metrics.Set(DiagnosticMetrics.ManagedHeap, GC.GetTotalMemory(false));
            var allocated = GC.GetTotalAllocatedBytes();
            metrics.Set(DiagnosticMetrics.AllocatedPerFrame, allocated - _allocated);
            _allocated = allocated;
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            metrics.Set(DiagnosticMetrics.Gen0Collections, gen0);
            metrics.Set(DiagnosticMetrics.Gen1Collections, gen1);
            metrics.Set(DiagnosticMetrics.Gen2Collections, gen2);

            var now = owner._uptime.Elapsed.TotalSeconds;
            if (now - _lastSample >= 0.25)
            {
                _lastSample = now;
                _process = MetricValue.Measured(Environment.WorkingSet);
                _gcSamples.Enqueue((now, gen0)); // gen0 counts every collection (higher generations include it)
                while (_gcSamples.Count > 2 && now - _gcSamples.Peek().Seconds > 60)
                {
                    _gcSamples.Dequeue();
                }

                if (owner._target is { } target)
                {
                    var wanted = owner.GpuMemoryRequested || owner.Budgets.States.Any(s => s.Budget.Metric == DiagnosticMetrics.GpuMemoryUsed);
                    if (wanted && now - _lastGpuSample >= 5)
                    {
                        _lastGpuSample = now;
                        GpuMemoryInfo gpu;
                        using (Core.Diagnostics.Profiler.Sample("GpuMemory"))
                        {
                            gpu = target.Device.QueryGpuMemory();
                        }

                        owner.GpuMemorySource = gpu.Supported ? gpu.Source : null;
                        _gpuUsed = !gpu.Supported ? MetricValue.Unsupported : gpu.UsedBytes is { } used ? MetricValue.Measured(used) : MetricValue.Unsupported;
                        _gpuTotal = !gpu.Supported ? MetricValue.Unsupported : gpu.TotalBytes is { } total ? MetricValue.Measured(total) : MetricValue.Unsupported;
                    }
                    else if (!wanted)
                    {
                        (_gpuUsed, _gpuTotal) = (MetricValue.Unavailable, MetricValue.Unavailable);
                        _lastGpuSample = double.NegativeInfinity;
                    }

                    // Exact sizes from the GPU allocator when the backend has one; otherwise width x height x 4 is only an estimate.
                    _allocation = target.Device.AllocationStatistics;
                    _textures = _allocation is { } allocation
                        ? MetricValue.Measured(allocation.TextureBytes + allocation.RenderTargetBytes)
                        : MetricValue.Estimated(target.Device.Resources.Count().TextureBytes);
                }
            }

            metrics.Set(DiagnosticMetrics.ProcessMemory, _process);
            metrics.Set(DiagnosticMetrics.GpuMemoryUsed, _gpuUsed);
            metrics.Set(DiagnosticMetrics.GpuMemoryTotal, _gpuTotal);
            metrics.Set(DiagnosticMetrics.TextureMemory, _textures);
            if (_allocation is { } engineGpu)
            {
                metrics.Set(DiagnosticMetrics.GpuAllocated, engineGpu.BlockBytes);
                metrics.Set(DiagnosticMetrics.GpuAllocations, engineGpu.AllocationCount);
                metrics.Set(DiagnosticMetrics.GpuAllocationFailures, engineGpu.FailedAllocations);
                metrics.Set(DiagnosticMetrics.BufferMemory, engineGpu.BufferBytes);
            }
            else if (owner._target is not null)
            {
                metrics.SetUnsupported(DiagnosticMetrics.GpuAllocated);
                metrics.SetUnsupported(DiagnosticMetrics.GpuAllocations);
                metrics.SetUnsupported(DiagnosticMetrics.GpuAllocationFailures);
                metrics.SetUnsupported(DiagnosticMetrics.BufferMemory);
            }

            var (oldest, oldCount) = _gcSamples.Count > 0 ? _gcSamples.Peek() : (now, gen0);
            if (now - oldest >= 10)
            {
                metrics.Set(DiagnosticMetrics.CollectionsPerMinute, (gen0 - oldCount) * 60.0 / (now - oldest));
            }

            if (_textures.HasValue)
            {
                metrics.Set(DiagnosticMetrics.AssetMemory, new MetricValue(_textures.Status, _textures.Value)); // textures; audio buffers are AudioMemory
            }
        }
    }

    private sealed class RendererProvider(RuntimeDiagnostics owner) : IMetricsProvider
    {
        private int _frame;
        private GraphicsResourceCounts _counts;

        public string Name => "Renderer";

        public void Collect(MetricsSnapshot metrics)
        {
            if (owner._target is not { } target)
            {
                return;
            }

            var stats = owner.SceneStatistics;
            metrics.Set(DiagnosticMetrics.DrawCalls, stats.DrawCalls);
            metrics.Set(DiagnosticMetrics.Vertices, stats.VerticesUploaded);
            metrics.Set(DiagnosticMetrics.Indices, stats.Indices);
            metrics.Set(DiagnosticMetrics.Triangles, stats.Triangles);
            metrics.Set(DiagnosticMetrics.TextureSwitches, stats.TextureSwitches);
            metrics.Set(DiagnosticMetrics.ShaderSwitches, stats.ShaderSwitches);
            metrics.Set(DiagnosticMetrics.RenderTargetSwitches, stats.RenderTargetSwitches);
            metrics.Set(DiagnosticMetrics.PipelineSwitches, stats.PipelineSwitches);
            metrics.Set(DiagnosticMetrics.DescriptorWrites, stats.DescriptorWrites);
            metrics.Set(DiagnosticMetrics.Barriers, stats.Barriers);
            metrics.Set(DiagnosticMetrics.Submits, stats.Submits);
            if (_frame++ % 15 == 0)
            {
                _counts = target.Device.Resources.Count();
            }

            metrics.Set(DiagnosticMetrics.Textures, _counts.Textures);
            metrics.Set(DiagnosticMetrics.RenderTargets, _counts.RenderTargets);
            metrics.Set(DiagnosticMetrics.Shaders, _counts.Shaders);
            if (target.SceneRenderer is { } renderer)
            {
                // Without a sprite pass (a project without world.2d) there are no sprite counts: unsupported, not zero.
                foreach (var (metric, statistic) in new[] { (DiagnosticMetrics.Sprites, RenderStatistics.SpritesDrawn), (DiagnosticMetrics.SpritesCulled, RenderStatistics.SpritesCulled) })
                {
                    if (renderer.Statistics.TryGetValue(statistic, out var value))
                    {
                        metrics.Set(metric, value);
                    }
                    else
                    {
                        metrics.SetUnsupported(metric);
                    }
                }

                metrics.Set(DiagnosticMetrics.SpriteBatches, renderer.BatchesDrawn);
                ParticleRenderMetrics(metrics, renderer);
            }

            metrics.Set(DiagnosticMetrics.DebugDrawCommands, owner.DrawBuffer.Requested);
            metrics.Set(DiagnosticMetrics.DebugDrawDropped, owner.DrawBuffer.Dropped);
        }
    }

    /// <summary>Particle rendering from the passes' statistics; without a particle pass (no particles module) unsupported, not zero.</summary>
    private static void ParticleRenderMetrics(MetricsSnapshot metrics, SceneRenderer renderer)
    {
        var statistics = renderer.Statistics;
        if (!statistics.TryGetValue(RenderStatistics.ParticlesDrawn, out var drawn))
        {
            foreach (var metric in new[] { DiagnosticMetrics.ParticlesDrawn, DiagnosticMetrics.ParticleDrawCalls, DiagnosticMetrics.ParticleVertices, DiagnosticMetrics.ParticleTextureSwitches, DiagnosticMetrics.ParticleRenderTime })
            {
                metrics.SetUnsupported(metric);
            }

            return;
        }

        metrics.Set(DiagnosticMetrics.ParticlesDrawn, drawn);
        metrics.Set(DiagnosticMetrics.ParticleVertices, drawn * 4);
        metrics.Set(DiagnosticMetrics.ParticleRenderTime, statistics.GetValueOrDefault(RenderStatistics.ParticleRenderMicroseconds) / 1000.0);
        foreach (var (metric, statistic) in new[] { (DiagnosticMetrics.ParticleDrawCalls, RenderStatistics.ParticleDrawCalls), (DiagnosticMetrics.ParticleTextureSwitches, RenderStatistics.ParticleTextureSwitches) })
        {
            if (statistics.TryGetValue(statistic, out var value))
            {
                metrics.Set(metric, value);
            }
            else
            {
                metrics.SetUnsupported(metric); // 2D: particles share the sprite batch, whose draw calls and switches are counted there
            }
        }
    }

    private sealed class SceneProvider(RuntimeDiagnostics owner) : IMetricsProvider
    {
        private int _frame;
        private (int Entities, int Components, int CSharp, int Systems) _counts;
        private Scene? _scene;

        public string Name => "Runtime";

        public void Collect(MetricsSnapshot metrics)
        {
            if (owner._target?.Scene() is not { } scene)
            {
                return;
            }

            if (_frame++ % 10 == 0 || !ReferenceEquals(scene, _scene))
            {
                _scene = scene;
                _counts = Count(scene);
            }

            metrics.Set(DiagnosticMetrics.Entities, _counts.Entities);
            metrics.Set(DiagnosticMetrics.Components, _counts.Components);
            metrics.Set(DiagnosticMetrics.Systems, _counts.Systems);
            metrics.Set(DiagnosticMetrics.CSharpScripts, _counts.CSharp);
        }

        private static (int, int, int, int) Count(Scene scene)
        {
            int components = 0, csharp = 0;
            foreach (var gameObject in scene.GameObjects)
            {
                foreach (var component in gameObject.Components)
                {
                    if (component is Transform)
                    {
                        continue;
                    }

                    components++;
                    if (IsGameCode(component.GetType()))
                    {
                        csharp++;
                    }
                }
            }

            return (scene.GameObjects.Count, components, csharp, scene.Systems.Count);
        }
    }

    private sealed class AssetProvider(RuntimeDiagnostics owner) : IMetricsProvider
    {
        private int _frame;
        private (int Loaded, int Failed) _counts;

        public string Name => "Assets";

        public void Collect(MetricsSnapshot metrics)
        {
            if (owner._target is not { } target || target.Resources is null)
            {
                return;
            }

            if (_frame++ % 30 == 0)
            {
                var importErrors = target.Assets?.Diagnostics.Count(d => d.Severity == Serialization.DiagnosticSeverity.Error) ?? 0;
                _counts = (target.Resources.LoadedTextureCount, target.Resources.FailedTextureCount + importErrors); // textures; clips are the audio module's
            }

            metrics.Set(DiagnosticMetrics.AssetsLoaded, _counts.Loaded);
            metrics.Set(DiagnosticMetrics.AssetsFailed, _counts.Failed);
        }
    }

    /// <summary>Game components: anything not defined in an engine assembly.</summary>
    public static bool IsGameCode(Type type) => type?.Assembly.FullName?.StartsWith("MNVTEngine.", StringComparison.Ordinal) != true;

    // ------------------------------------------------------------------ console commands

    private void RegisterCommands()
    {
        Commands
            .Register("crash", "Crashes the game on purpose, to test crash reporting (a report and a dump are written).", "<managed|native>", args =>
            {
                if (args.Count != 1 || !Enum.TryParse<CrashReporting.CrashKind>(args[0], ignoreCase: true, out var kind))
                {
                    return DebugCommandResult.Fail("Usage: crash <managed|native>");
                }

                // On a thread of its own: the console runs commands inside a catch, which would stop a managed crash.
                new Thread(() => CrashReporting.CrashReporter.ForceCrash(kind)) { Name = "crash command" }.Start();
                return Ok($"Crashing ({kind})...");
            })
            .Register("fps", "Frame rate and frame times.", _ => Ok(Lines(DiagnosticMetrics.Fps, DiagnosticMetrics.FrameTime, DiagnosticMetrics.CpuTime, DiagnosticMetrics.GpuTime)))
            .Register("stats", "Every metric with its current value.", _ => Ok(string.Join('\n', DiagnosticMetrics.Definitions.Select(d => $"{d.Subsystem,-11} {d.Name,-22} {DiagnosticMetrics.Format(Metrics.Get(d.Id), d.Unit)}"))))
            .Register("memory", "Heap, process, GPU and asset memory; GC counts.", _ => Ok(Lines(DiagnosticMetrics.ManagedHeap, DiagnosticMetrics.ProcessMemory, DiagnosticMetrics.AllocatedPerFrame, DiagnosticMetrics.TextureMemory, DiagnosticMetrics.AudioMemory, DiagnosticMetrics.Gen0Collections, DiagnosticMetrics.Gen1Collections, DiagnosticMetrics.Gen2Collections, DiagnosticMetrics.CollectionsPerMinute) + Environment.NewLine + GpuMemoryLine()))
            .Register("renderer", "Draw calls, geometry, switches and GPU resources.", _ => Ok(Lines(DiagnosticMetrics.DrawCalls, DiagnosticMetrics.Vertices, DiagnosticMetrics.Triangles, DiagnosticMetrics.TextureSwitches, DiagnosticMetrics.ShaderSwitches, DiagnosticMetrics.RenderTargetSwitches, DiagnosticMetrics.PipelineSwitches, DiagnosticMetrics.DescriptorWrites, DiagnosticMetrics.Barriers, DiagnosticMetrics.Textures, DiagnosticMetrics.RenderTargets, DiagnosticMetrics.Shaders, DiagnosticMetrics.Sprites, DiagnosticMetrics.SpritesCulled) + $"\nDevice: {_target?.Device.Description ?? "none"}" + BackendLines()))
            .Register("scene", "The running scene.", _ => _target?.Scene() is { } scene
                ? Ok($"Scene '{scene.Name}': {scene.GameObjects.Count} objects, {scene.Systems.Count} systems, camera {(scene.MainCamera?.GameObject.Name ?? "none")}, {scene.ScriptExceptionCount} script exceptions")
                : DebugCommandResult.Fail("No scene is running."))
            .Register("entities", "Lists objects (up to 50), optionally filtered by name.", "[filter]", Entities)
            .Register("scripts", "C# script components (mission VMs: the scripting module's 'missions').", _ => Scripts())
            .Register("budgets", "Lists budgets, or sets a limit for this session.", "[set <id> <limit>]", BudgetsCommand)
            .Register("profiler", "Summary, or 'capture' (report file), 'reset', 'detail frame|systems|scripts'.", "[capture|reset|detail <level>]", ProfilerCommand)
            .Register("gc", "Runs a full garbage collection and reports the heap before and after.", _ =>
            {
                var before = GC.GetTotalMemory(false);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                return Ok($"Managed heap {DiagnosticMetrics.FormatBytes(before)} -> {DiagnosticMetrics.FormatBytes(GC.GetTotalMemory(true))}");
            })
            .Register("draw", "'clear' removes timed and persistent debug draws; 'on'/'off' toggles drawing.", "clear|on|off", args => (args.Count > 0 ? args[0] : "") switch
            {
                "clear" => Done(DrawBuffer.Clear, "Debug draws cleared."),
                "on" => Done(() => Debug.DrawingEnabled = true, "Debug drawing on."),
                "off" => Done(() => Debug.DrawingEnabled = false, "Debug drawing off."),
                _ => DebugCommandResult.Fail("Usage: draw clear|on|off"),
            })
            .Register("providers", "Lists metric providers, or switches one on/off.", "[on|off <name>]", args =>
            {
                if (args.Count == 2 && args[0] is "on" or "off")
                {
                    Collector.SetEnabled(args[1], args[0] == "on");
                }

                return Ok(string.Join('\n', Collector.Providers.Select(p => $"{p.Name,-10} {(Collector.IsEnabled(p.Name) ? "on" : "off")}")));
            });
    }

    private static DebugCommandResult Ok(string text) => DebugCommandResult.Ok(text);

    private static DebugCommandResult Done(Action action, string text)
    {
        action();
        return DebugCommandResult.Ok(text);
    }

    private string Lines(params string[] ids) =>
        string.Join('\n', ids.Select(id => $"{DiagnosticMetrics.Find(id)?.Name ?? id,-22} {DiagnosticMetrics.Format(id, Metrics.Get(id))}"));

    /// <summary>Queried right now (a one-off command may stall the GPU once).</summary>
    private string BackendLines() =>
        _target?.Device.DescribeBackend() is { Count: > 0 } backend
            ? string.Concat(backend.Select(entry => $"\n{entry.Key}: {entry.Value}"))
            : string.Empty;

    private string GpuMemoryLine()
    {
        var memory = _target?.Device.QueryGpuMemory() ?? GpuMemoryInfo.Unsupported;
        if (!memory.Supported)
        {
            return "GPU memory: not exposed by this driver (unsupported)";
        }

        string Bytes(long? value) => value is { } v ? DiagnosticMetrics.FormatBytes(v) : "n/a";
        return $"GPU memory ({memory.Source}, all processes): used {Bytes(memory.UsedBytes)}, available {Bytes(memory.AvailableBytes)}, total {Bytes(memory.TotalBytes)}";
    }

    private DebugCommandResult Entities(IReadOnlyList<string> args)
    {
        if (_target?.Scene() is not { } scene)
        {
            return DebugCommandResult.Fail("No scene is running.");
        }

        var filter = args.Count > 0 ? args[0] : "";
        var matches = scene.GameObjects.Where(g => g.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{matches.Count} of {scene.GameObjects.Count} objects"));
        foreach (var gameObject in matches.Take(50))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n{gameObject.Name,-24} {(gameObject.ActiveInHierarchy ? "active  " : "inactive")} {gameObject.Transform.Position}  [{string.Join(", ", gameObject.Components.Skip(1).Select(c => c.GetType().Name))}]");
        }

        return Ok(text.ToString());
    }

    private DebugCommandResult Scripts()
    {
        if (_target?.Scene() is not { } scene)
        {
            return DebugCommandResult.Fail("No scene is running.");
        }

        var game = scene.GameObjects.SelectMany(g => g.Components).Where(c => IsGameCode(c.GetType()))
            .GroupBy(c => c.GetType()).Select(g => $"{g.Key.FullName} x{g.Count()} ({g.Key.Assembly.GetName().Name})").ToList();
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"C# script types: {game.Count}"));
        foreach (var line in game)
        {
            text.Append("\n  ").Append(line);
        }

        text.Append(CultureInfo.InvariantCulture, $"\nScript exceptions: {scene.ScriptExceptionCount}");
        return Ok(text.ToString());
    }

    private DebugCommandResult BudgetsCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 3 && args[0] == "set")
        {
            if (!double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var limit))
            {
                return DebugCommandResult.Fail($"'{args[2]}' is not a number.");
            }

            Budgets.SetLimit(args[1], limit);
            return Ok($"Budget {args[1]} limit set to {limit.ToString(CultureInfo.InvariantCulture)} for this session.");
        }

        return Ok(string.Join('\n', Budgets.States.Select(s =>
        {
            var unit = DiagnosticMetrics.Find(s.Budget.Metric)?.Unit ?? MetricUnit.Count;
            var usage = double.IsNaN(s.Ratio) ? "  n/a" : string.Create(CultureInfo.InvariantCulture, $"{s.Ratio * 100,5:0}%");
            return $"{s.Budget.Id,-20} {usage} {s.Severity,-11} {DiagnosticMetrics.Format(s.Value, unit)} / {DiagnosticMetrics.Format(MetricValue.Measured(s.Budget.Limit), unit)}";
        })));
    }

    private DebugCommandResult ProfilerCommand(IReadOnlyList<string> args)
    {
        if (Profiler is null)
        {
            return DebugCommandResult.Fail("The profiler is off (diagnostics.enableProfiler).");
        }

        switch (args.Count > 0 ? args[0] : "")
        {
            case "reset":
                Budgets.ResetPeaks();
                return Ok("Budget peaks reset.");
            case "detail" when args.Count > 1 && Enum.TryParse<ProfilerDetail>(args[1], ignoreCase: true, out var detail):
                Core.Diagnostics.Profiler.Detail = detail;
                return Ok($"Profiler detail: {detail}.");
            case "capture":
                Directory.CreateDirectory(_captureDirectory);
                var path = Path.Combine(_captureDirectory, string.Create(CultureInfo.InvariantCulture, $"profile-{DateTime.Now:yyyyMMdd-HHmmss}.txt"));
                File.WriteAllText(path, Profiler.FormatReport());
                return Ok($"Profile of {Profiler.Frames.Count} frames written to {path}");
            default:
                var summary = Profiler.Summarize(120);
                return Ok(string.Create(CultureInfo.InvariantCulture, $"Last {summary.Frames} frames: avg {summary.AverageFrameMilliseconds:0.00} ms, min {summary.MinFrameMilliseconds:0.00}, p95 {summary.P95FrameMilliseconds:0.00}, p99 {summary.P99FrameMilliseconds:0.00}, max {summary.MaxFrameMilliseconds:0.00}\n")
                    + string.Join('\n', summary.Samples.Where(s => s.Depth == 0).OrderByDescending(s => s.AverageMilliseconds).Select(s => string.Create(CultureInfo.InvariantCulture, $"  {s.Name,-28} {s.AverageMilliseconds,7:0.000} ms"))));
        }
    }
}
