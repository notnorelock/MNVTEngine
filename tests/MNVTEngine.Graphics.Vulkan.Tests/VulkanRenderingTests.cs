using Microsoft.Extensions.Logging;
using MNVTEngine.Mathematics;
using static MNVTEngine.Graphics.Vulkan.Tests.VulkanHarness;

namespace MNVTEngine.Graphics.Vulkan.Tests;

/// <summary>The OpenGL backend's rendering contract, on Vulkan: 64x64 target, pixel camera (world (0,0) = bottom-left).</summary>
[Collection(nameof(VulkanGroup))]
public class VulkanRenderingTests
{
    [Fact]
    public void Quad_LandsWhereTheCameraSays_YUp()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            var pixels = RenderToTarget(device, renderer, Color.Black, b =>
                b.FillRectangle(new Rect(0f, Size / 2f, Size, Size / 2f), Color.Red)); // top half in Y-up world

            AssertColor(Color.Red, Pixel(pixels, Size, 10, 5));
            AssertColor(Color.Black, Pixel(pixels, Size, 10, Size - 5));
        });
    }

    [Fact]
    public void UploadedTexture_IsUpright()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            using var texture = device.CreateTexture(1, 2, [255, 0, 0, 255, 0, 255, 0, 255], SamplerState.PointClamp); // top red, bottom green
            var pixels = RenderToTarget(device, renderer, Color.Black, b =>
                b.Draw(texture, Vector2.Zero, new Vector2(Size, Size), Color.White, pivot: Vector2.Zero));

            AssertColor(Color.Red, Pixel(pixels, Size, 32, 5));
            AssertColor(Color.Green, Pixel(pixels, Size, 32, Size - 5));
        });
    }

    [Fact]
    public void RenderTarget_IsSampledUpright_InTheSameFrame()
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
    public void AdditiveAndMultiplyMaterials_Blend()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            var additive = renderer.CreateSpriteMaterial(BlendMode.Additive, "Add");
            var pixels = RenderToTarget(device, renderer, new Color(0.25f, 0f, 0f, 1f), b =>
                b.FillRectangle(new Rect(0f, 0f, Size, Size), new Color(0.5f, 0.25f, 0f, 1f)), material: additive);
            AssertColor(new Color(0.75f, 0.25f, 0f, 1f), Pixel(pixels, Size, 32, 32), 3f / 255f);

            var multiply = renderer.CreateSpriteMaterial(BlendMode.Multiply, "Mul");
            pixels = RenderToTarget(device, renderer, new Color(0.5f, 1f, 1f, 1f), b =>
                b.FillRectangle(new Rect(0f, 0f, Size, Size), new Color(1f, 0.5f, 0f, 1f)), material: multiply);
            AssertColor(new Color(0.5f, 0.5f, 0f, 1f), Pixel(pixels, Size, 32, 32), 3f / 255f);
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
                b.FillRectangle(full, Color.Green, depth: 0f);
                b.FillRectangle(full, Color.Red, depth: 10f);
            }, SpriteSortMode.BackToFront);
            AssertColor(Color.Green, Pixel(pixels, Size, 32, 32));
        });
    }

    [Fact]
    public void Scissor_ClipsAndDynamicIndicesUpload()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            using var vertices = device.CreateVertexBuffer(SpriteVertex.Layout, 4);
            using var indices = device.CreateDynamicIndexBuffer(6);
            using var target = device.CreateRenderTarget(Size, Size, SamplerState.PointClamp);
            var white = Color.White.ToRgba32();
            vertices.SetData<SpriteVertex>(
            [
                new(new Vector2(0f, 0f), Vector2.Zero, white),
                new(new Vector2(Size, 0f), Vector2.Zero, white),
                new(new Vector2(Size, Size), Vector2.Zero, white),
                new(new Vector2(0f, Size), Vector2.Zero, white),
            ]);
            indices.SetData([0, 1, 2, 2, 3, 0]);

            device.SetRenderTarget(target);
            device.Clear(Color.Black);
            var command = new DrawCommand(renderer.DefaultMaterial, renderer.Batch.WhitePixel, vertices, indices, 0, 6,
                PixelCamera().ViewProjection, Scissor: new Rect(0f, 0f, Size / 2f, Size / 2f));
            device.Draw(in command);

            var pixels = new byte[Size * Size * 4];
            device.ReadPixels(pixels);
            device.SetRenderTarget(null);
            AssertColor(Color.White, Pixel(pixels, Size, 5, Size - 5)); // bottom-left quarter
            AssertColor(Color.Black, Pixel(pixels, Size, Size - 5, 5));
            AssertColor(Color.Black, Pixel(pixels, Size, 5, 5));

            using var immutable = device.CreateIndexBuffer([0, 1, 2]);
            Assert.Throws<InvalidOperationException>(() => immutable.SetData([0]));
        });
    }

    [Fact]
    public void ViewportAndBackBuffer_ReadBackTopDown()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            device.SetRenderTarget(null);
            device.Clear(Color.Black);
            var camera = PixelCamera(Size / 2);
            device.SetViewport(0, 0, Size / 2, Size / 2); // bottom-left quarter of the back buffer
            renderer.Batch.Begin(camera.ViewProjection);
            renderer.Batch.FillRectangle(new Rect(0, 0, Size / 2f, Size / 2f), Color.Yellow);
            renderer.Batch.End();

            var pixels = new byte[Size * Size * 4];
            device.ReadPixels(pixels);
            AssertColor(Color.Yellow, Pixel(pixels, Size, 5, Size - 5));
            AssertColor(Color.Black, Pixel(pixels, Size, Size - 5, 5));
            device.Present();
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
            Assert.Contains("Broken", ex.Log, StringComparison.Ordinal);
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
}
