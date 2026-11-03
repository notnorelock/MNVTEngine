using MNVTEngine.Mathematics;
using StbImageWriteSharp;

namespace MNVTEngine.Graphics.Tests;

public class CameraTests
{
    [Fact]
    public void ScreenCenter_IsCameraPosition()
    {
        var camera = new Camera2D(800, 600) { Position = new Vector2(10f, -20f), Zoom = 2f, Rotation = 30f };
        Assert.True(Vector2.Approximately(camera.Position, camera.ScreenToWorld(new Vector2(400f, 300f)), 1e-4f));
    }

    [Fact]
    public void PixelUnits_MapOneToOneWithYUp()
    {
        var camera = new Camera2D(800, 600);
        camera.UsePixelUnits();
        Assert.True(Vector2.Approximately(new Vector2(-400f, 300f), camera.ScreenToWorld(Vector2.Zero), 1e-3f));
        Assert.True(Vector2.Approximately(new Vector2(400f, -300f), camera.ScreenToWorld(new Vector2(800f, 600f)), 1e-3f));
        Assert.Equal(1f, camera.UnitsPerPixel);
    }

    [Fact]
    public void ScreenToWorld_And_WorldToScreen_AreInverse()
    {
        var camera = new Camera2D(1280, 720) { Position = new Vector2(3f, 4f), Zoom = 1.5f, Rotation = 45f };
        var screen = new Vector2(100f, 650f);
        Assert.True(Vector2.Approximately(screen, camera.WorldToScreen(camera.ScreenToWorld(screen)), 1e-2f));
    }

    [Fact]
    public void Zoom_ShrinksVisibleArea()
    {
        var camera = new Camera2D(800, 400) { OrthographicSize = 10f };
        Assert.Equal(new Vector2(20f, 10f), camera.HalfExtents);
        camera.Zoom = 2f;
        Assert.Equal(new Vector2(10f, 5f), camera.HalfExtents);
        Assert.Equal(Rect.FromMinMax(new Vector2(-10f, -5f), new Vector2(10f, 5f)), camera.VisibleWorldBounds);
    }

    [Fact]
    public void RotatedBounds_ContainAllCorners()
    {
        var camera = new Camera2D(800, 400) { OrthographicSize = 10f, Rotation = 30f };
        var bounds = camera.VisibleWorldBounds;
        foreach (var corner in new[] { Vector2.Zero, new Vector2(800f, 0f), new Vector2(0f, 400f), new Vector2(800f, 400f) })
        {
            var world = camera.ScreenToWorld(corner);
            Assert.True(world.X >= bounds.Min.X - 1e-3f && world.X <= bounds.Max.X + 1e-3f && world.Y >= bounds.Min.Y - 1e-3f && world.Y <= bounds.Max.Y + 1e-3f);
        }
    }

    [Fact]
    public void InvalidSettings_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Camera2D(0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Camera2D(100, 100) { Zoom = 0f });
    }
}

public class ResourceAndImageTests
{
    [Fact]
    public void Tracker_TracksAndReportsLeaks()
    {
        using var device = new FakeGraphicsDevice();
        var texture = device.CreateTexture(1, 1, new byte[4], SamplerState.PointClamp, "Leaky");
        var kept = device.CreateTexture(1, 1, new byte[4], SamplerState.PointClamp, "Kept");
        Assert.Equal(2, device.Resources.LiveCount);

        kept.Dispose();
        kept.Dispose(); // idempotent
        var leaked = device.Resources.DisposeAll();

        Assert.Same(texture, Assert.Single(leaked));
        Assert.True(texture.IsDisposed);
        Assert.Equal(0, device.Resources.LiveCount);
    }

    [Fact]
    public async Task Tracker_RejectsOtherThreads()
    {
        using var device = new FakeGraphicsDevice();
        var error = await Task.Run(() => Record.Exception(() => device.CreateTexture(1, 1, new byte[4], SamplerState.PointClamp)), TestContext.Current.CancellationToken);
        Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public void Material_RejectsReservedUniforms()
    {
        using var device = new FakeGraphicsDevice();
        var material = new Material(device.CreateShader(BuiltinShaders.Sprite));
        Assert.Throws<ArgumentException>(() => material.SetMatrix("uViewProjection", Matrix4x4.Identity));
        material.SetColor("uTint", Color.Red);
        Assert.Equal(UniformKind.Vector4, material.Uniforms["uTint"].Kind);
    }

    [Fact]
    public void Image_DecodesPngAsRgbaTopDown()
    {
        // 2x2: top row red, green; bottom row blue, transparent.
        byte[] pixels = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 0, 0, 0];
        using var stream = new MemoryStream();
        new ImageWriter().WritePng(pixels, 2, 2, ColorComponents.RedGreenBlueAlpha, stream);

        var image = Image.Decode(stream.ToArray(), "test.png");
        Assert.Equal(2, image.Width);
        Assert.Equal(pixels, image.Pixels);
    }

    [Fact]
    public void Image_CorruptDataThrowsWithName()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Image.Decode([1, 2, 3, 4], "broken.png"));
        Assert.Contains("broken.png", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Image_ValidatesPixelBufferSize() =>
        Assert.Throws<ArgumentException>(() => new Image(2, 2, new byte[15]));

    [Fact]
    public void Image_Solid()
    {
        var image = Image.Solid(2, 1, Color.Red);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 255, 0, 0, 255 }, image.Pixels);
    }
}
