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
public sealed partial class PhysicsWorld3D : GameSystem, IDisposable
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
    private readonly List<(ConstraintHandle Constraint, BodyHandle? Anchor)> _joints = [];
    private readonly Dictionary<CharacterController, (BodyHandle Body, float Radius, float Height)> _characters = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BodyHandle, CharacterController> _characterBodies = [];
    private readonly uint[] _layerMasks = Enumerable.Repeat(uint.MaxValue, 32).ToArray(); // bit b of entry a: layers a and b collide
    private readonly Lock _eventLock = new();
    private BufferPool? _pool;
    private Simulation? _simulation;
    private ThreadDispatcher? _dispatcher;
    private int _threadCount = 1;
    private bool _flushing;
    private bool _jointsDirty;
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

    /// <summary>Joints connecting bodies (or bodies to the world).</summary>
    public int JointCount => _joints.Count;

    /// <summary>Worker threads for stepping: 1 (default) is single-threaded and deterministic.</summary>
    public int ThreadCount
    {
        get => _threadCount;
        set
        {
            var count = Math.Clamp(value, 1, Environment.ProcessorCount);
            if (count != _threadCount)
            {
                _threadCount = count;
                _dispatcher?.Dispose();
                _dispatcher = count > 1 ? new ThreadDispatcher(count) : null;
            }
        }
    }

    /// <summary>Makes two layers pass through each other (or collide again).</summary>
    public void IgnoreLayerCollision(int a, int b, bool ignore)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual((uint)a < 32 && (uint)b < 32, true);
        if (ignore)
        {
            _layerMasks[a] &= ~(1u << b);
            _layerMasks[b] &= ~(1u << a);
        }
        else
        {
            _layerMasks[a] |= 1u << b;
            _layerMasks[b] |= 1u << a;
        }
    }

    public bool LayersCollide(int a, int b) => (_layerMasks[a] & (1u << b)) != 0;

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
    public RaycastHit? Raycast(Vector3 origin, Vector3 direction, float distance, uint layerMask = uint.MaxValue)
    {
        Flush();
        if (_simulation is null || direction == Vector3.Zero || !(distance > 0f))
        {
            return null;
        }

        var dir = direction.Normalized;
        var handler = new RayHandler { World = this, LayerMask = layerMask };
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
        _simulation.Timestep(fixedDeltaTime, _dispatcher);
        SyncFromPhysics();
        DispatchEvents();
    }

    /// <summary>Recreates every joint of the scene's bodies (after bodies were rebuilt, or a joint changed).</summary>
    private void RebuildJoints()
    {
        _jointsDirty = false;
        var simulation = _simulation!;
        foreach (var (constraint, anchor) in _joints)
        {
            if (simulation.Solver.ConstraintExists(constraint))
            {
                simulation.Solver.Remove(constraint);
            }

            if (anchor is { } body && simulation.Bodies.BodyExists(body))
            {
                simulation.Bodies.Remove(body);
            }
        }

        _joints.Clear();
        foreach (var binding in _bindings.Values.ToList())
        {
            foreach (var joint in binding.Owner.GetComponents<Joint>())
            {
                if (joint.IsActiveAndEnabled && binding.Body is { } body)
                {
                    AddJoint(joint, binding, body);
                }
            }
        }
    }

    private void AddJoint(Joint joint, Binding binding, BodyHandle bodyA)
    {
        var simulation = _simulation!;
        var transform = binding.Owner.Transform;
        var worldAnchor = transform.LocalToWorldMatrix.MultiplyPoint3x4(joint.Anchor);
        var poseA = simulation.Bodies[bodyA].Pose;
        BodyHandle bodyB;
        BodyHandle? anchorBody = null;
        if (joint.ConnectedBody is { } connected && _bindings.TryGetValue(connected.GameObject, out var other) && other.Body is { } otherBody)
        {
            bodyB = otherBody;
        }
        else
        {
            if (joint.ConnectedBody is not null)
            {
                Warn($"'{binding.Owner.Name}': the {joint.GetType().Name}'s connected body has no colliders or is static; the joint holds to the world instead.");
            }

            // A shapeless kinematic body holds the joint to the world.
            bodyB = simulation.Bodies.Add(BodyDescription.CreateKinematic(new RigidPose(N(worldAnchor)), new CollidableDescription(default, 0f), new BodyActivityDescription(-1f)));
            anchorBody = bodyB;
        }

        var poseB = simulation.Bodies[bodyB].Pose;
        NVector3 Local(RigidPose pose, NVector3 world) => NVector3.Transform(world - pose.Position, NQuaternion.Conjugate(pose.Orientation));
        var offsetA = Local(poseA, N(worldAnchor));
        var offsetB = Local(poseB, N(worldAnchor));
        var spring = new SpringSettings(30f, 1f);
        ConstraintHandle handle = joint switch
        {
            FixedJoint => simulation.Solver.Add(bodyA, bodyB, new Weld
            {
                LocalOffset = Local(poseA, poseB.Position),
                LocalOrientation = NQuaternion.Normalize(NQuaternion.Conjugate(poseA.Orientation) * poseB.Orientation), // B in A's frame
                SpringSettings = spring,
            }),
            HingeJoint hinge => simulation.Solver.Add(bodyA, bodyB, new Hinge
            {
                LocalOffsetA = offsetA,
                LocalHingeAxisA = NVector3.Normalize(NVector3.Transform(N(transform.Rotation * hinge.Axis), NQuaternion.Conjugate(poseA.Orientation))),
                LocalOffsetB = offsetB,
                LocalHingeAxisB = NVector3.Normalize(NVector3.Transform(N(transform.Rotation * hinge.Axis), NQuaternion.Conjugate(poseB.Orientation))),
                SpringSettings = spring,
            }),
            DistanceJoint distance => simulation.Solver.Add(bodyA, bodyB, new DistanceLimit(offsetA, offsetB, distance.MinDistance, MathF.Max(distance.MinDistance, distance.MaxDistance), spring)),
            SpringJoint springJoint => simulation.Solver.Add(bodyA, bodyB, new DistanceServo(offsetA, offsetB, springJoint.Distance, new SpringSettings(springJoint.Frequency, springJoint.DampingRatio), ServoSettings.Default)),
            _ => simulation.Solver.Add(bodyA, bodyB, new BallSocket { LocalOffsetA = offsetA, LocalOffsetB = offsetB, SpringSettings = spring }),
        };
        var awakened = simulation.Bodies[bodyA];
        awakened.Awake = true;
        _joints.Add((handle, anchorBody));
    }

    /// <summary>Moves a character controller with collide-and-slide; see <see cref="CharacterController"/>.</summary>
    internal static CharacterCollision Move(CharacterController controller, Vector3 motion)
    {
        if (controller.GameObject is not { } owner || !owner.Scene.IsRunning)
        {
            controller.GameObject.Transform.Position += motion;
            return CharacterCollision.None;
        }

        return For(owner.Scene).MoveCharacter(controller, motion);
    }

    /// <summary>Removes a character controller's capsule (disabled or destroyed).</summary>
    internal static void RemoveCharacter(CharacterController controller)
    {
        if (controller.GameObject is { } owner && owner.Scene.Systems.Get<PhysicsWorld3D>() is { } world && world._characters.Remove(controller, out var entry))
        {
            world._characterBodies.Remove(entry.Body);
            if (world._simulation is { } simulation && simulation.Bodies.BodyExists(entry.Body))
            {
                simulation.Shapes.RecursivelyRemoveAndDispose(simulation.Bodies[entry.Body].Collidable.Shape, world._pool!);
                simulation.Bodies.Remove(entry.Body);
            }

            world.ReleaseIfEmpty();
        }
    }

    private CharacterCollision MoveCharacter(CharacterController controller, Vector3 motion)
    {
        Flush();
        EnsureSimulation();
        var simulation = _simulation!;
        var body = CharacterBody(controller);
        var transform = controller.GameObject.Transform;
        var capsule = new Capsule(controller.Radius, MathF.Max(0f, controller.Height - (2f * controller.Radius)));
        var center = transform.Position + controller.Center;
        var start = center;
        var flags = CharacterCollision.None;
        var walkable = MathF.Cos(controller.SlopeLimit * Mathf.Deg2Rad);
        var skin = controller.SkinWidth;
        var remaining = motion;
        for (var iteration = 0; iteration < 4 && remaining.Magnitude > 1e-6f; iteration++)
        {
            if (Sweep(capsule, center, remaining, body, controller.Layer) is not var (t, normal, point))
            {
                center += remaining;
                break;
            }

            var length = remaining.Magnitude;
            var travel = MathF.Max(0f, (t * length) - skin);
            center += remaining / length * travel;
            var rest = remaining / length * (length - travel);
            if (IsGround(normal, point, center, controller.Layer, walkable))
            {
                flags |= CharacterCollision.Below;
                rest = rest with { Y = MathF.Max(0f, rest.Y) }; // ground stops the fall; walking continues along it
            }
            else if (normal.Y <= -0.7f)
            {
                flags |= CharacterCollision.Above;
            }
            else
            {
                flags |= CharacterCollision.Sides;
                var horizontal = new Vector3(rest.X, 0f, rest.Z);
                if (controller.StepOffset > 0f && horizontal.Magnitude > 1e-5f && TryStep(capsule, ref center, horizontal, controller, body, walkable))
                {
                    remaining = new Vector3(0f, rest.Y, 0f);
                    continue;
                }
            }

            var wall = normal.Y < walkable && normal.Y > -0.7f;
            remaining = rest - (normal * Vector3.Dot(rest, normal));
            if (wall && normal.Y > 0f && remaining.Y > 0f && !IsGround(normal, point, center, controller.Layer, walkable))
            {
                remaining = remaining with { Y = 0f }; // too steep: do not slide up it
            }
        }

        // Ground: a short sweep down finds walkable ground under the capsule.
        var grounded = (flags & CharacterCollision.Below) != 0
            || (Sweep(capsule, center, new Vector3(0f, -(skin * 2f) - 0.01f, 0f), body, controller.Layer) is var (_, groundNormal, groundPoint)
                && IsGround(groundNormal, groundPoint, center, controller.Layer, walkable));
        transform.Position = center - controller.Center;
        var character = simulation.Bodies[body];
        character.Pose = new RigidPose(N(center));
        character.Awake = true;
        controller.IsGrounded = grounded;
        controller.Collisions = flags | (grounded ? CharacterCollision.Below : CharacterCollision.None);
        controller.Velocity = (center - start) / _stepDelta; // per second, assuming one Move per physics step
        return controller.Collisions;
    }

    /// <summary>Climbs a ledge up to the step offset: up, forward, then down onto walkable ground.</summary>
    private bool TryStep(Capsule capsule, ref Vector3 center, Vector3 horizontal, CharacterController controller, BodyHandle body, float walkable)
    {
        var up = new Vector3(0f, controller.StepOffset, 0f);
        var raised = center + (Sweep(capsule, center, up, body, controller.Layer) is var (tUp, _, _) ? up * MathF.Max(0f, tUp - 0.01f) : up);
        if (Sweep(capsule, raised, horizontal, body, controller.Layer) is not null)
        {
            return false; // still blocked: a wall, not a step
        }

        var forward = raised + horizontal;
        var drop = new Vector3(0f, -(controller.StepOffset + controller.SkinWidth + 0.01f), 0f);
        if (Sweep(capsule, forward, drop, body, controller.Layer) is not var (tDown, ground, point) || !IsGround(ground, point, forward, controller.Layer, walkable))
        {
            return false;
        }

        center = forward + (drop * MathF.Max(0f, tDown - 0.01f));
        return true;
    }

    /// <summary>
    /// Whether a hit supports the character: a walkable surface, or a ledge's edge under the rounded cap (its normal is
    /// tilted, so the top just past the edge decides).
    /// </summary>
    private bool IsGround(Vector3 normal, Vector3 point, Vector3 center, int layer, float walkable)
    {
        if (normal.Y >= walkable)
        {
            return true;
        }

        var outward = new Vector3(point.X - center.X, 0f, point.Z - center.Z);
        return normal.Y > 0f && point.Y < center.Y && outward.Magnitude > 1e-4f
            && Raycast(point + (outward.Normalized * 0.02f) + new Vector3(0f, 0.05f, 0f), -Vector3.Up, 0.1f, _layerMasks[layer]) is { } top && top.Normal.Y >= walkable;
    }

    /// <summary>The first solid hit when sweeping the capsule by <paramref name="motion"/>: fraction of the motion, the normal facing back and the contact point.</summary>
    private (float T, Vector3 Normal, Vector3 Point)? Sweep(Capsule capsule, Vector3 center, Vector3 motion, BodyHandle self, int layer)
    {
        var handler = new CharacterSweep { World = this, Self = self, Layer = layer, T = float.MaxValue };
        _simulation!.Sweep(capsule, new RigidPose(N(center)), new BodyVelocity(N(motion)), 1f, _pool!, ref handler);
        if (!handler.Hit)
        {
            return null;
        }

        var normal = E(handler.Normal).Normalized;
        return (handler.T, Vector3.Dot(normal, motion) > 0f ? -normal : normal, E(handler.Point));
    }

    private BodyHandle CharacterBody(CharacterController controller)
    {
        if (_characters.TryGetValue(controller, out var entry) && entry.Radius == controller.Radius && entry.Height == controller.Height && _simulation!.Bodies.BodyExists(entry.Body))
        {
            return entry.Body;
        }

        if (entry.Body.Value != 0 || _characters.ContainsKey(controller))
        {
            RemoveCharacter(controller);
            EnsureSimulation();
        }

        var simulation = _simulation!;
        var shape = simulation.Shapes.Add(new Capsule(controller.Radius, MathF.Max(0f, controller.Height - (2f * controller.Radius))));
        var center = controller.GameObject.Transform.Position + controller.Center;
        var body = simulation.Bodies.Add(BodyDescription.CreateKinematic(new RigidPose(N(center)), new CollidableDescription(shape, 0.1f), new BodyActivityDescription(0.01f)));
        _characters[controller] = (body, controller.Radius, controller.Height);
        _characterBodies[body] = controller;
        return body;
    }

    private struct CharacterSweep : ISweepHitHandler
    {
        public PhysicsWorld3D World;
        public BodyHandle Self;
        public int Layer;
        public bool Hit;
        public float T;
        public NVector3 Normal;
        public NVector3 Point;

        public readonly bool AllowTest(CollidableReference collidable) =>
            !(collidable.Mobility != CollidableMobility.Static && (collidable.BodyHandle == Self || World._characterBodies.ContainsKey(collidable.BodyHandle))) && World.Resolve(collidable) is not null;

        public readonly bool AllowTest(CollidableReference collidable, int child) =>
            World.ColliderOf(collidable, child) is { IsTrigger: false } c && World.LayersCollide(Layer, c.Layer);

        public void OnHit(ref float maximumT, float t, in NVector3 hitLocation, in NVector3 hitNormal, CollidableReference collidable)
        {
            if (t < maximumT && t < T)
            {
                maximumT = t;
                (Hit, T, Normal, Point) = (true, t, hitNormal, hitLocation);
            }
        }

        public readonly void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
        {
            // Already touching at the start (inside the skin): ignored, so the capsule can move away.
        }
    }

    /// <summary>Joints are rebuilt at the next settled point (after the bodies they connect).</summary>
    internal static void RefreshJoints(Joint joint)
    {
        if (joint.GameObject is { } owner && owner.Scene.IsRunning)
        {
            For(owner.Scene)._jointsDirty = true;
        }
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
    public void Dispose()
    {
        ReleaseSimulation();
        _dispatcher?.Dispose();
        _dispatcher = null;
    }

    /// <summary>Rebuilds every dirty body, then the joints.</summary>
    public void Flush()
    {
        if (_flushing || (_dirty.Count == 0 && !_jointsDirty))
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
                _jointsDirty = true;
            }

            if (_jointsDirty && _simulation is not null)
            {
                RebuildJoints();
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

        // A triangle mesh is a body's only shape (static or kinematic); a convex hull is a compound child (it is recentered).
        if (colliders.Find(c => c is MeshCollider { Convex: false }) is { } meshCollider)
        {
            if (kind == RigidbodyType.Dynamic || colliders.Count > 1)
            {
                Warn($"'{owner.Name}': a non-convex MeshCollider must be the only collider of a static or kinematic body; it is used as a convex hull.");
            }
            else if (TriangleMesh(owner, scale) is { } triangles)
            {
                colliders = [meshCollider];
                var meshBinding = new Binding(owner, rigidbody, colliders, simulation.Shapes.Add(triangles), false, Vector3.Zero, transform.Position, rotation, scale);
                AddBody(meshBinding, kind, rigidbody, default, velocity, angularVelocity);
                return;
            }
            else
            {
                colliders.Remove(meshCollider);
                if (colliders.Count == 0)
                {
                    ReleaseIfEmpty();
                    return;
                }
            }
        }

        // One collider without an offset is the body's shape; several (or an offset, or a convex hull) form a compound.
        TypedIndex shapeIndex;
        BodyInertia inertia = default;
        var comOffset = NVector3.Zero;
        var compound = colliders.Count > 1 || colliders[0].Center != Vector3.Zero || colliders[0] is MeshCollider;
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
                    case CylinderCollider cylinder:
                        builder.Add(CylinderShape(cylinder, scale), localPose, 1f);
                        break;
                    case MeshCollider when ConvexHull(owner, scale) is var (hull, center):
                        builder.Add(hull, new RigidPose(localPose.Position + center), 1f);
                        break;
                    case MeshCollider:
                        Warn($"'{owner.Name}': the MeshCollider has no mesh to collide with (no MeshRenderer, or no triangles).");
                        builder.Add(new Sphere(0.001f), localPose, 0.001f);
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

        AddBody(new Binding(owner, rigidbody, colliders, shapeIndex, compound, E(comOffset), transform.Position, rotation, scale), kind, rigidbody, inertia, velocity, angularVelocity);
    }

    private void AddBody(Binding binding, RigidbodyType kind, Rigidbody? rigidbody, BodyInertia inertia, Vector3? velocity, Vector3 angularVelocity)
    {
        var simulation = _simulation!;
        var pose = new RigidPose(N(binding.Position) + NVector3.Transform(N(binding.ComOffset), N(binding.Rotation)), N(binding.Rotation));
        var continuity = rigidbody?.Continuous == true ? ContinuousDetection.Continuous() : ContinuousDetection.Discrete;
        if (kind == RigidbodyType.Static)
        {
            binding.Static = simulation.Statics.Add(new StaticDescription(pose, binding.Shape));
            _statics[binding.Static.Value] = binding;
        }
        else
        {
            var collidable = new CollidableDescription(binding.Shape, 0.1f, continuity); // a speculative margin: contacts exist before touching, so resting bodies keep full friction
            var description = kind == RigidbodyType.Dynamic
                ? BodyDescription.CreateDynamic(pose, new BodyVelocity(N(velocity ?? rigidbody!.InitialVelocity), N((velocity is null ? rigidbody!.InitialAngularVelocity : angularVelocity) * Mathf.Deg2Rad)), inertia, collidable, new BodyActivityDescription(0.01f))
                : BodyDescription.CreateKinematic(pose, collidable, new BodyActivityDescription(0.01f));
            binding.Body = simulation.Bodies.Add(description);
            _bodies[binding.Body.Value] = binding;
        }

        _bindings[binding.Owner] = binding;
    }

    /// <summary>The GameObject's MeshRenderer triangles (scaled), as a Bepu mesh; null without mesh data.</summary>
    private Mesh? TriangleMesh(GameObject owner, Vector3 scale)
    {
        if (MeshData(owner) is not { } data || data.Indices.Length < 3)
        {
            return null;
        }

        _pool!.Take<Triangle>(data.Indices.Length / 3, out var triangles);
        for (var t = 0; t < triangles.Length; t++)
        {
            // Bepu's triangles face the side their vertices wind clockwise from: swap two vertices of the engine's
            // counter-clockwise triangles so they collide from the side they are drawn on.
            triangles[t] = new Triangle(N(data.Positions[data.Indices[t * 3]] * scale), N(data.Positions[data.Indices[(t * 3) + 2]] * scale), N(data.Positions[data.Indices[(t * 3) + 1]] * scale));
        }

        return new Mesh(triangles, NVector3.One, _pool);
    }

    /// <summary>The convex hull of the GameObject's MeshRenderer vertices (scaled) and its center; null without mesh data.</summary>
    private (ConvexHull Hull, NVector3 Center)? ConvexHull(GameObject owner, Vector3 scale)
    {
        if (MeshData(owner) is not { Positions.Length: >= 4 } data)
        {
            return null;
        }

        var points = data.Positions.Select(p => N(p * scale)).ToArray();
        ConvexHullHelper.CreateShape(points, _pool!, out var center, out var hull);
        return (hull, center);
    }

    private static MeshData? MeshData(GameObject owner) =>
        owner.GetComponent<MeshRenderer>() is { } renderer ? owner.Scene.MeshData?.GetMeshData(renderer) : null;

    private void Warn(string message) => LogWarning(_scene.Logger, message);

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

            case CylinderCollider cylinder:
            {
                var shape = CylinderShape(cylinder, scale);
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

    private static Cylinder CylinderShape(CylinderCollider cylinder, Vector3 scale) =>
        new(cylinder.Radius * MathF.Max(scale.X, scale.Z), cylinder.Height * scale.Y);

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

        simulation.Shapes.RecursivelyRemoveAndDispose(binding.Shape, _pool!); // meshes and hulls own pool memory

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
        if (_bindings.Count == 0 && _characters.Count == 0)
        {
            ReleaseSimulation();
        }
    }

    private void ReleaseSimulation()
    {
        _joints.Clear();
        _characters.Clear();
        _characterBodies.Clear();
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
            else if (binding.Rigidbody is { BodyType: RigidbodyType.Kinematic } && binding.Body is { } kinematic)
            {
                // Kinematic bodies moved by code travel there by velocity during the step, so what they touch is pushed
                // and carried along; far jumps (more than 2 m in a step) teleport.
                var reference = _simulation!.Bodies[kinematic];
                var delta = transform.Position - binding.Position;
                if (delta.Magnitude > 2f)
                {
                    reference.Pose = new RigidPose(N(transform.Position) + NVector3.Transform(N(binding.ComOffset), N(transform.Rotation)), N(transform.Rotation));
                    reference.Velocity = default;
                }
                else
                {
                    var turn = transform.Rotation * binding.Rotation.Inverse;
                    var angle = 2f * MathF.Acos(Math.Clamp(MathF.Abs(turn.W), 0f, 1f));
                    var axis = new Vector3(turn.X, turn.Y, turn.Z) * (turn.W < 0f ? -1f : 1f);
                    var angular = axis.Magnitude > 1e-6f ? axis.Normalized * (angle / _stepDelta) : Vector3.Zero;
                    reference.Velocity = new BodyVelocity(N(delta / _stepDelta), N(angular));
                }

                reference.Awake = true;
                WakeTouching(binding);
                (binding.Position, binding.Rotation) = (transform.Position, transform.Rotation);
            }
            else if (binding.Rigidbody is { BodyType: RigidbodyType.Kinematic } && binding.Body is { } resting)
            {
                _simulation!.Bodies[resting].Velocity = default; // not moved this step: stop
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

    /// <summary>Wakes the bodies resting on a moving kinematic body (Bepu does not wake sleeping bodies a kinematic one pushes).</summary>
    private void WakeTouching(Binding moving)
    {
        foreach (var pair in _touching)
        {
            var other = moving.Colliders.Contains(pair.A) ? pair.B : moving.Colliders.Contains(pair.B) ? pair.A : null;
            if (other is not null && _bindings.TryGetValue(other.GameObject, out var binding) && binding.Body is { } body)
            {
                var reference = _simulation!.Bodies[body];
                reference.Awake = true;
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
        if ((pair.A.Mobility != CollidableMobility.Static && _characterBodies.ContainsKey(pair.A.BodyHandle))
            || (pair.B.Mobility != CollidableMobility.Static && _characterBodies.ContainsKey(pair.B.BodyHandle)))
        {
            var other = _characterBodies.ContainsKey(pair.A.BodyHandle) && pair.A.Mobility != CollidableMobility.Static ? (pair.B, childB) : (pair.A, childA);
            return ColliderOf(other.Item1, other.Item2) is { IsTrigger: false };
        }

        if (ColliderOf(pair.A, childA) is not { } a || ColliderOf(pair.B, childB) is not { } b)
        {
            return false;
        }

        if (!LayersCollide(a.Layer, b.Layer))
        {
            return false;
        }

        if (a.IsTrigger || b.IsTrigger)
        {
            if (touching)
            {
                lock (_eventLock)
                {
                    _stepOverlaps.Add(new Pair(a, b));
                }
            }

            return false; // triggers report overlaps and never push
        }

        material = new PairMaterialProperties(MathF.Sqrt(a.Friction * b.Friction), 2f, new SpringSettings(30f, 1f - (0.9f * MathF.Max(a.Bounciness, b.Bounciness))));
        if (touching)
        {
            var origin = Resolve(pair.A) is { } binding ? PositionOf(binding) : NVector3.Zero;
            // Bepu's normal points from B to A; Collision.Normal points from the collider (A) to the other (B), as in 2D.
            lock (_eventLock)
            {
                _stepContacts.TryAdd(new Pair(a, b), (-E(normal), E(origin + offset)));
            }
        }

        return true;
    }

    private NVector3 PositionOf(Binding binding) =>
        binding.Body is { } body ? _simulation!.Bodies[body].Pose.Position : binding.Static is { } handle ? _simulation!.Statics[handle].Pose.Position : NVector3.Zero;

    private bool AllowContacts(CollidableReference a, CollidableReference b)
    {
        // Character capsules push dynamic bodies (and are moved by their controller, not by contacts).
        var aCharacter = a.Mobility != CollidableMobility.Static && _characterBodies.ContainsKey(a.BodyHandle);
        var bCharacter = b.Mobility != CollidableMobility.Static && _characterBodies.ContainsKey(b.BodyHandle);
        if (aCharacter || bCharacter)
        {
            return !(aCharacter && bCharacter) && (aCharacter ? b : a).Mobility == CollidableMobility.Dynamic;
        }

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
            var configured = World.Configure(pair, 0, 0, touching, offset, normal, out pairMaterial);
            if (manifold.Convex && manifold.Count > 1)
            {
                // Bepu 2.4 bounds a convex manifold's friction by the average contact's normal impulse, not their sum
                // (measured: a box slid at a quarter of mu*g on its four contacts); scale so friction is mu times the load.
                pairMaterial.FrictionCoefficient *= manifold.Count;
            }

            return configured;
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
        public uint LayerMask;
        public Collider? Collider;
        public float T;
        public NVector3 Normal;

        public readonly bool AllowTest(CollidableReference collidable) => World.Resolve(collidable) is not null;

        public readonly bool AllowTest(CollidableReference collidable, int childIndex) => World.ColliderOf(collidable, childIndex) is { IsTrigger: false } c && (LayerMask & (1u << c.Layer)) != 0;

        public void OnRayHit(in RayData ray, ref float maximumT, float t, in NVector3 normal, CollidableReference collidable, int childIndex)
        {
            if (t <= 0f || t >= maximumT || World.ColliderOf(collidable, childIndex) is not { IsTrigger: false } collider || (LayerMask & (1u << collider.Layer)) == 0)
            {
                return; // inside the collider at the start, farther than a hit found already, or a trigger
            }

            maximumT = t;
            (Collider, T, Normal) = (collider, t, normal);
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(EventId = 1500, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Physics3D: {Message}")]
    private static partial void LogWarning(Microsoft.Extensions.Logging.ILogger logger, string message);
}
