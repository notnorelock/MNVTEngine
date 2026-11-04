using MNVTEngine.Graphics.Vulkan.Instance;
using MNVTEngine.Graphics.Vulkan.Shaders;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace MNVTEngine.Graphics.Vulkan.Pipelines;

/// <summary>Where a material uniform goes: which stage block, and the reflected member.</summary>
internal readonly record struct UniformSlot(ShaderStage Stage, SpirvUniformMember Member);

/// <summary>
/// A compiled and linked program: two SPIR-V modules, the push-descriptor set layout derived from reflection, the
/// pipeline layout, and the pipelines created for it so far (one per blend mode, topology, vertex layout and target
/// format; created on first use, never per frame).
/// </summary>
internal sealed unsafe class VulkanShader : Shader
{
    private readonly VulkanGraphicsDevice _device;
    private readonly Dictionary<string, List<UniformSlot>> _uniforms = new(StringComparer.Ordinal);

    private VulkanShader(VulkanGraphicsDevice device, string name, SpirvModule vertex, SpirvModule fragment)
        : base(device.Resources, name)
    {
        _device = device;
        VertexBlock = vertex.UniformBlocks.Count > 0 ? vertex.UniformBlocks[0] : null;
        FragmentBlock = fragment.UniformBlocks.Count > 0 ? fragment.UniformBlocks[0] : null;
        UsesTexture = vertex.Samplers.Count + fragment.Samplers.Count > 0;
        AddUniforms(ShaderStage.Vertex, VertexBlock);
        AddUniforms(ShaderStage.Fragment, FragmentBlock);
        try
        {
            CreateNative(name, vertex, fragment);
        }
        catch
        {
            Dispose(); // destroys whatever was created
            throw;
        }
    }

    public ShaderModule VertexModule { get; private set; }

    public ShaderModule FragmentModule { get; private set; }

    public DescriptorSetLayout SetLayout { get; private set; }

    public PipelineLayout Layout { get; private set; }

    public SpirvUniformBlock? VertexBlock { get; }

    public SpirvUniformBlock? FragmentBlock { get; }

    public bool UsesTexture { get; }

    public Dictionary<PipelineKey, Pipeline> Pipelines { get; } = [];

    /// <summary>Compiles, reflects and links both stages before anything is created or tracked.</summary>
    /// <exception cref="ShaderCompilationException">GLSL errors (stage "vertex"/"fragment") or interface/resource problems ("link").</exception>
    public static VulkanShader Create(VulkanGraphicsDevice device, ShaderSource source, ShaderCompiler compiler)
    {
        var (vertex, fragment) = CompileAndLink(source, compiler);
        return new VulkanShader(device, source.Name, vertex, fragment);
    }

    /// <summary>Both stages compiled, reflected and linked; the checks every shader must pass, at run time or at build time.</summary>
    /// <exception cref="ShaderCompilationException">GLSL errors (stage "vertex"/"fragment") or interface/resource problems ("link").</exception>
    internal static (SpirvModule Vertex, SpirvModule Fragment) CompileAndLink(ShaderSource source, ShaderCompiler compiler)
    {
        var vertex = SpirvModule.Parse(compiler.Compile(source.Name, ShaderStage.Vertex, source.VertexGlsl));
        var fragment = SpirvModule.Parse(compiler.Compile(source.Name, ShaderStage.Fragment, source.FragmentGlsl));

        var errors = new List<string>(SpirvModule.LinkByName(vertex, fragment));
        errors.AddRange(vertex.UnsupportedResources.Concat(fragment.UnsupportedResources).Select(r => $"Resource {r} is not supported; use loose uniforms and 'uTexture'."));
        foreach (var sampler in vertex.Samplers.Concat(fragment.Samplers))
        {
            if (sampler.Set != 0 || sampler.Binding != ShaderCompiler.TextureBinding)
            {
                errors.Add($"Sampler '{sampler.Name}' must be 'layout(binding = 0) uniform sampler2D uTexture'; only one texture per draw is supported.");
            }
        }

        if (errors.Count > 0)
        {
            throw new ShaderCompilationException(source.Name, "link", string.Join(Environment.NewLine, errors));
        }

        return (vertex, fragment);
    }

    public IReadOnlyList<UniformSlot> Slots(string name) => _uniforms.TryGetValue(name, out var slots) ? slots : [];

    public override bool HasUniform(string name) => _uniforms.ContainsKey(name);

    protected override void ReleaseCore() => _device.DestroyLater(this);

    private void CreateNative(string name, SpirvModule vertex, SpirvModule fragment)
    {
        var context = _device.Context;
        VertexModule = CreateModule(vertex.Words, $"{name}.vert");
        FragmentModule = CreateModule(fragment.Words, $"{name}.frag");

        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        var count = 0u;
        if (UsesTexture)
        {
            bindings[count++] = new DescriptorSetLayoutBinding(ShaderCompiler.TextureBinding, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit | ShaderStageFlags.VertexBit);
        }

        if (VertexBlock is not null)
        {
            bindings[count++] = new DescriptorSetLayoutBinding(ShaderCompiler.VertexUniformBinding, DescriptorType.UniformBuffer, 1, ShaderStageFlags.VertexBit);
        }

        if (FragmentBlock is not null)
        {
            bindings[count++] = new DescriptorSetLayoutBinding(ShaderCompiler.FragmentUniformBinding, DescriptorType.UniformBuffer, 1, ShaderStageFlags.FragmentBit);
        }

        var setInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            Flags = DescriptorSetLayoutCreateFlags.PushDescriptorBit,
            BindingCount = count,
            PBindings = bindings,
        };
        DescriptorSetLayout setLayout;
        VulkanInstance.Check(context.Vk.CreateDescriptorSetLayout(context.Device, &setInfo, null, &setLayout), "vkCreateDescriptorSetLayout");
        SetLayout = setLayout;

        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout };
        PipelineLayout pipelineLayout;
        VulkanInstance.Check(context.Vk.CreatePipelineLayout(context.Device, &layoutInfo, null, &pipelineLayout), "vkCreatePipelineLayout");
        Layout = pipelineLayout;
        context.SetName(ObjectType.PipelineLayout, pipelineLayout.Handle, name);
    }

    private void AddUniforms(ShaderStage stage, SpirvUniformBlock? block)
    {
        foreach (var member in block?.Members ?? [])
        {
            if (!_uniforms.TryGetValue(member.Name, out var slots))
            {
                _uniforms[member.Name] = slots = [];
            }

            slots.Add(new UniformSlot(stage, member));
        }
    }

    private ShaderModule CreateModule(uint[] words, string name)
    {
        var context = _device.Context;
        fixed (uint* code = words)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)(words.Length * sizeof(uint)), PCode = code };
            ShaderModule module;
            VulkanInstance.Check(context.Vk.CreateShaderModule(context.Device, &info, null, &module), "vkCreateShaderModule");
            context.SetName(ObjectType.ShaderModule, module.Handle, name);
            return module;
        }
    }
}

/// <summary>Everything that selects a pipeline for a shader; viewport and scissor are dynamic.</summary>
internal readonly record struct PipelineKey(BlendMode Blend, PrimitiveTopology Topology, VertexLayout Layout, Format Format);

/// <summary>
/// Creates graphics pipelines (dynamic rendering, dynamic viewport and scissor) through one <c>VkPipelineCache</c> that
/// is loaded from and saved to disk, so a second run builds them from the driver cache.
/// </summary>
internal sealed unsafe partial class PipelineFactory : IDisposable
{
    private static readonly byte[] EntryPoint = "main\0"u8.ToArray();
    private readonly Device.VulkanContext _context;
    private readonly string? _cachePath;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    public PipelineFactory(Device.VulkanContext context, string? cachePath, Microsoft.Extensions.Logging.ILogger logger)
    {
        _context = context;
        _cachePath = cachePath;
        _logger = logger;
        byte[] initial = [];
        if (cachePath is not null && File.Exists(cachePath))
        {
            try
            {
                initial = File.ReadAllBytes(cachePath); // the driver ignores data from another device or driver version
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogCacheIo(ex, cachePath);
            }
        }

        fixed (byte* data = initial)
        {
            var info = new PipelineCacheCreateInfo { SType = StructureType.PipelineCacheCreateInfo, InitialDataSize = (nuint)initial.Length, PInitialData = data };
            PipelineCache cache;
            var result = context.Vk.CreatePipelineCache(context.Device, &info, null, &cache);
            if (result != Result.Success && initial.Length > 0)
            {
                LogCacheRejected(cachePath!, result);
                info.InitialDataSize = 0;
                info.PInitialData = null;
                result = context.Vk.CreatePipelineCache(context.Device, &info, null, &cache);
            }

            VulkanInstance.Check(result, "vkCreatePipelineCache");
            Cache = cache;
        }

        LoadedBytes = initial.Length;
    }

    public PipelineCache Cache { get; }

    public int LoadedBytes { get; }

    public int Created { get; private set; }

    public Pipeline Create(VulkanShader shader, in PipelineKey key)
    {
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        fixed (byte* entry = EntryPoint)
        {
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = shader.VertexModule, PName = entry };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = shader.FragmentModule, PName = entry };

            var vertexBinding = new VertexInputBindingDescription(0, (uint)key.Layout.Stride, VertexInputRate.Vertex);
            var attributes = stackalloc VertexInputAttributeDescription[key.Layout.Elements.Count];
            for (var i = 0; i < key.Layout.Elements.Count; i++)
            {
                var e = key.Layout.Elements[i];
                attributes[i] = new VertexInputAttributeDescription((uint)e.Location, 0, AttributeFormat(e), (uint)e.Offset);
            }

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &vertexBinding,
                VertexAttributeDescriptionCount = (uint)key.Layout.Elements.Count,
                PVertexAttributeDescriptions = attributes,
            };
            var assembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = key.Topology == PrimitiveTopology.Lines ? Silk.NET.Vulkan.PrimitiveTopology.LineList : Silk.NET.Vulkan.PrimitiveTopology.TriangleList,
            };
            var viewport = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var raster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1f,
            };
            var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            var attachment = Blend(key.Blend);
            var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &attachment };
            var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };
            var format = key.Format;
            var rendering = new PipelineRenderingCreateInfo { SType = StructureType.PipelineRenderingCreateInfo, ColorAttachmentCount = 1, PColorAttachmentFormats = &format };
            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &assembly,
                PViewportState = &viewport,
                PRasterizationState = &raster,
                PMultisampleState = &multisample,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = shader.Layout,
            };

            Pipeline pipeline;
            VulkanInstance.Check(_context.Vk.CreateGraphicsPipelines(_context.Device, Cache, 1, &info, null, &pipeline), "vkCreateGraphicsPipelines");
            _context.SetName(ObjectType.Pipeline, pipeline.Handle, $"{shader.DebugName} {key.Blend} {key.Topology} {key.Format}");
            Created++;
            return pipeline;
        }
    }

    /// <summary>Writes the driver's pipeline cache to disk (atomically). Failures are logged; the cache is an optimization.</summary>
    public void Save()
    {
        if (_cachePath is null)
        {
            return;
        }

        nuint size = 0;
        if (_context.Vk.GetPipelineCacheData(_context.Device, Cache, &size, null) != Result.Success || size == 0)
        {
            return;
        }

        var data = new byte[size];
        fixed (byte* p = data)
        {
            if (_context.Vk.GetPipelineCacheData(_context.Device, Cache, &size, p) != Result.Success)
            {
                return;
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cachePath))!);
            var temp = _cachePath + ".tmp";
            File.WriteAllBytes(temp, data.AsSpan(0, (int)size).ToArray());
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheIo(ex, _cachePath);
        }
    }

    public void Dispose() => _context.Vk.DestroyPipelineCache(_context.Device, Cache, null);

    private static Format AttributeFormat(VertexElement element) => (element.Type, element.Components) switch
    {
        (VertexElementType.Float32, 1) => Format.R32Sfloat,
        (VertexElementType.Float32, 2) => Format.R32G32Sfloat,
        (VertexElementType.Float32, 3) => Format.R32G32B32Sfloat,
        (VertexElementType.Float32, 4) => Format.R32G32B32A32Sfloat,
        (VertexElementType.UNormByte, 1) => Format.R8Unorm,
        (VertexElementType.UNormByte, 2) => Format.R8G8Unorm,
        (VertexElementType.UNormByte, 4) => Format.R8G8B8A8Unorm,
        _ => throw new NotSupportedException($"Vertex element {element.Type} x{element.Components} is not supported."),
    };

    private static PipelineColorBlendAttachmentState Blend(BlendMode mode)
    {
        var state = new PipelineColorBlendAttachmentState
        {
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = mode != BlendMode.Opaque,
            ColorBlendOp = BlendOp.Add,
            AlphaBlendOp = BlendOp.Add,
        };
        (state.SrcColorBlendFactor, state.DstColorBlendFactor, state.SrcAlphaBlendFactor, state.DstAlphaBlendFactor) = mode switch
        {
            BlendMode.Opaque => (BlendFactor.One, BlendFactor.Zero, BlendFactor.One, BlendFactor.Zero),
            BlendMode.Alpha => (BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendFactor.One, BlendFactor.OneMinusSrcAlpha),
            BlendMode.Premultiplied => (BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendFactor.One, BlendFactor.OneMinusSrcAlpha),
            BlendMode.Additive => (BlendFactor.SrcAlpha, BlendFactor.One, BlendFactor.Zero, BlendFactor.One),
            BlendMode.Multiply => (BlendFactor.DstColor, BlendFactor.Zero, BlendFactor.Zero, BlendFactor.One),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown blend mode."),
        };
        return state;
    }

    [Microsoft.Extensions.Logging.LoggerMessage(EventId = 1120, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Pipeline cache {Path} could not be read or written")]
    private partial void LogCacheIo(Exception exception, string path);

    [Microsoft.Extensions.Logging.LoggerMessage(EventId = 1121, Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Pipeline cache {Path} was rejected by the driver ({Result}); starting empty")]
    private partial void LogCacheRejected(string path, Result result);
}
