namespace MNVTEngine.Mathematics.Tests;

public sealed class Geometry3DTests
{
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void LookRotation_PointsForwardAlongTheDirection_WithUpKept()
    {
        var q = Quaternion.LookRotation(new Vector3(1, 0, 0));
        Near(Vector3.Right, q * Vector3.Forward);
        Near(Vector3.Up, q * Vector3.Up);
        Assert.Equal(Quaternion.Identity, Quaternion.LookRotation(Vector3.Forward));

        var down = Quaternion.LookRotation(Vector3.Down); // parallel to up: still a valid rotation
        Near(Vector3.Down, down * Vector3.Forward);
        Assert.Equal(1f, down.Normalized.W * down.Normalized.W + (down.X * down.X) + (down.Y * down.Y) + (down.Z * down.Z), 4);
    }

    [Fact]
    public void FromAxes_And_FromToRotation_AreConsistentWithRotate()
    {
        var q = Quaternion.Euler(30, 70, -110);
        var m = Matrix4x4.Rotate(q);
        var back = Quaternion.FromAxes(m.MultiplyVector(Vector3.UnitX), m.MultiplyVector(Vector3.UnitY), m.MultiplyVector(Vector3.UnitZ));
        Assert.True(Quaternion.Approximately(q, back, 0.01f));

        var from = new Vector3(1, 2, 3);
        var to = new Vector3(-2, 0.5f, 1);
        Near(to.Normalized, Quaternion.FromToRotation(from, to) * from.Normalized);
        Near(-from.Normalized, Quaternion.FromToRotation(from, -from) * from.Normalized); // opposite vectors
    }

    [Fact]
    public void LookAt_PutsTheTargetOnTheCamerasForwardAxis()
    {
        var view = Matrix4x4.LookAt(new Vector3(3, 4, 5), new Vector3(0, 1, 0), Vector3.Up);
        var target = view.MultiplyPoint3x4(new Vector3(0, 1, 0));
        Near(new Vector3(0, 0, -new Vector3(3, 3, 5).Magnitude), target);
        Near(Vector3.Zero, view.MultiplyPoint3x4(new Vector3(3, 4, 5)));
    }

    [Theory]
    [InlineData(0.1f, 100f)]
    [InlineData(0.5f, 2000f)]
    public void ZeroToOneProjections_MapNearAndFarToVulkanDepth(float near, float far)
    {
        var perspective = Matrix4x4.PerspectiveZeroToOne(60f, 16f / 9f, near, far);
        Assert.Equal(0f, perspective.MultiplyPoint(new Vector3(0, 0, -near)).Z, 4);
        Assert.Equal(1f, perspective.MultiplyPoint(new Vector3(0, 0, -far)).Z, 4);
        var mid = perspective.MultiplyPoint(new Vector3(0, 0, -(near + far) / 2f)).Z;
        Assert.InRange(mid, 0f, 1f);

        var ortho = Matrix4x4.OrthoZeroToOne(-2, 2, -1, 1, near, far);
        Assert.Equal(0f, ortho.MultiplyPoint(new Vector3(2, 1, -near)).Z, 4);
        Assert.Equal(1f, ortho.MultiplyPoint(new Vector3(-2, -1, -far)).Z, 4);
        Assert.Equal(1f, ortho.MultiplyPoint(new Vector3(2, 1, -near)).X, 4);
    }

    [Fact]
    public void Frustum_CullsBoxesAndSpheresOutsideTheView()
    {
        var viewProjection = Matrix4x4.PerspectiveZeroToOne(90f, 1f, 0.1f, 50f) * Matrix4x4.LookAt(Vector3.Zero, Vector3.Forward, Vector3.Up);
        var frustum = Frustum.FromViewProjection(viewProjection);
        Assert.Equal(6, frustum.Planes.Length);

        Assert.True(frustum.Contains(new Vector3(0, 0, -10)));
        Assert.False(frustum.Contains(new Vector3(0, 0, 10)));       // behind
        Assert.False(frustum.Contains(new Vector3(0, 0, -60)));      // beyond far
        Assert.False(frustum.Contains(new Vector3(20, 0, -10)));     // outside the 90° cone
        Assert.True(frustum.Intersects(new Bounds(new Vector3(11, 0, -10), new Vector3(4, 4, 4)))); // straddles the edge
        Assert.False(frustum.Intersects(new Bounds(new Vector3(20, 0, -10), new Vector3(2, 2, 2))));
        Assert.True(frustum.Intersects(new BoundingSphere(new Vector3(0, 0, 0.5f), 1f)));
        Assert.False(frustum.Intersects(new BoundingSphere(new Vector3(0, 0, 5), 1f)));

        var gl = Frustum.FromViewProjection(Matrix4x4.Perspective(90f, 1f, 0.1f, 50f), DepthRange.MinusOneToOne);
        Assert.True(gl.Contains(new Vector3(0, 0, -1)));
        Assert.False(gl.Contains(new Vector3(0, 0, -0.05f)));
    }

    [Fact]
    public void PlanesSpheresAndTriangles_IntersectRays()
    {
        var ground = Plane.FromPointNormal(new Vector3(0, 2, 0), Vector3.Up);
        Assert.Equal(3f, ground.GetDistanceToPoint(new Vector3(7, 5, 1)), 5);
        Assert.True(ground.IntersectRay(new Ray(new Vector3(0, 10, 0), Vector3.Down), out var d));
        Assert.Equal(8f, d, 5);
        Assert.False(ground.IntersectRay(new Ray(new Vector3(0, 10, 0), Vector3.Up), out _));
        Assert.Equal(new Vector3(0, 0, 1), Plane.FromPoints(Vector3.Zero, Vector3.UnitX, Vector3.UnitY).Normal);

        var sphere = new BoundingSphere(new Vector3(0, 0, -10), 2f);
        Assert.True(sphere.IntersectRay(new Ray(Vector3.Zero, Vector3.Forward), out d));
        Assert.Equal(8f, d, 4);
        Assert.False(sphere.IntersectRay(new Ray(Vector3.Zero, Vector3.Back), out _));
        Assert.True(sphere.Intersects(new Bounds(new Vector3(0, 0, -13), Vector3.One * 2)));

        var ray = new Ray(new Vector3(0.2f, 0.2f, 5), Vector3.Forward);
        Assert.True(ray.IntersectTriangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, out d));
        Assert.Equal(5f, d, 5);
        Assert.False(new Ray(new Vector3(0.8f, 0.8f, 5), Vector3.Forward).IntersectTriangle(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, out _));
    }
}
