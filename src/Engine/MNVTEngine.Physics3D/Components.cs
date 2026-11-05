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

    /// <summary>Closest non-trigger collider along the ray, or null. A collider containing the origin is ignored.</summary>
    public static RaycastHit? Raycast(Vector3 origin, Vector3 direction, float distance = 1000f) =>
        PhysicsWorld3D.For(SceneManager.ActiveScene).Raycast(origin, direction, distance);
}

public static class Physics3DComponents
{
    public static Scenes.ComponentRegistry Register(Scenes.ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Register<Rigidbody>().Register<BoxCollider>().Register<SphereCollider>().Register<CapsuleCollider>();
    }
}
