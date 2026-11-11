using MNVTEngine.Audio;
using MNVTEngine.Editor.Services;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.Tests;

public sealed class SceneViewToggleTests
{
    [Fact]
    public void HidingAnObjectHidesItsChildren_ShowHiddenDrawsThemAnyway_AndItRoundTrips()
    {
        var scene = new Scene();
        var parent = scene.CreateGameObject("Parent");
        var child = scene.CreateGameObject("Child", parent.Transform);
        var other = scene.CreateGameObject("Other");
        var visibility = new SceneVisibility();
        var changes = 0;
        visibility.Changed += () => changes++;

        visibility.Toggle(parent);
        Assert.Equal((false, false, true), (visibility.IsVisible(parent), visibility.IsVisible(child), visibility.IsVisible(other)));
        Assert.True(visibility.IsHiddenSelf(parent));
        Assert.False(visibility.IsHiddenSelf(child));
        visibility.ShowHidden = true;
        Assert.True(visibility.IsVisible(child));

        var restored = new SceneVisibility();
        Assert.True(restored.Load(visibility.ToJson()));
        Assert.False(restored.IsVisible(child));
        restored.ShowAll();
        Assert.True(restored.IsVisible(child));
        Assert.False(new SceneVisibility().Load("{ broken"));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void HiddenObjectsCannotBePicked()
    {
        var scene = new Scene();
        var cube = scene.CreateGameObject("Cube");
        cube.AddComponent<MeshRenderer>().Primitive = PrimitiveMesh.Cube;
        var mesh = Graphics.Meshes.MeshPrimitives.Cube();
        var ray = new Ray(new Vector3(0f, 0f, 5f), Vector3.Forward);
        var visibility = new SceneVisibility();
        Assert.Same(cube, ScenePicker3D.Pick(scene, ray, _ => mesh, _ => 0.01f, visibility.IsVisible));
        visibility.Toggle(cube);
        Assert.Null(ScenePicker3D.Pick(scene, ray, _ => mesh, _ => 0.01f, visibility.IsVisible));
    }

    [Fact]
    public void CenterModeRotatesAndScalesAboutTheHandles()
    {
        var transform = new Scene().CreateGameObject("Box").Transform;
        transform.Position = new Vector3(2f, 0f, 0f);
        var camera = new EditorCamera3D();
        camera.SetViewport(800, 600);
        camera.SetView(0f, -90f); // top view: rotating about Y is a drag in the view plane
        camera.Pivot = Vector3.Zero;
        var gizmo = new TransformGizmo3D { Tool = GizmoTool.Rotate, HandleOrigin = Vector3.Zero };
        Assert.Equal(Vector3.Zero, gizmo.Origin(transform));

        Vector2 Pixel(Vector3 world) => new(camera.WorldToScreen(world).X, camera.WorldToScreen(world).Y);
        var ring = TransformGizmo3D.RingRadiusPixels * camera.UnitsPerPixel(Vector3.Zero);
        gizmo.BeginDrag(transform, Gizmo3DHandle.Y, camera, Pixel(new Vector3(ring, 0f, 0f)));
        Assert.True(gizmo.Drag(transform, camera, Pixel(new Vector3(0f, 0f, -ring)), snap: false)); // a quarter turn
        gizmo.EndDrag();
        Assert.True(Vector3.Approximately(new Vector3(0f, 0f, -2f), transform.Position, 1e-3f)); // swung about the center

        gizmo.Tool = GizmoTool.Scale;
        transform.Position = new Vector3(2f, 0f, 0f);
        gizmo.BeginDrag(transform, Gizmo3DHandle.Uniform, camera, new Vector2(400f, 300f));
        gizmo.Drag(transform, camera, new Vector2(500f, 300f), snap: false); // factor 2
        Assert.True(Vector3.Approximately(new Vector3(4f, 0f, 0f), transform.Position, 1e-3f));
    }

    [Fact]
    public void AudioPreviewRunsOnlyWhenEnabledAndNotPlaying()
    {
        var scene = new Scene();
        var preview = new SceneAudioPreview(new NullAudioDevice(), _ => []);
        preview.Update(scene, playing: false, Vector3.Zero, 0.016f);
        Assert.Null(AudioSystem.For(scene)); // off: the edit scene is left alone

        preview.Enabled = true;
        preview.Update(scene, playing: false, new Vector3(3f, 4f, 0f), 0.016f);
        Assert.NotNull(AudioSystem.For(scene));
        Assert.False(new SceneAudioPreview(null, _ => []).Available);
    }
}
