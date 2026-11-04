using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Silk.NET.Shaderc;
using ShadercApi = Silk.NET.Shaderc.Shaderc;
using ShadercUniformKind = Silk.NET.Shaderc.UniformKind;

namespace MNVTEngine.Graphics.Vulkan.Shaders;

internal enum ShaderStage
{
    Vertex,
    Fragment,
}

/// <summary>
/// GLSL to SPIR-V with shaderc (glslang) for Vulkan 1.4, under Vulkan <em>relaxed rules</em>: loose <c>uniform</c>s are
/// gathered into <c>gl_DefaultUniformBlock</c>, and unassigned locations and bindings are filled in. Engine GLSL
/// (<c>#version 450 core</c>, GL style) therefore compiles unchanged. Binding layout of set 0: 0 = <c>uTexture</c>,
/// 1 = vertex uniforms, 2 = fragment uniforms. Results are cached in memory and, when a directory is given, on disk,
/// keyed by a SHA-256 of the source, stage and <see cref="OptionsVersion"/>.
/// </summary>
internal sealed unsafe partial class ShaderCompiler : IDisposable
{
    public const uint TextureBinding = 0;
    public const uint VertexUniformBinding = 1;
    public const uint FragmentUniformBinding = 2;

    /// <summary>Bump when compile options change, so cached SPIR-V from older options is not reused.</summary>
    private const string OptionsVersion = "vk1.3env-spv1.6-relaxed-b012-o0";

    private readonly ShadercApi _api;
    private readonly Compiler* _compiler;
    private readonly CompileOptions* _options;
    private readonly string? _cacheDirectory;
    private readonly ILogger _logger;
    private readonly Dictionary<string, uint[]> _memory = new(StringComparer.Ordinal);

    public ShaderCompiler(string? cacheDirectory, ILogger logger)
    {
        _logger = logger;
        _cacheDirectory = cacheDirectory;
        try
        {
            _api = ShadercApi.GetApi();
        }
        catch (Exception ex) when (ex is DllNotFoundException or FileNotFoundException or EntryPointNotFoundException)
        {
            throw new GraphicsInitializationException("The shader compiler (shaderc_shared) could not be loaded.", ex);
        }

        _compiler = _api.CompilerInitialize();
        _options = _api.CompileOptionsInitialize();
        // The bundled shaderc knows no Vulkan 1.4 environment (it silently falls back to 1.0). SPIR-V 1.6 is the newest
        // version Vulkan 1.4 consumes and is what the 1.3 environment produces, so nothing is lost.
        _api.CompileOptionsSetTargetEnv(_options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
        _api.CompileOptionsSetTargetSpirv(_options, SpirvVersion.Shaderc16);
        _api.CompileOptionsSetVulkanRulesRelaxed(_options, true);
        _api.CompileOptionsSetAutoMapLocations(_options, true);
        _api.CompileOptionsSetAutoBindUniforms(_options, true);
        _api.CompileOptionsSetBindingBaseForStage(_options, ShaderKind.VertexShader, ShadercUniformKind.Buffer, VertexUniformBinding);
        _api.CompileOptionsSetBindingBaseForStage(_options, ShaderKind.FragmentShader, ShadercUniformKind.Buffer, FragmentUniformBinding);
        _api.CompileOptionsSetOptimizationLevel(_options, OptimizationLevel.Zero); // the optimizer strips the names reflection and linking need; drivers optimize anyway
    }

    /// <summary>Compiles and reflects one stage.</summary>
    /// <exception cref="ShaderCompilationException">GLSL errors, with the compiler log.</exception>
    public uint[] Compile(string shaderName, ShaderStage stage, string glsl)
    {
        var key = CacheKey(stage, glsl);
        if (_memory.TryGetValue(key, out var cached))
        {
            return (uint[])cached.Clone(); // linking patches the words
        }

        var file = _cacheDirectory is null ? null : Path.Combine(_cacheDirectory, key + ".spv");
        if (file is not null && TryReadCache(file) is { } fromDisk)
        {
            _memory[key] = fromDisk;
            return (uint[])fromDisk.Clone();
        }

        var words = CompileCore(shaderName, stage, glsl);
        _memory[key] = words;
        if (file is not null)
        {
            WriteCache(file, words);
        }

        return (uint[])words.Clone();
    }

    public void Dispose()
    {
        _api.CompileOptionsRelease(_options);
        _api.CompilerRelease(_compiler);
        _api.Dispose();
    }

    private uint[] CompileCore(string shaderName, ShaderStage stage, string glsl)
    {
        var source = Encoding.UTF8.GetBytes(glsl);
        var kind = stage == ShaderStage.Vertex ? ShaderKind.VertexShader : ShaderKind.FragmentShader;
        var stageName = stage == ShaderStage.Vertex ? "vertex" : "fragment";
        CompilationResult* result;
        fixed (byte* p = source)
        {
            result = _api.CompileIntoSpv(_compiler, p, (nuint)source.Length, kind, shaderName, "main", _options);
        }

        try
        {
            if (_api.ResultGetCompilationStatus(result) != CompilationStatus.Success)
            {
                var log = Marshal.PtrToStringUTF8((nint)_api.ResultGetErrorMessage(result)) ?? "Unknown shaderc error.";
                throw new ShaderCompilationException(shaderName, stageName, log.Trim());
            }

            var length = (int)_api.ResultGetLength(result);
            return new ReadOnlySpan<uint>(_api.ResultGetBytes(result), length / sizeof(uint)).ToArray();
        }
        finally
        {
            _api.ResultRelease(result);
        }
    }

    private static string CacheKey(ShaderStage stage, string glsl) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{OptionsVersion}\n{stage}\n{glsl}")));

    private uint[]? TryReadCache(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(file);
            if (bytes.Length < 20 || bytes.Length % 4 != 0 || BitConverter.ToUInt32(bytes, 0) != 0x07230203)
            {
                LogCacheCorrupt(file);
                return null;
            }

            return MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheFailed(ex, file);
            return null;
        }
    }

    private void WriteCache(string file, uint[] words)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllBytes(temp, MemoryMarshal.AsBytes(words.AsSpan()).ToArray());
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheFailed(ex, file);
        }
    }

    [LoggerMessage(EventId = 1110, Level = LogLevel.Warning, Message = "Shader cache entry {File} is not SPIR-V; recompiling")]
    private partial void LogCacheCorrupt(string file);

    [LoggerMessage(EventId = 1111, Level = LogLevel.Warning, Message = "Shader cache {File} could not be read or written; compiling without it")]
    private partial void LogCacheFailed(Exception exception, string file);
}
