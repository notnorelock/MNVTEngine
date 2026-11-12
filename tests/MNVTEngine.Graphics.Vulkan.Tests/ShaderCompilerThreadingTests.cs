using MNVTEngine.Graphics.Materials;
using MNVTEngine.Graphics.Vulkan.Shaders;
using MNVTEngine.Graphics.Vulkan.ShaderCompilation;

namespace MNVTEngine.Graphics.Vulkan.Tests;

/// <summary>shaderc used from several threads at once, as the editor does (device on the main thread, builds on a worker).</summary>
public class ShaderCompilerThreadingTests
{
    [Fact]
    public void CompilersOnSeveralThreads_AllLink()
    {
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        void Work()
        {
            for (var i = 0; i < 5; i++)
            {
                using var precompiler = new VulkanShaderPrecompiler(new ShadercCompiler());
                foreach (var source in new[] { MaterialShaders.Standard, MaterialShaders.Unlit, MaterialShaders.StandardSkinned, MaterialShaders.StandardInstanced, MaterialShaders.ShadowCaster })
                {
                    try
                    {
                        precompiler.Add(source);
                    }
                    catch (ShaderCompilationException ex)
                    {
                        errors.Add(ex.Message);
                    }
                }
            }
        }

        var threads = Enumerable.Range(0, 4).Select(_ => new Thread(Work)).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.True(errors.IsEmpty, $"{errors.Count} failed: {errors.FirstOrDefault()}");
    }
}

/// <summary>The engine assigns descriptor bindings itself, whatever bindings the compiled (or cached) SPIR-V carries.</summary>
public class ShaderBindingTests
{
    [Fact]
    public void Bindings_AreAssignedAtLink_EvenWhenTheCompilerNumberedThemFromZero()
    {
        using var compiler = new ShaderCompiler(null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, glsl: new ShadercCompiler());
        var vertex = SpirvModule.Parse(compiler.Compile("Standard", ShaderStage.Vertex, MaterialShaders.Standard.VertexGlsl));
        var fragment = SpirvModule.Parse(compiler.Compile("Standard", ShaderStage.Fragment, MaterialShaders.Standard.FragmentGlsl));

        // What a build in the editor once produced: samplers numbered from 0 (colliding with uTexture and the uniform blocks).
        for (var i = 0; i < fragment.Samplers.Count; i++)
        {
            fragment.Words[fragment.Samplers[i].BindingWord] = (uint)i;
        }

        fragment.Words[fragment.UniformBlocks[0].BindingWord] = 0;
        var (v, f) = SpirvModule.AssignBindings(SpirvModule.Parse(vertex.Words), SpirvModule.Parse(fragment.Words), ShaderCompiler.TextureBinding, ShaderCompiler.VertexUniformBinding, ShaderCompiler.FragmentUniformBinding, ShaderCompiler.FragmentSamplerBase, ShaderCompiler.VertexSamplerBase);

        Assert.Equal(Enumerable.Range((int)ShaderCompiler.FragmentSamplerBase, f.Samplers.Count).Select(b => (uint)b), f.Samplers.Select(s => s.Binding));
        Assert.Equal(ShaderCompiler.FragmentUniformBinding, Assert.Single(f.UniformBlocks).Binding);
        Assert.Equal(ShaderCompiler.VertexUniformBinding, Assert.Single(v.UniformBlocks).Binding);
    }
}
