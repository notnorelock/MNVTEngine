using MNVTEngine.Mathematics;
using static MNVTEngine.Graphics.Vulkan.Tests.VulkanHarness;

namespace MNVTEngine.Graphics.Vulkan.Tests;

/// <summary>Vulkan-specific behaviour: version, frames in flight, deferred destruction, caches, materials, diagnostics.</summary>
[Collection(nameof(VulkanGroup))]
public class VulkanDeviceTests
{
    private const string TintShader = """
        #version 450 core
        in vec4 vColor;
        in vec2 vTexCoord;
        layout(binding = 0) uniform sampler2D uTexture;
        uniform vec4 uTint;
        uniform float uStrength;
        out vec4 fragColor;
        void main() { fragColor = vec4(uTint.rgb * uStrength, 1.0) + 0.0 * texture(uTexture, vTexCoord) * vColor; }
        """;

    [Fact]
    public void Device_RunsVulkan14_AndDescribesItself()
    {
        Run(device =>
        {
            Assert.StartsWith("Vulkan 1.4", device.Description, StringComparison.Ordinal);
            Assert.StartsWith("headless 64x64", device.SwapchainDescription, StringComparison.Ordinal);

            IGraphicsDevice abstraction = device; // diagnostics see the backend only through the interface
            var details = abstraction.DescribeBackend().ToDictionary(e => e.Key, e => e.Value);
            Assert.StartsWith("Vulkan 1.4", details["API"], StringComparison.Ordinal);
            Assert.StartsWith(device.Validation.Enabled ? "on: 0 errors" : "off", details["Validation"], StringComparison.Ordinal);
            Assert.Contains("Frames in flight", details.Keys);
            Assert.NotNull(abstraction.AllocationStatistics);
        });
    }

    [Fact]
    public void ManyFrames_ReuseStreamingMemory_AndDestroyResourcesAfterTheGpuIsDone()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            long blocksAfterWarmup = 0;
            var liveBefore = device.Resources.LiveCount;
            for (var frame = 0; frame < 30; frame++)
            {
                // A texture created, drawn and disposed within the frame: destruction must wait for the GPU.
                var texture = device.CreateTexture(2, 2, new byte[16], SamplerState.PointClamp, $"Temp{frame}");
                var batch = renderer.Begin(PixelCamera());
                device.Clear(Color.Black);
                for (var i = 0; i < 500; i++)
                {
                    batch.Draw(texture, new Vector2(i % Size, i / 8f), Vector2.One, Color.White);
                }

                renderer.End();
                texture.Dispose();
                device.Present();
                if (frame == 5)
                {
                    blocksAfterWarmup = device.MemoryStatistics.BlockBytes;
                }
            }

            Assert.Equal(30, device.FrameNumber);
            Assert.Equal(blocksAfterWarmup, device.MemoryStatistics.BlockBytes); // steady state allocates nothing new
            Assert.Equal(liveBefore, device.Resources.LiveCount);
        });
    }

    [Fact]
    public void DynamicBuffer_FromAnEarlierFrame_IsRejected()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            using var vertices = device.CreateVertexBuffer(SpriteVertex.Layout, 4);
            using var indices = device.CreateIndexBuffer([0, 1, 2]);
            vertices.SetData<SpriteVertex>([default, default, default, default]);
            device.Present();
            var command = new DrawCommand(renderer.DefaultMaterial, renderer.Batch.WhitePixel, vertices, indices, 0, 3, Matrix4x4.Identity);
            Assert.Throws<InvalidOperationException>(() => device.Draw(in command));
        });
    }

    [Fact]
    public void MaterialUniforms_AreSetByName_AndLinkedByNameAcrossStages()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            // The fragment declares its inputs in the opposite order of the vertex outputs: linking is by name, as in GL.
            using var shader = device.CreateShader(BuiltinShaders.Sprite with { Name = "Tint", FragmentGlsl = TintShader });
            Assert.True(shader.HasUniform("uTint"));
            Assert.True(shader.HasUniform("uViewProjection"));
            Assert.False(shader.HasUniform("uMissing"));

            var material = new Material(shader, BlendMode.Opaque, "Tint").SetColor("uTint", new Color(0f, 1f, 0.5f, 1f)).SetFloat("uStrength", 0.5f);
            var pixels = RenderToTarget(device, renderer, Color.Black, b => b.FillRectangle(new Rect(0, 0, Size, Size), Color.White), material: material);
            AssertColor(new Color(0f, 0.5f, 0.25f, 1f), Pixel(pixels, Size, 32, 32), 3f / 255f);

            material.SetFloat("uStrength", 1f); // changed between draws: a new uniform block is streamed
            pixels = RenderToTarget(device, renderer, Color.Black, b => b.FillRectangle(new Rect(0, 0, Size, Size), Color.White), material: material);
            AssertColor(new Color(0f, 1f, 0.5f, 1f), Pixel(pixels, Size, 32, 32), 3f / 255f);
        });
    }

    [Fact]
    public void UnsupportedShaderResources_FailAtLink()
    {
        Run(device =>
        {
            var storage = BuiltinShaders.Sprite with
            {
                Name = "Storage",
                FragmentGlsl = """
                    #version 450 core
                    layout(std430, binding = 3) buffer Data { vec4 values[]; };
                    out vec4 fragColor;
                    void main() { fragColor = values[0]; }
                    """,
            };
            var ex = Assert.Throws<ShaderCompilationException>(() => device.CreateShader(storage));
            Assert.Equal("link", ex.Stage);
            Assert.Equal(0, device.Resources.LiveCount);
        });
    }

    [Fact]
    public void Mipmaps_AreGenerated_AndAverageWhenMinified()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            var checker = new byte[64 * 64 * 4];
            for (var y = 0; y < 64; y++)
            {
                for (var x = 0; x < 64; x++)
                {
                    var on = ((x + y) & 1) == 0 ? (byte)255 : (byte)0;
                    var i = ((y * 64) + x) * 4;
                    (checker[i], checker[i + 1], checker[i + 2], checker[i + 3]) = (on, on, on, 255);
                }
            }

            using var texture = device.CreateTexture(64, 64, checker, new SamplerState(TextureFilter.Linear, TextureWrap.Clamp, GenerateMipmaps: true));
            var pixels = RenderToTarget(device, renderer, Color.Black, b => b.Draw(texture, new Vector2(30, 30), new Vector2(4, 4), Color.White, pivot: Vector2.Zero));
            var p = Pixel(pixels, Size, 32, Size - 32);
            AssertColor(new Color(0.5f, 0.5f, 0.5f, 1f), p, 0.1f);
        });
    }

    [Fact]
    public void PipelineAndShaderCaches_PersistBetweenDevices()
    {
        var directory = Directory.CreateTempSubdirectory("mnvt-vk-cache-").FullName;
        try
        {
            var options = new VulkanDeviceOptions { Validation = true, PipelineCachePath = Path.Combine(directory, "pipelines.bin"), ShaderCacheDirectory = Path.Combine(directory, "Shaders") };
            Run(device =>
            {
                using var renderer = new Renderer2D(device);
                RenderToTarget(device, renderer, Color.Black, b => b.FillRectangle(new Rect(0, 0, 4, 4), Color.Red));
                Assert.Equal(1, device.PipelinesCreated);
                RenderToTarget(device, renderer, Color.Black, b => b.FillRectangle(new Rect(0, 0, 4, 4), Color.Red));
                Assert.Equal(1, device.PipelinesCreated); // never rebuilt
            }, options);

            Assert.True(new FileInfo(options.PipelineCachePath).Length > 0);
            Assert.Equal(2, Directory.GetFiles(options.ShaderCacheDirectory, "*.spv").Length);
            Run(device =>
            {
                using var renderer = new Renderer2D(device);
                var pixels = RenderToTarget(device, renderer, Color.Black, b => b.FillRectangle(new Rect(0, 0, Size, Size), Color.Red));
                AssertColor(Color.Red, Pixel(pixels, Size, 1, 1));
            }, options);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Statistics_CountDrawsPipelinesAndDescriptors_PerFrame()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            using var other = device.CreateTexture(1, 1, [255, 255, 255, 255], SamplerState.PointClamp);
            device.Present();
            device.Statistics.EndFrame(); // drop setup work

            var batch = renderer.Begin(new Camera2D(64, 64));
            batch.FillRectangle(new Rect(0, 0, 1, 1), Color.Red);
            batch.Draw(other, Vector2.Zero, Vector2.One, Color.White); // second texture: second draw
            renderer.End();
            device.Present();
            device.Statistics.EndFrame();

            var frame = device.Statistics.LastFrame;
            Assert.Equal(2, frame.DrawCalls);
            Assert.Equal(4, frame.Triangles);
            Assert.Equal(8, frame.VerticesUploaded);
            Assert.Equal(1, frame.PipelineSwitches);
            Assert.True(frame.DescriptorWrites >= 3, $"{frame.DescriptorWrites} descriptor writes"); // texture + vertex block, then a new texture
            Assert.Equal(1, frame.Submits);
            Assert.True(frame.TextureSwitches >= 1);
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
            Assert.NotNull(timer);
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
                device.Present();
            }

            Assert.True(timer.LastFrameIndex >= 0, $"No GPU timing after 20 frames ({timer.DroppedFrames} dropped).");
            Assert.InRange(timer.LastFrameMilliseconds, 0, 1000);
            var scope = Assert.Single(timer.LastScopes);
            Assert.Equal("Scene", scope.Name);
            Assert.InRange(scope.Milliseconds, 0, timer.LastFrameMilliseconds + 1e-3);
            Assert.Equal(0, timer.DroppedFrames);
        });
    }

    [Fact]
    public void GpuMemory_ComesFromTheAllocatorAndTheDriverBudget()
    {
        Run(device =>
        {
            var before = device.MemoryStatistics;
            using var texture = device.CreateTexture(256, 256, new byte[256 * 256 * 4], SamplerState.PointClamp);
            var after = device.MemoryStatistics;
            Assert.True(after.TextureBytes - before.TextureBytes >= 256 * 256 * 4);
            Assert.True(after.AllocationCount > before.AllocationCount);
            Assert.Equal(0, after.FailedAllocations);

            var memory = device.QueryGpuMemory();
            if (!after.BudgetMeasured)
            {
                Assert.False(memory.Supported);
                return;
            }

            Assert.True(memory.Supported);
            Assert.True(memory.TotalBytes > 0);
            Assert.InRange(memory.AvailableBytes!.Value, 0, memory.TotalBytes!.Value);
        });
    }

    [Fact]
    public void Text_RendersFromAnAtlasUpdatedInPlace_OverSeveralFrames()
    {
        Run(device =>
        {
            using var renderer = new Renderer2D(device);
            using var font = Text.Font.LoadDefault();
            using var atlas = new Text.GlyphAtlas(device);
            var style = new Text.TextStyle(font, 20f);
            var page = atlas.Pages[0];
            byte[] pixels = [];
            foreach (var text in (string[])["Zażółć", "gęślą jaźń"]) // the second frame adds glyphs to a page already drawn from
            {
                using var target = device.CreateRenderTarget(Size, Size, SamplerState.PointClamp);
                var batch = renderer.Begin(PixelCamera(), target: target);
                device.Clear(Color.Black);
                Text.TextDrawing.DrawText(batch, atlas, Text.TextLayout.Create(text, style), new Vector2(2, Size - 2), Color.White);
                atlas.Flush();
                renderer.End();
                pixels = new byte[Size * Size * 4];
                device.ReadPixels(pixels);
                device.SetRenderTarget(null);
                device.Present();
            }

            Assert.Same(page, atlas.Pages[0]); // updated in place, never recreated
            Assert.Equal(1, atlas.PageCount);
            var lit = 0;
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var p = Pixel(pixels, Size, x, y);
                    if (p.R > 0.5f)
                    {
                        lit++;
                        Assert.True(y < 30, $"text pixel at y={y}, expected near the top"); // one 20 px line
                    }
                }
            }

            Assert.InRange(lit, 40, 1500);
        });
    }
}