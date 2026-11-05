using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;

namespace MNVTEngine.Physics3DTests;

/// <summary>Layers, cylinder and mesh colliders, joints, kinematic carrying, character controllers and threading.</summary>
public class Physics3DFeatureTests
{
    private const float Step = 1f / 60f;

    /// <summary>Gives mesh colliders a 1 m cube or a 20 m floor quad (two triangles, facing +Y), by the renderer's primitive.</summary>
    private sealed class Meshes : IMeshDataProvider
    {
        public MeshData? GetMeshData(MeshRenderer renderer) => renderer.Primitive switch
        {
            PrimitiveMesh.Plane => new MeshData([new(-10f, 0f, -10f), new(10f, 0f, -10f), new(10f, 0f, 10f), new(-10f, 0f, 10f)], [0, 2, 1, 0, 3, 2]),
            PrimitiveMesh.Cube => new MeshData([.. from x in new[] { -0.5f, 0.5f } from y in new[] { -0.5f, 0.5f } from z in new[] { -0.5f, 0.5f } select new Vector3(x, y, z)], [0, 1, 2]),
            _ => null,
        };
    }

    private static Scene NewScene() => new() { RethrowScriptExceptions = true, MeshData = new Meshes() };

    private static GameObject Box(Scene scene, string name, Vector3 position, Vector3 size, RigidbodyType? body, int layer = 0)
    {
        var go = scene.CreateGameObject(name);
        go.Transform.Position = position;
        var collider = go.AddComponent<BoxCollider>();
        (collider.Size, collider.Layer) = (size, layer);
        if (body is { } type)
        {
            go.AddComponent<Rigidbody>().BodyType = type;
        }

        return go;
    }

    private static GameObject Floor(Scene scene, int layer = 0) => Box(scene, "Floor", new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f), body: null, layer);

    private static void Simulate(Scene scene, float seconds, Action? everyStep = null)
    {
        for (var t = 0f; t < seconds - 1e-4f; t += Step)
        {
            everyStep?.Invoke();
            scene.FixedUpdate(Step);
        }
    }

    [Fact]
    public void Layers_ThatIgnoreEachOther_PassThrough_AndRaycastsFilterByLayer()
    {
        var scene = NewScene();
        Floor(scene, layer: 2);
        var ghost = Box(scene, "Ghost", new Vector3(0f, 2f, 0f), Vector3.One, RigidbodyType.Dynamic, layer: 1);
        var solid = Box(scene, "Solid", new Vector3(3f, 2f, 0f), Vector3.One, RigidbodyType.Dynamic, layer: 0);
        scene.Start();
        var world = PhysicsWorld3D.For(scene);
        world.IgnoreLayerCollision(1, 2, true);

        Simulate(scene, 2f);
        Assert.True(ghost.Transform.Position.Y < -3f, "layer 1 falls through layer 2");
        Assert.Equal(0.5f, solid.Transform.Position.Y, 1);

        Assert.Equal("Floor", world.Raycast(new Vector3(-5f, 5f, 0f), -Vector3.Up, 20f)!.Collider.GameObject.Name);
        Assert.Null(world.Raycast(new Vector3(-5f, 5f, 0f), -Vector3.Up, 20f, layerMask: ~(1u << 2))); // the floor's layer skipped
    }

    [Fact]
    public void CylinderAndMeshColliders_Rest_OnTriangleMeshes_AndConvexHulls()
    {
        var scene = NewScene();
        var ground = scene.CreateGameObject("Ground");
        ground.AddComponent<MeshRenderer>().Primitive = PrimitiveMesh.Plane;
        ground.AddComponent<MeshCollider>();
        var cylinder = scene.CreateGameObject("Cylinder");
        cylinder.Transform.Position = new Vector3(-2f, 3f, 0f);
        cylinder.AddComponent<CylinderCollider>(); // height 2
        cylinder.AddComponent<Rigidbody>().FreezeRotation = true;
        var hull = scene.CreateGameObject("Hull");
        hull.Transform.Position = new Vector3(2f, 3f, 0f);
        hull.AddComponent<MeshRenderer>().Primitive = PrimitiveMesh.Cube;
        hull.AddComponent<MeshCollider>().Convex = true;
        hull.AddComponent<Rigidbody>();
        scene.Start();

        Simulate(scene, 3f);

        Assert.Equal(1f, cylinder.Transform.Position.Y, 1);  // half its height above the triangle floor
        Assert.Equal(0.5f, hull.Transform.Position.Y, 1);    // the hull of a unit cube
    }

    [Fact]
    public void BallAndDistanceJoints_HoldBodiesToTheWorld()
    {
        var scene = NewScene();
        var pendulum = Box(scene, "Pendulum", new Vector3(0f, 5f, 0f), new Vector3(0.2f, 0.2f, 0.2f), RigidbodyType.Dynamic);
        pendulum.AddComponent<BallJoint>().Anchor = new Vector3(1f, 0f, 0f); // held 1 m to its right
        var rope = Box(scene, "Rope", new Vector3(5f, 5f, 0f), new Vector3(0.2f, 0.2f, 0.2f), RigidbodyType.Dynamic);
        var distance = rope.AddComponent<DistanceJoint>();
        (distance.Anchor, distance.MinDistance, distance.MaxDistance) = (Vector3.Zero, 0f, 2f);
        scene.Start();
        scene.FixedUpdate(Step);
        var world = PhysicsWorld3D.For(scene);
        Assert.Equal(2, world.JointCount);

        Simulate(scene, 4f);

        Assert.Equal(1f, Vector3.Distance(pendulum.Transform.Position, new Vector3(1f, 5f, 0f)), 1); // swings around the anchor
        Assert.InRange(Vector3.Distance(rope.Transform.Position, new Vector3(5f, 5f, 0f)), 1.8f, 2.1f); // hangs at the rope's length
    }

    [Fact]
    public void FixedAndHingeJoints_ConnectTwoBodies()
    {
        var scene = NewScene();
        var a = Box(scene, "A", new Vector3(0f, 10f, 0f), Vector3.One, RigidbodyType.Dynamic);
        var b = Box(scene, "B", new Vector3(2f, 10f, 0f), Vector3.One, RigidbodyType.Dynamic);
        b.AddComponent<FixedJoint>().ConnectedBody = a.GetComponent<Rigidbody>();
        var door = Box(scene, "Door", new Vector3(10f, 2f, 0f), new Vector3(1f, 2f, 0.1f), RigidbodyType.Dynamic);
        door.GetComponent<Rigidbody>()!.UseGravity = false;
        var hinge = door.AddComponent<HingeJoint>();
        (hinge.Anchor, hinge.Axis) = (new Vector3(-0.5f, 0f, 0f), Vector3.UnitY);
        scene.Start();
        scene.FixedUpdate(Step);

        door.GetComponent<Rigidbody>()!.AddTorque(new Vector3(5f, 5f, 0f), ForceMode.Impulse);
        Simulate(scene, 1f);

        Assert.Equal(2f, Vector3.Distance(a.Transform.Position, b.Transform.Position), 1); // fell together, still 2 m apart
        Assert.True(a.Transform.Position.Y < 6f);
        var up = door.Transform.Rotation * Vector3.UnitY;
        Assert.True(up.Y > 0.98f, $"the hinge only turns around Y ({up})");
        Assert.True(MathF.Abs(Vector3.Dot(door.Transform.Rotation * Vector3.UnitX, Vector3.UnitX)) < 0.99f, "and it did turn");
    }

    [Fact]
    public void KinematicPlatforms_CarryWhatStandsOnThem()
    {
        var scene = NewScene();
        var platform = Box(scene, "Platform", Vector3.Zero, new Vector3(4f, 0.5f, 4f), RigidbodyType.Kinematic);
        var crate = Box(scene, "Crate", new Vector3(0f, 0.75f, 0f), Vector3.One, RigidbodyType.Dynamic);
        scene.Start();
        Simulate(scene, 0.5f); // settle

        Simulate(scene, 1f, () => platform.Transform.Position += new Vector3(1f * Step, 0f, 0f)); // 1 m/s along +X
        Assert.InRange(crate.Transform.Position.X, 0.7f, 1.1f);
        Assert.InRange(crate.Transform.Position.Y, 0.6f, 0.9f);
    }

    [Fact]
    public void CharacterController_Walks_IsBlockedByWalls_ClimbsSteps_AndStaysGrounded()
    {
        var scene = NewScene();
        Floor(scene);
        Box(scene, "Wall", new Vector3(0f, 1f, -5f), new Vector3(10f, 2f, 1f), body: null);  // face at z = -4.5
        Box(scene, "Step", new Vector3(5f, 0.1f, 0f), new Vector3(2f, 0.2f, 2f), body: null); // 0.2 m high
        Box(scene, "Ledge", new Vector3(-5f, 0.3f, 0f), new Vector3(2f, 0.6f, 2f), body: null); // 0.6 m high
        var player = scene.CreateGameObject("Player");
        player.Transform.Position = new Vector3(0f, 0.05f, 0f);
        var controller = player.AddComponent<CharacterController>();
        scene.Start();

        void Walk(Vector3 velocity, float seconds) => Simulate(scene, seconds, () => controller.Move((velocity + new Vector3(0f, -5f, 0f)) * Step));

        Walk(new Vector3(0f, 0f, -3f), 3f); // into the wall
        Assert.True(controller.IsGrounded);
        Assert.InRange(player.Transform.Position.Z, -4.2f, -3.8f); // radius 0.4 from the wall
        Assert.InRange(player.Transform.Position.Y, -0.05f, 0.1f);

        player.Transform.Position = new Vector3(2f, 0.05f, 0f);
        Walk(new Vector3(3f, 0f, 0f), 1f); // onto the low step (spans x 4..6)
        Assert.InRange(player.Transform.Position.Y, 0.15f, 0.3f);

        player.Transform.Position = new Vector3(-2f, 0.05f, 0f);
        Walk(new Vector3(-3f, 0f, 0f), 1.5f); // into the high ledge: too tall to step on
        Assert.True(player.Transform.Position.X > -3.7f, $"stopped at the ledge ({player.Transform.Position})");
        Assert.InRange(player.Transform.Position.Y, -0.05f, 0.1f);
    }

    [Fact]
    public void DynamicBodies_AreBlockedByCharacters_AndThreadedSteppingWorks()
    {
        var scene = NewScene();
        Floor(scene);
        var player = scene.CreateGameObject("Player");
        var controller = player.AddComponent<CharacterController>();
        var ball = scene.CreateGameObject("Ball");
        ball.Transform.Position = new Vector3(0f, 4f, 0f);
        ball.AddComponent<SphereCollider>();
        ball.AddComponent<Rigidbody>();
        scene.Start();
        controller.Move(Vector3.Zero); // creates the capsule
        PhysicsWorld3D.For(scene).ThreadCount = 4;

        Simulate(scene, 2f);
        Assert.True(ball.Transform.Position.Y > 1.5f, $"the ball lands on the character's head, not through it ({ball.Transform.Position})");
    }
}
