using Box2D.NET;
using MNVTEngine.ECS;
using MNVTEngine.Mathematics;
using static Box2D.NET.B2Bodies;
using static Box2D.NET.B2Contacts;
using static Box2D.NET.B2MathFunction;
using static Box2D.NET.B2Shapes;
using static Box2D.NET.B2Types;
using static Box2D.NET.B2Worlds;

namespace MNVTEngine;

/// <summary>
/// The physics simulation of one scene: a Box2D world stepped after the components' FixedUpdate. Created by the first
/// physics component and torn down with the last one. When a GameObject's physics components or their settings change
/// it is marked dirty and its body is rebuilt at the next settled point (the next step, a query, or body access), never
/// half-way through a GameObject's teardown. Transforms are synchronised before (teleports, scale) and after (simulated
/// motion) each step, then collision and trigger callbacks are delivered.
/// </summary>
public sealed class PhysicsWorld2D : GameSystem
{
    private const int SubSteps = 4;

    // Box2D.NET keeps worlds in a process-wide table; creating and destroying them is not thread-safe.
    private static readonly Lock WorldTable = new();

    private readonly Scene _scene;
    private readonly Dictionary<GameObject, Binding> _bindings = [];
    private readonly Dictionary<ShapeKey, Collider2D> _shapes = [];
    private readonly HashSet<Pair> _touching = [];
    private readonly HashSet<Pair> _overlapping = [];
    private readonly HashSet<GameObject> _dirty = [];
    private bool _flushing;
    private B2WorldId _world;
    private bool _hasWorld;
    private Vector2 _gravity = new(0f, -9.81f);

    private PhysicsWorld2D(Scene scene)
    {
        _scene = scene;
        scene.Unloaded += OnSceneUnloaded;
    }

    /// <summary>Runs after ECS systems with the default order, so systems can apply forces before the step.</summary>
    public override int Order => 1000;

    public Vector2 Gravity
    {
        get => _gravity;
        set
        {
            _gravity = value;
            if (_hasWorld)
            {
                b2World_SetGravity(_world, Rigidbody2D.Convert(value));
            }
        }
    }

    /// <summary>GameObjects with a physics body.</summary>
    public int BodyCount => _bindings.Count;

    public static PhysicsWorld2D For(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Systems.Get<PhysicsWorld2D>() is { } existing)
        {
            return existing;
        }

        var world = new PhysicsWorld2D(scene);
        scene.Systems.Add(world);
        return world;
    }

    /// <summary>Closest non-trigger collider along the ray, or null.</summary>
    public RaycastHit2D? Raycast(Vector2 origin, Vector2 direction, float distance)
    {
        Flush();
        if (!_hasWorld || direction == Vector2.Zero || !(distance > 0f))
        {
            return null;
        }

        var translation = direction.Normalized * Math.Min(distance, 1e6f);
        var filter = b2DefaultQueryFilter();
        RaycastHit2D? closest = null;
        b2World_CastRay(_world, Rigidbody2D.Convert(origin), Rigidbody2D.Convert(translation), in filter, (shape, point, normal, fraction, _) =>
        {
            if (!_shapes.TryGetValue(Key(shape), out var collider) || collider.IsTrigger)
            {
                return -1f; // ignore and continue
            }

            closest = new RaycastHit2D(collider, Rigidbody2D.Convert(point), Rigidbody2D.Convert(normal), fraction * translation.Magnitude);
            return fraction; // clip the ray: only closer hits are reported from now on
        }, null);
        return closest;
    }

    public override void FixedUpdate(World world, float fixedDeltaTime)
    {
        Flush();
        if (!_hasWorld)
        {
            return;
        }

        SyncToPhysics();
        b2World_Step(_world, fixedDeltaTime, SubSteps);
        SyncFromPhysics();
        DispatchEvents();
    }

    /// <summary>Marks the body of a component's GameObject for rebuilding (no-op outside running scenes).</summary>
    internal static void Refresh(Component component)
    {
        if (component.GameObject is { } owner && owner.Scene.IsRunning)
        {
            For(owner.Scene)._dirty.Add(owner);
        }
    }

    /// <summary>Brings bodies up to date before a component reads or drives its body.</summary>
    internal static void Flush(Component component)
    {
        if (component.GameObject is { } owner && owner.Scene.IsRunning && owner.Scene.Systems.Get<PhysicsWorld2D>() is { } world)
        {
            world.Flush();
        }
    }

    /// <summary>Rebuilds every dirty body.</summary>
    public void Flush()
    {
        if (_flushing || _dirty.Count == 0)
        {
            return;
        }

        _flushing = true;
        try
        {
            while (_dirty.Count > 0)
            {
                var owner = _dirty.First();
                _dirty.Remove(owner);
                Rebuild(owner);
            }
        }
        finally
        {
            _flushing = false;
        }
    }

    private void Rebuild(GameObject owner)
    {
        Vector2? velocity = null;
        float angularVelocity = 0f;
        if (_bindings.Remove(owner, out var old))
        {
            if (old.Rigidbody is not null && b2Body_IsValid(old.Body))
            {
                velocity = Rigidbody2D.Convert(b2Body_GetLinearVelocity(old.Body));
                angularVelocity = b2Body_GetAngularVelocity(old.Body) * Mathf.Rad2Deg;
            }

            DestroyBinding(old);
        }

        var rigidbody = owner.IsDestroyed || !owner.ActiveInHierarchy ? null : owner.GetComponent<Rigidbody2D>();
        if (rigidbody is { IsActiveAndEnabled: false } or { IsDestroyed: true })
        {
            rigidbody = null;
        }

        var colliders = owner.IsDestroyed || !owner.ActiveInHierarchy
            ? []
            : owner.GetComponents<Collider2D>().Where(c => c.IsActiveAndEnabled && !c.IsDestroyed).ToList();
        if (rigidbody is null && colliders.Count == 0)
        {
            ReleaseWorldIfEmpty();
            return;
        }

        EnsureWorld();
        var transform = owner.Transform;
        var definition = b2DefaultBodyDef();
        definition.type = rigidbody?.BodyType switch
        {
            RigidbodyType2D.Dynamic => B2BodyType.b2_dynamicBody,
            RigidbodyType2D.Kinematic => B2BodyType.b2_kinematicBody,
            _ => B2BodyType.b2_staticBody,
        };
        definition.position = Rigidbody2D.Convert(transform.Position);
        definition.rotation = b2MakeRot(transform.Rotation * Mathf.Deg2Rad);
        if (rigidbody is not null)
        {
            definition.linearVelocity = Rigidbody2D.Convert(velocity ?? rigidbody.InitialVelocity);
            definition.angularVelocity = (velocity is null ? rigidbody.InitialAngularVelocity : angularVelocity) * Mathf.Deg2Rad;
            definition.gravityScale = rigidbody.GravityScale;
            definition.linearDamping = rigidbody.LinearDamping;
            definition.angularDamping = rigidbody.AngularDamping;
            definition.motionLocks.angularZ = rigidbody.FixedRotation;
            definition.isBullet = rigidbody.Continuous;
        }

        var body = b2CreateBody(_world, in definition);
        var scale = Vector2.Abs(transform.LossyScale);
        var binding = new Binding(owner, body, rigidbody, colliders, transform.Position, transform.Rotation, transform.LossyScale);
        foreach (var collider in colliders)
        {
            var shape = collider.CreateDefinition();
            binding.Shapes.Add(collider.CreateShape(body, ref shape, scale));
            _shapes[Key(binding.Shapes[^1])] = collider;
        }

        if (rigidbody is not null)
        {
            rigidbody.Body = body;
            rigidbody.HasBody = true;
        }

        _bindings[owner] = binding;
    }

    private void DestroyBinding(Binding binding)
    {
        foreach (var shape in binding.Shapes)
        {
            _shapes.Remove(Key(shape));
        }

        if (binding.Rigidbody is { } rigidbody)
        {
            rigidbody.HasBody = false;
        }

        if (b2Body_IsValid(binding.Body))
        {
            b2DestroyBody(binding.Body);
        }

        // Box2D reports no end events for removed shapes; synthesise exits for the other side.
        var gone = binding.Colliders.ToHashSet();
        foreach (var pair in _touching.Where(p => gone.Contains(p.A) || gone.Contains(p.B)).ToList())
        {
            _touching.Remove(pair);
            CollisionExit(pair.A, pair.B);
        }

        foreach (var pair in _overlapping.Where(p => gone.Contains(p.A) || gone.Contains(p.B)).ToList())
        {
            _overlapping.Remove(pair);
            TriggerExit(pair.A, pair.B);
        }
    }

    /// <summary>Every object is gone: free the Box2D world and detach, so a restarted scene starts clean.</summary>
    private void OnSceneUnloaded(Scene scene)
    {
        scene.Unloaded -= OnSceneUnloaded;
        _dirty.Clear();
        foreach (var binding in _bindings.Values)
        {
            if (binding.Rigidbody is { } rigidbody)
            {
                rigidbody.HasBody = false;
            }
        }

        _bindings.Clear();
        ReleaseWorldIfEmpty();
        scene.Systems.Remove(this);
    }

    private void EnsureWorld()
    {
        if (_hasWorld)
        {
            return;
        }

        var definition = b2DefaultWorldDef();
        definition.gravity = Rigidbody2D.Convert(_gravity);
        lock (WorldTable)
        {
            _world = b2CreateWorld(in definition);
        }

        _hasWorld = true;
    }

    private void ReleaseWorldIfEmpty()
    {
        if (!_hasWorld || _bindings.Count > 0)
        {
            return;
        }

        lock (WorldTable)
        {
            b2DestroyWorld(_world);
        }

        _hasWorld = false;
        _shapes.Clear();
        _touching.Clear();
        _overlapping.Clear();
    }

    /// <summary>Applies transform changes made by game code: teleports move the body, scale changes rebuild it.</summary>
    private void SyncToPhysics()
    {
        foreach (var binding in _bindings.Values.ToList())
        {
            var transform = binding.Owner.Transform;
            if (transform.LossyScale != binding.Scale)
            {
                Rebuild(binding.Owner);
            }
            else if (transform.Position != binding.Position || transform.Rotation != binding.Rotation)
            {
                b2Body_SetTransform(binding.Body, Rigidbody2D.Convert(transform.Position), b2MakeRot(transform.Rotation * Mathf.Deg2Rad));
                binding.Position = transform.Position;
                binding.Rotation = transform.Rotation;
            }
        }
    }

    private void SyncFromPhysics()
    {
        foreach (var binding in _bindings.Values)
        {
            if (binding.Rigidbody is not { BodyType: not RigidbodyType2D.Static })
            {
                continue;
            }

            var transform = binding.Owner.Transform;
            var rotation = b2Body_GetRotation(binding.Body);
            transform.Position = Rigidbody2D.Convert(b2Body_GetPosition(binding.Body));
            transform.Rotation = b2Rot_GetAngle(in rotation) * Mathf.Rad2Deg;

            // Store what the transform reports (after any parent conversion) so it is not mistaken for a teleport.
            binding.Position = transform.Position;
            binding.Rotation = transform.Rotation;
        }
    }

    private void DispatchEvents()
    {
        // Copy everything first: callbacks may add or remove bodies, which invalidates Box2D's event buffers.
        var events = new List<(bool Begin, bool Trigger, Collider2D A, Collider2D B, Vector2 Normal, Vector2 Point)>();
        var contacts = b2World_GetContactEvents(_world);
        for (var i = 0; i < contacts.beginCount; i++)
        {
            var e = contacts.beginEvents[i];
            if (Resolve(e.shapeIdA, e.shapeIdB) is var (a, b))
            {
                var normal = Vector2.Zero;
                var point = Vector2.Zero;
                if (b2Contact_IsValid(e.contactId))
                {
                    var manifold = b2Contact_GetData(e.contactId).manifold;
                    normal = Rigidbody2D.Convert(manifold.normal);
                    point = manifold.pointCount > 0 ? Rigidbody2D.Convert(manifold.points[0].point) : Vector2.Zero;
                }

                events.Add((true, false, a, b, normal, point));
            }
        }

        for (var i = 0; i < contacts.endCount; i++)
        {
            var e = contacts.endEvents[i];
            if (Resolve(e.shapeIdA, e.shapeIdB) is var (a, b))
            {
                events.Add((false, false, a, b, Vector2.Zero, Vector2.Zero));
            }
        }

        var sensors = b2World_GetSensorEvents(_world);
        for (var i = 0; i < sensors.beginCount; i++)
        {
            var e = sensors.beginEvents[i];
            if (Resolve(e.sensorShapeId, e.visitorShapeId) is var (a, b))
            {
                events.Add((true, true, a, b, Vector2.Zero, Vector2.Zero));
            }
        }

        for (var i = 0; i < sensors.endCount; i++)
        {
            var e = sensors.endEvents[i];
            if (Resolve(e.sensorShapeId, e.visitorShapeId) is var (a, b))
            {
                events.Add((false, true, a, b, Vector2.Zero, Vector2.Zero));
            }
        }

        foreach (var (begin, trigger, a, b, normal, point) in events)
        {
            var pair = new Pair(a, b);
            var set = trigger ? _overlapping : _touching;
            if (begin ? !set.Add(pair) : !set.Remove(pair))
            {
                continue;
            }

            switch (begin, trigger)
            {
                case (true, false):
                    Dispatch<ICollisionHandler2D>(a.GameObject, h => h.OnCollisionEnter2D(new Collision2D(a, b, normal, point)), "OnCollisionEnter2D");
                    Dispatch<ICollisionHandler2D>(b.GameObject, h => h.OnCollisionEnter2D(new Collision2D(b, a, -normal, point)), "OnCollisionEnter2D");
                    break;
                case (false, false):
                    CollisionExit(a, b);
                    break;
                case (true, true):
                    Dispatch<ITriggerHandler2D>(a.GameObject, h => h.OnTriggerEnter2D(b), "OnTriggerEnter2D");
                    Dispatch<ITriggerHandler2D>(b.GameObject, h => h.OnTriggerEnter2D(a), "OnTriggerEnter2D");
                    break;
                default:
                    TriggerExit(a, b);
                    break;
            }
        }
    }

    private (Collider2D, Collider2D)? Resolve(B2ShapeId a, B2ShapeId b) =>
        _shapes.TryGetValue(Key(a), out var first) && _shapes.TryGetValue(Key(b), out var second) ? (first, second) : null;

    private void CollisionExit(Collider2D a, Collider2D b)
    {
        Dispatch<ICollisionHandler2D>(a.GameObject, h => h.OnCollisionExit2D(new Collision2D(a, b, Vector2.Zero, Vector2.Zero)), "OnCollisionExit2D");
        Dispatch<ICollisionHandler2D>(b.GameObject, h => h.OnCollisionExit2D(new Collision2D(b, a, Vector2.Zero, Vector2.Zero)), "OnCollisionExit2D");
    }

    private void TriggerExit(Collider2D a, Collider2D b)
    {
        Dispatch<ITriggerHandler2D>(a.GameObject, h => h.OnTriggerExit2D(b), "OnTriggerExit2D");
        Dispatch<ITriggerHandler2D>(b.GameObject, h => h.OnTriggerExit2D(a), "OnTriggerExit2D");
    }

    private void Dispatch<T>(GameObject target, Action<T> callback, string name)
        where T : class
    {
        if (target.IsDestroyed)
        {
            return;
        }

        foreach (var component in target.GetComponents<Component>().ToArray())
        {
            if (component is T handler && component.IsActiveAndEnabled)
            {
                _scene.InvokeCallback(component, _ => callback(handler), name);
            }
        }
    }

    private static ShapeKey Key(B2ShapeId id) => new(id.index1, id.world0, id.generation);

    private readonly record struct ShapeKey(int Index, ushort World, ushort Generation);

    /// <summary>Unordered pair of colliders.</summary>
    private readonly struct Pair(Collider2D a, Collider2D b) : IEquatable<Pair>
    {
        public Collider2D A { get; } = a;

        public Collider2D B { get; } = b;

        public bool Equals(Pair other) => (A == other.A && B == other.B) || (A == other.B && B == other.A);

        public override bool Equals(object? obj) => obj is Pair other && Equals(other);

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(A) ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(B);
    }

    private sealed class Binding(GameObject owner, B2BodyId body, Rigidbody2D? rigidbody, List<Collider2D> colliders, Vector2 position, float rotation, Vector2 scale)
    {
        public GameObject Owner { get; } = owner;

        public B2BodyId Body { get; } = body;

        public Rigidbody2D? Rigidbody { get; } = rigidbody;

        public List<Collider2D> Colliders { get; } = colliders;

        public List<B2ShapeId> Shapes { get; } = [];

        public Vector2 Position { get; set; } = position;

        public float Rotation { get; set; } = rotation;

        public Vector2 Scale { get; } = scale;
    }
}
