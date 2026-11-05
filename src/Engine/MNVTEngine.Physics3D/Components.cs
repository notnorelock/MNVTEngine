using MNVTEngine.Mathematics;

namespace MNVTEngine;

public enum RigidbodyType
{
    /// <summary>Moved by forces, gravity and collisions.</summary>
    Dynamic,

    /// <summary>Moved only by setting its transform or velocity; pushes dynamic bodies, is not pushed.</summary>
    Kinematic,

    /// <summary>Never moves (floors, walls). Colliders without a rigidbody are static too.</summary>
    Static,
}

public enum ForceMode
{
    /// <summary>Continuous force in newtons, applied over the next physics step.</summary>
    Force,

    /// <summary>Instant change of momentum (newton-seconds).</summary>
    Impulse,

    /// <summary>Instant change of velocity, ignoring mass.</summary>
    VelocityChange,
}

/// <summary>
/// A 3D physics body over BepuPhysics v2: its GameObject's colliders form one shape; the body moves the transform after
/// each fixed step (dynamic and kinematic), and moving the transform from code teleports it. Without a rigidbody,
/// colliders are static.
/// </summary>
[ComponentId("MNVTEngine.Rigidbody")]
public sealed class Rigidbody : Component
{
    private RigidbodyType _bodyType;
    private float _mass = 1f;
    private bool _useGravity = true;
    private float _linearDamping;
    private float _angularDamping = 0.05f;
    private bool _freezeRotation;
    private bool _continuous;
    private Vector3 _velocity;
    private Vector3 _angularVelocity;

    [SerializeField]
    public RigidbodyType BodyType { get => _bodyType; set => Set(ref _bodyType, value); }

    /// <summary>Kilograms (dynamic bodies).</summary>
    [SerializeField]
    public float Mass { get => _mass; set => Set(ref _mass, Math.Max(0.0001f, value)); }

    [SerializeField]
    public bool UseGravity { get => _useGravity; set => Set(ref _useGravity, value); }

    /// <summary>Fraction of linear velocity lost per second (0 = none).</summary>
    [SerializeField]
    public float LinearDamping { get => _linearDamping; set => Set(ref _linearDamping, Math.Max(0f, value)); }

    /// <summary>Fraction of angular velocity lost per second.</summary>
    [SerializeField]
    public float AngularDamping { get => _angularDamping; set => Set(ref _angularDamping, Math.Max(0f, value)); }

    /// <summary>Collisions do not rotate the body (characters).</summary>
    [SerializeField]
    public bool FreezeRotation { get => _freezeRotation; set => Set(ref _freezeRotation, value); }

    /// <summary>Continuous collision detection: fast bodies do not tunnel through thin colliders (costs more).</summary>
    [SerializeField]
    public bool Continuous { get => _continuous; set => Set(ref _continuous, value); }

    /// <summary>World units per second.</summary>
    public Vector3 Velocity
    {
        get => PhysicsWorld3D.Velocity(this) ?? _velocity;
        set
        {
            _velocity = value;
            PhysicsWorld3D.SetVelocity(this, value, null);
        }
    }

    /// <summary>Degrees per second around each world axis.</summary>
    public Vector3 AngularVelocity
    {
        get => PhysicsWorld3D.AngularVelocity(this) ?? _angularVelocity;
        set
        {
            _angularVelocity = value;
            PhysicsWorld3D.SetVelocity(this, null, value);
        }
    }

    internal Vector3 InitialVelocity => _velocity;

    internal Vector3 InitialAngularVelocity => _angularVelocity;

    /// <summary>Applies a force or impulse through the center of mass (dynamic bodies).</summary>
    public void AddForce(Vector3 force, ForceMode mode = ForceMode.Force) => PhysicsWorld3D.AddForce(this, force, mode);

    /// <summary>Applies a torque (newton-metres, world axes) or angular impulse.</summary>
    public void AddTorque(Vector3 torque, ForceMode mode = ForceMode.Force) => PhysicsWorld3D.AddTorque(this, torque, mode);

    public override void OnEnable() => PhysicsWorld3D.Refresh(this);

    public override void OnDisable() => PhysicsWorld3D.Refresh(this);

    public override void OnDestroy() => PhysicsWorld3D.Refresh(this);

    private void Set<T>(ref T field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PhysicsWorld3D.Refresh(this);
        }
    }
}

/// <summary>A collision shape on its GameObject, sized by the transform's scale.</summary>
public abstract class Collider : Component
{
    private bool _isTrigger;
    private Vector3 _center;
    private float _friction = 0.6f;
    private float _bounciness;

    /// <summary>Reports overlaps (<see cref="ITriggerHandler"/>) instead of colliding.</summary>
    [SerializeField]
    public bool IsTrigger { get => _isTrigger; set => Set(ref _isTrigger, value); }

    /// <summary>Offset from the GameObject's origin, in its local space.</summary>
    [SerializeField]
    public Vector3 Center { get => _center; set => Set(ref _center, value); }

    [SerializeField]
    public float Friction { get => _friction; set => Set(ref _friction, Math.Max(0f, value)); }

    /// <summary>0 = no bounce, 1 = very bouncy. Approximate: Bepu has no restitution; it softens the contact instead.</summary>
    [SerializeField]
    public float Bounciness { get => _bounciness; set => Set(ref _bounciness, Math.Clamp(value, 0f, 1f)); }

    private int _layer;

    /// <summary>Collision layer 0–31: layers set to ignore each other (<see cref="Physics.IgnoreLayerCollision"/>) do not collide or trigger, and ray casts can skip layers.</summary>
    [SerializeField]
    public int Layer { get => _layer; set => Set(ref _layer, Math.Clamp(value, 0, 31)); }

    public Rigidbody? AttachedRigidbody => GameObject.GetComponent<Rigidbody>() is { IsActiveAndEnabled: true } body ? body : null;

    public override void OnEnable() => PhysicsWorld3D.Refresh(this);

    public override void OnDisable() => PhysicsWorld3D.Refresh(this);

    public override void OnDestroy() => PhysicsWorld3D.Refresh(this);

    private protected void Set<T>(ref T field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PhysicsWorld3D.Refresh(this);
        }
    }
}

[ComponentId("MNVTEngine.BoxCollider")]
public sealed class BoxCollider : Collider
{
    private Vector3 _size = Vector3.One;

    /// <summary>Full size along each local axis (before scale).</summary>
    [SerializeField]
    public Vector3 Size { get => _size; set => Set(ref _size, new Vector3(Math.Max(0.001f, value.X), Math.Max(0.001f, value.Y), Math.Max(0.001f, value.Z))); }
}

[ComponentId("MNVTEngine.SphereCollider")]
public sealed class SphereCollider : Collider
{
    private float _radius = 0.5f;

    /// <summary>Scaled by the largest axis of the transform's scale.</summary>
    [SerializeField]
    public float Radius { get => _radius; set => Set(ref _radius, Math.Max(0.001f, value)); }
}

/// <summary>A capsule along the local Y axis: <see cref="Height"/> includes both caps (like the Capsule primitive).</summary>
[ComponentId("MNVTEngine.CapsuleCollider")]
public sealed class CapsuleCollider : Collider
{
    private float _radius = 0.5f;
    private float _height = 2f;

    [SerializeField]
    public float Radius { get => _radius; set => Set(ref _radius, Math.Max(0.001f, value)); }

    [SerializeField]
    public float Height { get => _height; set => Set(ref _height, Math.Max(0.002f, value)); }
}

/// <summary>A cylinder along the local Y axis.</summary>
[ComponentId("MNVTEngine.CylinderCollider")]
public sealed class CylinderCollider : Collider
{
    private float _radius = 0.5f;
    private float _height = 2f;

    [SerializeField]
    public float Radius { get => _radius; set => Set(ref _radius, Math.Max(0.001f, value)); }

    [SerializeField]
    public float Height { get => _height; set => Set(ref _height, Math.Max(0.002f, value)); }
}

/// <summary>
/// The triangles of the GameObject's <see cref="MeshRenderer"/> (primitive or model mesh) as a collider. Static and
/// kinematic bodies use the exact triangles (one-sided per triangle winding, as rendered); dynamic bodies need
/// <see cref="Convex"/> and use the mesh's convex hull. Must be the only collider on its GameObject unless convex.
/// </summary>
[ComponentId("MNVTEngine.MeshCollider")]
public sealed class MeshCollider : Collider
{
    private bool _convex;

    /// <summary>Use the convex hull of the vertices (required for dynamic bodies; can share the GameObject with other colliders).</summary>
    [SerializeField]
    public bool Convex { get => _convex; set => Set(ref _convex, value); }
}

/// <summary>
/// Connects this GameObject's <see cref="Rigidbody"/> to <see cref="ConnectedBody"/> (or to a fixed point in the world
/// when none). The anchor is in this body's local space; the connected anchor is where that point is on the other body
/// when the joint is created.
/// </summary>
public abstract class Joint : Component
{
    private Rigidbody? _connectedBody;
    private Vector3 _anchor;

    /// <summary>The other body; none fixes the joint to the world where the anchor is when it is created.</summary>
    [SerializeField]
    public Rigidbody? ConnectedBody { get => _connectedBody; set => Set(ref _connectedBody, value); }

    /// <summary>Where the joint is, in this body's local space.</summary>
    [SerializeField]
    public Vector3 Anchor { get => _anchor; set => Set(ref _anchor, value); }

    public override void OnEnable() => PhysicsWorld3D.RefreshJoints(this);

    public override void OnDisable() => PhysicsWorld3D.RefreshJoints(this);

    public override void OnDestroy() => PhysicsWorld3D.RefreshJoints(this);

    private protected void Set<T>(ref T field, T value)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PhysicsWorld3D.RefreshJoints(this);
        }
    }
}

/// <summary>Holds the two bodies together rigidly (or this body in place).</summary>
[ComponentId("MNVTEngine.FixedJoint")]
public sealed class FixedJoint : Joint;

/// <summary>A ball-and-socket: the anchors stay together, every rotation is free (ragdoll shoulders, chains).</summary>
[ComponentId("MNVTEngine.BallJoint")]
public sealed class BallJoint : Joint;

/// <summary>A hinge: the anchors stay together and the bodies only turn around <see cref="Axis"/> (doors, wheels).</summary>
[ComponentId("MNVTEngine.HingeJoint")]
public sealed class HingeJoint : Joint
{
    private Vector3 _axis = Vector3.UnitY;

    /// <summary>The hinge axis in this body's local space.</summary>
    [SerializeField]
    public Vector3 Axis { get => _axis; set => Set(ref _axis, value == Vector3.Zero ? Vector3.UnitY : value.Normalized); }
}

/// <summary>Keeps the anchors between <see cref="MinDistance"/> and <see cref="MaxDistance"/> apart (ropes when min is 0).</summary>
[ComponentId("MNVTEngine.DistanceJoint")]
public sealed class DistanceJoint : Joint
{
    private float _min;
    private float _max = 1f;

    [SerializeField]
    public float MinDistance { get => _min; set => Set(ref _min, Math.Max(0f, value)); }

    [SerializeField]
    public float MaxDistance { get => _max; set => Set(ref _max, Math.Max(0.001f, value)); }
}

/// <summary>Pulls the anchors towards <see cref="Distance"/> apart like a spring.</summary>
[ComponentId("MNVTEngine.SpringJoint")]
public sealed class SpringJoint : Joint
{
    private float _distance = 1f;
    private float _frequency = 2f;
    private float _damping = 0.3f;

    [SerializeField]
    public float Distance { get => _distance; set => Set(ref _distance, Math.Max(0f, value)); }

    /// <summary>Oscillations per second when undamped: higher is stiffer.</summary>
    [SerializeField]
    public float Frequency { get => _frequency; set => Set(ref _frequency, Math.Max(0.01f, value)); }

    /// <summary>0 bounces forever, 1 settles without overshooting.</summary>
    [SerializeField]
    public float DampingRatio { get => _damping; set => Set(ref _damping, Math.Max(0f, value)); }
}

[Flags]
public enum CharacterCollision
{
    None = 0,
    Sides = 1,
    Above = 2,
    Below = 4,
}

/// <summary>
/// A capsule character moved by game code (<see cref="Move"/>), not by forces: it slides along walls, walks up slopes up
/// to <see cref="SlopeLimit"/> and steps up ledges up to <see cref="StepOffset"/>, and stops at what is too steep.
/// Dynamic bodies collide with it (it is a kinematic body for them). Gravity is the caller's: add it to the motion.
/// </summary>
[ComponentId("MNVTEngine.CharacterController")]
public sealed class CharacterController : Component
{
    private float _radius = 0.4f;
    private float _height = 1.8f;
    private float _slopeLimit = 45f;
    private float _stepOffset = 0.3f;
    private float _skinWidth = 0.02f;

    /// <summary>Capsule radius (world units, not scaled by the transform).</summary>
    [SerializeField]
    public float Radius { get => _radius; set => _radius = Math.Max(0.01f, value); }

    /// <summary>Capsule height including the caps.</summary>
    [SerializeField]
    public float Height { get => _height; set => _height = Math.Max(_radius * 2f, value); }

    /// <summary>Offset of the capsule's center from the GameObject's origin (e.g. half the height to stand on the origin).</summary>
    [SerializeField]
    public Vector3 Center { get; set; } = new(0f, 0.9f, 0f);

    /// <summary>Steepest walkable slope in degrees.</summary>
    [SerializeField]
    public float SlopeLimit { get => _slopeLimit; set => _slopeLimit = Math.Clamp(value, 0f, 89f); }

    /// <summary>Highest ledge the character steps onto while walking.</summary>
    [SerializeField]
    public float StepOffset { get => _stepOffset; set => _stepOffset = Math.Max(0f, value); }

    /// <summary>Gap kept between the capsule and what it touches, so it never sticks inside geometry.</summary>
    [SerializeField]
    public float SkinWidth { get => _skinWidth; set => _skinWidth = Math.Clamp(value, 0.001f, 0.2f); }

    /// <summary>Collision layer used against <see cref="Physics.IgnoreLayerCollision"/>.</summary>
    [SerializeField]
    public int Layer { get; set; }

    /// <summary>Standing on walkable ground after the last <see cref="Move"/>.</summary>
    public bool IsGrounded { get; internal set; }

    /// <summary>What the last <see cref="Move"/> touched.</summary>
    public CharacterCollision Collisions { get; internal set; }

    /// <summary>The motion of the last <see cref="Move"/> per second.</summary>
    public Vector3 Velocity { get; internal set; }

    /// <summary>Moves by <paramref name="motion"/> (world units) with collisions; returns what it touched.</summary>
    public CharacterCollision Move(Vector3 motion) => PhysicsWorld3D.Move(this, motion);

    public override void OnDisable() => PhysicsWorld3D.RemoveCharacter(this);

    public override void OnDestroy() => PhysicsWorld3D.RemoveCharacter(this);
}

/// <summary>A contact between a collider of this GameObject and another collider.</summary>
/// <param name="Normal">Points from <paramref name="Collider"/> towards <paramref name="Other"/> (zero on exit), as in 2D.</param>
public sealed record Collision(Collider Collider, Collider Other, Vector3 Normal, Vector3 Point)
{
    public GameObject GameObject => Other.GameObject;

    public Rigidbody? Rigidbody => Other.AttachedRigidbody;
}

/// <summary>Implement on a component to receive collisions of colliders on its GameObject. Called after the physics step.</summary>
public interface ICollisionHandler
{
    void OnCollisionEnter(Collision collision)
    {
    }

    void OnCollisionExit(Collision collision)
    {
    }
}

/// <summary>Implement on a component to receive trigger overlaps involving colliders on its GameObject.</summary>
public interface ITriggerHandler
{
    void OnTriggerEnter(Collider other)
    {
    }

    void OnTriggerExit(Collider other)
    {
    }
}

/// <summary>Closest hit of a ray cast.</summary>
public sealed record RaycastHit(Collider Collider, Vector3 Point, Vector3 Normal, float Distance);

/// <summary>3D physics queries and settings for the scene whose callbacks are running.</summary>
public static class Physics
{
    /// <summary>Default (0, -9.81, 0).</summary>
    public static Vector3 Gravity
    {
        get => PhysicsWorld3D.For(SceneManager.ActiveScene).Gravity;
        set => PhysicsWorld3D.For(SceneManager.ActiveScene).Gravity = value;
    }

    /// <summary>Closest non-trigger collider along the ray on the layers of <paramref name="layerMask"/>, or null. A collider containing the origin is ignored.</summary>
    public static RaycastHit? Raycast(Vector3 origin, Vector3 direction, float distance = 1000f, uint layerMask = uint.MaxValue) =>
        PhysicsWorld3D.For(SceneManager.ActiveScene).Raycast(origin, direction, distance, layerMask);

    /// <summary>Makes two layers pass through each other (or collide again).</summary>
    public static void IgnoreLayerCollision(int a, int b, bool ignore = true) => PhysicsWorld3D.For(SceneManager.ActiveScene).IgnoreLayerCollision(a, b, ignore);

    /// <summary>Worker threads for the simulation: 1 (default) is deterministic; more is faster for large scenes.</summary>
    public static int ThreadCount
    {
        get => PhysicsWorld3D.For(SceneManager.ActiveScene).ThreadCount;
        set => PhysicsWorld3D.For(SceneManager.ActiveScene).ThreadCount = value;
    }
}

public static class Physics3DComponents
{
    public static Scenes.ComponentRegistry Register(Scenes.ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Register<Rigidbody>().Register<BoxCollider>().Register<SphereCollider>().Register<CapsuleCollider>()
            .Register<CylinderCollider>().Register<MeshCollider>().Register<FixedJoint>().Register<BallJoint>().Register<HingeJoint>()
            .Register<DistanceJoint>().Register<SpringJoint>().Register<CharacterController>();
    }
}
