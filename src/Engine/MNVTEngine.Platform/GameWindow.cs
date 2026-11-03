using System.Diagnostics;
using MNVTEngine.Core.Timing;
using MNVTEngine.Platform.Input;
using Silk.NET.Core.Contexts;
using Silk.NET.Input;
using Silk.NET.Input.Glfw;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;

namespace MNVTEngine.Platform;

public sealed record GameWindowOptions
{
    public string Title { get; init; } = "MNVTEngine";
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public bool VSync { get; init; } = true;
    public bool Resizable { get; init; } = true;

    /// <summary>Requests a debug GL context so driver errors are logged. Costs some performance.</summary>
    public bool DebugContext { get; init; }

    /// <summary>Starts hidden; for automated runs that only read back pixels.</summary>
    public bool Hidden { get; init; }

    public float FixedTimestep { get; init; } = 1f / 60f;
}

/// <summary>Callbacks driven by <see cref="GameWindow.Run"/>.</summary>
public interface IGameLoop
{
    /// <summary>The GL context is current; create graphics resources here.</summary>
    void Load(GameWindow window);

    /// <summary>Zero or more times per frame at the fixed timestep. Physics and deterministic simulation.</summary>
    void FixedUpdate(float fixedDeltaTime);

    void Update(FrameTime time);

    /// <param name="interpolation">Fraction of a fixed step elapsed since the last <see cref="FixedUpdate"/>, for smoothing.</param>
    void Render(FrameTime time, float interpolation);

    /// <summary>Framebuffer size changed (pixels).</summary>
    void Resize(int width, int height);

    /// <summary>Release graphics resources here; the context is still current.</summary>
    void Unload();
}

/// <summary>
/// Native window with an OpenGL 4.5 core context (GLFW via Silk.NET) and the engine frame loop:
/// pump events, advance <see cref="Clock"/>, run fixed steps, update, render, swap.
/// </summary>
public sealed class GameWindow : IDisposable
{
    private readonly IWindow _window;
    private readonly GameWindowOptions _options;
    private SilkInputBridge? _input;
    private bool _running;

    public GameWindow(GameWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        // Explicit registration instead of reflection-based discovery, so trimming and single-file publish keep working.
        GlfwWindowing.RegisterPlatform();
        GlfwInput.RegisterPlatform();

        var flags = ContextFlags.ForwardCompatible | (options.DebugContext ? ContextFlags.Debug : ContextFlags.Default);
        var silkOptions = WindowOptions.Default with
        {
            Title = options.Title,
            Size = new Vector2D<int>(options.Width, options.Height),
            VSync = options.VSync,
            WindowBorder = options.Resizable ? WindowBorder.Resizable : WindowBorder.Fixed,
            IsVisible = !options.Hidden,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, flags, new APIVersion(4, 5)),
            ShouldSwapAutomatically = false,
            IsEventDriven = false,
        };

        _window = Window.Create(silkOptions);
        Clock = new GameClock();
        FixedStep = new FixedTimestep(options.FixedTimestep);
    }

    public InputState Input { get; } = new();

    public GameClock Clock { get; }

    public FixedTimestep FixedStep { get; }

    /// <summary>The window's GL context; pass to the graphics backend.</summary>
    public IGLContextSource GraphicsContext => _window;

    public int FramebufferWidth => _window.FramebufferSize.X;

    public int FramebufferHeight => _window.FramebufferSize.Y;

    public string Title
    {
        get => _window.Title;
        set => _window.Title = value;
    }

    /// <summary>Requests the loop to exit after the current frame.</summary>
    public void Close() => _window.Close();

    public void Run(IGameLoop game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (_running)
        {
            throw new InvalidOperationException("The window is already running.");
        }

        _running = true;
        _window.Initialize();
        _input = new SilkInputBridge(_window.CreateInput(), Input);
        _window.FocusChanged += focused =>
        {
            if (!focused)
            {
                Input.ReleaseAll();
            }
        };
        _window.FramebufferResize += size =>
        {
            if (size.X > 0 && size.Y > 0)
            {
                game.Resize(size.X, size.Y);
            }
        };

        game.Load(this);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var last = stopwatch.Elapsed;
            while (!_window.IsClosing)
            {
                Input.BeginFrame();
                _window.DoEvents();
                if (_window.IsClosing)
                {
                    break;
                }

                var now = stopwatch.Elapsed;
                var time = Clock.Advance((now - last).TotalSeconds);
                last = now;

                var steps = FixedStep.Accumulate(time.DeltaTime);
                for (var i = 0; i < steps; i++)
                {
                    game.FixedUpdate(FixedStep.StepSeconds);
                }

                game.Update(time);

                if (FramebufferWidth == 0 || FramebufferHeight == 0)
                {
                    // Minimized: nothing to present, avoid spinning a core.
                    Thread.Sleep(16);
                    continue;
                }

                game.Render(time, FixedStep.Alpha);
                _window.SwapBuffers();
            }
        }
        finally
        {
            game.Unload();
            _running = false;
        }
    }

    public void Dispose()
    {
        _input?.Dispose();
        _window.Dispose();
    }
}
