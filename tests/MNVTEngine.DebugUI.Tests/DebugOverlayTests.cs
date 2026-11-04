using ImGuiNET;
using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Core.Timing;
using MNVTEngine.Diagnostics;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.OpenGL;
using MNVTEngine.Mathematics;
using MNVTEngine.Platform;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using Debug = MNVTEngine.Diagnostics.Debug;

namespace MNVTEngine.DebugUI.Tests;

/// <summary>The in-game overlay on a real GL context: ImGui context isolation, shortcuts, input capture, rendering.</summary>
[Collection(nameof(OverlayGroup))]
public sealed class DebugOverlayTests
{
    private const int Width = 320;
    private const int Height = 200;

    /// <summary>A hidden window with the overlay of a tiny scene; the body drives frames itself.</summary>
    /// <param name="view">Logical size the overlay lays out for (defaults to the window); larger than the window is fine.</param>
    private static void Run(Action<Harness> body, Action<DiagnosticsSettings>? configure = null, (int Width, int Height)? view = null)
    {
        GameWindow window;
        try
        {
            window = new GameWindow(new GameWindowOptions { Hidden = true, Width = Width, Height = Height, VSync = false });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Assert.Skip($"No window system available: {ex.Message}");
            throw;
        }

        using (window)
        {
            var loop = new Loop(body, configure, view ?? (Width, Height));
            try
            {
                window.Run(loop);
            }
            catch (Exception ex) when (!loop.Loaded && ex is not OutOfMemoryException)
            {
                Assert.Skip($"No OpenGL 4.5 context available: {ex.Message}");
            }
        }
    }

    [Fact]
    public void Overlay_KeepsItsOwnImGuiContext_AndRestoresTheCallers()
    {
        Run(h =>
        {
            var editor = ImGui.CreateContext();
            try
            {
                ImGui.SetCurrentContext(editor);
                h.Frame();
                Assert.Equal(editor, ImGui.GetCurrentContext());
                h.Overlay.Dispose();
                Assert.Equal(editor, ImGui.GetCurrentContext());
            }
            finally
            {
                ImGui.DestroyContext(editor);
            }
        });
    }

    [Fact]
    public void F1_TogglesTheHud_AndTheShortcutNeverReachesTheGame()
    {
        Run(h =>
        {
            Assert.False(h.Overlay.HudVisible);
            var hidden = h.Frame();
            GpuAssert.Near(Color.Black, Pixel(hidden, 30, 30));

            h.Input.BeginFrame();
            h.Input.SetKey(Key.F1, true);
            h.Input.SetKey(Key.D, true);
            h.Frame(newInputFrame: false);
            Assert.True(h.Overlay.HudVisible);
            Assert.False(h.Input.WasKeyPressed(Key.F1));
            Assert.True(h.Input.IsKeyDown(Key.D)); // the HUD takes no input
            var shown = h.Frame(); // auto-sized windows appear from their second frame
            Assert.True(BrightestIn(shown, 0, 0, 160, 60) > 0.5f, "HUD text expected in the top-left corner");
            Assert.True(BrightestIn(hidden, 0, 0, 160, 60) < 0.01f);
        });
    }

    [Fact]
    public void Console_CapturesTheKeyboardWhileOpen_AndGivesItBackWhenClosed()
    {
        Run(h =>
        {
            h.Diagnostics.Commands.Execute("window console on");
            for (var i = 0; i < 5 && !h.Input.KeyboardCaptured; i++)
            {
                h.Frame();
            }

            Assert.True(h.Input.KeyboardCaptured, "The console's input field should take the keyboard.");
            h.Input.BeginFrame();
            h.Input.SetKey(Key.A, true);
            h.Frame(newInputFrame: false);
            Assert.False(h.Input.IsKeyDown(Key.A));

            h.Diagnostics.Commands.Execute("window console off");
            h.Frame();
            Assert.False(h.Input.KeyboardCaptured);
            Assert.True(h.Input.IsKeyDown(Key.A));
        });
    }

    [Fact]
    public void DebugDraws_RenderInScreenAndWorldSpace_AndCanBeSwitchedOff()
    {
        Run(h =>
        {
            Debug.DrawRect(new Rect(10, 10, 30, 30), Color.Red, filled: true, space: DebugSpace.Screen);
            Debug.DrawCircle(Vector2.Zero, 10f, Color.Green, filled: true); // world origin = view center (pixel camera)
            var pixels = h.Frame();
            GpuAssert.Near(Color.Red, Pixel(pixels, 20, 20));
            GpuAssert.Near(Color.Green, Pixel(pixels, Width / 2, Height / 2));

            Debug.DrawingEnabled = false;
            try
            {
                Debug.DrawRect(new Rect(10, 10, 30, 30), Color.Red, filled: true, space: DebugSpace.Screen);
                GpuAssert.Near(Color.Black, Pixel(h.Frame(), 20, 20));
            }
            finally
            {
                Debug.DrawingEnabled = true;
            }
        });
    }

    [Fact]
    public void GameRendering_IsUnaffectedByTheOverlay()
    {
        Run(h =>
        {
            h.Overlay.HudVisible = true;
            h.Diagnostics.Commands.Execute("window profiler on");
            h.Frame();

            // The next game frame draws a full-screen quad: the overlay must not leave scissor, target or blend behind.
            var batch = h.Renderer.Begin(h.Camera);
            batch.FillRectangle(new Rect(-Width, -Height, Width * 2, Height * 2), Color.Blue);
            h.Renderer.End();
            var pixels = h.Read();
            GpuAssert.Near(Color.Blue, Pixel(pixels, 30, 30));
            GpuAssert.Near(Color.Blue, Pixel(pixels, Width - 10, Height - 10));
        }, s => s.ShowOverlayByDefault = true);
    }

    [Fact]
    public void EveryWindow_Draws_WithWarningsAndPercentSignsInTheText()
    {
        Run(h =>
        {
            h.Overlay.HudVisible = true;
            foreach (var panel in h.Overlay.Panels)
            {
                panel.Open = true;
            }

            Debug.LogWarning("100% sure: %s %d %n must print verbatim");
            for (var i = 0; i < 6; i++)
            {
                Debug.DrawText("50% done %s", new Vector2(5, 5), Color.White);
                h.Frame();
            }

            Assert.All(h.Overlay.Panels, p => Assert.True(p.Open));
        }, s => s.Budgets.MaxEntities = 0.001, view: (1280, 720));
    }

    [Fact]
    public void BudgetsWindow_Draws_InALargeView_BeforeAnyBudgetWasEvaluated()
    {
        // Regression: ImGui.InputDouble in this window hung inside Dear ImGui with a 1280x720 view.
        Run(h =>
        {
            h.Diagnostics.Commands.Execute("window budgets on");
            for (var i = 0; i < 4; i++)
            {
                h.Frame();
            }
        }, s => s.WarmupFrames = 1000, view: (1280, 720));
    }

    private static float BrightestIn(byte[] rgba, int x0, int y0, int x1, int y1)
    {
        var max = 0f;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var c = Pixel(rgba, x, y);
                max = MathF.Max(max, MathF.Max(c.R, MathF.Max(c.G, c.B)));
            }
        }

        return max;
    }

    private static Color Pixel(byte[] rgba, int x, int y)
    {
        var i = ((y * Width) + x) * 4;
        return Color.FromBytes(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    public sealed class Harness(GLGraphicsDevice device, Renderer2D renderer, RuntimeDiagnostics diagnostics, DebugOverlay overlay, InputState input, Scene scene)
    {
        private long _frame;

        public GLGraphicsDevice Device => device;

        public Renderer2D Renderer => renderer;

        public RuntimeDiagnostics Diagnostics => diagnostics;

        public DebugOverlay Overlay => overlay;

        public InputState Input => input;

        public Camera2D Camera { get; } = CreateCamera();

        /// <summary>One game frame: overlay input, (empty) scene, black clear, overlay render; returns the back buffer.</summary>
        public byte[] Frame(bool newInputFrame = true)
        {
            if (newInputFrame)
            {
                input.BeginFrame();
            }

            _frame++;
            diagnostics.BeginFrame();
            overlay.BeginFrame();
            var time = new FrameTime(0.016f, 0.016f, _frame * 0.016, _frame * 0.016, _frame);
            scene.Update(time);
            device.SetRenderTarget(null);
            device.Clear(Color.Black);
            diagnostics.SceneRendered();
            overlay.Render(Camera, null);
            var pixels = Read();
            diagnostics.EndFrame(time);
            return pixels;
        }

        public byte[] Read()
        {
            device.SetRenderTarget(null);
            var pixels = new byte[Width * Height * 4];
            device.ReadPixels(pixels);
            return pixels;
        }

        private static Camera2D CreateCamera()
        {
            var camera = new Camera2D(Width, Height);
            camera.UsePixelUnits();
            return camera;
        }
    }

    private sealed class Loop(Action<Harness> body, Action<DiagnosticsSettings>? configure, (int Width, int Height) view) : IGameLoop
    {
        public bool Loaded { get; private set; }

        public void Load(GameWindow window)
        {
            var device = GLGraphicsDevice.Create(window.GraphicsContext, window.FramebufferWidth, window.FramebufferHeight, NullLogger.Instance);
            Loaded = true;
            var renderer = new Renderer2D(device);
            var settings = new DiagnosticsSettings { WarmupFrames = 0 };
            configure?.Invoke(settings);
            var scene = new Scene("Test");
            scene.Start();
            var input = new InputState();
            var diagnostics = new RuntimeDiagnostics(settings, NullLogger.Instance, Path.GetTempPath());
            diagnostics.Attach(new DiagnosticsTarget(device, () => scene));
            diagnostics.Install();
            var overlay = new DebugOverlay(NullLogger.Instance);
            try
            {
                overlay.Load(new RuntimeOverlayContext(device, renderer, diagnostics, input, () => new OverlayDisplay(view.Width, view.Height, 1f), () => scene));
                body(new Harness(device, renderer, diagnostics, overlay, input, scene));
            }
            finally
            {
                overlay.Dispose();
                diagnostics.Uninstall();
                scene.Unload();
                renderer.Dispose();
                device.Dispose();
                window.Close();
            }
        }

        public void FixedUpdate(float fixedDeltaTime)
        {
        }

        public void Update(FrameTime time)
        {
        }

        public void Render(FrameTime time, float interpolation)
        {
        }

        public void Resize(int width, int height)
        {
        }

        public void Unload()
        {
        }
    }
}

internal static class GpuAssert
{
    public static void Near(Color expected, Color actual, float tolerance = 3f / 255f) =>
        Assert.True(
            MathF.Abs(expected.R - actual.R) <= tolerance && MathF.Abs(expected.G - actual.G) <= tolerance && MathF.Abs(expected.B - actual.B) <= tolerance,
            $"Expected {expected}, got {actual}");
}

[CollectionDefinition(nameof(OverlayGroup), DisableParallelization = true)]
public sealed class OverlayGroup;
