using MNVTEngine.Graphics.Materials;
using MNVTEngine.Graphics.Vulkan.Shaders;

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
                using var precompiler = new VulkanShaderPrecompiler();
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
