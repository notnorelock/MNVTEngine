using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Graphics.Vulkan.Pipelines;

namespace MNVTEngine.Graphics.Vulkan.Shaders;

/// <summary>
/// Build-time shader compilation for exported games: compiles, reflects and links each <see cref="ShaderSource"/>
/// exactly as the device would, so shader errors fail the build, and collects the SPIR-V of every stage as archive
/// entries (<c>Shaders/&lt;hash&gt;.spv</c>). A game passes those entries to <see cref="VulkanDeviceOptions.PrecompiledShaders"/>
/// and then compiles nothing at run time. Needs no GPU.
/// </summary>
public sealed class VulkanShaderPrecompiler : IDisposable
{
    private readonly ShaderCompiler _compiler = new(null, NullLogger.Instance);
    private readonly SortedDictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

    /// <summary>Archive entries collected so far, sorted by name (identical sources share an entry).</summary>
    public IReadOnlyDictionary<string, byte[]> Entries => _entries;

    /// <exception cref="ShaderCompilationException">The shader would fail on the device: GLSL errors, or link/resource problems.</exception>
    public void Add(ShaderSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        VulkanShader.CompileAndLink(source, _compiler);
        foreach (var (stage, glsl) in new[] { (ShaderStage.Vertex, source.VertexGlsl), (ShaderStage.Fragment, source.FragmentGlsl) })
        {
            // Unlinked words: the device links again on load (linking patches the fragment's input locations).
            var words = _compiler.Compile(source.Name, stage, glsl);
            _entries[ShaderCompiler.EntryFor(stage, glsl)] = System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()).ToArray();
        }
    }

    public void Dispose() => _compiler.Dispose();
}