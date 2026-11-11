using MNVTEngine.Graphics.Materials;

namespace MNVTEngine.Graphics.Tests;

public sealed class TerrainSplatShaderTests
{
    [Fact]
    public void ReplacesTheStandardBaseColorWithTheFourLayerSplat()
    {
        // The splat is text-patched into the Standard fragment stage: fail loudly if Standard changes under it.
        var fragment = MaterialShaders.TerrainSplat.FragmentGlsl;
        Assert.Contains("uniform sampler2D _ControlMap;", fragment, StringComparison.Ordinal);
        Assert.Contains("uniform vec4 _LayerTiling;", fragment, StringComparison.Ordinal);
        Assert.Contains("vec4 base = vec4(splat, 1.0) * _BaseColor * vColor;", fragment, StringComparison.Ordinal);
        Assert.DoesNotContain("texture(_BaseColorMap, uvSet(_BaseColorUVSet))", fragment, StringComparison.Ordinal);
        Assert.Equal(MaterialShaders.Standard.VertexGlsl, MaterialShaders.TerrainSplat.VertexGlsl);
    }
}
