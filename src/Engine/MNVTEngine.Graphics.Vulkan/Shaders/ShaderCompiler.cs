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
/// 1 = vertex uniforms, 2 = fragment uniforms.
/// <para>
/// Lookup order: memory, precompiled SPIR-V (an exported game's default resources, keyed by <see cref="EntryFor"/>),
/// the disk cache, then shaderc. shaderc is loaded on first use only, so a game whose shaders were all precompiled at
/// build time never loads it. Keys are a SHA-256 of the source, stage and <see cref="OptionsVersion"/>.
/// </para>
/// </summary>
internal sealed unsafe partial class ShaderCompiler : IDisposable
{
    public const uint TextureBinding = 0;
    public const uint VertexUniformBinding = 1;
    public const uint FragmentUniformBinding = 2;

    /// <summary>
    /// Sampler bindings are shifted by this (fragment stage) or <see cref="VertexSamplerBase"/> (vertex): shaderc's binding
    /// base shifts explicit bindings too, so samplers never collide with the uniform blocks. Textures bind by name.
    /// </summary>
    public const uint FragmentSamplerBase = 3;

    public const uint VertexSamplerBase = 24;

    /// <summary>Bindings at or above are rejected (push descriptors are limited per set).</summary>
    public const uint MaxBindings = 32;

    /// <summary>Archive folder of precompiled SPIR-V.</summary>
    public const string EntryFolder = "Shaders/";

    /// <summary>Bump when compile options change, so cached SPIR-V from older options is not reused.</summary>
    // Part of every cache key: bump it when the options or the output change, so old cache entries are not used. "-2":
    // caches written before compiles were serialized can hold SPIR-V that was not compiled with these options.
    private const string OptionsVersion = "vk1.3env-spv1.6-relaxed-b012-o0-2";

    // shaderc is used from several threads in one process (the editor's device on the main thread, builds on a worker).
    // glslang keeps process-wide state, so compiles run one at a time; they are rare (a few per shader, then cached).
    private static readonly Lock CompileLock = new();

    private readonly string? _cacheDirectory;
    private readonly Func<string, byte[]?>? _precompiled;
    private readonly ILogger _logger;
    private readonly Dictionary<string, uint[]> _memory = new(StringComparer.Ordinal);
    private ShadercApi? _api;
    private Compiler* _compiler;
    private CompileOptions* _options;

    /// <param name="precompiled">Reads precompiled SPIR-V by entry name (<see cref="EntryFor"/>); null when there is none.</param>
    public ShaderCompiler(string? cacheDirectory, ILogger logger, Func<string, byte[]?>? precompiled = null)
    {
        _logger = logger;
        _cacheDirectory = cacheDirectory;
        _precompiled = precompiled;
    }

    /// <summary>Stages compiled by shaderc in this process (not found precompiled, cached or in memory).</summary>
    public int Compiled { get; private set; }

    /// <summary>Stages served from precompiled SPIR-V.</summary>
    public int Precompiled { get; private set; }

    /// <summary>Archive entry holding the precompiled SPIR-V of this stage and source.</summary>
    public static string EntryFor(ShaderStage stage, string glsl) => EntryFolder + CacheKey(stage, glsl) + ".spv";

    /// <summary>Compiles and reflects one stage.</summary>
    /// <exception cref="ShaderCompilationException">GLSL errors, with the compiler log.</exception>
    public uint[] Compile(string shaderName, ShaderStage stage, string glsl)
    {
        var key = CacheKey(stage, glsl);
        if (_memory.TryGetValue(key, out var cached))
        {
            return (uint[])cached.Clone(); // linking patches the words
        }

        if (_precompiled?.Invoke(EntryFolder + key + ".spv") is { } bytes)
        {
            var words = ToWords(bytes) ?? throw new InvalidDataException($"Precompiled shader '{shaderName}' ({stage}) is not SPIR-V.");
            Precompiled++;
            _memory[key] = words;
            return (uint[])words.Clone();
        }

        var file = _cacheDirectory is null ? null : Path.Combine(_cacheDirectory, key + ".spv");
        if (file is not null && TryReadCache(file) is { } fromDisk)
        {
            _memory[key] = fromDisk;
            return (uint[])fromDisk.Clone();
        }

        var compiled = CompileCore(shaderName, stage, glsl);
        Compiled++;
        _memory[key] = compiled;
        if (file is not null)
        {
            WriteCache(file, compiled);
        }

        return (uint[])compiled.Clone();
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

    private static uint[]? ToWords(byte[] bytes) =>
        bytes.Length >= 20 && bytes.Length % 4 == 0 && BitConverter.ToUInt32(bytes, 0) == 0x07230203 ? MemoryMarshal.Cast<byte, uint>(bytes).ToArray() : null;

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
        api.CompileOptionsSetBindingBaseForStage(_options, ShaderKind.VertexShader, ShadercUniformKind.Buffer, VertexUniformBinding);
        api.CompileOptionsSetBindingBaseForStage(_options, ShaderKind.FragmentShader, ShadercUniformKind.Buffer, FragmentUniformBinding);
        api.CompileOptionsSetBindingBaseForStage(_options, ShaderKind.FragmentShader, ShadercUniformKind.Texture, FragmentSamplerBase);
        api.CompileOptionsSetBindingBaseForStage(_options, ShaderKind.VertexShader, ShadercUniformKind.Texture, VertexSamplerBase);
        api.CompileOptionsSetOptimizationLevel(_options, OptimizationLevel.Zero); // the optimizer strips the names reflection and linking need; drivers optimize anyway
        return _api = api;
    }

    private uint[] CompileCore(string shaderName, ShaderStage stage, string glsl)
    {
        lock (CompileLock)
        {
            return CompileLocked(shaderName, stage, glsl);
        }
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

            if (ToWords(File.ReadAllBytes(file)) is { } words)
            {
                return words;
            }

            LogCacheCorrupt(file);
            return null;
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
