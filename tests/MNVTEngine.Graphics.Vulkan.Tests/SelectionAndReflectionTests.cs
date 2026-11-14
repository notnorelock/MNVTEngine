using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Graphics.Vulkan.Device;
using MNVTEngine.Graphics.Vulkan.Shaders;
using MNVTEngine.Graphics.Vulkan.Swapchain;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using MNVTEngine.Graphics.Vulkan.ShaderCompilation;

namespace MNVTEngine.Graphics.Vulkan.Tests;

/// <summary>Rules that need no GPU: device selection, version checks, swapchain choices, SPIR-V reflection and linking.</summary>
public class SelectionAndReflectionTests
{
    private static PhysicalDeviceCandidate Gpu(int index, string name, PhysicalDeviceType type, long megabytes, params string[] missing) =>
        new(index, name, type, VulkanVersion.Api14, megabytes * 1024 * 1024, missing);

    [Fact]
    public void Selector_PrefersDiscrete_ThenMemory_AndSkipsUnsuitable()
    {
        PhysicalDeviceCandidate[] gpus =
        [
            Gpu(0, "Intel UHD", PhysicalDeviceType.IntegratedGpu, 2048),
            Gpu(1, "Old Discrete", PhysicalDeviceType.DiscreteGpu, 8192, "supports Vulkan 1.3.0, 1.4 is required"),
            Gpu(2, "Small Discrete", PhysicalDeviceType.DiscreteGpu, 4096),
            Gpu(3, "Big Discrete", PhysicalDeviceType.DiscreteGpu, 8192),
            Gpu(4, "llvmpipe", PhysicalDeviceType.Cpu, 16384),
        ];

        Assert.Equal("Big Discrete", PhysicalDeviceSelector.Select(gpus, null)?.Name);
        Assert.Equal("Intel UHD", PhysicalDeviceSelector.Select(gpus, "intel")?.Name);
        Assert.Equal("Big Discrete", PhysicalDeviceSelector.Select(gpus, "old discrete")?.Name); // unsuitable preference is ignored
        Assert.Equal("Intel UHD", PhysicalDeviceSelector.Select([gpus[0], gpus[1]], null)?.Name); // integrated beats an unsuitable discrete
        Assert.Null(PhysicalDeviceSelector.Select([gpus[1]], null));
        Assert.Contains("1.4 is required", gpus[1].ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 3, 290, false)]
    [InlineData(1, 4, 0, true)]
    [InlineData(1, 4, 357, true)]
    [InlineData(2, 0, 0, true)]
    public void Version_14IsTheMinimum(uint major, uint minor, uint patch, bool supported)
    {
        var version = (major << 22) | (minor << 12) | patch;
        Assert.Equal(supported, VulkanVersion.AtLeast14(version));
        Assert.Equal($"{major}.{minor}.{patch}", VulkanVersion.Format(version));
    }

    [Fact]
    public void Swapchain_PrefersUnormBgra_AndFifoWithVSync()
    {
        SurfaceFormatKHR[] formats =
        [
            new(Format.A2B10G10R10UnormPack32, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
            new(Format.B8G8R8A8Srgb, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
            new(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
        ];
        Assert.Equal(Format.B8G8R8A8Unorm, VulkanSwapchain.ChooseFormat(formats).Format);
        Assert.Equal(Format.A2B10G10R10UnormPack32, VulkanSwapchain.ChooseFormat(formats[..1]).Format);

        PresentModeKHR[] all = [PresentModeKHR.FifoKhr, PresentModeKHR.ImmediateKhr, PresentModeKHR.MailboxKhr];
        Assert.Equal(PresentModeKHR.FifoKhr, VulkanSwapchain.ChoosePresentMode(all, vsync: true));
        Assert.Equal(PresentModeKHR.MailboxKhr, VulkanSwapchain.ChoosePresentMode(all, vsync: false));
        Assert.Equal(PresentModeKHR.ImmediateKhr, VulkanSwapchain.ChoosePresentMode([PresentModeKHR.FifoKhr, PresentModeKHR.ImmediateKhr], vsync: false));
        Assert.Equal(PresentModeKHR.FifoKhr, VulkanSwapchain.ChoosePresentMode([PresentModeKHR.FifoKhr], vsync: false));
    }

    [Fact]
    public void Reflection_FindsDefaultUniformBlocks_PerStage_WithOffsetsAndKinds()
    {
        using var compiler = new ShaderCompiler(null, NullLogger.Instance, glsl: new ShadercCompiler());
        var vertex = SpirvModule.Parse(compiler.Compile("Sprite", ShaderStage.Vertex, BuiltinShaders.Sprite.VertexGlsl));
        var fragment = SpirvModule.Parse(compiler.Compile("Pulse", ShaderStage.Fragment, """
            #version 450 core
            in vec2 vTexCoord;
            in vec4 vColor;
            layout(binding = 0) uniform sampler2D uTexture;
            uniform sampler2D uMask;
            uniform float uTime;
            uniform vec4 uGlow;
            uniform vec3 uUnsettable;
            out vec4 fragColor;
            void main() { fragColor = texture(uTexture, vTexCoord) * texture(uMask, vTexCoord).r * vColor + uGlow * sin(uTime) + vec4(uUnsettable, 0.0); }
            """));

        var vp = Assert.Single(Assert.Single(vertex.UniformBlocks).Members);
        Assert.Equal(("uViewProjection", 0, 64, (UniformKind?)UniformKind.Matrix4x4), (vp.Name, vp.Offset, vp.Size, vp.Kind));
        Assert.Equal(ShaderCompiler.VertexUniformBinding, vertex.UniformBlocks[0].Binding);

        var block = Assert.Single(fragment.UniformBlocks);
        Assert.Equal(ShaderCompiler.FragmentUniformBinding, block.Binding);
        Assert.Equal(UniformKind.Float, block.Members.Single(m => m.Name == "uTime").Kind);
        Assert.Equal(UniformKind.Vector4, block.Members.Single(m => m.Name == "uGlow").Kind);
        Assert.Null(block.Members.Single(m => m.Name == "uUnsettable").Kind);
        Assert.Equal(0, block.Size % 16);
        Assert.Empty(SpirvModule.LinkByName(vertex, fragment));

        // Linking assigns the engine layout: uTexture stays at 0, other samplers are shifted past the uniform blocks.
        (_, fragment) = SpirvModule.AssignBindings(vertex, fragment, ShaderCompiler.TextureBinding, ShaderCompiler.VertexUniformBinding, ShaderCompiler.FragmentUniformBinding, ShaderCompiler.FragmentSamplerBase, ShaderCompiler.VertexSamplerBase);
        Assert.Equal(
            [("uTexture", ShaderCompiler.TextureBinding), ("uMask", ShaderCompiler.FragmentSamplerBase)],
            fragment.Samplers.Select(s => (s.Name, s.Binding)).ToArray());
    }

    [Fact]
    public void Linking_MatchesFragmentInputsToVertexOutputsByName()
    {
        using var compiler = new ShaderCompiler(null, NullLogger.Instance, glsl: new ShadercCompiler());
        var vertex = SpirvModule.Parse(compiler.Compile("V", ShaderStage.Vertex, BuiltinShaders.Sprite.VertexGlsl));
        var reversed = SpirvModule.Parse(compiler.Compile("F", ShaderStage.Fragment, """
            #version 450 core
            in vec4 vColor;
            in vec2 vTexCoord;
            out vec4 fragColor;
            void main() { fragColor = vColor + vec4(vTexCoord, 0.0, 0.0); }
            """));
        Assert.NotEqual(vertex.Outputs.Single(o => o.Name == "vColor").Location, reversed.Inputs.Single(i => i.Name == "vColor").Location);

        Assert.Empty(SpirvModule.LinkByName(vertex, reversed));
        var relinked = SpirvModule.Parse(reversed.Words);
        foreach (var input in relinked.Inputs)
        {
            Assert.Equal(vertex.Outputs.Single(o => o.Name == input.Name).Location, input.Location);
        }

        var unknown = SpirvModule.Parse(compiler.Compile("G", ShaderStage.Fragment, "#version 450 core\nin vec4 vNormal;\nout vec4 c;\nvoid main() { c = vNormal; }"));
        Assert.Contains("vNormal", Assert.Single(SpirvModule.LinkByName(vertex, unknown)), StringComparison.Ordinal);
    }

    [Fact]
    public void Compiler_CachesSpirvOnDisk_AndRejectsCorruptEntries()
    {
        var directory = Directory.CreateTempSubdirectory("mnvt-spv-").FullName;
        try
        {
            uint[] first;
            using (var compiler = new ShaderCompiler(directory, NullLogger.Instance, glsl: new ShadercCompiler()))
            {
                first = compiler.Compile("Sprite", ShaderStage.Fragment, BuiltinShaders.Sprite.FragmentGlsl);
            }

            var file = Assert.Single(Directory.GetFiles(directory, "*.spv"));
            using (var compiler = new ShaderCompiler(directory, NullLogger.Instance, glsl: new ShadercCompiler()))
            {
                Assert.Equal(first, compiler.Compile("Sprite", ShaderStage.Fragment, BuiltinShaders.Sprite.FragmentGlsl));
            }

            File.WriteAllBytes(file, [1, 2, 3, 4]);
            using (var compiler = new ShaderCompiler(directory, NullLogger.Instance, glsl: new ShadercCompiler()))
            {
                Assert.Equal(first, compiler.Compile("Sprite", ShaderStage.Fragment, BuiltinShaders.Sprite.FragmentGlsl)); // recompiled
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MalformedSpirv_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => SpirvModule.Parse([1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => SpirvModule.Parse([0x07230203, 0, 0, 10, 0, 0x00FF0005]));
    }
}
