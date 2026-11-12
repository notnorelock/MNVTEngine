using MNVTEngine.Mathematics;

namespace MNVTEngine.Scenes.Tests;

public sealed class HierarchyStyleTests
{
    [Fact]
    public void HierarchyStyle_SurvivesSerialization()
    {
        var scene = new Scene();
        var style = scene.CreateGameObject("Managers").AddComponent<HierarchyStyle>();
        (style.Kind, style.Color, style.Gradient) = (HierarchyStyleKind.Header, new Color(0.2f, 0.5f, 1f), true);

        var serializer = new SceneSerializer(TestRegistry.Create());
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene.Find("Managers")!.GetComponent<HierarchyStyle>()!;
        Assert.Equal((HierarchyStyleKind.Header, new Color(0.2f, 0.5f, 1f), true), (copy.Kind, copy.Color, copy.Gradient));
    }
}
