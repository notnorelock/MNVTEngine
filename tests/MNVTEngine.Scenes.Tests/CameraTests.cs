using MNVTEngine.Mathematics;

namespace MNVTEngine.Scenes.Tests;

public sealed class CameraTests
{
    private static Camera Perspective(Vector3 position, Vector3 lookAt)
    {
        var camera = new Scene().CreateGameObject("Camera").AddComponent<Camera>();
        (camera.Projection, camera.FieldOfView, camera.NearClip, camera.FarClip, camera.ViewportSize) = (CameraProjection.Perspective, 90f, 0.5f, 100f, new Vector2(800f, 400f));
        camera.Transform.Position = position;
        camera.Transform.LookAt(lookAt);
        return camera;
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void Perspective_ProjectsTheLookTargetToTheCenter_WithVulkanDepth()
    {
        var camera = Perspective(new Vector3(0f, 2f, 10f), new Vector3(0f, 2f, 0f));
        Assert.Equal(2f, camera.Aspect);
        Near(new Vector3(400f, 200f, 10f), camera.WorldToScreenPoint(new Vector3(0f, 2f, 0f)));

        var clip = camera.ViewProjectionMatrix.MultiplyPoint(new Vector3(0f, 2f, 9.5f)); // on the near plane
        Assert.Equal(0f, clip.Z, 3);
        clip = camera.ViewProjectionMatrix.MultiplyPoint(new Vector3(0f, 2f, -90f));  // on the far plane
        Assert.Equal(1f, clip.Z, 3);

        // 90° vertical FOV: at distance 10 the top edge is 10 units up; higher is above the screen (y < 0).
        Assert.Equal(0f, camera.WorldToScreenPoint(new Vector3(0f, 12f, 0f)).Y, 2);
        Assert.True(camera.WorldToScreenPoint(new Vector3(0f, 2f, 20f)).Z < 0f); // behind the camera
    }

    [Fact]
    public void ScreenPointToRay_GoesThroughThePixel()
    {
        var camera = Perspective(new Vector3(1f, 1f, 5f), new Vector3(1f, 1f, 0f));
        var center = camera.ScreenPointToRay(new Vector2(400f, 200f));
        Near(Vector3.Forward, center.Direction);
        Near(new Vector3(1f, 1f, 4.5f), center.Origin); // starts on the near plane

        var corner = camera.ScreenPointToRay(new Vector2(0f, 0f)); // top-left: up and to the left
        Assert.True(corner.Direction.X < 0f && corner.Direction.Y > 0f);
        var target = new Vector3(-3f, 4f, -2f);
        var screen = camera.WorldToScreenPoint(target);
        var ray = camera.ScreenPointToRay(new Vector2(screen.X, screen.Y));
        Assert.True(Vector3.Distance(target, ray.GetPoint(Vector3.Distance(ray.Origin, target))) < 1e-2f);
    }

    [Fact]
    public void Frustum_FollowsTheCamera_AndOrthographicUsesTheSize()
    {
        var camera = Perspective(Vector3.Zero, Vector3.Forward);
        var frustum = camera.CalculateFrustum();
        Assert.True(frustum.Contains(new Vector3(0f, 0f, -10f)));
        Assert.False(frustum.Contains(new Vector3(0f, 0f, 10f)));

        camera.Transform.LookAt(Vector3.Right);
        Assert.True(camera.CalculateFrustum().Contains(new Vector3(10f, 0f, 0f)));

        camera.Projection = CameraProjection.Orthographic;
        camera.OrthographicSize = 3f; // half height 3, half width 6 at aspect 2
        camera.Transform.Rotation = Quaternion.Identity;
        Assert.Equal(1f, camera.ViewProjectionMatrix.MultiplyPoint(new Vector3(6f, 3f, -1f)).X, 4);
        Assert.Equal(1f, camera.ViewProjectionMatrix.MultiplyPoint(new Vector3(6f, 3f, -1f)).Y, 4);
        Assert.True(camera.CalculateFrustum().Contains(new Vector3(5f, -2f, -50f)));
        Assert.False(camera.CalculateFrustum().Contains(new Vector3(7f, 0f, -50f)));
    }

    [Fact]
    public void InvalidClipPlanes_AreReported()
    {
        var camera = Perspective(Vector3.Zero, Vector3.Forward);
        camera.FarClip = 0.2f;
        Assert.Throws<InvalidOperationException>(() => camera.ProjectionMatrix);
        Assert.Throws<ArgumentOutOfRangeException>(() => camera.FieldOfView = 0f);
        Assert.Throws<ArgumentOutOfRangeException>(() => camera.NearClip = 0f);
    }

    [Fact]
    public void Settings_SurviveSerialization()
    {
        var scene = new Scene();
        var camera = scene.CreateGameObject("Eye").AddComponent<Camera>();
        (camera.Projection, camera.FieldOfView, camera.NearClip, camera.FarClip) = (CameraProjection.Perspective, 75f, 0.3f, 500f);
        var serializer = new SceneSerializer(ComponentRegistry.CreateDefault());
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene.Find("Eye")!.GetComponent<Camera>()!;
        Assert.Equal((CameraProjection.Perspective, 75f, 0.3f, 500f), (copy.Projection, copy.FieldOfView, copy.NearClip, copy.FarClip));
    }
}
