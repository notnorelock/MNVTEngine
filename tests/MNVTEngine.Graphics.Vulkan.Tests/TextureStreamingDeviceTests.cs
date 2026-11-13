using MNVTEngine.Mathematics;
using static MNVTEngine.Graphics.Vulkan.Tests.VulkanHarness;

namespace MNVTEngine.Graphics.Vulkan.Tests;

/// <summary>Streaming textures under validation: a proxy smaller than the logical size, replaced in place by full images of other sizes.</summary>
[Collection(nameof(VulkanGroup))]
public class TextureStreamingDeviceTests
{
    private static byte[] Solid(int size, Color color)
    {
        var pixels = new byte[size * size * 4];
        var (r, g, b, a) = ((byte)(color.R * 255), (byte)(color.G * 255), (byte)(color.B * 255), (byte)(color.A * 255));
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (r, g, b, a);
        }

        return pixels;
    }

    [Fact]
    public void ReplaceTexture_SwapsTheImage_KeepingTheTextureAndItsLogicalSize()
    {
        Run(device =>
        {
            Assert.True(device.SupportsTextureStreaming);
            using var renderer = new Renderer2D(device);
            using var texture = device.CreateStreamingTexture(32, 32, 2, 2, Solid(2, Color.Blue), SamplerState.LinearClamp with { GenerateMipmaps = true }, "Streamed");
            Assert.Equal((32, 32, (2, 2)), (texture.Width, texture.Height, texture.ResidentSize));

            Color Center() => Pixel(RenderToTarget(device, renderer, Color.Black, batch => batch.Draw(texture, Vector2.Zero, new Vector2(Size, Size), Color.White, pivot: Vector2.Zero)), Size, Size / 2, Size / 2);

            AssertColor(Color.Blue, Center());
            device.Present();
            device.ReplaceTexture(texture, 32, 32, Solid(32, Color.Red));
            Assert.Equal((32, 32), texture.ResidentSize);
            AssertColor(Color.Red, Center());
            Assert.Equal(device.FrameNumber, texture.LastDrawnFrame);
            device.Present();

            device.ReplaceTexture(texture, 2, 2, Solid(2, Color.Green)); // released back to a proxy
            AssertColor(Color.Green, Center());
            Assert.Throws<NotSupportedException>(() => device.UpdateTexture(texture, 0, 0, 1, 1, new byte[4]));
            for (var i = 0; i < 4; i++)
            {
                device.Present(); // the replaced images are destroyed once their frames are done
            }
        });
    }
}
