using MNVTEngine.Serialization;

namespace MNVTEngine.Scenes.Tests;

public sealed class MaterialDefinitionTests
{
    private static readonly Guid BaseMap = Guid.Parse("e4000000-0000-4000-8000-000000000001");
    private static readonly Guid FragmentShader = Guid.Parse("e4000000-0000-4000-8000-000000000002");
    private static readonly Guid VertexShader = Guid.Parse("e4000000-0000-4000-8000-000000000003");

    [Fact]
    public void StandardMaterials_RoundTrip_WithRenderSettingsAndTextures()
    {
        var definition = new MaterialDefinition { Builtin = MaterialDefinition.BuiltinStandard, Blend = "Opaque", Cull = "None", DepthWrite = true, RenderQueue = 2450 };
        definition.Properties["_BaseColor"] = MaterialValue.Vector(1f, 0.5f, 0.25f, 1f);
        definition.Properties["_Metallic"] = MaterialValue.Number(0.8f);
        definition.Properties["_BaseColorMap"] = MaterialValue.TextureAsset(BaseMap);
        var json = definition.ToJson();

        var copy = MaterialDefinition.Parse(json, "Body.mnvtmat");
        Assert.Equal(json, copy.ToJson()); // stable
        Assert.Equal((MaterialDefinition.BuiltinStandard, (Guid?)null, "Opaque", "None", (bool?)null, (bool?)true, (int?)2450),
            (copy.Builtin, copy.Shader, copy.Blend, copy.Cull, copy.DepthTest, copy.DepthWrite, copy.RenderQueue));
        Assert.Equal(MaterialValue.Vector(1f, 0.5f, 0.25f, 1f), copy.Properties["_BaseColor"]);
        Assert.Equal(BaseMap, copy.Properties["_BaseColorMap"].Texture);
        Assert.Equal([BaseMap], copy.Dependencies);
        Assert.Equal([0.8f], copy.Uniforms["_Metallic"]);
    }

    [Fact]
    public void CustomShaders_TakeAnOptionalVertexStage_AndBothAreDependencies()
    {
        var copy = MaterialDefinition.Parse($$"""{ "schemaVersion": 2, "shader": "asset:{{FragmentShader}}", "vertexShader": "asset:{{VertexShader}}", "properties": { "_Mask": "asset:{{BaseMap}}" } }""", "custom.mnvtmat");
        Assert.Null(copy.Builtin);
        Assert.Equal((FragmentShader, VertexShader), (copy.Shader!.Value, copy.VertexShader!.Value));
        Assert.Equal("Alpha", copy.Blend); // custom shaders default to alpha blending, as sprite materials always did
        Assert.Equal(new[] { FragmentShader, VertexShader, BaseMap }.Order(), copy.Dependencies.Order());
    }

    [Fact]
    public void Schema1Materials_Migrate_UniformsBecomeProperties()
    {
        var copy = MaterialDefinition.Parse($$"""{ "schemaVersion": 1, "shader": "asset:{{FragmentShader}}", "blend": "Additive", "uniforms": { "uTint": [1, 0, 0, 1] } }""", "glow.mnvtmat");
        Assert.Equal("Additive", copy.Blend);
        Assert.Equal([1f, 0f, 0f, 1f], copy.Uniforms["uTint"]);
        Assert.Contains("\"schemaVersion\": 2", copy.ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"properties\"", copy.ToJson(), StringComparison.Ordinal);
        Assert.Equal(MaterialDefinition.BuiltinSprite, MaterialDefinition.Parse("""{ "schemaVersion": 2 }""", "plain.mnvtmat").Builtin);
    }

    [Fact]
    public void Mistakes_AreReported_WithTheirPaths()
    {
        var ex = Assert.Throws<SerializationException>(() => MaterialDefinition.Parse("""
            { "schemaVersion": 2, "shader": "builtin:standard", "vertexShader": "asset:00000000-0000-0000-0000-000000000001", "cull": "Sideways",
              "depthTest": "yes", "renderQueue": 9000, "properties": { "uModel": 1, "_Map": "textures/a.png", "_Three": [1, 2, 3] } }
            """, "bad.mnvtmat"));
        Assert.Equal(
            ["vertexShader", "cull", "depthTest", "renderQueue", "properties.uModel", "properties._Map", "properties._Three"],
            ex.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Path));
        Assert.False(MaterialDefinition.IsValidPropertyName("uTexture"));
        Assert.True(MaterialDefinition.IsValidPropertyName("_EmissiveColor"));
        Assert.Throws<ArgumentException>(() => MaterialValue.Vector(1f, 2f, 3f));
    }
}
