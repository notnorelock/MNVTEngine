using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;

namespace MNVTEngine.Physics3DTests;

public class Physics3DTests
{
    private const float Step = 1f / 60f;

    private sealed class Recorder : Component, ICollisionHandler, ITriggerHandler
    {
        public List<string> Events { get; } = [];

        public Collision? LastEnter { get; private set; }

        public void OnCollisionEnter(Collision collision)
        {
            LastEnter = collision;
            Events.Add("enter " + collision.GameObject.Name);
        }

        public void OnCollisionExit(Collision collision) => Events.Add("exit " + collision.GameObject.Name);

        public void OnTriggerEnter(Collider other) => Events.Add("trigger-enter " + other.GameObject.Name);

        public void OnTriggerExit(Collider other) => Events.Add("trigger-exit " + other.GameObject.Name);
    }

    private static Scene NewScene() => new() { RethrowScriptExceptions = true };

    private static GameObject Box(Scene scene, string name, Vector3 position, Vector3 size, RigidbodyType? body)
    {
        var go = scene.CreateGameObject(name);
        go.Transform.Position = position;
        go.AddComponent<BoxCollider>().Size = size;
        if (body is { } type)
        {
            go.AddComponent<Rigidbody>().BodyType = type;
        }

        return go;
    }

    private static GameObject Floor(Scene scene) => Box(scene, "Floor", new Vector3(0f, -0.5f, 0f), new Vector3(20f, 1f, 20f), body: null); // top at y = 0

    private static void Simulate(Scene scene, float seconds)
    {
        for (var t = 0f; t < seconds - 1e-4f; t += Step)
        {
            scene.FixedUpdate(Step);
        }
    }

    [Fact]
    public void DynamicBody_FallsUnderGravity()
    {
        var scene = NewScene();
        var box = Box(scene, "Box", Vector3.Zero, Vector3.One, RigidbodyType.Dynamic);
        scene.Start();

        Simulate(scene, 1f);

        Assert.InRange(box.Transform.Position.Y, -5.2f, -4.6f); // ½·g·t² = 4.9
        Assert.InRange(box.GetComponent<Rigidbody>()!.Velocity.Y, -10.1f, -9.5f);
        Assert.Equal(0f, box.Transform.Position.X, 4);
    }

    [Fact]
    public void Box_ComesToRestOnAStaticFloor_AndReportsTheCollision()
    {
        var scene = NewScene();
        Floor(scene);
        var box = Box(scene, "Box", new Vector3(0f, 3f, 0f), Vector3.One, RigidbodyType.Dynamic);
        var recorder = box.AddComponent<Recorder>();
        scene.Start();

        Simulate(scene, 3f);

        Assert.Equal(0.5f, box.Transform.Position.Y, 1);
        Assert.InRange(box.GetComponent<Rigidbody>()!.Velocity.Magnitude, 0f, 0.05f);
        Assert.Equal(["enter Floor"], recorder.Events);
        Assert.True(recorder.LastEnter!.Normal.Y < -0.9f, $"normal points from the box to the floor, got {recorder.LastEnter.Normal}");
        Assert.InRange(recorder.LastEnter.Point.Y, -0.1f, 0.1f); // on the floor's surface
        Assert.Equal(1, PhysicsWorld3D.For(scene).ContactCount);
    }

    [Fact]
    public void SpheresAndCapsules_RestAtTheirSize_AndScaleCounts()
    {
        var scene = NewScene();
        Floor(scene);
        var sphere = scene.CreateGameObject("Sphere");
        sphere.Transform.Position = new Vector3(-3f, 2f, 0f);
        sphere.Transform.LocalScale = new Vector3(2f, 2f, 2f);
        sphere.AddComponent<SphereCollider>().Radius = 0.5f; // radius 1 after scale
        sphere.AddComponent<Rigidbody>();
        var capsule = scene.CreateGameObject("Capsule");
        capsule.Transform.Position = new Vector3(3f, 3f, 0f);
        capsule.AddComponent<CapsuleCollider>(); // height 2, radius 0.5
        capsule.AddComponent<Rigidbody>().FreezeRotation = true;
        scene.Start();

        Simulate(scene, 3f);

        Assert.Equal(1f, sphere.Transform.Position.Y, 1);
        Assert.Equal(1f, capsule.Transform.Position.Y, 1);
        Assert.True(Quaternion.Approximately(Quaternion.Identity, capsule.Transform.Rotation, 0.5f), "frozen rotation keeps it upright");
    }

    [Fact]
    public void Triggers_ReportEnterAndExit_WithoutPushing()
    {
        var scene = NewScene();
        var zone = Box(scene, "Zone", new Vector3(0f, -2f, 0f), new Vector3(4f, 1f, 4f), body: null);
        zone.GetComponent<BoxCollider>()!.IsTrigger = true;
        var ball = Box(scene, "Ball", Vector3.Zero, new Vector3(0.5f, 0.5f, 0.5f), RigidbodyType.Dynamic);
        var recorder = ball.AddComponent<Recorder>();
        scene.Start();

        Simulate(scene, 1.5f); // falls through the zone

        Assert.Equal(["trigger-enter Zone", "trigger-exit Zone"], recorder.Events);
        Assert.True(ball.Transform.Position.Y < -4f, "a trigger never stops a body");
    }

    [Fact]
    public void Raycast_HitsTheClosestSolidCollider_IgnoringTriggersAndTheOrigin()
    {
        var scene = NewScene();
        Floor(scene);
        var trigger = Box(scene, "Trigger", new Vector3(0f, 2f, 0f), Vector3.One, body: null);
        trigger.GetComponent<BoxCollider>()!.IsTrigger = true;
        var caster = Box(scene, "Caster", new Vector3(0f, 5f, 0f), Vector3.One, RigidbodyType.Kinematic);
        scene.Start();
        scene.FixedUpdate(Step);

        var hit = PhysicsWorld3D.For(scene).Raycast(new Vector3(0f, 5f, 0f), -Vector3.Up, 100f); // starts inside "Caster"

        Assert.NotNull(hit);
        Assert.Equal("Floor", hit.Collider.GameObject.Name);
        Assert.Equal(5f, hit.Distance, 2);
        Assert.True(hit.Normal.Y > 0.99f);
        Assert.Null(PhysicsWorld3D.For(scene).Raycast(new Vector3(0f, 5f, 0f), Vector3.Up, 100f));
        _ = caster;
    }

    [Fact]
    public void Forces_Impulses_AndGravityOptOut()
    {
        var scene = NewScene();
        var pushed = Box(scene, "Pushed", Vector3.Zero, Vector3.One, RigidbodyType.Dynamic).GetComponent<Rigidbody>()!;
        var floating = Box(scene, "Floating", new Vector3(10f, 0f, 0f), Vector3.One, RigidbodyType.Dynamic).GetComponent<Rigidbody>()!;
        (pushed.UseGravity, floating.UseGravity, pushed.Mass) = (false, false, 2f);
        scene.Start();
        scene.FixedUpdate(Step);

        pushed.AddForce(new Vector3(0f, 0f, -4f), ForceMode.Impulse); // 4 N·s on 2 kg: 2 m/s
        Simulate(scene, 1f);

        Assert.Equal(-2f, pushed.Velocity.Z, 2);
        Assert.InRange(pushed.GameObject.Transform.Position.Z, -2.1f, -1.9f);
        Assert.Equal(0f, floating.GameObject.Transform.Position.Y, 3); // no gravity
        floating.Velocity = new Vector3(1f, 0f, 0f);
        Simulate(scene, 1f);
        Assert.InRange(floating.GameObject.Transform.Position.X, 10.9f, 11.1f);
    }

    [Fact]
    public void Teleports_AndRemovingColliders_EndContacts()
    {
        var scene = NewScene();
        var floor = Floor(scene);
        var box = Box(scene, "Box", new Vector3(0f, 0.6f, 0f), Vector3.One, RigidbodyType.Dynamic);
        var recorder = box.AddComponent<Recorder>();
        scene.Start();
        Simulate(scene, 0.5f);
        Assert.Equal(["enter Floor"], recorder.Events);

        box.Transform.Position = new Vector3(0f, 10f, 0f); // teleport away
        Simulate(scene, 0.1f);
        Assert.Equal(["enter Floor", "exit Floor"], recorder.Events);
        Assert.True(box.Transform.Position.Y > 9f);

        floor.GetComponent<BoxCollider>()!.Enabled = false;
        Simulate(scene, 2f);
        Assert.True(box.Transform.Position.Y < 0f, "the floor is gone");
    }

    [Fact]
    public void OffsetAndSeveralColliders_FormOneBody()
    {
        var scene = NewScene();
        Floor(scene);
        var dumbbell = scene.CreateGameObject("Dumbbell");
        dumbbell.Transform.Position = new Vector3(0f, 2f, 0f);
        dumbbell.AddComponent<SphereCollider>().Center = new Vector3(-1f, 0f, 0f); // radius 0.5
        dumbbell.AddComponent<BoxCollider>().Center = new Vector3(1f, 0f, 0f);    // 1 m cube
        dumbbell.AddComponent<Rigidbody>();
        scene.Start();

        Simulate(scene, 3f);

        Assert.Equal(0.5f, dumbbell.Transform.Position.Y, 1); // the ball and the cube both rest on the floor
        Assert.Equal(0f, dumbbell.Transform.Position.X, 1);
        Assert.Equal(1, PhysicsWorld3D.For(scene).BodyCount - 1); // one body plus the floor
    }

    [Fact]
    public void UnloadingTheScene_ReleasesTheSimulation()
    {
        var scene = NewScene();
        Floor(scene);
        scene.Start();
        scene.FixedUpdate(Step);
        Assert.Equal(1, PhysicsWorld3D.For(scene).BodyCount);

        scene.Unload();
        Assert.Null(scene.Systems.Get<PhysicsWorld3D>());
    }

    [Fact]
    public void Components_SurviveSerialization()
    {
        var scene = new Scene();
        var go = scene.CreateGameObject("Body");
        var body = go.AddComponent<Rigidbody>();
        (body.BodyType, body.Mass, body.UseGravity, body.LinearDamping, body.AngularDamping, body.FreezeRotation, body.Continuous) = (RigidbodyType.Kinematic, 3f, false, 0.5f, 0.2f, true, true);
        var box = go.AddComponent<BoxCollider>();
        (box.Size, box.Center, box.IsTrigger, box.Friction, box.Bounciness) = (new Vector3(1f, 2f, 3f), new Vector3(0f, 1f, 0f), true, 0.3f, 0.4f);
        go.AddComponent<SphereCollider>().Radius = 2f;
        var capsule = go.AddComponent<CapsuleCollider>();
        (capsule.Radius, capsule.Height) = (0.25f, 1.5f);

        var registry = Physics3DComponents.Register(ComponentRegistry.CreateDefault());
        var serializer = new SceneSerializer(registry);
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene.Find("Body")!;
        var b = copy.GetComponent<Rigidbody>()!;
        Assert.Equal((RigidbodyType.Kinematic, 3f, false, 0.5f, 0.2f, true, true), (b.BodyType, b.Mass, b.UseGravity, b.LinearDamping, b.AngularDamping, b.FreezeRotation, b.Continuous));
        var c = copy.GetComponent<BoxCollider>()!;
        Assert.Equal((new Vector3(1f, 2f, 3f), new Vector3(0f, 1f, 0f), true, 0.3f, 0.4f), (c.Size, c.Center, c.IsTrigger, c.Friction, c.Bounciness));
        Assert.Equal(2f, copy.GetComponent<SphereCollider>()!.Radius);
        Assert.Equal((0.25f, 1.5f), (copy.GetComponent<CapsuleCollider>()!.Radius, copy.GetComponent<CapsuleCollider>()!.Height));
    }

    [Fact]
    public void ColliderLayers_JointsAndCharacters_SurviveSerialization()
    {
        var scene = new Scene();
        var other = scene.CreateGameObject("Other").AddComponent<Rigidbody>();
        var go = scene.CreateGameObject("Body");
        var cylinder = go.AddComponent<CylinderCollider>();
        (cylinder.Radius, cylinder.Height, cylinder.Layer) = (0.3f, 1.2f, 5);
        go.AddComponent<MeshCollider>().Convex = true;
        var hinge = go.AddComponent<HingeJoint>();
        (hinge.ConnectedBody, hinge.Anchor, hinge.Axis) = (other, new Vector3(1f, 0f, 0f), Vector3.UnitX);
        var distance = go.AddComponent<DistanceJoint>();
        (distance.MinDistance, distance.MaxDistance) = (0.5f, 3f);
        var spring = go.AddComponent<SpringJoint>();
        (spring.Distance, spring.Frequency, spring.DampingRatio) = (2f, 4f, 0.7f);
        go.AddComponent<FixedJoint>();
        go.AddComponent<BallJoint>();
        var character = go.AddComponent<CharacterController>();
        (character.Radius, character.Height, character.Center, character.SlopeLimit, character.StepOffset, character.SkinWidth, character.Layer) = (0.3f, 1.6f, new Vector3(0f, 0.8f, 0f), 30f, 0.25f, 0.05f, 3);

        var serializer = new SceneSerializer(Physics3DComponents.Register(ComponentRegistry.CreateDefault()));
        var copy = serializer.Deserialize(serializer.Serialize(scene)).Scene;
        var body = copy.Find("Body")!;
        var c = body.GetComponent<CylinderCollider>()!;
        Assert.Equal((0.3f, 1.2f, 5), (c.Radius, c.Height, c.Layer));
        Assert.True(body.GetComponent<MeshCollider>()!.Convex);
        var h = body.GetComponent<HingeJoint>()!;
        Assert.Equal((copy.Find("Other")!.GetComponent<Rigidbody>(), new Vector3(1f, 0f, 0f), Vector3.UnitX), (h.ConnectedBody, h.Anchor, h.Axis));
        Assert.Equal((0.5f, 3f), (body.GetComponent<DistanceJoint>()!.MinDistance, body.GetComponent<DistanceJoint>()!.MaxDistance));
        var s = body.GetComponent<SpringJoint>()!;
        Assert.Equal((2f, 4f, 0.7f), (s.Distance, s.Frequency, s.DampingRatio));
        Assert.NotNull(body.GetComponent<FixedJoint>());
        Assert.NotNull(body.GetComponent<BallJoint>());
        var k = body.GetComponent<CharacterController>()!;
        Assert.Equal((0.3f, 1.6f, new Vector3(0f, 0.8f, 0f), 30f, 0.25f, 0.05f, 3), (k.Radius, k.Height, k.Center, k.SlopeLimit, k.StepOffset, k.SkinWidth, k.Layer));
    }
}
