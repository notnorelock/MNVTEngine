using MNVTEngine.Editor.Services;
using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.Tests;

/// <summary>The 3D Scene view's camera, picking and gizmo math (UI-free).</summary>
public sealed class SceneView3DTests
{
    private static EditorCamera3D Camera()
    {
        var camera = new EditorCamera3D();
        camera.SetViewport(800, 600);
        return camera;
    }

    private static Vector2 Screen(EditorCamera3D camera, Vector3 world) => camera.WorldToScreen(world) is var s ? new Vector2(s.X, s.Y) : default;

    [Fact]
    public void Camera_LooksAtItsPivot_WhileOrbitingZoomingAndPanning()
    {
        var camera = Camera();
        camera.Pivot = new Vector3(1f, 2f, 3f);
        Assert.True(Vector2.Distance(Screen(camera, camera.Pivot), new Vector2(400f, 300f)) < 0.5f);

        camera.Orbit(new Vector2(120f, -40f));
        Assert.True(Vector2.Distance(Screen(camera, camera.Pivot), new Vector2(400f, 300f)) < 0.5f, "orbiting keeps the pivot centered");
        Assert.Equal(camera.Distance, Vector3.Distance(camera.Position, camera.Pivot), 3);

        var before = camera.Distance;
        camera.Zoom(2f);
        Assert.True(camera.Distance < before);

        var world = camera.Pivot;
        camera.Pan(new Vector2(50f, 0f));
        Assert.Equal(450f, Screen(camera, world).X, 0); // the scene follows the mouse
    }

    [Fact]
    public void Frame_FitsTheBounds_AndTheCenterRayHitsThePivot()
    {
        var camera = Camera();
        camera.Frame(new Bounds(new Vector3(10f, 0f, -5f), new Vector3(4f, 4f, 4f)));
        Assert.Equal(new Vector3(10f, 0f, -5f), camera.Pivot);
        foreach (var corner in new[] { new Vector3(8f, -2f, -7f), new Vector3(12f, 2f, -3f) })
        {
            var s = camera.WorldToScreen(corner);
            Assert.InRange(s.X, 0f, 800f);
            Assert.InRange(s.Y, 0f, 600f);
        }

        var ray = camera.ScreenRay(new Vector2(400f, 300f));
        Assert.True(Vector3.Distance(ray.GetPoint(Vector3.Distance(ray.Origin, camera.Pivot)), camera.Pivot) < 0.01f);
    }

    [Fact]
    public void Picker_SelectsTheNearestMesh_ByItsTriangles_AndIconsOfLights()
    {
        var scene = new Scene();
        GameObject Cube(string name, Vector3 position, float scale = 1f)
        {
            var go = scene.CreateGameObject(name);
            go.Transform.Position = position;
            go.Transform.LocalScale = new Vector3(scale, scale, scale);
            go.AddComponent<MeshRenderer>().Primitive = PrimitiveMesh.Cube;
            return go;
        }

        var near = Cube("Near", new Vector3(0f, 0f, 2f));
        Cube("Far", new Vector3(0f, 0f, -2f), scale: 3f);
        var sun = scene.CreateGameObject("Sun");
        sun.Transform.Position = new Vector3(5f, 0f, 0f);
        sun.AddComponent<Light>();
        var cube = MeshPrimitives.Cube();
        Mesh? MeshOf(MeshRenderer _) => cube;
        static float Pixel(Vector3 _) => 0.01f;

        Assert.Same(near, ScenePicker3D.Pick(scene, new Ray(new Vector3(0f, 0f, 10f), -Vector3.UnitZ), MeshOf, Pixel));
        Assert.Equal("Far", ScenePicker3D.Pick(scene, new Ray(new Vector3(1.2f, 0f, 10f), -Vector3.UnitZ), MeshOf, Pixel)!.Name); // misses the small cube
        Assert.Same(sun, ScenePicker3D.Pick(scene, new Ray(new Vector3(5.05f, 0f, 10f), -Vector3.UnitZ), MeshOf, Pixel));    // within the icon radius
        Assert.Null(ScenePicker3D.Pick(scene, new Ray(new Vector3(0f, 9f, 10f), -Vector3.UnitZ), MeshOf, Pixel));
        Assert.Equal(1.5f, ScenePicker3D.Intersect(new Ray(new Vector3(0f, 0f, 3f), -Vector3.UnitZ), cube, Matrix4x4.Scale(new Vector3(3f, 3f, 3f)))!.Value, 3);
    }

    [Fact]
    public void MoveGizmo_DragsAlongAnAxis_AndInAPlane_WithSnapping()
    {
        var camera = Camera();
        var transform = new Scene().CreateGameObject("Box").Transform;
        var gizmo = new TransformGizmo3D();
        var length = TransformGizmo3D.HandleLength(transform, camera);

        var grab = Screen(camera, new Vector3(length * 0.6f, 0f, 0f));
        Assert.Equal(Gizmo3DHandle.X, gizmo.HitTest(transform, camera, grab));
        gizmo.BeginDrag(transform, Gizmo3DHandle.X, camera, grab);
        Assert.True(gizmo.Drag(transform, camera, Screen(camera, new Vector3((length * 0.6f) + 1.3f, 0f, 0f)), snap: false));
        Assert.Equal(1.3f, transform.Position.X, 2);
        Assert.Equal(0f, transform.Position.Y, 4); // stays on the axis
        gizmo.Drag(transform, camera, Screen(camera, new Vector3((length * 0.6f) + 1.3f, 0f, 0f)), snap: true);
        Assert.Equal(1.5f, transform.Position.X, 3);
        gizmo.EndDrag();

        var plane = Screen(camera, new Vector3(length * 0.35f, 0f, length * 0.35f) + transform.Position);
        Assert.Equal(Gizmo3DHandle.XZ, gizmo.HitTest(transform, camera, plane));
        gizmo.BeginDrag(transform, Gizmo3DHandle.XZ, camera, plane);
        gizmo.Drag(transform, camera, Screen(camera, new Vector3(length * 0.35f, 0f, length * 0.35f) + transform.Position + new Vector3(0f, 0f, 2f)), snap: false);
        Assert.Equal(new Vector3(1.5f, 0f, 2f), new Vector3(MathF.Round(transform.Position.X, 2), MathF.Round(transform.Position.Y, 2), MathF.Round(transform.Position.Z, 2)));
    }

    [Fact]
    public void RotateAndScaleGizmos_TurnAroundWorldAxes_AndScaleLocalAxes()
    {
        var camera = Camera();
        var transform = new Scene().CreateGameObject("Box").Transform;
        var gizmo = new TransformGizmo3D { Tool = GizmoTool.Rotate };
        var radius = TransformGizmo3D.RingRadiusPixels * camera.UnitsPerPixel(transform.Position);

        // Drag a quarter turn around the Y ring, starting at 45 degrees (only on that ring): (1, 0, 1) to (1, 0, -1).
        var d = radius / MathF.Sqrt(2f);
        var from = Screen(camera, new Vector3(d, 0f, d));
        Assert.Equal(Gizmo3DHandle.Y, gizmo.HitTest(transform, camera, from));
        gizmo.BeginDrag(transform, Gizmo3DHandle.Y, camera, from);
        gizmo.Drag(transform, camera, Screen(camera, new Vector3(d, 0f, -d)), snap: true);
        Assert.True(Quaternion.Approximately(Quaternion.AngleAxis(90f, Vector3.UnitY), transform.Rotation, 0.5f), $"got {transform.Rotation}");
        gizmo.EndDrag();

        gizmo.Tool = GizmoTool.Scale;
        transform.Rotation = Quaternion.Identity;
        var length = TransformGizmo3D.HandleLength(transform, camera);
        var grab = Screen(camera, new Vector3(0f, length * 0.5f, 0f));
        Assert.Equal(Gizmo3DHandle.Y, gizmo.HitTest(transform, camera, grab));
        gizmo.BeginDrag(transform, Gizmo3DHandle.Y, camera, grab);
        gizmo.Drag(transform, camera, Screen(camera, new Vector3(0f, length, 0f)), snap: false);
        Assert.Equal(2f, transform.LocalScale.Y, 1);
        Assert.Equal(1f, transform.LocalScale.X);

        Assert.Equal(Gizmo3DHandle.Uniform, gizmo.HitTest(transform, camera, Screen(camera, transform.Position)));
    }
}
