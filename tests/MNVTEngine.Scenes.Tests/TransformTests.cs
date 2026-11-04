using MNVTEngine.Mathematics;

namespace MNVTEngine.Scenes.Tests;

public class TransformTests
{
    private static (Transform Parent, Transform Child) Pair()
    {
        var scene = new Scene();
        var parent = scene.CreateGameObject("P").Transform;
        var child = scene.CreateGameObject("C", parent).Transform;
        return (parent, child);
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    [Fact]
    public void ChildFollowsParentTranslationRotationAndScale_In2D()
    {
        var (parent, child) = Pair();
        parent.LocalPosition = new Vector2(10f, 0f);
        parent.LocalRotation2D = 90f;
        parent.LocalScale = new Vector3(2f, 2f, 1f);
        child.LocalPosition = new Vector2(1f, 0f);

        // Local +X of the parent points along world +Y after a 90 degree turn, scaled by 2.
        Near(new Vector3(10f, 2f, 0f), child.Position);
        Assert.Equal(90f, child.Rotation2D, 3);
        Assert.Equal(new Vector3(2f, 2f, 1f), child.LossyScale);
    }

    [Fact]
    public void ChildFollowsParent_In3D()
    {
        var (parent, child) = Pair();
        parent.LocalPosition = new Vector3(1f, 2f, 3f);
        parent.LocalRotation = Quaternion.Euler(0f, 90f, 0f); // +Y yaw: local -Z (forward) turns to world -X
        child.LocalPosition = new Vector3(0f, 0f, -2f);

        Near(new Vector3(-1f, 2f, 3f), child.Position);
        Near(Vector3.Left, child.Forward);
        Near(Vector3.Up, child.Up);
        Near(Vector3.Forward, child.Right);
        Assert.True(Quaternion.Approximately(parent.Rotation, child.Rotation, 0.01f));
    }

    [Fact]
    public void WorldMatrices_AreCached_AndUpdateWhenAnAncestorMoves()
    {
        var (parent, child) = Pair();
        child.LocalPosition = new Vector3(1f, 1f, 1f);
        Assert.Equal(new Vector3(1f, 1f, 1f), child.Position);
        var inverse = child.WorldToLocalMatrix;
        Assert.Equal(inverse, child.WorldToLocalMatrix); // cached: same value without changes

        parent.Position = new Vector3(5f, 5f, -2f);
        Assert.Equal(new Vector3(6f, 6f, -1f), child.Position);
        Near(Vector3.Zero, child.WorldToLocalMatrix.MultiplyPoint3x4(child.Position));
    }

    [Fact]
    public void SettingWorldValues_ComputesLocal()
    {
        var (parent, child) = Pair();
        parent.LocalPosition = new Vector2(10f, 10f);
        parent.LocalRotation2D = 180f;
        child.Position = new Vector2(12f, 10f);
        Near(new Vector3(-2f, 0f, 0f), child.LocalPosition);
        Near(new Vector3(12f, 10f, 0f), child.Position);

        var target = Quaternion.Euler(10f, 20f, 30f);
        child.Rotation = target;
        Assert.True(Quaternion.Approximately(target, child.Rotation, 0.01f));
    }

    [Fact]
    public void SetParent_WorldPositionStays()
    {
        var scene = new Scene();
        var parent = scene.CreateGameObject("P").Transform;
        parent.LocalPosition = new Vector3(5f, 0f, 1f);
        parent.LocalRotation = Quaternion.Euler(0f, 0f, 45f);
        parent.LocalScale = new Vector3(2f, 2f, 2f);
        var obj = scene.CreateGameObject("O").Transform;
        obj.LocalPosition = new Vector3(1f, 2f, 3f);
        obj.LocalRotation = Quaternion.Euler(0f, 0f, 10f);

        obj.SetParent(parent);
        Near(new Vector3(1f, 2f, 3f), obj.Position);
        Assert.True(Mathf.Approximately(10f, obj.Rotation2D, 1e-3f));
        Near(Vector3.One, obj.LossyScale);

        obj.SetParent(null, worldPositionStays: false);
        Assert.Equal(obj.LocalPosition, obj.Position);
    }

    [Fact]
    public void LookAt_TurnsForwardTowardsTheTarget()
    {
        var t = new Scene().CreateGameObject().Transform;
        t.Position = new Vector3(0f, 2f, 5f);
        t.LookAt(new Vector3(0f, 2f, 0f));
        Near(Vector3.Forward, t.Forward);

        t.LookAt(new Vector3(10f, 2f, 5f));
        Near(Vector3.Right, t.Forward);
        Near(Vector3.Up, t.Up);
    }

    [Fact]
    public void EulerHint_KeepsTheAnglesThatWereSet()
    {
        var t = new Scene().CreateGameObject().Transform;
        t.LocalEulerAngles = new Vector3(0f, 0f, 450f);
        Assert.Equal(new Vector3(0f, 0f, 450f), t.LocalEulerAngles); // as typed, not folded to 90
        t.LocalRotation2D += 10f;
        Assert.Equal(460f, t.LocalRotation2D);

        t.LocalRotation = Quaternion.Euler(30f, 0f, 0f); // a quaternion replaces the hint
        Assert.Equal(30f, t.LocalEulerAngles.X, 3);
    }

    [Fact]
    public void Cycles_AreRejected()
    {
        var (parent, child) = Pair();
        Assert.Throws<InvalidOperationException>(() => parent.SetParent(child));
        Assert.Throws<InvalidOperationException>(() => parent.SetParent(parent));
    }

    [Fact]
    public void CrossSceneParenting_IsRejected()
    {
        var a = new Scene().CreateGameObject().Transform;
        var b = new Scene().CreateGameObject().Transform;
        Assert.Throws<InvalidOperationException>(() => a.SetParent(b));
    }

    [Fact]
    public void RootsAndSiblingOrder_AreTracked()
    {
        var scene = new Scene();
        var a = scene.CreateGameObject("A");
        var b = scene.CreateGameObject("B");
        var c = scene.CreateGameObject("C", a.Transform);
        Assert.Equal([a, b], scene.RootObjects);
        Assert.Equal(0, c.Transform.GetSiblingIndex());
        Assert.Equal(1, b.Transform.GetSiblingIndex());

        c.Transform.SetParent(null);
        Assert.Equal([a, b, c], scene.RootObjects);
        Assert.Same(c.Transform, scene.Find("C")!.Transform);
    }

    [Fact]
    public void PointAndDirectionConversions_AreInverse()
    {
        var (parent, child) = Pair();
        parent.LocalPosition = new Vector3(3f, -2f, 4f);
        parent.LocalRotation = Quaternion.Euler(15f, 30f, 45f);
        child.LocalScale = new Vector3(2f, 0.5f, 1.5f);
        var local = new Vector3(1f, 4f, -2f);
        Near(local, child.InverseTransformPoint(child.TransformPoint(local)));
        Near(local, child.InverseTransformDirection(child.TransformDirection(local)));
        Assert.Equal(local.Magnitude, child.TransformDirection(local).Magnitude, 4);

        child.SetLocalScale2D(new Vector2(3f, 4f)); // 2D helper keeps z
        Assert.Equal(new Vector3(3f, 4f, 1.5f), child.LocalScale);
    }
}
