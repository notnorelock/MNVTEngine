using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;

namespace MNVTEngine.Scenes.Tests;

public sealed class LightTests
{
    [Fact]
    public void Lights_SurviveSerialization()
    {
        var scene = new Scene();
        var light = scene.CreateGameObject("Spot").AddComponent<Light>();
        (light.Type, light.Color, light.Intensity, light.Range, light.SpotAngle, light.InnerSpotAngle) = (LightType.Spot, new Color(1f, 0.5f, 0.25f), 3f, 7f, 50f, 35f);
        (light.Shadows, light.ShadowStrength, light.ShadowDistance) = (true, 0.6f, 25f);
        var ambient = scene.CreateGameObject("Ambient").AddComponent<AmbientLight>();
        (ambient.Color, ambient.Intensity) = (new Color(0.1f, 0.2f, 0.3f), 0.5f);

        var serializer = new SceneSerializer(ComponentRegistry.CreateDefault());
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene;
        var l = copy.Find("Spot")!.GetComponent<Light>()!;
        Assert.Equal((LightType.Spot, new Color(1f, 0.5f, 0.25f), 3f, 7f, 50f, 35f), (l.Type, l.Color, l.Intensity, l.Range, l.SpotAngle, l.InnerSpotAngle));
        Assert.Equal((true, 0.6f, 25f), (l.Shadows, l.ShadowStrength, l.ShadowDistance));
        var a = copy.Find("Ambient")!.GetComponent<AmbientLight>()!;
        Assert.Equal((new Color(0.1f, 0.2f, 0.3f), 0.5f), (a.Color, a.Intensity));
    }

    [Fact]
    public void Defaults_AreAWhiteSun_AndValuesAreClamped()
    {
        var light = new Scene().CreateGameObject().AddComponent<Light>();
        Assert.Equal((LightType.Directional, Color.White, 1f, false), (light.Type, light.Color, light.Intensity, light.Shadows));

        (light.Range, light.SpotAngle, light.ShadowStrength, light.ShadowDistance) = (-1f, 500f, 2f, 0f);
        Assert.Equal((0.01f, 179f, 1f, 1f), (light.Range, light.SpotAngle, light.ShadowStrength, light.ShadowDistance));
    }

    [Fact]
    public void MaterialFiles_CannotSetTheRenderersLightingUniforms()
    {
        var error = Assert.Throws<SerializationException>(() => MaterialDefinition.Parse(
            """{ "schemaVersion": 2, "shader": "builtin:standard", "blend": "Opaque", "properties": { "uLight0": [1, 2, 3, 4] } }""", "Bad.mnvtmat"));
        Assert.Contains("uLight0", error.Message, StringComparison.Ordinal);
    }
}
