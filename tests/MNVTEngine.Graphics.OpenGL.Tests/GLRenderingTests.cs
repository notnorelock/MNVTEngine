using Microsoft.Extensions.Logging;
using MNVTEngine.Mathematics;
using static MNVTEngine.Graphics.OpenGL.Tests.GpuHarness;

namespace MNVTEngine.Graphics.OpenGL.Tests;

/// <summary>Renders into a 64x64 target with a pixel-unit camera (world (0,0) = bottom-left) and reads pixels back.</summary>
public class GLRenderingTests
{
    private const int Size = 64;

    private static Camera2D PixelCamera()
    {
        var camera = new Camera2D(Size, Size) { Position = new Vector2(Size / 2f, Size / 2f) };
        camera.UsePixelUnits();
        return camera;
    }

    private static byte[] RenderToTarget(GLGraphicsDevice device, Renderer2D renderer, Color clear, Action<SpriteBatch> draw, SpriteSortMode sort = SpriteSortMode.Deferred, Material? material = null)
    {
        using var target = device.CreateRenderTarget(Size, Size, SamplerState.PointClamp);
        var batch = renderer.Begin(PixelCamera(), sort, material, target);
        device.Clear(clear);
        draw(batch);
        renderer.End();
        var pixels = new byte[Size * Size * 4];
        device.ReadPixels(pixels);
        device.SetRenderTarget(null);
        return pixels;
    }

    [Fact]
    public void Quad_LandsWhereTheCameraSays_YUp()
    {
        var logs = Run(device =>
        {
            using var renderer = new Renderer2D(device);
            var pixels = RenderToTarget(device, renderer, Color.Black, b =>
                b.FillRectangle(new Rect(0f, Size / 2f, Size, Size / 2f), Color.Red)); // top half in Y-up world

            AssertColor(Color.Red, Pixel(pixels, Size, 10, 5));        // near the top of the image
            AssertColor(Color.Black, Pixel(pixels, Size, 10, Size - 5)); // near the bottom
        });
        AssertNoGLErrors(logs);
    }

    [Fact]
    public void UploadedTexture_IsUpright()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            // 1x2 image: row 0 (top) red, row 1 (bottom) green.
            using var texture = device.CreateTexture(1, 2, [255, 0, 0, 255, 0, 255, 0, 255], SamplerState.PointClamp);
            var pixels = RenderToTarget(device, renderer, Color.Black, b =>
                b.Draw(texture, Vector2.Zero, new Vector2(Size, Size), Color.White, pivot: Vector2.Zero));

            AssertColor(Color.Red, Pixel(pixels, Size, 32, 5));
            AssertColor(Color.Green, Pixel(pixels, Size, 32, Size - 5));
        });
    }

    [Fact]
    public void RenderTarget_IsSampledUpright()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            using var source = device.CreateRenderTarget(Size, Size, SamplerState.PointClamp, "Source");
            renderer.Begin(PixelCamera(), target: source);
            device.Clear(Color.Black);
            renderer.Batch.FillRectangle(new Rect(0f, Size / 2f, Size, Size / 2f), Color.Blue);
            renderer.End();

            var pixels = RenderToTarget(device, renderer, Color.Black, b =>
                b.Draw(source, Vector2.Zero, new Vector2(Size, Size), Color.White, pivot: Vector2.Zero));

            AssertColor(Color.Blue, Pixel(pixels, Size, 32, 5));
            AssertColor(Color.Black, Pixel(pixels, Size, 32, Size - 5));
        });
    }

    [Fact]
    public void AlphaBlending_MixesWithBackground()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            var pixels = RenderToTarget(device, renderer, Color.Blue, b =>
                b.FillRectangle(new Rect(0f, 0f, Size, Size), new Color(1f, 1f, 1f, 0.5f)));
            var p = Pixel(pixels, Size, 32, 32);
            AssertColor(new Color(0.5f, 0.5f, 1f, p.A), p, 3f / 255f);
        });
    }

    [Fact]
    public void AdditiveMaterial_AddsColor()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            var additive = renderer.CreateSpriteMaterial(BlendMode.Additive, "Add");
            var pixels = RenderToTarget(device, renderer, new Color(0.25f, 0f, 0f, 1f), b =>
                b.FillRectangle(new Rect(0f, 0f, Size, Size), new Color(0.5f, 0.25f, 0f, 1f)), material: additive);
            AssertColor(new Color(0.75f, 0.25f, 0f, 1f), Pixel(pixels, Size, 32, 32), 3f / 255f);
        });
    }

    [Fact]
    public void BackToFront_NearestSpriteWins()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            var full = new Rect(0f, 0f, Size, Size);
            var pixels = RenderToTarget(device, renderer, Color.Black, b =>
            {
                b.FillRectangle(full, Color.Green, depth: 0f); // near, submitted first
                b.FillRectangle(full, Color.Red, depth: 10f);  // far, submitted last
            }, SpriteSortMode.BackToFront);
            AssertColor(Color.Green, Pixel(pixels, Size, 32, 32));
        });
    }

    [Fact]
    public void ShaderErrors_ReportStageAndLog()
    {
        Run(device =>
        {
            var broken = BuiltinShaders.Sprite with { Name = "Broken", FragmentGlsl = "#version 450 core\nvoid main() { this is not glsl; }" };
            var ex = Assert.Throws<ShaderCompilationException>(() => device.CreateShader(broken));
            Assert.Equal("fragment", ex.Stage);
            Assert.False(string.IsNullOrWhiteSpace(ex.Log));
            Assert.Equal(0, device.Resources.LiveCount);
        });
    }

    [Fact]
    public void DeviceDispose_ReleasesAndReportsLeaks()
    {
        var logs = Run(device => device.CreateTexture(1, 1, [0, 0, 0, 0], SamplerState.PointClamp, "Forgotten"));
        Assert.Contains(logs, e => e.Level == LogLevel.Warning && e.Message.Contains("Forgotten", StringComparison.Ordinal));
    }

    [Fact]
    public void Misuse_IsRejected()
    {
        Run(device =>
        {
            using var target = device.CreateRenderTarget(4, 4, SamplerState.PointClamp);
            Assert.Throws<ArgumentException>(() => device.CreateTexture(2, 2, new byte[3], SamplerState.PointClamp));
            Assert.Equal(1, device.Resources.LiveCount);

            using var renderer = new Renderer2D(device);
            renderer.Begin(PixelCamera(), target: target);
            renderer.Batch.Draw(target, Vector2.Zero, Vector2.One, Color.White);
            Assert.Throws<InvalidOperationException>(renderer.End);
        });
    }

    private static void AssertNoGLErrors(IReadOnlyList<Core.Logging.LogEntry> logs) =>
        Assert.DoesNotContain(logs, e => e.Level >= LogLevel.Error);
}
