using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Assets;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Graphics.Vulkan.ShaderCompilation;
using MNVTEngine.IO;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;

namespace MNVTEngine.Runtime.Tests;

/// <summary>
/// Texture streaming on a real device: proxy first, then the level the drawn size needs swapped into the same texture
/// (reduced, proxy only, full), released and streamed again, unloaded when unused.
/// </summary>
public sealed class TextureStreamingTests
{
    private static readonly Guid Big = Guid.Parse("57e40000-0000-4000-8000-000000000001");
    private static readonly Guid Small = Guid.Parse("57e40000-0000-4000-8000-000000000002");

    private static byte[] Png(int size, byte r, byte g, byte b)
    {
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (r, g, b, 255);
        }

        return Png(new Image(size, size, pixels));
    }

    private static byte[] Png(Image image)
    {
        using var stream = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WritePng(image.Pixels, image.Width, image.Height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
        return stream.ToArray();
    }

    /// <summary>What a build packages: the asset index, the textures, and the large one's proxy and reduced levels.</summary>
    private static VirtualFileSystem Content()
    {
        var big = Png(1024, 200, 40, 40);
        var files = new MemoryFileSystemProvider("game");
        files.Add(AssetDatabase.IndexEntry, System.Text.Encoding.UTF8.GetBytes($$"""
            { "schemaVersion": 1, "assets": [
              { "guid": "{{Big}}", "path": "Textures/big.png", "type": "Texture", "settings": {} },
              { "guid": "{{Small}}", "path": "Textures/small.png", "type": "Texture", "settings": {} } ] }
            """));
        files.Add("Assets/Textures/big.png", big, Big);
        files.Add("Assets/Textures/small.png", Png(32, 0, 0, 255), Small);
        var full = Image.Decode(big, "big.png");
        files.Add(AssetDatabase.ArtifactEntry(Big, TextureProxy.ArtifactName), TextureProxy.Create(full));
        Assert.Equal(2, TextureProxy.ReducedLevels(1024, 1024)); // 512 and 256; 128 would not save enough over the proxy
        for (var level = 1; level <= 2; level++)
        {
            files.Add(AssetDatabase.ArtifactEntry(Big, TextureProxy.LevelArtifactName(level)), Png(TextureProxy.Downsample(full, 1024 >> level, 1024 >> level)));
        }

        var vfs = new VirtualFileSystem();
        vfs.Mount(files);
        return vfs;
    }

    [Fact]
    public void LargeTextures_ShowTheirProxy_ThenTheFullImage_InTheSameTextureObject()
    {
        VulkanGraphicsDevice device;
        try
        {
            device = VulkanGraphicsDevice.CreateHeadless(256, 256, NullLogger.Instance, new VulkanDeviceOptions().WithShaderc());
        }
        catch (GraphicsInitializationException ex)
        {
            Assert.Skip($"No Vulkan 1.4 device available: {ex.Message}");
            throw;
        }

        using (device)
        using (var content = Content())
        {
            var assets = AssetDatabase.OpenPackaged(content);
            using var resources = new ResourceCache(device, assets, NullLogger.Instance);
            using var renderer = new Renderer2D(device);
            resources.EnableTextureStreaming(new TextureStreamingOptions(BudgetBytes: 0, UnusedFrames: 20));
            Assert.True(resources.CanStream(Big));
            Assert.False(resources.CanStream(Small)); // no proxy: small textures load whole

            var texture = resources.GetTexture(new AssetRef(Big));
            Assert.True(texture.TrackScreenScale);
            Assert.Equal((1024, 1024), (texture.Width, texture.Height)); // the logical size from the start: sprite sizes stay right
            Assert.Equal((64, 64), texture.ResidentSize);
            Assert.Equal((32, 32), resources.GetTexture(new AssetRef(Small)).ResidentSize);

            // The 256 x 256 camera shows one world unit per pixel.
            var camera = new Camera2D(256, 256);
            camera.UsePixelUnits();
            void Frames(int count, float? drawnSize)
            {
                for (var i = 0; i < count; i++)
                {
                    var batch = renderer.Begin(camera);
                    device.Clear(Color.Black);
                    if (drawnSize is { } size)
                    {
                        batch.Draw(texture, Vector2.Zero, new Vector2(size, size), Color.White);
                    }

                    renderer.End();
                    resources.Update();
                    device.Present();
                }
            }

            void DrawUntil(float size, (int, int) resident)
            {
                for (var i = 0; i < 400 && texture.ResidentSize != resident; i++)
                {
                    Frames(1, size);
                    Thread.Sleep(1);
                }

                Assert.Equal(resident, texture.ResidentSize);
            }

            // Drawn 256 pixels wide: a quarter of its resolution is enough.
            DrawUntil(256, (256, 256));
            Assert.Same(texture, resources.GetTexture(new AssetRef(Big)));
            var stats = resources.LoadStatistics;
            Assert.Equal((0, 0, 1), (stats.Proxies, stats.FullResolution, stats.Reduced));
            Assert.Equal(256L * 256 * 4, stats.FullBytes); // the default sampler has no mipmaps
            Assert.NotNull(stats.AverageLoad);
            Frames(3, 256);
            Assert.Equal(device.FrameNumber - 1, texture.LastDrawnFrame); // the device stamps every bind

            // Drawn 300 pixels wide: half resolution; 1000 pixels: the full image.
            DrawUntil(300, (512, 512));
            DrawUntil(1000, (1024, 1024));
            Assert.Equal(1, resources.LoadStatistics.FullResolution);

            // Drawn no larger than the proxy: the proxy alone, once the larger draws age out of the size window.
            DrawUntil(40, (64, 64));

            // Over the budget and not drawn for a while: back to the proxy.
            DrawUntil(256, (256, 256));
            resources.EnableTextureStreaming(new TextureStreamingOptions(BudgetBytes: 1, UnusedFrames: 20));
            Frames(60, null);
            Assert.Equal((64, 64), texture.ResidentSize);
            Assert.Equal(1, resources.LoadStatistics.Downgrades);

            // Drawn again with room in the budget: streamed in again.
            resources.EnableTextureStreaming(new TextureStreamingOptions(BudgetBytes: 0, UnusedFrames: 20));
            DrawUntil(1000, (1024, 1024));

            // Unused for long enough: unloaded; using it again loads a new texture.
            Frames(30, null);
            Assert.Equal(2, resources.UnloadUnused(20)); // both textures
            Assert.True(texture.IsDisposed);
            Assert.NotSame(texture, resources.GetTexture(new AssetRef(Big)));
            Frames(2, null);
        }
    }
}
