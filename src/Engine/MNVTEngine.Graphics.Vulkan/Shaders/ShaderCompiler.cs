using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MNVTEngine.Graphics.Vulkan.Shaders;

public enum ShaderStage
{
    Vertex,
    Fragment,
}

/// <summary>
/// GLSL to SPIR-V for one stage, under the engine's binding layout (<see cref="ShaderBindings"/>). Development hosts and
/// the build use shaderc (MNVTEngine.Graphics.Vulkan.ShaderCompiler); exported games have none and run on precompiled
/// SPIR-V only.
/// </summary>
public interface IGlslCompiler : IDisposable
{
    /// <exception cref="ShaderCompilationException">GLSL errors, with the compiler log.</exception>
    uint[] Compile(string shaderName, ShaderStage stage, string glsl);
}

/// <summary>Descriptor set 0 of every engine shader: what a <see cref="IGlslCompiler"/> must produce.</summary>
public static class ShaderBindings
{
    public const uint Texture = 0;
    public const uint VertexUniforms = 1;
    public const uint FragmentUniforms = 2;

    /// <summary>Fragment sampler bindings are shifted by this, so they never collide with the uniform blocks.</summary>
    public const uint FragmentSamplerBase = 3;

    /// <summary>Vertex sampler bindings are shifted by this.</summary>
    public const uint VertexSamplerBase = 24;
}

/// <summary>
/// SPIR-V for the device: from memory, precompiled SPIR-V (an exported game's default resources, keyed by
/// <see cref="EntryFor"/>), the disk cache, or else the host's <see cref="IGlslCompiler"/>. Exported games have none: a
/// shader that was not precompiled is an error there. Keys are a SHA-256 of the source, stage and <see cref="OptionsVersion"/>.
/// Binding layout of set 0: 0 = <c>uTexture</c>, 1 = vertex uniforms, 2 = fragment uniforms (<see cref="ShaderBindings"/>).
/// </summary>
internal sealed partial class ShaderCompiler : IDisposable
{
    public const uint TextureBinding = ShaderBindings.Texture;
    public const uint VertexUniformBinding = ShaderBindings.VertexUniforms;
    public const uint FragmentUniformBinding = ShaderBindings.FragmentUniforms;

    /// <summary>
    /// Sampler bindings are shifted by this (fragment stage) or <see cref="VertexSamplerBase"/> (vertex): shaderc's binding
    /// base shifts explicit bindings too, so samplers never collide with the uniform blocks. Textures bind by name.
    /// </summary>
    public const uint FragmentSamplerBase = ShaderBindings.FragmentSamplerBase;

    public const uint VertexSamplerBase = ShaderBindings.VertexSamplerBase;

    /// <summary>Bindings at or above are rejected (push descriptors are limited per set).</summary>
    public const uint MaxBindings = 32;

    /// <summary>Archive folder of precompiled SPIR-V.</summary>
    public const string EntryFolder = "Shaders/";

    /// <summary>Bump when compile options change, so cached SPIR-V from older options is not reused.</summary>
    // Part of every cache key: bump it when the options or the output change, so old cache entries are not used. "-2":
    // caches written before compiles were serialized can hold SPIR-V that was not compiled with these options.
    private const string OptionsVersion = "vk1.3env-spv1.6-relaxed-b012-o0-2";

    private readonly string? _cacheDirectory;
    private readonly Func<string, byte[]?>? _precompiled;
    private readonly ILogger _logger;
    private readonly Dictionary<string, uint[]> _memory = new(StringComparer.Ordinal);
    private readonly IGlslCompiler? _glsl;

    /// <param name="precompiled">Reads precompiled SPIR-V by entry name (<see cref="EntryFor"/>); null when there is none.</param>
    /// <param name="glsl">Compiles what is not precompiled or cached; null in exported games. Owned by this instance.</param>
    public ShaderCompiler(string? cacheDirectory, ILogger logger, Func<string, byte[]?>? precompiled = null, IGlslCompiler? glsl = null)
    {
        _logger = logger;
        _cacheDirectory = cacheDirectory;
        _precompiled = precompiled;
        _glsl = glsl;
    }

    /// <summary>Stages compiled from GLSL in this process (not found precompiled, cached or in memory).</summary>
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

    public void Dispose() => _glsl?.Dispose();

    private static uint[]? ToWords(byte[] bytes) =>
        bytes.Length >= 20 && bytes.Length % 4 == 0 && BitConverter.ToUInt32(bytes, 0) == 0x07230203 ? MemoryMarshal.Cast<byte, uint>(bytes).ToArray() : null;

    private uint[] CompileCore(string shaderName, ShaderStage stage, string glsl) =>
        _glsl?.Compile(shaderName, stage, glsl)
        ?? throw new ShaderCompilationException(shaderName, stage == ShaderStage.Vertex ? "vertex" : "fragment",
            "The shader was not precompiled, and this executable has no shader compiler (MNVTEngine.Graphics.Vulkan.ShaderCompiler). Rebuild the game.");

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
