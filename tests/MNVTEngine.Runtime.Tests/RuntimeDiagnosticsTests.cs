using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Core.Timing;
using MNVTEngine.Diagnostics;
using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;
using Debug = MNVTEngine.Diagnostics.Debug;
using DiagnosticMetrics = MNVTEngine.Diagnostics.Metrics;

namespace MNVTEngine.Runtime.Tests;

[Collection(nameof(RuntimeStatics))]
public sealed class RuntimeDiagnosticsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mnvt-diag-").FullName;
    private readonly StubDevice _device = new();
    private readonly Scene _scene = new("Level") { RethrowScriptExceptions = true };
    private long _frame;

    public RuntimeDiagnosticsTests()
    {
        for (var i = 0; i < 3; i++)
        {
            _scene.CreateGameObject($"Crate {i}").AddComponent<Camera>();
        }

        _scene.CreateGameObject("Player");
        _scene.Start();
    }

    public void Dispose()
    {
        _device.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private RuntimeDiagnostics Create(DiagnosticsSettings? settings = null)
    {
        var diagnostics = new RuntimeDiagnostics(settings ?? new DiagnosticsSettings { WarmupFrames = 0 }, NullLogger.Instance, Path.Combine(_root, "Profiles"));
        diagnostics.Attach(new DiagnosticsTarget(_device, () => _scene));
        return diagnostics;
    }

    private void Frame(RuntimeDiagnostics diagnostics, float delta = 0.016f)
    {
        _frame++;
        diagnostics.BeginFrame();
        _scene.Update(new FrameTime(delta, delta, _frame * delta, _frame * delta, _frame));
        diagnostics.SceneRendered();
        diagnostics.EndFrame(new FrameTime(delta, delta, _frame * delta, _frame * delta, _frame));
    }

    [Fact]
    public void Frames_ProduceMetrics_AndUnmeasurableOnesAreNotZero()
    {
        var diagnostics = Create();
        Frame(diagnostics);
        Frame(diagnostics);

        var metrics = diagnostics.Metrics;
        Assert.Equal(16, metrics.Get(DiagnosticMetrics.FrameTime).Value, 0);
        Assert.Equal(1000.0 / 16, metrics.Get(DiagnosticMetrics.Fps).Value, 0);
        Assert.True(metrics.Get(DiagnosticMetrics.CpuTime).IsMeasured);
        Assert.Equal(4, metrics.Get(DiagnosticMetrics.Entities).Value);
        Assert.Equal(3, metrics.Get(DiagnosticMetrics.Components).Value); // Transforms are not counted
        Assert.True(metrics.Get(DiagnosticMetrics.ManagedHeap).Value > 0);
        Assert.Equal(MetricStatus.Unsupported, metrics.Get(DiagnosticMetrics.GpuTime).Status);         // the stub has no timer queries
        Assert.Equal(MetricStatus.Unavailable, metrics.Get(DiagnosticMetrics.GpuMemoryUsed).Status);   // not requested by anything
        Assert.Equal(2, diagnostics.Frame);
    }

    [Fact]
    public void Budgets_WaitForTheWarmUp_ThenReport()
    {
        var settings = new DiagnosticsSettings { WarmupFrames = 3 };
        settings.Budgets.MaxEntities = 2;
        var diagnostics = Create(settings);
        var warnings = new List<BudgetWarning>();
        diagnostics.Budgets.WarningRaised += warnings.Add;

        for (var i = 0; i < 3; i++)
        {
            Frame(diagnostics);
        }

        Assert.Empty(warnings);
        Frame(diagnostics);
        var warning = Assert.Single(warnings);
        Assert.Equal("entities", warning.Budget.Id);
        Assert.Equal(BudgetSeverity.Critical, warning.Severity); // 4 of 2 = 200 %
    }

    [Fact]
    public void InstallRoutesDebugDraws_AndOneFrameDrawsEndWithTheFrame()
    {
        var diagnostics = Create();
        diagnostics.Install();
        try
        {
            Assert.Same(diagnostics.DrawBuffer, Debug.Buffer);
            Assert.Same(diagnostics.Commands, Debug.Commands);
            Assert.Same(diagnostics.Profiler, Core.Diagnostics.Profiler.Current);
            Debug.DrawLine(Vector2.Zero, Vector2.One, Color.Red);
            Debug.DrawCircle(Vector2.Zero, 1f, Color.Red, Debug.Persistent);
            Frame(diagnostics);
            Assert.Equal(1, diagnostics.DrawBuffer.Count);
            Assert.Equal(2, diagnostics.Metrics.Get(DiagnosticMetrics.DebugDrawCommands).Value);
        }
        finally
        {
            diagnostics.Uninstall();
        }

        Assert.Null(Debug.Buffer);
        Assert.Null(Core.Diagnostics.Profiler.Current);
    }

    [Fact]
    public void ConsoleCommands_InspectAndAdjustTheRunningGame()
    {
        var diagnostics = Create();
        Frame(diagnostics);

        Assert.Contains("1 of 4 objects", diagnostics.Commands.Execute("entities player").Output, StringComparison.Ordinal);
        Assert.Contains("Scene 'Level': 4 objects", diagnostics.Commands.Execute("scene").Output, StringComparison.Ordinal);
        Assert.True(diagnostics.Commands.Execute("budgets set drawCalls 5").Success);
        Assert.Equal(5, diagnostics.Budgets.States.Single(s => s.Budget.Id == "drawCalls").Budget.Limit);
        Assert.False(diagnostics.Commands.Execute("budgets set drawCalls lots").Success);
        Assert.Contains("FPS", diagnostics.Commands.Execute("fps").Output, StringComparison.Ordinal);
        Assert.Contains("GPU memory: not exposed by this driver (unsupported)", diagnostics.Commands.Execute("memory").Output, StringComparison.Ordinal);

        var capture = diagnostics.Commands.Execute("profiler capture");
        Assert.True(capture.Success, capture.Output);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "Profiles"), "profile-*.txt"));

        Assert.True(diagnostics.Commands.Execute("providers off Runtime").Success);
        Frame(diagnostics);
        Assert.Equal(MetricStatus.Unavailable, diagnostics.Metrics.Get(DiagnosticMetrics.Entities).Status);
    }

    [Fact]
    public void SharedProfiler_IsNotBracketedOrUninstalled()
    {
        var shared = new Core.Diagnostics.FrameProfiler();
        Core.Diagnostics.Profiler.Current = shared;
        try
        {
            var diagnostics = new RuntimeDiagnostics(new DiagnosticsSettings(), NullLogger.Instance, _root, shared, ownsProfilerFrames: false);
            diagnostics.Attach(new DiagnosticsTarget(_device, () => _scene));
            diagnostics.Install();
            Frame(diagnostics);
            diagnostics.Uninstall();
            Assert.Same(shared, Core.Diagnostics.Profiler.Current);
            Assert.Empty(shared.Frames); // the host brackets frames, not the play session
        }
        finally
        {
            Core.Diagnostics.Profiler.Current = null;
        }
    }

    [Fact]
    public void ProjectDiagnostics_LoadValidateAndSaveWithoutTouchingTheRest()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "Main.mnvt"), """{ "schemaVersion": 2, "gameObjects": [] }""");
        var file = Path.Combine(_root, "Game.mnvtproj");
        File.WriteAllText(file, """
            { "schemaVersion": 1, "name": "Game", "startupScene": "Scenes/Main.mnvt", "window": { "width": 800 },
              "diagnostics": { "showOverlayByDefault": true, "maxDebugDrawCommands": -4,
                               "budgets": { "maxDrawCalls": 300, "gpuMemoryMb": 2048 }, "overlay": { "corner": "bottomRight" } } }
            """);

        var project = GameProject.Load(file);
        Assert.True(project.Diagnostics.ShowOverlayByDefault);
        Assert.Equal(20_000, project.Diagnostics.MaxDebugDrawCommands); // invalid value replaced (a warning, not an error)
        Assert.Equal(300, project.Diagnostics.Budgets.MaxDrawCalls);
        Assert.Equal(OverlayCorner.BottomRight, project.Diagnostics.Overlay.Corner);

        var changed = project.Diagnostics.Clone();
        changed.Budgets.MaxDrawCalls = 50;
        changed.Budgets.TextureMemoryMb = null;
        project.SaveDiagnostics(changed);

        var reloaded = GameProject.Load(file);
        Assert.Equal(50, reloaded.Diagnostics.Budgets.MaxDrawCalls);
        Assert.Null(reloaded.Diagnostics.Budgets.TextureMemoryMb);
        Assert.Equal(800, reloaded.Window.Width);
        Assert.Equal(2048, reloaded.Diagnostics.Budgets.GpuMemoryMb);
    }

    /// <summary>Just enough device for diagnostics: real resource tracking and statistics, no GPU.</summary>
    private sealed class StubDevice : IGraphicsDevice
    {
        public GraphicsResourceTracker Resources { get; } = new();

        public string Description => "Stub";

        public int BackBufferWidth => 320;

        public int BackBufferHeight => 240;

        public void ResizeBackBuffer(int width, int height)
        {
        }

        public Texture2D CreateTexture(int width, int height, ReadOnlySpan<byte> rgba, SamplerState sampler, string debugName = "Texture") => throw new NotSupportedException();

        public RenderTarget2D CreateRenderTarget(int width, int height, SamplerState sampler, string debugName = "RenderTarget") => throw new NotSupportedException();

        public Shader CreateShader(ShaderSource source) => throw new NotSupportedException();

        public VertexBuffer CreateVertexBuffer(VertexLayout layout, int capacity, string debugName = "VertexBuffer") => throw new NotSupportedException();

        public IndexBuffer CreateIndexBuffer(ReadOnlySpan<ushort> indices, string debugName = "IndexBuffer") => throw new NotSupportedException();

        public IndexBuffer CreateDynamicIndexBuffer(int capacity, string debugName = "DynamicIndexBuffer") => throw new NotSupportedException();

        public void SetRenderTarget(RenderTarget2D? target)
        {
        }

        public void SetViewport(int x, int y, int width, int height)
        {
        }

        public void Clear(Color color)
        {
        }

        public void Draw(in DrawCommand command) => Resources.Statistics.RecordDraw(command.Topology, command.IndexCount);

        public void ReadPixels(Span<byte> destination)
        {
        }

        public void Dispose()
        {
        }
    }
}

[CollectionDefinition(nameof(RuntimeStatics), DisableParallelization = true)]
public sealed class RuntimeStatics;
