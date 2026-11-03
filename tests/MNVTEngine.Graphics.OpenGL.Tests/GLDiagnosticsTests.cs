using MNVTEngine.Mathematics;
using static MNVTEngine.Graphics.OpenGL.Tests.GpuHarness;

namespace MNVTEngine.Graphics.OpenGL.Tests;

/// <summary>Frame statistics, GPU timer queries and GPU memory on a real GL context.</summary>
[Collection(nameof(GpuGroup))]
public class GLDiagnosticsTests
{
    [Fact]
    public void Statistics_CountDrawsUploadsAndSwitches_PerFrame()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            using var other = device.CreateTexture(1, 1, [255, 255, 255, 255], SamplerState.PointClamp);
            device.Statistics.EndFrame(); // drop setup work

            var camera = new Camera2D(64, 64);
            var batch = renderer.Begin(camera);
            batch.FillRectangle(new Rect(0, 0, 1, 1), Color.Red);
            batch.Draw(other, Vector2.Zero, Vector2.One, Color.White); // second texture: second draw
            renderer.End();
            device.Statistics.EndFrame();

            var frame = device.Statistics.LastFrame;
            Assert.Equal(2, frame.DrawCalls);
            Assert.Equal(4, frame.Triangles);
            Assert.Equal(12, frame.Indices);
            Assert.Equal(8, frame.VerticesUploaded);
            Assert.True(frame.TextureSwitches >= 1);
            Assert.Equal(0, device.Statistics.Current.DrawCalls);

            var counts = device.Resources.Count();
            Assert.True(counts.Textures >= 2 && counts.Shaders >= 1);
            Assert.True(counts.TextureBytes >= 8);
        });
    }

    [Fact]
    public void GpuTimer_DeliversResultsLater_WithoutWaiting()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            using var target = device.CreateRenderTarget(64, 64, SamplerState.PointClamp);
            var timer = device.GpuTimer;
            Assert.True(double.IsNaN(timer.LastFrameMilliseconds));

            for (var frame = 0; frame < 20 && timer.LastFrameIndex < 0; frame++)
            {
                timer.BeginFrame();
                using (new GpuScope(timer, "Scene"))
                {
                    var batch = renderer.Begin(new Camera2D(64, 64), target: target);
                    device.Clear(Color.Black);
                    batch.FillRectangle(new Rect(-1, -1, 2, 2), Color.Red);
                    renderer.End();
                }

                timer.EndFrame();
                if (frame % 4 == 3)
                {
                    var pixels = new byte[64 * 64 * 4];
                    device.ReadPixels(pixels); // forces completion now and then, like a real frame's swap would
                }
            }

            Assert.True(timer.LastFrameIndex >= 0, $"No GPU timing after 20 frames ({timer.DroppedFrames} dropped).");
            Assert.True(timer.LastFrameMilliseconds >= 0 && timer.LastFrameMilliseconds < 1000);
            var scope = Assert.Single(timer.LastScopes);
            Assert.Equal("Scene", scope.Name);
            Assert.InRange(scope.Milliseconds, 0, timer.LastFrameMilliseconds + 1e-3);
        });
    }

    [Fact]
    public void GpuMemory_IsReportedOnlyWhenTheDriverExposesIt()
    {
        Run(device =>
        {
            var memory = device.QueryGpuMemory();
            if (!memory.Supported)
            {
                Assert.Null(memory.TotalBytes);
                Assert.Null(memory.UsedBytes);
                return;
            }

            Assert.NotNull(memory.AvailableBytes);
            Assert.True(memory.AvailableBytes > 0);
            if (memory.TotalBytes is { } total)
            {
                Assert.InRange(memory.UsedBytes!.Value, 0, total);
            }
        });
    }
}
