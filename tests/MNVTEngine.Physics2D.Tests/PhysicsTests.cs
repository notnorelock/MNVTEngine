using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;

namespace MNVTEngine.PhysicsTests;

public class PhysicsTests
{
    private const float Step = 1f / 60f;

    private sealed class Recorder : Component, ICollisionHandler2D, ITriggerHandler2D
    {
        public List<string> Events { get; } = [];

        public Collision2D? LastEnter { get; private set; }

        public void OnCollisionEnter2D(Collision2D collision)
        {
            LastEnter = collision;
            Events.Add("enter " + collision.GameObject.Name);
        }

        public void OnCollisionExit2D(Collision2D collision) => Events.Add("exit " + collision.GameObject.Name);

        public void OnTriggerEnter2D(Collider2D other) => Events.Add("trigger-enter " + other.GameObject.Name);

        public void OnTriggerExit2D(Collider2D other) => Events.Add("trigger-exit " + other.GameObject.Name);
    }

    private static Scene NewScene() => new() { RethrowScriptExceptions = true };

    private static GameObject Box(Scene scene, string name, Vector2 position, Vector2 size, RigidbodyType2D? body)
    {
        var go = scene.CreateGameObject(name);
        go.Transform.Position = position;
        go.AddComponent<BoxCollider2D>().Size = size;
        if (body is { } type)
        {
            go.AddComponent<Rigidbody2D>().BodyType = type;
        }

        return go;
    }

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
        var box = Box(scene, "Box", Vector2.Zero, Vector2.One, RigidbodyType2D.Dynamic);
        scene.Start();

        Simulate(scene, 1f);

        Assert.InRange(box.Transform.Position.Y, -5.2f, -4.6f); // ½·g·t² = 4.9
        Assert.InRange(box.GetComponent<Rigidbody2D>()!.Velocity.Y, -10.1f, -9.5f);
        Assert.Equal(0f, box.Transform.Position.X, 4);
    }

    [Fact]
    public void Box_ComesToRestOnAStaticFloor_AndReportsTheCollision()
    {
        var scene = NewScene();
        Box(scene, "Floor", new Vector2(0, -0.5f), new Vector2(20, 1), body: null); // top at y = 0
        var box = Box(scene, "Box", new Vector2(0, 3), Vector2.One, RigidbodyType2D.Dynamic);
        var recorder = box.AddComponent<Recorder>();
        scene.Start();

        Simulate(scene, 3f);

        Assert.Equal(0.5f, box.Transform.Position.Y, 1);
        Assert.InRange(box.GetComponent<Rigidbody2D>()!.Velocity.Magnitude, 0f, 0.05f);
        Assert.Equal(["enter Floor"], recorder.Events);
        Assert.True(recorder.LastEnter!.Normal.Y < -0.9f, $"normal points from the box to the floor, got {recorder.LastEnter.Normal}");
        Assert.InRange(recorder.LastEnter.Point.Y, -0.1f, 0.1f);
    }

    [Fact]
    public void Trigger_ReportsEnterAndExit_WithoutStoppingTheBody()
    {
        var scene = NewScene();
        var zone = Box(scene, "Zone", new Vector2(0, -2), new Vector2(4, 1), body: null);
        zone.GetComponent<BoxCollider2D>()!.IsTrigger = true;
        var zoneEvents = zone.AddComponent<Recorder>();
        var ball = scene.CreateGameObject("Ball");
        ball.AddComponent<CircleCollider2D>().Radius = 0.25f;
        ball.AddComponent<Rigidbody2D>();
        var ballEvents = ball.AddComponent<Recorder>();
        scene.Start();

        Simulate(scene, 1.5f);

        Assert.True(ball.Transform.Position.Y < -4f, "the ball passed through the trigger");
        Assert.Equal(["trigger-enter Ball", "trigger-exit Ball"], zoneEvents.Events);
        Assert.Equal(["trigger-enter Zone", "trigger-exit Zone"], ballEvents.Events);
    }

    [Fact]
    public void Impulse_ChangesVelocityByImpulseOverMass_AndForcesNeedADynamicBody()
    {
        var scene = NewScene();
        var box = Box(scene, "Box", Vector2.Zero, new Vector2(2, 1), RigidbodyType2D.Dynamic);
        var body = box.GetComponent<Rigidbody2D>()!;
        body.GravityScale = 0f;
        scene.Start();

        Assert.Equal(2f, body.Mass, 3); // density 1 × area 2
        body.AddForce(new Vector2(4, 0), ForceMode2D.Impulse);
        Assert.Equal(2f, body.Velocity.X, 3);
        Simulate(scene, 1f);
        Assert.Equal(2f, box.Transform.Position.X, 1);

        body.BodyType = RigidbodyType2D.Static;
        body.AddForce(new Vector2(100, 0), ForceMode2D.Impulse);
        Simulate(scene, 0.5f);
        Assert.Equal(2f, box.Transform.Position.X, 1);
    }

    [Fact]
    public void KinematicBody_FollowsItsVelocity_IgnoringGravity()
    {
        var scene = NewScene();
        var platform = Box(scene, "Platform", Vector2.Zero, new Vector2(3, 0.5f), RigidbodyType2D.Kinematic);
        platform.GetComponent<Rigidbody2D>()!.Velocity = new Vector2(1, 0.5f);
        scene.Start();

        Simulate(scene, 2f);

        Assert.Equal(2f, platform.Transform.Position.X, 2);
        Assert.Equal(1f, platform.Transform.Position.Y, 2);
    }

    [Fact]
    public void SettingTheTransform_TeleportsTheBody()
    {
        var scene = NewScene();
        var box = Box(scene, "Box", Vector2.Zero, Vector2.One, RigidbodyType2D.Dynamic);
        box.GetComponent<Rigidbody2D>()!.GravityScale = 0f;
        scene.Start();
        Simulate(scene, 0.1f);

        box.Transform.Position = new Vector2(10, 5);
        Simulate(scene, 0.1f);

        Assert.Equal(new Vector2(10, 5), box.Transform.Position);
        Assert.Equal(10f, Assert.IsType<RaycastHit2D>(PhysicsWorld2D.For(scene).Raycast(new Vector2(0, 5), Vector2.Right, 50f)).Point.X + 0.5f, 3);
    }

    [Fact]
    public void Raycast_ReturnsTheClosestSolidCollider()
    {
        var scene = NewScene();
        Box(scene, "Near", new Vector2(0, -1), new Vector2(4, 1), body: null);
        Box(scene, "Far", new Vector2(0, -6), new Vector2(4, 1), body: null);
        var zone = Box(scene, "Zone", new Vector2(0, 0.5f), new Vector2(4, 1), body: null);
        zone.GetComponent<BoxCollider2D>()!.IsTrigger = true;
        scene.Start();
        var world = PhysicsWorld2D.For(scene);

        var hit = Assert.IsType<RaycastHit2D>(world.Raycast(new Vector2(0, 5), Vector2.Down, 100f));
        Assert.Equal("Near", hit.Collider.GameObject.Name);
        Assert.Equal(5.5f, hit.Distance, 3);
        Assert.Equal(Vector2.Up, hit.Normal);
        Assert.Null(world.Raycast(new Vector2(0, 5), Vector2.Up, 100f));
        Assert.Null(world.Raycast(new Vector2(0, 5), Vector2.Down, 2f));
        var fromInside = Assert.IsType<RaycastHit2D>(world.Raycast(new Vector2(0, -1f), Vector2.Down, 10f)); // starts inside "Near"
        Assert.Equal("Far", fromInside.Collider.GameObject.Name);
    }

    [Fact]
    public void DestroyingAToucher_SendsExit_AndTheLastBodyReleasesTheWorld()
    {
        var scene = NewScene();
        var floor = Box(scene, "Floor", new Vector2(0, -0.5f), new Vector2(20, 1), body: null);
        var floorEvents = floor.AddComponent<Recorder>();
        var box = Box(scene, "Box", new Vector2(0, 0.6f), Vector2.One, RigidbodyType2D.Dynamic);
        scene.Start();
        Simulate(scene, 0.5f);
        Assert.Equal(["enter Box"], floorEvents.Events);

        scene.Destroy(box);
        scene.Update(new Core.Timing.FrameTime(Step, Step, Step, Step, 1)); // destruction happens at the end of the frame
        scene.FixedUpdate(Step); // the exit is delivered with the next physics step
        Assert.Equal(["enter Box", "exit Box"], floorEvents.Events);

        var world = PhysicsWorld2D.For(scene);
        Assert.Equal(1, world.BodyCount);
        scene.Unload();
        Assert.Equal(0, world.BodyCount);
    }

    [Fact]
    public void ScenesThatAreNotRunning_HaveNoPhysics_AndSettingsSurviveSerialization()
    {
        var scene = new Scene();
        var go = Box(scene, "Box", Vector2.Zero, new Vector2(2, 3), RigidbodyType2D.Kinematic);
        var body = go.GetComponent<Rigidbody2D>()!;
        body.GravityScale = 0.5f;
        body.FixedRotation = true;
        var collider = go.GetComponent<BoxCollider2D>()!;
        collider.IsTrigger = true;
        collider.Bounciness = 0.75f;
        go.AddComponent<CircleCollider2D>().Radius = 2f;
        Assert.Null(scene.Systems.Get<PhysicsWorld2D>());

        var serializer = new SceneSerializer(Physics2DComponents.Register(ComponentRegistry.CreateDefault()));
        var loaded = serializer.Deserialize(serializer.Serialize(scene));
        Assert.Empty(loaded.Diagnostics);
        var copy = loaded.Scene.Find("Box")!;
        Assert.Equal((RigidbodyType2D.Kinematic, 0.5f, true), (copy.GetComponent<Rigidbody2D>()!.BodyType, copy.GetComponent<Rigidbody2D>()!.GravityScale, copy.GetComponent<Rigidbody2D>()!.FixedRotation));
        Assert.Equal((new Vector2(2, 3), true, 0.75f), (copy.GetComponent<BoxCollider2D>()!.Size, copy.GetComponent<BoxCollider2D>()!.IsTrigger, copy.GetComponent<BoxCollider2D>()!.Bounciness));
        Assert.Equal(2f, copy.GetComponent<CircleCollider2D>()!.Radius);
    }

    [Fact]
    public void ScaledColliders_AreScaled()
    {
        var scene = NewScene();
        var floor = Box(scene, "Floor", new Vector2(0, -1), Vector2.One, body: null);
        floor.Transform.LocalScale = new Vector3(10, 2, 1); // 10 × 2, top at y = 0
        scene.Start();

        var hit = Assert.IsType<RaycastHit2D>(PhysicsWorld2D.For(scene).Raycast(new Vector2(4.9f, 5), Vector2.Down, 100f));
        Assert.Equal(5f, hit.Distance, 3);
    }

    [Fact]
    public void UnloadingScenes_FreesTheirWorlds()
    {
        // Box2D allows a limited number of live worlds per process; leaking one per scene would fail long before 300.
        for (var i = 0; i < 300; i++)
        {
            var scene = NewScene();
            Box(scene, "Box", Vector2.Zero, Vector2.One, RigidbodyType2D.Dynamic);
            scene.Start();
            scene.FixedUpdate(Step);
            scene.Unload();
            Assert.Null(scene.Systems.Get<PhysicsWorld2D>());
        }
    }
}

public class TilemapColliderTests
{
    [Fact]
    public void RowsOfTiles_BecomeOneBoxPerRun_IgnoringListedTiles()
    {
        var go = new Scene().CreateGameObject("Map");
        var map = go.AddComponent<Tilemap>();
        foreach (var x in new[] { 0, 1, 2, 4, 5 })
        {
            map.SetTile(x, 0, 1);
        }

        map.SetTile(1, 1, 9); // decoration
        map.SetTile(3, 2, 1);
        var collider = go.AddComponent<TilemapCollider2D>();
        collider.IgnoredTiles = [9];

        Assert.Equal([(0, 0, 3), (4, 0, 2), (3, 2, 1)], collider.SolidRuns());

        var serializer = new SceneSerializer(Physics2DComponents.Register(ComponentRegistry.CreateDefault()));
        var copy = serializer.Deserialize(serializer.Serialize(go.Scene)).Scene.Find("Map")!;
        Assert.Equal([9], copy.GetComponent<TilemapCollider2D>()!.IgnoredTiles);
        Assert.Equal(collider.SolidRuns(), copy.GetComponent<TilemapCollider2D>()!.SolidRuns());
    }

    [Fact]
    public void BodiesLandOnTiles_AndPaintingAtRuntimeRebuildsTheCollider()
    {
        var scene = new Scene { RethrowScriptExceptions = true };
        var level = scene.CreateGameObject("Level");
        var map = level.AddComponent<Tilemap>();
        for (var x = -5; x < 5; x++)
        {
            map.SetTile(x, -1, 0); // floor top at y = 0
        }

        level.AddComponent<TilemapCollider2D>();
        var box = scene.CreateGameObject("Box");
        box.Transform.Position = new Vector2(0.5f, 3);
        box.AddComponent<BoxCollider2D>();
        box.AddComponent<Rigidbody2D>();
        scene.Start();

        for (var i = 0; i < 120; i++)
        {
            scene.FixedUpdate(1f / 60f);
        }

        Assert.Equal(0.5f, box.Transform.Position.Y, 1);

        map.SetTile(0, -1, -1); // dig a hole under the box
        for (var i = 0; i < 60; i++)
        {
            scene.FixedUpdate(1f / 60f);
        }

        Assert.True(box.Transform.Position.Y < -0.5f, $"the box fell through the hole, y = {box.Transform.Position.Y}");
    }
}
