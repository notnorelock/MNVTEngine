using MNVTEngine.Editor.Services;
using MNVTEngine.Mathematics;
using MNVTEngine.Terrain.Project;

namespace MNVTEngine.Editor.Tests;

public sealed class SceneNavigationTests
{
    private static Vector3 Forward(EditorCamera3D camera) => (camera.Rotation * Vector3.Forward).Normalized;

    [Theory]
    [InlineData(90f, 0f, -1f, 0f, 0f)]   // from +X (Right)
    [InlineData(-90f, 0f, 1f, 0f, 0f)]   // from -X (Left)
    [InlineData(0f, -90f, 0f, -1f, 0f)]  // from +Y (Top)
    [InlineData(0f, 0f, 0f, 0f, -1f)]    // from +Z (Front)
    [InlineData(180f, 0f, 0f, 0f, 1f)]   // from -Z (Back)
    public void AxisViewsLookAlongTheAxis(float yaw, float pitch, float x, float y, float z)
    {
        var camera = new EditorCamera3D();
        camera.SetViewport(800, 600);
        var pivot = camera.Pivot;
        camera.SetView(yaw, pitch);
        Assert.True(Vector3.Approximately(new Vector3(x, y, z), Forward(camera), 1e-4f));
        Assert.Equal(pivot, camera.Pivot);
    }

    [Fact]
    public void AlignsToAnotherCamera_AndKeepsTheViewSizeWhenGoingOrthographic()
    {
        var camera = new EditorCamera3D();
        camera.SetViewport(800, 600);
        var rotation = Quaternion.Euler(-30f, 45f, 0f);
        camera.AlignTo(new Vector3(1f, 5f, 2f), rotation);
        Assert.True(Vector3.Approximately((rotation * Vector3.Forward).Normalized, Forward(camera), 1e-4f));
        Assert.True(Vector3.Approximately(new Vector3(1f, 5f, 2f), camera.Position, 1e-3f));

        var perspective = camera.UnitsPerPixel(camera.Pivot);
        camera.Orthographic = true;
        Assert.Equal(perspective, camera.UnitsPerPixel(camera.Pivot), 4);
    }

    [Fact]
    public void ClickingATerrainSurfacePicksItsGameObject()
    {
        var project = new TerrainProject();
        project.World.Add(new Terrain.Elements.Terrain(Vector3.Zero, 20, 20));
        project.World.Update();
        var scene = new Scene();
        var ground = scene.CreateGameObject("Terrain");
        ground.Transform.Position = new Vector3(100f, 0f, 0f);
        ground.AddComponent<WorldTerrain>().SetData(project);

        Assert.Same(ground, ScenePicker3D.Pick(scene, new Ray(new Vector3(103f, 10f, 2f), Vector3.Down), _ => null, _ => 0.01f));
        Assert.Null(ScenePicker3D.Pick(scene, new Ray(new Vector3(3f, 10f, 2f), Vector3.Down), _ => null, _ => 0.01f)); // the world moved with its GameObject
    }

    [Fact]
    public void GizmoUsesLocalAxesInLocalSpace()
    {
        var transform = new Scene().CreateGameObject("Box").Transform;
        transform.Rotation = Quaternion.AngleAxis(90f, Vector3.Up);
        var gizmo = new TransformGizmo3D();
        Assert.Equal(Vector3.UnitX, gizmo.Axis(transform, Gizmo3DHandle.X));
        gizmo.LocalSpace = true;
        Assert.True(Vector3.Approximately(transform.Rotation * Vector3.UnitX, gizmo.Axis(transform, Gizmo3DHandle.X)));
    }
}
