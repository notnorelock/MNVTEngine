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
        Assert.Equal(MetricStatus.Unsupported, metrics.Get(DiagnosticMetrics.GpuAllocated).Status);    // the stub has no GPU allocator
        Assert.Equal(MetricStatus.Estimated, metrics.Get(DiagnosticMetrics.TextureMemory).Status);     // ...so texture memory is only estimated
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
    public void GameUI_IsMeasured_WhenTheSceneHasOne()
    {
        var diagnostics = Create();
        Frame(diagnostics);
        Assert.Equal(MetricStatus.Unavailable, diagnostics.Metrics.Get(DiagnosticMetrics.UIElements).Status); // no UI module: not 0

        ModuleIntegration.AddDiagnostics([new UI.UIModule()], diagnostics.Collector, diagnostics.Commands, () => _scene);
        Frame(diagnostics);
        Assert.Equal(MetricStatus.Unavailable, diagnostics.Metrics.Get(DiagnosticMetrics.UIElements).Status); // a scene without UI: still not 0

        var system = UI.UISystem.Attach(_scene, new UI.UIServices(Graphics.Text.Font.LoadDefault(), null, null, null));
        var context = system.CreateContext();
        var panel = context.Root.AddChild(new UI.Widgets.Panel { Layout = UI.LayoutMode.Column });
        panel.AddChild(new UI.Widgets.Button { Text = "Play" });
        panel.AddChild(new UI.Widgets.Label { Text = "Hidden", Visible = false });
        system.Update(new InputState(), new Vector2(640, 360), 1f, 0.016f);
        Frame(diagnostics);

        Assert.Equal(context.Statistics.Elements, diagnostics.Metrics.Get(DiagnosticMetrics.UIElements).Value);
        Assert.Equal(5, diagnostics.Metrics.Get(DiagnosticMetrics.UIElements).Value); // root, overlay, panel, button, hidden label
        Assert.True(diagnostics.Metrics.Get(DiagnosticMetrics.UILayoutPasses).Value >= 1);
        Assert.Equal(MetricStatus.Unavailable, diagnostics.Metrics.Get(DiagnosticMetrics.UIGlyphs).Status); // no renderer attached
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

    [Fact]
    public void WindowSettings_LoadModesIconAndLimits_AndRejectBadValues()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Scenes"));
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "Icons"));
        File.WriteAllText(Path.Combine(_root, "Assets", "Scenes", "Main.mnvt"), """{ "schemaVersion": 2, "gameObjects": [] }""");
        File.WriteAllBytes(Path.Combine(_root, "Assets", "Icons", "game.png"), [1]);
        var file = Path.Combine(_root, "Game.mnvtproj");
        File.WriteAllText(file, """
            { "schemaVersion": 1, "name": "Game", "startupScene": "Scenes/Main.mnvt",
              "window": { "mode": "borderlessFullscreen", "icon": "Icons/game.png", "minWidth": 640, "minHeight": 360, "x": 10, "y": 20, "topMost": true } }
            """);
        var window = GameProject.Load(file).Window;
        Assert.Equal((WindowMode.BorderlessFullscreen, "Icons/game.png", 640, 360, 10, 20, true), (window.Mode, window.Icon, window.MinWidth, window.MinHeight, window.X, window.Y, window.TopMost));

        File.WriteAllText(file, """{ "schemaVersion": 1, "name": "Game", "startupScene": "Scenes/Main.mnvt", "window": { "icon": "Icons/missing.png", "minWidth": 900, "maxWidth": 800, "x": 5 } }""");
        var ex = Assert.Throws<Serialization.SerializationException>(() => GameProject.Load(file));
        Assert.Contains("window.icon", ex.Diagnostics.Select(d => d.Path));
        Assert.Contains("Icons/missing.png", ex.Message, StringComparison.Ordinal);
        Assert.Contains("maximum", ex.Message, StringComparison.Ordinal);
        Assert.Contains("window.y", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowIcons_AreScaledToStandardSizes_WithAlphaWeightedFiltering()
    {
        var pixels = new byte[64 * 64 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 3]) = (255, (byte)(i / 4 % 2 == 0 ? 255 : 0)); // red, every other pixel transparent
        }

        var icons = RuntimeWindow.IconsFrom(new Graphics.Image(64, 64, pixels));
        Assert.Equal([64, 16, 24, 32, 48], icons.Select(i => i.Width));
        var small = icons.Single(i => i.Width == 16);
        Assert.Equal(255, small.Rgba[0]);   // transparent neighbours do not darken the color
        Assert.InRange(small.Rgba[3], 120, 135); // half coverage
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

        public RenderTarget2D CreateRenderTarget(int width, int height, SamplerState sampler, string debugName = "RenderTarget", bool depth = false) => throw new NotSupportedException();

        public Shader CreateShader(ShaderSource source) => throw new NotSupportedException();

        public VertexBuffer CreateVertexBuffer(VertexLayout layout, int capacity, string debugName = "VertexBuffer") => throw new NotSupportedException();

        public IndexBuffer CreateIndexBuffer(ReadOnlySpan<ushort> indices, string debugName = "IndexBuffer") => throw new NotSupportedException();

        public IndexBuffer CreateDynamicIndexBuffer(int capacity, string debugName = "DynamicIndexBuffer") => throw new NotSupportedException();

        public VertexBuffer CreateStaticVertexBuffer(VertexLayout layout, ReadOnlySpan<byte> vertices, string debugName = "StaticVertexBuffer") => throw new NotSupportedException();

        public IndexBuffer CreateIndexBuffer(ReadOnlySpan<uint> indices, string debugName = "IndexBuffer") => throw new NotSupportedException();

        public void SetRenderTarget(RenderTarget2D? target)
        {
        }

        public void SetViewport(int x, int y, int width, int height)
        {
        }

        public void Clear(Color color)
        {
        }

        public void ClearDepth(float depth = 1f)
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
