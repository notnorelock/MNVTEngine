using System.Runtime.InteropServices;
using System.Text;
using MNVTEngine.Graphics.Vulkan.Shaders;
using Silk.NET.Shaderc;
using ShadercApi = Silk.NET.Shaderc.Shaderc;
using ShadercUniformKind = Silk.NET.Shaderc.UniformKind;

namespace MNVTEngine.Graphics.Vulkan.ShaderCompilation;

/// <summary>
/// GLSL to SPIR-V with shaderc (glslang) for Vulkan 1.4, under Vulkan <em>relaxed rules</em>: loose <c>uniform</c>s are
/// gathered into <c>gl_DefaultUniformBlock</c>, and unassigned locations and bindings are filled in. Engine GLSL
/// (<c>#version 450 core</c>, GL style) therefore compiles unchanged. A development and build tool: exported games run on
/// precompiled SPIR-V and do not ship it (nor <c>shaderc_shared.dll</c>). shaderc is loaded on first use.
/// </summary>
public sealed unsafe class ShadercCompiler : IGlslCompiler
{
    // shaderc is used from several threads in one process (the editor's device on the main thread, builds on a worker).
    // glslang keeps process-wide state, so compiles run one at a time; they are rare (a few per shader, then cached).
    private static readonly Lock CompileLock = new();

    private ShadercApi? _api;
    private Compiler* _compiler;
    private CompileOptions* _options;

    /// <exception cref="ShaderCompilationException">GLSL errors, with the compiler log.</exception>
    /// <exception cref="GraphicsInitializationException">shaderc could not be loaded.</exception>
    public uint[] Compile(string shaderName, ShaderStage stage, string glsl)
    {
        lock (CompileLock)
        {
            return CompileLocked(shaderName, stage, glsl);
        }
    }

    public void Dispose()
    {
        if (_api is not null)
        {
            _api.CompileOptionsRelease(_options);
            _api.CompilerRelease(_compiler);
            _api.Dispose();
            _api = null;
        }
    }

    private ShadercApi Api()
    {
        if (_api is not null)
        {
            return _api;
        }

        ShadercApi api;
        try
        {
            api = ShadercApi.GetApi();
        }
        catch (Exception ex) when (ex is DllNotFoundException or FileNotFoundException or EntryPointNotFoundException)
        {
            throw new GraphicsInitializationException("The shader compiler (shaderc_shared) could not be loaded.", ex);
        }

        _compiler = api.CompilerInitialize();
        _options = api.CompileOptionsInitialize();
        // The bundled shaderc knows no Vulkan 1.4 environment (it silently falls back to 1.0). SPIR-V 1.6 is the newest
        // version Vulkan 1.4 consumes and is what the 1.3 environment produces, so nothing is lost.
        api.CompileOptionsSetTargetEnv(_options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
        api.CompileOptionsSetTargetSpirv(_options, SpirvVersion.Shaderc16);
        api.CompileOptionsSetVulkanRulesRelaxed(_options, true);
        api.CompileOptionsSetAutoMapLocations(_options, true);
        api.CompileOptionsSetAutoBindUniforms(_options, true);
        api.CompileOptionsSetBindingBaseForStage(_options, ShaderKind.VertexShader, ShadercUniformKind.Buffer, ShaderBindings.VertexUniforms);
        api.CompileOptionsSetBindingBaseForStage(_options, ShaderKind.FragmentShader, ShadercUniformKind.Buffer, ShaderBindings.FragmentUniforms);
        api.CompileOptionsSetBindingBaseForStage(_options, ShaderKind.FragmentShader, ShadercUniformKind.Texture, ShaderBindings.FragmentSamplerBase);
        api.CompileOptionsSetBindingBaseForStage(_options, ShaderKind.VertexShader, ShadercUniformKind.Texture, ShaderBindings.VertexSamplerBase);
        api.CompileOptionsSetOptimizationLevel(_options, OptimizationLevel.Zero); // the optimizer strips the names reflection and linking need; drivers optimize anyway
        return _api = api;
    }

    private uint[] CompileLocked(string shaderName, ShaderStage stage, string glsl)
    {
        var api = Api();
        var source = Encoding.UTF8.GetBytes(glsl);
        var kind = stage == ShaderStage.Vertex ? ShaderKind.VertexShader : ShaderKind.FragmentShader;
        var stageName = stage == ShaderStage.Vertex ? "vertex" : "fragment";
        CompilationResult* result;
        fixed (byte* p = source)
        {
            result = api.CompileIntoSpv(_compiler, p, (nuint)source.Length, kind, shaderName, "main", _options);
        }

        try
        {
            if (api.ResultGetCompilationStatus(result) != CompilationStatus.Success)
            {
                var log = Marshal.PtrToStringUTF8((nint)api.ResultGetErrorMessage(result)) ?? "Unknown shaderc error.";
                throw new ShaderCompilationException(shaderName, stageName, log.Trim());
            }

            var length = (int)api.ResultGetLength(result);
            return new ReadOnlySpan<uint>(api.ResultGetBytes(result), length / sizeof(uint)).ToArray();
        }
        finally
        {
            api.ResultRelease(result);
        }
    }
}

/// <summary>Device options with the shaderc compiler: for hosts that compile shaders at run time (editor, Player, tools, tests).</summary>
public static class ShadercDeviceOptions
{
    public static VulkanDeviceOptions WithShaderc(this VulkanDeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with { ShaderCompiler = () => new ShadercCompiler() };
    }
}
