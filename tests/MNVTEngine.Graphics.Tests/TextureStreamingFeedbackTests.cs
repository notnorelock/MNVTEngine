using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Tests;

/// <summary>What texture streaming learns from renderers: drawn sizes, UV density, and the levels a build stores.</summary>
public sealed class TextureStreamingFeedbackTests
{
    [Fact]
    public void RequiredScale_KeepsTheLargestRecentReport_AndForgetsOldOnes()
    {
        using var device = new FakeGraphicsDevice();
        var texture = device.CreateTexture(512, 512, new byte[512 * 512 * 4], SamplerState.LinearClamp);
        Assert.Null(texture.RequiredScale(0)); // never reported

        texture.ReportScreenScale(0.5f, 10);
        texture.ReportScreenScale(0.1f, 11);
        Assert.Equal(0.5f, texture.RequiredScale(11));
        texture.ReportScreenScale(0.1f, Texture2D.ScreenScaleWindow + 5); // the next window still remembers the last one
        Assert.Equal(0.5f, texture.RequiredScale(Texture2D.ScreenScaleWindow + 5));
        texture.ReportScreenScale(0.1f, (2 * Texture2D.ScreenScaleWindow) + 5);
        Assert.Equal(0.1f, texture.RequiredScale((2 * Texture2D.ScreenScaleWindow) + 5));
        Assert.Null(texture.RequiredScale(10 * Texture2D.ScreenScaleWindow)); // stale: drawn without reports means full size
    }

    [Fact]
    public void UVDensity_IsUVUnitsPerObjectUnit()
    {
        Assert.Equal(0.25f, GpuMesh.UVDensityOf(MeshPrimitives.Quad(4f, 4f)), 4);
        Assert.Equal(0f, GpuMesh.UVDensityOf(new Mesh { Positions = [Vector3.Zero, Vector3.Right, Vector3.Up] })); // no UVs: unknown
    }

    [Theory]
    [InlineData(128, 128, 0)]
    [InlineData(129, 64, 0)] // streamed, but half of it would not beat the proxy by much
    [InlineData(512, 512, 1)]
    [InlineData(2048, 1024, 3)]
    public void ReducedLevels_StopAboveTwiceTheProxySize(int width, int height, int levels) =>
        Assert.Equal(levels, TextureProxy.ReducedLevels(width, height));

    [Fact]
    public void Downsample_AveragesWithoutColorFringesFromTransparentTexels()
    {
        var image = new Image(2, 1, [255, 0, 0, 255, 0, 255, 0, 0]); // opaque red next to transparent green
        var half = TextureProxy.Downsample(image, 1, 1);
        Assert.Equal(new byte[] { 255, 0, 0, 127 }, half.Pixels);
    }
}
