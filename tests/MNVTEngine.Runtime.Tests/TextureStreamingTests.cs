using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Assets;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Graphics.Vulkan.ShaderCompilation;
using MNVTEngine.IO;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;

namespace MNVTEngine.Runtime.Tests;

/// <summary>Texture streaming on a real device: proxy first, full image swapped into the same texture, released and streamed again, unloaded when unused.</summary>
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

        using var stream = new MemoryStream();
        new StbImageWriteSharp.ImageWriter().WritePng(pixels, size, size, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);
        return stream.ToArray();
    }

    /// <summary>What a build packages: the asset index, the textures, and the large one's proxy.</summary>
    private static VirtualFileSystem Content()
    {
        var big = Png(512, 200, 40, 40);
        var files = new MemoryFileSystemProvider("game");
        files.Add(AssetDatabase.IndexEntry, System.Text.Encoding.UTF8.GetBytes($$"""
            { "schemaVersion": 1, "assets": [
              { "guid": "{{Big}}", "path": "Textures/big.png", "type": "Texture", "settings": {} },
              { "guid": "{{Small}}", "path": "Textures/small.png", "type": "Texture", "settings": {} } ] }
            """));
        files.Add("Assets/Textures/big.png", big, Big);
        files.Add("Assets/Textures/small.png", Png(32, 0, 0, 255), Small);
        files.Add(AssetDatabase.ArtifactEntry(Big, TextureProxy.ArtifactName), TextureProxy.Create(Image.Decode(big, "big.png")));
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
            device = VulkanGraphicsDevice.CreateHeadless(16, 16, NullLogger.Instance, new VulkanDeviceOptions().WithShaderc());
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
            Assert.Equal((512, 512), (texture.Width, texture.Height)); // the logical size from the start: sprite sizes stay right
            Assert.Equal((64, 64), texture.ResidentSize);
            Assert.Equal((32, 32), resources.GetTexture(new AssetRef(Small)).ResidentSize);

            void Frames(int count, bool draw)
            {
                for (var i = 0; i < count; i++)
                {
                    var batch = renderer.Begin(new Camera2D(16, 16));
                    device.Clear(Color.Black);
                    if (draw)
                    {
                        batch.Draw(texture, Vector2.Zero, new Vector2(16, 16), Color.White);
                    }

                    renderer.End();
                    resources.Update();
                    device.Present();
                }
            }

            for (var i = 0; i < 300 && resources.LoadStatistics.FullResolution == 0; i++)
            {
                Frames(1, draw: true);
                Thread.Sleep(1);
            }

            Assert.Same(texture, resources.GetTexture(new AssetRef(Big)));
            Assert.Equal((512, 512), texture.ResidentSize);
            Frames(3, draw: true);
            Assert.Equal(device.FrameNumber - 1, texture.LastDrawnFrame); // the device stamps every bind
            Assert.NotNull(resources.LoadStatistics.AverageLoad);

            // Over the budget and not drawn for a while: back to the proxy.
            resources.EnableTextureStreaming(new TextureStreamingOptions(BudgetBytes: 1, UnusedFrames: 20));
            Frames(60, draw: false);
            Assert.Equal((64, 64), texture.ResidentSize);
            Assert.Equal(1, resources.LoadStatistics.Downgrades);

            // Drawn again with room in the budget: streamed in again.
            resources.EnableTextureStreaming(new TextureStreamingOptions(BudgetBytes: 0, UnusedFrames: 20));
            for (var i = 0; i < 300 && texture.ResidentSize != (512, 512); i++)
            {
                Frames(1, draw: true);
                Thread.Sleep(1);
            }

            Assert.Equal((512, 512), texture.ResidentSize);

            // Unused for long enough: unloaded; using it again loads a new texture.
            Frames(30, draw: false);
            Assert.Equal(2, resources.UnloadUnused(20)); // both textures
            Assert.True(texture.IsDisposed);
            Assert.NotSame(texture, resources.GetTexture(new AssetRef(Big)));
            Frames(2, draw: false);
        }
    }
}
