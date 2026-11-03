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

    [Fact]
    public void ChildFollowsParentTranslationRotationAndScale()
    {
        var (parent, child) = Pair();
        parent.LocalPosition = new Vector2(10f, 0f);
        parent.LocalRotation = 90f;
        parent.LocalScale = new Vector2(2f, 2f);
        child.LocalPosition = new Vector2(1f, 0f);

        // Local +X of the parent points along world +Y after a 90 degree turn, scaled by 2.
        Assert.True(Vector2.Approximately(new Vector2(10f, 2f), child.Position, 1e-4f));
        Assert.Equal(90f, child.Rotation);
        Assert.Equal(new Vector2(2f, 2f), child.LossyScale);
    }

    [Fact]
    public void WorldMatrixUpdatesWhenAncestorMoves()
    {
        var (parent, child) = Pair();
        child.LocalPosition = new Vector2(1f, 1f);
        Assert.Equal(new Vector2(1f, 1f), child.Position);
        parent.Position = new Vector2(5f, 5f);
        Assert.Equal(new Vector2(6f, 6f), child.Position);
    }

    [Fact]
    public void SettingWorldPosition_ComputesLocal()
    {
        var (parent, child) = Pair();
        parent.LocalPosition = new Vector2(10f, 10f);
        parent.LocalRotation = 180f;
        child.Position = new Vector2(12f, 10f);
        Assert.True(Vector2.Approximately(new Vector2(-2f, 0f), child.LocalPosition, 1e-4f));
        Assert.True(Vector2.Approximately(new Vector2(12f, 10f), child.Position, 1e-4f));
    }

    [Fact]
    public void SetParent_WorldPositionStays()
    {
        var scene = new Scene();
        var parent = scene.CreateGameObject("P").Transform;
        parent.LocalPosition = new Vector2(5f, 0f);
        parent.LocalRotation = 45f;
        parent.LocalScale = new Vector2(2f, 2f);
        var obj = scene.CreateGameObject("O").Transform;
        obj.LocalPosition = new Vector2(1f, 2f);
        obj.LocalRotation = 10f;

        obj.SetParent(parent);
        Assert.True(Vector2.Approximately(new Vector2(1f, 2f), obj.Position, 1e-4f));
        Assert.True(Mathf.Approximately(10f, obj.Rotation, 1e-4f));
        Assert.True(Vector2.Approximately(Vector2.One, obj.LossyScale, 1e-4f));

        obj.SetParent(null, worldPositionStays: false);
        Assert.Equal(obj.LocalPosition, obj.Position);
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
    public void PointConversionsAreInverse()
    {
        var (parent, child) = Pair();
        parent.LocalPosition = new Vector2(3f, -2f);
        parent.LocalRotation = 30f;
        child.LocalScale = new Vector2(2f, 0.5f);
        var local = new Vector2(1f, 4f);
        Assert.True(Vector2.Approximately(local, child.InverseTransformPoint(child.TransformPoint(local)), 1e-4f));
        Assert.True(Vector2.Approximately(Vector2.Rotate(Vector2.Up, 30f), child.Up, 1e-5f));
    }
}
