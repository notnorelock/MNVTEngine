using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuPhysics.Trees;
using BepuUtilities;
using BepuUtilities.Memory;
using MNVTEngine.ECS;
using MNVTEngine.Mathematics;
using NQuaternion = System.Numerics.Quaternion;
using NVector3 = System.Numerics.Vector3;
using Quaternion = MNVTEngine.Mathematics.Quaternion;
using Vector3 = MNVTEngine.Mathematics.Vector3;

namespace MNVTEngine;

/// <summary>
/// The 3D physics simulation of one scene: a BepuPhysics v2 simulation stepped (single-threaded, deterministic) after
/// the components' FixedUpdate. Created by the first 3D physics component and released with the last one. Like the
/// 2D <c>PhysicsWorld2D</c>: changed GameObjects are rebuilt at the next settled point; transforms are synchronised
/// before (teleports, scale) and after (simulated motion) each step; then collision and trigger callbacks run. Bepu has
/// no contact events, so enter and exit are found by comparing the touching collider pairs of consecutive steps.
/// </summary>
public sealed class PhysicsWorld3D : GameSystem, IDisposable
{
    private readonly Scene _scene;
    private readonly Dictionary<GameObject, Binding> _bindings = [];
    private readonly Dictionary<BodyHandle, Binding> _bodies = [];
    private readonly Dictionary<StaticHandle, Binding> _statics = [];
    private readonly HashSet<GameObject> _dirty = [];
    private readonly Dictionary<Pair, (Vector3 Normal, Vector3 Point)> _stepContacts = [];
    private readonly HashSet<Pair> _stepOverlaps = [];
    private readonly HashSet<Pair> _touching = [];
    private readonly HashSet<Pair> _overlapping = [];
    private BufferPool? _pool;
    private Simulation? _simulation;
    private bool _flushing;
    private float _stepDelta = 1f / 50f;

    private PhysicsWorld3D(Scene scene)
    {
        _scene = scene;
        scene.Unloaded += OnSceneUnloaded;
    }

    /// <summary>Runs after ECS systems with the default order, so systems can apply forces before the step.</summary>
    public override int Order => 1001;

    public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);

    /// <summary>GameObjects with a physics body or static collider.</summary>
    public int BodyCount => _bindings.Count;

    /// <summary>Collider pairs touching (non-trigger contacts).</summary>
    public int ContactCount => _touching.Count;

    /// <summary>Collider pairs overlapping a trigger.</summary>
    public int TriggerOverlapCount => _overlapping.Count;

    public static PhysicsWorld3D For(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.Systems.Get<PhysicsWorld3D>() is { } existing)
        {
            return existing;
        }

        var world = new PhysicsWorld3D(scene);
        scene.Systems.Add(world);
        return world;
    }

    /// <summary>Closest non-trigger collider along the ray, or null. Colliders containing the origin are ignored.</summary>
    public RaycastHit? Raycast(Vector3 origin, Vector3 direction, float distance)
    {
        Flush();
        if (_simulation is null || direction == Vector3.Zero || !(distance > 0f))
        {
            return null;
        }

        var dir = direction.Normalized;
        var handler = new RayHandler { World = this };
        _simulation.RayCast(N(origin), N(dir), Math.Min(distance, 1e6f), ref handler);
        return handler.Collider is { } collider
            ? new RaycastHit(collider, origin + (dir * handler.T), E(handler.Normal).Normalized, handler.T)
            : null;
    }

    public override void FixedUpdate(World world, float fixedDeltaTime)
    {
        Flush();
        if (_simulation is null)
        {
            return;
        }

        _stepDelta = fixedDeltaTime;
        SyncToPhysics();
        PrepareBodies(fixedDeltaTime);
        Core.Diagnostics.Profiler.Count("Physics 3D bodies", _bindings.Count);
        _stepContacts.Clear();
        _stepOverlaps.Clear();
        _simulation.Timestep(fixedDeltaTime);
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

    internal static Vector3? Velocity(Rigidbody body) => Body(body) is { } b ? E(b.Velocity.Linear) : null;

    internal static Vector3? AngularVelocity(Rigidbody body) => Body(body) is { } b ? E(b.Velocity.Angular) * Mathf.Rad2Deg : null;

    internal static void SetVelocity(Rigidbody body, Vector3? linear, Vector3? angularDegrees)
    {
        if (Body(body) is { } b)
        {
            if (linear is { } v)
            {
                b.Velocity.Linear = N(v);
            }

            if (angularDegrees is { } w)
            {
                b.Velocity.Angular = N(w * Mathf.Deg2Rad);
            }

            b.Awake = true;
        }
    }

    internal static void AddForce(Rigidbody body, Vector3 force, ForceMode mode)
    {
        if (body.BodyType != RigidbodyType.Dynamic || BindingOf(body) is not { } binding || Body(body) is not { } b)
        {
            return;
        }

        switch (mode)
        {
            case ForceMode.Force:
                binding.PendingForce += force;
                break;
            case ForceMode.Impulse:
                b.ApplyLinearImpulse(N(force));
                break;
            default:
                b.Velocity.Linear += N(force);
                break;
        }

        b.Awake = true;
    }

    internal static void AddTorque(Rigidbody body, Vector3 torque, ForceMode mode)
    {
        if (body.BodyType != RigidbodyType.Dynamic || BindingOf(body) is not { } binding || Body(body) is not { } b)
        {
            return;
        }

        switch (mode)
        {
            case ForceMode.Force:
                binding.PendingTorque += torque;
                break;
            case ForceMode.Impulse:
                b.ApplyAngularImpulse(N(torque));
                break;
            default:
                b.Velocity.Angular += N(torque * Mathf.Deg2Rad);
                break;
        }

        b.Awake = true;
    }

    /// <summary>Releases the simulation (also done when the scene unloads or the last body goes).</summary>
    public void Dispose() => ReleaseSimulation();

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

    private static Binding? BindingOf(Rigidbody body)
    {
        if (body.GameObject is not { } owner || !owner.Scene.IsRunning || owner.Scene.Systems.Get<PhysicsWorld3D>() is not { } world)
        {
            return null;
        }

        world.Flush();
        return world._bindings.GetValueOrDefault(owner) is { Rigidbody: not null } binding && ReferenceEquals(binding.Rigidbody, body) ? binding : null;
    }

    private static BodyReference? Body(Rigidbody body) =>
        BindingOf(body) is { Body: { } handle } && body.GameObject.Scene.Systems.Get<PhysicsWorld3D>()!._simulation is { } simulation ? simulation.Bodies[handle] : null;

    private void Rebuild(GameObject owner)
    {
        Vector3? velocity = null;
        Vector3 angularVelocity = default;
        if (_bindings.Remove(owner, out var old))
        {
            if (old.Body is { } handle && _simulation!.Bodies.BodyExists(handle))
            {
                var v = _simulation.Bodies[handle].Velocity;
                velocity = E(v.Linear);
                angularVelocity = E(v.Angular) * Mathf.Rad2Deg;
            }

            DestroyBinding(old);
        }

        var alive = !owner.IsDestroyed && owner.ActiveInHierarchy;
        var rigidbody = alive ? owner.GetComponent<Rigidbody>() : null;
        if (rigidbody is { IsActiveAndEnabled: false } or { IsDestroyed: true })
        {
            rigidbody = null;
        }

        var colliders = alive ? owner.GetComponents<Collider>().Where(c => c.IsActiveAndEnabled && !c.IsDestroyed).ToList() : [];
        if (colliders.Count == 0)
        {
            // A rigidbody without colliders has nothing to simulate against; it simply does not move.
            ReleaseIfEmpty();
            return;
        }

        EnsureSimulation();
        var simulation = _simulation!;
        var transform = owner.Transform;
        var scale = Vector3.Abs(transform.LossyScale);
        var rotation = transform.Rotation;
        var kind = rigidbody?.BodyType ?? RigidbodyType.Static;
        var mass = rigidbody?.Mass ?? 1f;

        // One collider without an offset is the body's shape; several (or an offset) form a compound.
        TypedIndex shapeIndex;
        BodyInertia inertia = default;
        var comOffset = NVector3.Zero;
        var compound = colliders.Count > 1 || colliders[0].Center != Vector3.Zero;
        if (!compound)
        {
            (shapeIndex, inertia) = AddShape(simulation, colliders[0], scale, mass);
        }
        else
        {
            using var builder = new CompoundBuilder(_pool!, simulation.Shapes, colliders.Count);
            foreach (var collider in colliders)
            {
                var localPose = new RigidPose(N(collider.Center * scale));
                switch (collider)
                {
                    case BoxCollider box:
                        builder.Add(BoxShape(box, scale), localPose, 1f);
                        break;
                    case SphereCollider sphere:
                        builder.Add(SphereShape(sphere, scale), localPose, 1f);
                        break;
                    case CapsuleCollider capsule:
                        builder.Add(CapsuleShape(capsule, scale), localPose, 1f);
                        break;
                }
            }

            if (kind == RigidbodyType.Dynamic)
            {
                builder.BuildDynamicCompound(out var children, out inertia, out comOffset);
                inertia = Scale(inertia, mass / colliders.Count); // children were added with weight 1 each
                shapeIndex = simulation.Shapes.Add(new Compound(children));
            }
            else
            {
                builder.BuildKinematicCompound(out var children);
                shapeIndex = simulation.Shapes.Add(new Compound(children));
            }
        }

        if (rigidbody?.FreezeRotation == true)
        {
            inertia.InverseInertiaTensor = default;
        }

        var pose = new RigidPose(N(transform.Position) + NVector3.Transform(comOffset, N(rotation)), N(rotation));
        var binding = new Binding(owner, rigidbody, colliders, shapeIndex, compound, E(comOffset), transform.Position, rotation, scale);
        var continuity = rigidbody?.Continuous == true ? ContinuousDetection.Continuous() : ContinuousDetection.Discrete;
        if (kind == RigidbodyType.Static)
        {
            binding.Static = simulation.Statics.Add(new StaticDescription(pose, shapeIndex));
            _statics[binding.Static.Value] = binding;
        }
        else
        {
            var collidable = new CollidableDescription(shapeIndex, continuity);
            var description = kind == RigidbodyType.Dynamic
                ? BodyDescription.CreateDynamic(pose, new BodyVelocity(N(velocity ?? rigidbody!.InitialVelocity), N((velocity is null ? rigidbody!.InitialAngularVelocity : angularVelocity) * Mathf.Deg2Rad)), inertia, collidable, new BodyActivityDescription(0.01f))
                : BodyDescription.CreateKinematic(pose, collidable, new BodyActivityDescription(0.01f));
            binding.Body = simulation.Bodies.Add(description);
            _bodies[binding.Body.Value] = binding;
        }

        _bindings[owner] = binding;
    }

    private static (TypedIndex Shape, BodyInertia Inertia) AddShape(Simulation simulation, Collider collider, Vector3 scale, float mass)
    {
        switch (collider)
        {
            case BoxCollider box:
            {
                var shape = BoxShape(box, scale);
                return (simulation.Shapes.Add(shape), shape.ComputeInertia(mass));
            }

            case SphereCollider sphere:
            {
                var shape = SphereShape(sphere, scale);
                return (simulation.Shapes.Add(shape), shape.ComputeInertia(mass));
            }

            case CapsuleCollider capsule:
            {
                var shape = CapsuleShape(capsule, scale);
                return (simulation.Shapes.Add(shape), shape.ComputeInertia(mass));
            }

            default:
                throw new NotSupportedException($"Collider type {collider.GetType().Name} is not supported.");
        }
    }

    private static Box BoxShape(BoxCollider box, Vector3 scale) => new(box.Size.X * scale.X, box.Size.Y * scale.Y, box.Size.Z * scale.Z);

    private static Sphere SphereShape(SphereCollider sphere, Vector3 scale) => new(sphere.Radius * MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)));

    private static Capsule CapsuleShape(CapsuleCollider capsule, Vector3 scale)
    {
        var radius = capsule.Radius * MathF.Max(scale.X, scale.Z);
        var height = capsule.Height * scale.Y;
        return new Capsule(radius, MathF.Max(0f, height - (2f * radius)));
    }

    private static BodyInertia Scale(BodyInertia inertia, float factor)
    {
        // Inverse values scale by 1/factor when the mass scales by factor.
        inertia.InverseMass /= factor;
        inertia.InverseInertiaTensor.XX /= factor;
        inertia.InverseInertiaTensor.YX /= factor;
        inertia.InverseInertiaTensor.YY /= factor;
        inertia.InverseInertiaTensor.ZX /= factor;
        inertia.InverseInertiaTensor.ZY /= factor;
        inertia.InverseInertiaTensor.ZZ /= factor;
        return inertia;
    }

    private void DestroyBinding(Binding binding)
    {
        var simulation = _simulation!;
        if (binding.Body is { } body)
        {
            _bodies.Remove(body);
            simulation.Bodies.Remove(body);
        }

        if (binding.Static is { } handle)
        {
            _statics.Remove(handle);
            simulation.Statics.Remove(handle);
        }

        if (binding.IsCompound)
        {
            simulation.Shapes.RecursivelyRemoveAndDispose(binding.Shape, _pool!);
        }
        else
        {
            simulation.Shapes.Remove(binding.Shape);
        }

        // Removed colliders end their contacts and overlaps.
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

    private void OnSceneUnloaded(Scene scene)
    {
        scene.Unloaded -= OnSceneUnloaded;
        _dirty.Clear();
        _bindings.Clear();
        _bodies.Clear();
        _statics.Clear();
        ReleaseSimulation();
        scene.Systems.Remove(this);
    }

    private void EnsureSimulation()
    {
        if (_simulation is not null)
        {
            return;
        }

        _pool = new BufferPool();
        _simulation = Simulation.Create(_pool, new NarrowPhase { World = this }, new PoseIntegrator { World = this }, new SolveDescription(8, 1));
    }

    private void ReleaseIfEmpty()
    {
        if (_bindings.Count == 0)
        {
            ReleaseSimulation();
        }
    }

    private void ReleaseSimulation()
    {
        _simulation?.Dispose();
        _simulation = null;
        _pool?.Clear();
        _pool = null;
        _touching.Clear();
        _overlapping.Clear();
    }

    /// <summary>Applies transform changes made by game code: teleports move the body, scale changes rebuild it.</summary>
    private void SyncToPhysics()
    {
        foreach (var binding in _bindings.Values.ToList())
        {
            var transform = binding.Owner.Transform;
            if (Vector3.Abs(transform.LossyScale) != binding.Scale)
            {
                Rebuild(binding.Owner);
            }
            else if (transform.Position != binding.Position || transform.Rotation != binding.Rotation)
            {
                var pose = new RigidPose(N(transform.Position) + NVector3.Transform(N(binding.ComOffset), N(transform.Rotation)), N(transform.Rotation));
                if (binding.Body is { } body)
                {
                    var reference = _simulation!.Bodies[body];
                    reference.Pose = pose;
                    reference.Awake = true;
                }
                else if (binding.Static is { } handle)
                {
                    var reference = _simulation!.Statics[handle];
                    reference.Pose = pose;
                    reference.UpdateBounds();
                }

                (binding.Position, binding.Rotation) = (transform.Position, transform.Rotation);
            }
        }
    }

    /// <summary>Forces, gravity opt-out and damping, which Bepu leaves to the caller.</summary>
    private void PrepareBodies(float dt)
    {
        foreach (var binding in _bindings.Values)
        {
            if (binding.Body is not { } handle || binding.Rigidbody is not { BodyType: RigidbodyType.Dynamic } rigidbody)
            {
                continue;
            }

            var body = _simulation!.Bodies[handle];
            if (binding.PendingForce != Vector3.Zero)
            {
                body.ApplyLinearImpulse(N(binding.PendingForce * dt));
                binding.PendingForce = Vector3.Zero;
            }

            if (binding.PendingTorque != Vector3.Zero)
            {
                body.ApplyAngularImpulse(N(binding.PendingTorque * dt));
                binding.PendingTorque = Vector3.Zero;
            }

            if (!body.Awake)
            {
                continue;
            }

            if (!rigidbody.UseGravity)
            {
                body.Velocity.Linear -= N(Gravity * dt); // the integrator adds gravity to every dynamic body
            }

            body.Velocity.Linear *= MathF.Max(0f, 1f - (rigidbody.LinearDamping * dt));
            body.Velocity.Angular *= MathF.Max(0f, 1f - (rigidbody.AngularDamping * dt));
        }
    }

    private void SyncFromPhysics()
    {
        foreach (var binding in _bindings.Values)
        {
            if (binding.Body is not { } handle)
            {
                continue;
            }

            var pose = _simulation!.Bodies[handle].Pose;
            var rotation = E(pose.Orientation);
            var transform = binding.Owner.Transform;
            transform.Rotation = rotation;
            transform.Position = E(pose.Position) - (rotation * binding.ComOffset);

            // Store what the transform reports (after any parent conversion) so it is not mistaken for a teleport.
            (binding.Position, binding.Rotation) = (transform.Position, transform.Rotation);
        }
    }

    private void DispatchEvents()
    {
        var events = new List<(bool Begin, bool Trigger, Collider A, Collider B, Vector3 Normal, Vector3 Point)>();
        foreach (var (pair, contact) in _stepContacts)
        {
            if (!_touching.Contains(pair))
            {
                events.Add((true, false, pair.A, pair.B, contact.Normal, contact.Point));
            }
        }

        // Bepu skips sleeping pairs in the narrow phase: they still touch.
        foreach (var pair in _touching.Where(p => !_stepContacts.ContainsKey(p) && !Asleep(p)).ToList())
        {
            events.Add((false, false, pair.A, pair.B, Vector3.Zero, Vector3.Zero));
        }

        foreach (var pair in _stepOverlaps.Where(p => !_overlapping.Contains(p)))
        {
            events.Add((true, true, pair.A, pair.B, Vector3.Zero, Vector3.Zero));
        }

        foreach (var pair in _overlapping.Where(p => !_stepOverlaps.Contains(p) && !Asleep(p)).ToList())
        {
            events.Add((false, true, pair.A, pair.B, Vector3.Zero, Vector3.Zero));
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
                    Dispatch<ICollisionHandler>(a.GameObject, h => h.OnCollisionEnter(new Collision(a, b, normal, point)), "OnCollisionEnter");
                    Dispatch<ICollisionHandler>(b.GameObject, h => h.OnCollisionEnter(new Collision(b, a, -normal, point)), "OnCollisionEnter");
                    break;
                case (false, false):
                    CollisionExit(a, b);
                    break;
                case (true, true):
                    Dispatch<ITriggerHandler>(a.GameObject, h => h.OnTriggerEnter(b), "OnTriggerEnter");
                    Dispatch<ITriggerHandler>(b.GameObject, h => h.OnTriggerEnter(a), "OnTriggerEnter");
                    break;
                default:
                    TriggerExit(a, b);
                    break;
            }
        }
    }

    /// <summary>Neither collider's body is simulated this step (asleep, or static).</summary>
    private bool Asleep(Pair pair) => Asleep(pair.A) && Asleep(pair.B);

    private bool Asleep(Collider collider) =>
        !_bindings.TryGetValue(collider.GameObject, out var binding) || binding.Body is not { } body || !_simulation!.Bodies[body].Awake;

    private void CollisionExit(Collider a, Collider b)
    {
        Dispatch<ICollisionHandler>(a.GameObject, h => h.OnCollisionExit(new Collision(a, b, Vector3.Zero, Vector3.Zero)), "OnCollisionExit");
        Dispatch<ICollisionHandler>(b.GameObject, h => h.OnCollisionExit(new Collision(b, a, Vector3.Zero, Vector3.Zero)), "OnCollisionExit");
    }

    private void TriggerExit(Collider a, Collider b)
    {
        Dispatch<ITriggerHandler>(a.GameObject, h => h.OnTriggerExit(b), "OnTriggerExit");
        Dispatch<ITriggerHandler>(b.GameObject, h => h.OnTriggerExit(a), "OnTriggerExit");
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

    private Binding? Resolve(CollidableReference collidable) =>
        collidable.Mobility == CollidableMobility.Static ? _statics.GetValueOrDefault(collidable.StaticHandle) : _bodies.GetValueOrDefault(collidable.BodyHandle);

    private Collider? ColliderOf(CollidableReference collidable, int child) =>
        Resolve(collidable) is { } binding ? binding.Colliders[binding.IsCompound ? Math.Clamp(child, 0, binding.Colliders.Count - 1) : 0] : null;

    /// <summary>Narrow phase: whether a pair (or child pair of compounds) collides, and records touching and overlapping pairs.</summary>
    /// <param name="touching">Whether a contact of the manifold has non-negative depth; offset and normal are the deepest one's.</param>
    private bool Configure(CollidablePair pair, int childA, int childB, bool touching, NVector3 offset, NVector3 normal, out PairMaterialProperties material)
    {
        material = new PairMaterialProperties(0.6f, 2f, new SpringSettings(30f, 1f));
        if (ColliderOf(pair.A, childA) is not { } a || ColliderOf(pair.B, childB) is not { } b)
        {
            return false;
        }

        if (a.IsTrigger || b.IsTrigger)
        {
            if (touching)
            {
                _stepOverlaps.Add(new Pair(a, b));
            }

            return false; // triggers report overlaps and never push
        }

        material = new PairMaterialProperties(MathF.Sqrt(a.Friction * b.Friction), 2f, new SpringSettings(30f, 1f - (0.9f * MathF.Max(a.Bounciness, b.Bounciness))));
        if (touching)
        {
            var origin = Resolve(pair.A) is { } binding ? PositionOf(binding) : NVector3.Zero;
            // Bepu's normal points from B to A; Collision.Normal points from the collider (A) to the other (B), as in 2D.
            _stepContacts.TryAdd(new Pair(a, b), (-E(normal), E(origin + offset)));
        }

        return true;
    }

    private NVector3 PositionOf(Binding binding) =>
        binding.Body is { } body ? _simulation!.Bodies[body].Pose.Position : binding.Static is { } handle ? _simulation!.Statics[handle].Pose.Position : NVector3.Zero;

    private bool AllowContacts(CollidableReference a, CollidableReference b)
    {
        if (Resolve(a) is not { } first || Resolve(b) is not { } second || ReferenceEquals(first, second))
        {
            return false;
        }

        // Static and kinematic bodies do not collide with each other, but triggers still report them.
        return a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic
            || first.Colliders.Any(c => c.IsTrigger) || second.Colliders.Any(c => c.IsTrigger);
    }

    private static NVector3 N(Vector3 v) => new(v.X, v.Y, v.Z);

    private static NQuaternion N(Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    private static Vector3 E(NVector3 v) => new(v.X, v.Y, v.Z);

    private static Quaternion E(NQuaternion q) => new(q.X, q.Y, q.Z, q.W);

    private sealed class Binding(GameObject owner, Rigidbody? rigidbody, List<Collider> colliders, TypedIndex shape, bool isCompound, Vector3 comOffset, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        public GameObject Owner { get; } = owner;

        public Rigidbody? Rigidbody { get; } = rigidbody;

        public List<Collider> Colliders { get; } = colliders;

        public TypedIndex Shape { get; } = shape;

        public bool IsCompound { get; } = isCompound;

        /// <summary>Center of mass in the GameObject's frame (compounds are recentered on it).</summary>
        public Vector3 ComOffset { get; } = comOffset;

        public BodyHandle? Body { get; set; }

        public StaticHandle? Static { get; set; }

        public Vector3 Position { get; set; } = position;

        public Quaternion Rotation { get; set; } = rotation;

        public Vector3 Scale { get; } = scale;

        public Vector3 PendingForce { get; set; }

        public Vector3 PendingTorque { get; set; }
    }

    private readonly record struct Pair(Collider A, Collider B)
    {
        public bool Equals(Pair other) => (ReferenceEquals(A, other.A) && ReferenceEquals(B, other.B)) || (ReferenceEquals(A, other.B) && ReferenceEquals(B, other.A));

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(A) ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(B);
    }

    private struct NarrowPhase : INarrowPhaseCallbacks
    {
        public PhysicsWorld3D World;

        public readonly void Initialize(Simulation simulation)
        {
        }

        public readonly bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin) => World.AllowContacts(a, b);

        public readonly bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

        public readonly bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            var (touching, offset, normal) = Deepest(ref manifold);
            return World.Configure(pair, 0, 0, touching, offset, normal, out pairMaterial);
        }

        public readonly bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold)
        {
            // Compound children: record per child collider; the parent pair's material comes from the top-level call.
            var (touching, offset, normal) = Deepest(ref manifold);
            return World.Configure(pair, childIndexA, childIndexB, touching, offset, normal, out _);
        }

        private static (bool Touching, NVector3 Offset, NVector3 Normal) Deepest<TManifold>(ref TManifold manifold)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            var best = (false, NVector3.Zero, NVector3.Zero);
            var bestDepth = float.MinValue;
            for (var i = 0; i < manifold.Count; i++)
            {
                manifold.GetContact(i, out var offset, out var normal, out var depth, out _);
                if (depth >= 0f && depth > bestDepth)
                {
                    (best, bestDepth) = ((true, offset, normal), depth);
                }
            }

            return best;
        }

        public readonly void Dispose()
        {
        }
    }

    private struct PoseIntegrator : IPoseIntegratorCallbacks
    {
        public PhysicsWorld3D World;
        private Vector3Wide _gravityDt;

        public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;

        public readonly bool AllowSubstepsForUnconstrainedBodies => false;

        public readonly bool IntegrateVelocityForKinematics => false;

        public readonly void Initialize(Simulation simulation)
        {
        }

        public void PrepareForIntegration(float dt) => _gravityDt = Vector3Wide.Broadcast(N(World.Gravity * dt));

        public readonly void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity) =>
            velocity.Linear += _gravityDt;
    }

    private struct RayHandler : IRayHitHandler
    {
        public PhysicsWorld3D World;
        public Collider? Collider;
        public float T;
        public NVector3 Normal;

        public readonly bool AllowTest(CollidableReference collidable) => World.Resolve(collidable) is not null;

        public readonly bool AllowTest(CollidableReference collidable, int childIndex) => World.ColliderOf(collidable, childIndex) is { IsTrigger: false };

        public void OnRayHit(in RayData ray, ref float maximumT, float t, in NVector3 normal, CollidableReference collidable, int childIndex)
        {
            if (t <= 0f || t >= maximumT || World.ColliderOf(collidable, childIndex) is not { IsTrigger: false } collider)
            {
                return; // inside the collider at the start, farther than a hit found already, or a trigger
            }

            maximumT = t;
            (Collider, T, Normal) = (collider, t, normal);
        }
    }
}
