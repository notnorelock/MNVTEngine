using Box2D.NET;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;
using static Box2D.NET.B2Bodies;
using static Box2D.NET.B2Geometries;
using static Box2D.NET.B2Shapes;

namespace MNVTEngine;

public enum RigidbodyType2D
{
    /// <summary>Moved by forces, gravity and collisions.</summary>
    Dynamic,

    /// <summary>Moved only by its velocity or by setting the transform; pushes dynamic bodies, ignores forces.</summary>
    Kinematic,

    /// <summary>Never moves (walls, floors).</summary>
    Static,
}

public enum ForceMode2D
{
    /// <summary>Continuous force in newtons, applied over the next step.</summary>
    Force,

    /// <summary>Instant change of momentum.</summary>
    Impulse,
}

/// <summary>
/// Puts the GameObject under physics control. Colliders on the same GameObject give it its shape and mass (density ×
/// area). After each fixed step the body's position and rotation are written to the transform; setting the transform
/// teleports the body.
/// </summary>
[ComponentId("MNVTEngine.Rigidbody2D")]
public sealed class Rigidbody2D : Component
{
    private RigidbodyType2D _bodyType;
    private float _gravityScale = 1f;
    private float _linearDamping;
    private float _angularDamping;
    private bool _fixedRotation;
    private bool _continuous;
    private Vector2 _velocity;
    private float _angularVelocity;

    [SerializeField]
    public RigidbodyType2D BodyType { get => _bodyType; set => Set(ref _bodyType, value); }

    [SerializeField]
    public float GravityScale { get => _gravityScale; set => Set(ref _gravityScale, value); }

    [SerializeField]
    public float LinearDamping { get => _linearDamping; set => Set(ref _linearDamping, Math.Max(0f, value)); }

    [SerializeField]
    public float AngularDamping { get => _angularDamping; set => Set(ref _angularDamping, Math.Max(0f, value)); }

    /// <summary>Prevents rotation from collisions and torque (characters).</summary>
    [SerializeField]
    public bool FixedRotation { get => _fixedRotation; set => Set(ref _fixedRotation, value); }

    /// <summary>Continuous collision against other dynamic bodies, for fast projectiles (costs more).</summary>
    [SerializeField]
    public bool Continuous { get => _continuous; set => Set(ref _continuous, value); }

    /// <summary>Linear velocity in units per second. Before the scene runs, sets the initial velocity.</summary>
    public Vector2 Velocity
    {
        get => Bound ? Convert(b2Body_GetLinearVelocity(Body)) : _velocity;
        set
        {
            _velocity = value;
            if (Bound)
            {
                b2Body_SetLinearVelocity(Body, Convert(value));
            }
        }
    }

    /// <summary>Angular velocity in degrees per second (counter-clockwise).</summary>
    public float AngularVelocity
    {
        get => Bound ? b2Body_GetAngularVelocity(Body) * Mathf.Rad2Deg : _angularVelocity;
        set
        {
            _angularVelocity = value;
            if (Bound)
            {
                b2Body_SetAngularVelocity(Body, value * Mathf.Deg2Rad);
            }
        }
    }

    /// <summary>Mass computed from the colliders' density and area; 0 while the body does not exist.</summary>
    public float Mass => Bound ? b2Body_GetMass(Body) : 0f;

    internal Vector2 InitialVelocity => _velocity;

    internal float InitialAngularVelocity => _angularVelocity;

    internal B2BodyId Body { get; set; }

    internal bool HasBody { get; set; }

    public void AddForce(Vector2 force, ForceMode2D mode = ForceMode2D.Force)
    {
        if (!Bound || _bodyType != RigidbodyType2D.Dynamic)
        {
            return;
        }

        if (mode == ForceMode2D.Force)
        {
            b2Body_ApplyForceToCenter(Body, Convert(force), true);
        }
        else
        {
            b2Body_ApplyLinearImpulseToCenter(Body, Convert(force), true);
        }
    }

    /// <summary>Torque in newton-metres, counter-clockwise.</summary>
    public void AddTorque(float torque)
    {
        if (Bound && _bodyType == RigidbodyType2D.Dynamic)
        {
            b2Body_ApplyTorque(Body, torque, true);
        }
    }

    public override void OnEnable() => PhysicsWorld2D.Refresh(this);

    public override void OnDisable() => PhysicsWorld2D.Refresh(this);

    public override void OnDestroy() => PhysicsWorld2D.Refresh(this);

    protected override void OnMovedToScene(Scene previous) => PhysicsWorld2D.Moved(this, previous);

    /// <summary>Applies pending component changes first, so the body reflects them.</summary>
    private bool Bound
    {
        get
        {
            PhysicsWorld2D.Flush(this);
            return HasBody;
        }
    }

    internal static B2Vec2 Convert(Vector2 v) => new(v.X, v.Y);

    internal static Vector2 Convert(B2Vec2 v) => new(v.X, v.Y);

    private void Set<T>(ref T field, T value)
    {
        field = value;
        PhysicsWorld2D.Refresh(this);
    }
}

/// <summary>Shape of a GameObject for collisions. Without a <see cref="Rigidbody2D"/> on the same GameObject it is static.</summary>
public abstract class Collider2D : Component
{
    private bool _isTrigger;
    private Vector2 _offset;
    private float _density = 1f;
    private float _friction = 0.6f;
    private float _bounciness;

    /// <summary>Detects overlaps (OnTriggerEnter2D/Exit2D) without colliding.</summary>
    [SerializeField]
    public bool IsTrigger { get => _isTrigger; set => Set(ref _isTrigger, value); }

    /// <summary>Center relative to the GameObject, in local units.</summary>
    [SerializeField]
    public Vector2 Offset { get => _offset; set => Set(ref _offset, value); }

    [SerializeField]
    public float Density { get => _density; set => Set(ref _density, Math.Max(0f, value)); }

    [SerializeField]
    public float Friction { get => _friction; set => Set(ref _friction, Math.Max(0f, value)); }

    /// <summary>0 = no bounce, 1 = perfectly elastic.</summary>
    [SerializeField]
    public float Bounciness { get => _bounciness; set => Set(ref _bounciness, Math.Clamp(value, 0f, 1f)); }

    public Rigidbody2D? AttachedRigidbody => GameObject.GetComponent<Rigidbody2D>() is { IsActiveAndEnabled: true } body ? body : null;

    public override void OnEnable() => PhysicsWorld2D.Refresh(this);

    public override void OnDisable() => PhysicsWorld2D.Refresh(this);

    public override void OnDestroy() => PhysicsWorld2D.Refresh(this);

    protected override void OnMovedToScene(Scene previous) => PhysicsWorld2D.Moved(this, previous);

    internal abstract IEnumerable<B2ShapeId> CreateShapes(B2BodyId body, B2ShapeDef definition, Vector2 scale);

    internal B2ShapeDef CreateDefinition()
    {
        var definition = B2Types.b2DefaultShapeDef();
        definition.density = _density;
        definition.material.friction = _friction;
        definition.material.restitution = _bounciness;
        definition.isSensor = _isTrigger;
        definition.enableSensorEvents = true; // non-sensor shapes must opt in to be seen by triggers
        definition.enableContactEvents = true;
        return definition;
    }

    protected void Set<T>(ref T field, T value)
    {
        field = value;
        PhysicsWorld2D.Refresh(this);
    }
}

[ComponentId("MNVTEngine.BoxCollider2D")]
public sealed class BoxCollider2D : Collider2D
{
    private Vector2 _size = Vector2.One;

    /// <summary>Width and height in local units (scaled by the transform).</summary>
    [SerializeField]
    public Vector2 Size { get => _size; set => Set(ref _size, new Vector2(Math.Max(0.001f, value.X), Math.Max(0.001f, value.Y))); }

    internal override IEnumerable<B2ShapeId> CreateShapes(B2BodyId body, B2ShapeDef definition, Vector2 scale)
    {
        var box = b2MakeOffsetBox(_size.X * scale.X * 0.5f, _size.Y * scale.Y * 0.5f, new B2Vec2(Offset.X * scale.X, Offset.Y * scale.Y), B2MathFunction.b2MakeRot(0f));
        return [b2CreatePolygonShape(body, in definition, in box)];
    }
}

[ComponentId("MNVTEngine.CircleCollider2D")]
public sealed class CircleCollider2D : Collider2D
{
    private float _radius = 0.5f;

    /// <summary>Radius in local units (scaled by the larger axis of the transform's scale).</summary>
    [SerializeField]
    public float Radius { get => _radius; set => Set(ref _radius, Math.Max(0.001f, value)); }

    internal override IEnumerable<B2ShapeId> CreateShapes(B2BodyId body, B2ShapeDef definition, Vector2 scale)
    {
        var circle = new B2Circle { center = new B2Vec2(Offset.X * scale.X, Offset.Y * scale.Y), radius = _radius * Math.Max(scale.X, scale.Y) };
        return [b2CreateCircleShape(body, in definition, in circle)];
    }
}

/// <summary>
/// Collides with the painted cells of the <see cref="Tilemap"/> on the same GameObject. Each row's contiguous run of
/// solid cells becomes one box, so characters slide along floors without catching on tile seams. Rebuilt whenever the
/// tilemap changes.
/// </summary>
[ComponentId("MNVTEngine.TilemapCollider2D")]
public sealed class TilemapCollider2D : Collider2D
{
    private Tilemap? _tilemap;
    private IReadOnlyList<int> _ignoredTiles = [];

    /// <summary>Sheet indices that do not collide (decoration, background).</summary>
    [SerializeField]
    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public IReadOnlyList<int> IgnoredTiles { get => _ignoredTiles; set => Set(ref _ignoredTiles, value ?? []); }

    public override void OnEnable()
    {
        _tilemap = GameObject.GetComponent<Tilemap>();
        if (_tilemap is not null)
        {
            _tilemap.Changed += OnTilesChanged;
        }

        base.OnEnable();
    }

    public override void OnDisable()
    {
        if (_tilemap is not null)
        {
            _tilemap.Changed -= OnTilesChanged;
        }

        base.OnDisable();
    }

    /// <summary>Boxes in cells: (x, y, width) per run of solid tiles in a row.</summary>
    public IReadOnlyList<(int X, int Y, int Width)> SolidRuns()
    {
        var tilemap = GameObject.GetComponent<Tilemap>();
        if (tilemap is null)
        {
            return [];
        }

        var ignored = _ignoredTiles.ToHashSet();
        var runs = new List<(int X, int Y, int Width)>();
        foreach (var row in tilemap.EnumerateTiles().Where(c => !ignored.Contains(c.Tile)).GroupBy(c => c.Y).OrderBy(g => g.Key))
        {
            var xs = row.Select(c => c.X).Order().ToList();
            var start = xs[0];
            for (var i = 1; i <= xs.Count; i++)
            {
                if (i == xs.Count || xs[i] != xs[i - 1] + 1)
                {
                    runs.Add((start, row.Key, xs[i - 1] - start + 1));
                    if (i < xs.Count)
                    {
                        start = xs[i];
                    }
                }
            }
        }

        return runs;
    }

    internal override IEnumerable<B2ShapeId> CreateShapes(B2BodyId body, B2ShapeDef definition, Vector2 scale)
    {
        var cell = GameObject.GetComponent<Tilemap>()?.CellSize ?? Vector2.One;
        var shapes = new List<B2ShapeId>();
        foreach (var (x, y, width) in SolidRuns())
        {
            var center = new B2Vec2(((x + (width * 0.5f)) * cell.X + Offset.X) * scale.X, ((y + 0.5f) * cell.Y + Offset.Y) * scale.Y);
            var box = b2MakeOffsetBox(width * cell.X * scale.X * 0.5f, cell.Y * scale.Y * 0.5f, center, B2MathFunction.b2MakeRot(0f));
            shapes.Add(b2CreatePolygonShape(body, in definition, in box));
        }

        return shapes;
    }

    private void OnTilesChanged(Tilemap tilemap) => PhysicsWorld2D.Refresh(this);
}

/// <summary>Contact between two non-trigger colliders, as seen from one side.</summary>
/// <param name="Collider">The receiving side's collider.</param>
/// <param name="Other">The collider it touches.</param>
/// <param name="Normal">Contact normal pointing from <paramref name="Collider"/> towards <paramref name="Other"/>; zero on exit.</param>
/// <param name="Point">A world-space contact point; zero on exit.</param>
public sealed record Collision2D(Collider2D Collider, Collider2D Other, Vector2 Normal, Vector2 Point)
{
    public GameObject GameObject => Other.GameObject;

    public Rigidbody2D? Rigidbody => Other.AttachedRigidbody;
}

/// <summary>Implement on a component to receive collisions of colliders on its GameObject. Called after the physics step.</summary>
public interface ICollisionHandler2D
{
    void OnCollisionEnter2D(Collision2D collision)
    {
    }

    void OnCollisionExit2D(Collision2D collision)
    {
    }
}

/// <summary>Implement on a component to receive trigger overlaps involving colliders on its GameObject.</summary>
public interface ITriggerHandler2D
{
    void OnTriggerEnter2D(Collider2D other)
    {
    }

    void OnTriggerExit2D(Collider2D other)
    {
    }
}

/// <summary>Closest hit of a ray cast.</summary>
public sealed record RaycastHit2D(Collider2D Collider, Vector2 Point, Vector2 Normal, float Distance);

/// <summary>Physics queries and settings for the scene whose callbacks are running.</summary>
public static class Physics2D
{
    /// <summary>Default (0, -9.81).</summary>
    public static Vector2 Gravity
    {
        get => PhysicsWorld2D.For(SceneManager.ActiveScene).Gravity;
        set => PhysicsWorld2D.For(SceneManager.ActiveScene).Gravity = value;
    }

    /// <summary>Closest non-trigger collider along the ray, or null. A collider containing the origin is ignored, so a character can cast from inside its own collider.</summary>
    public static RaycastHit2D? Raycast(Vector2 origin, Vector2 direction, float distance = 1000f) =>
        PhysicsWorld2D.For(SceneManager.ActiveScene).Raycast(origin, direction, distance);
}

public static class Physics2DComponents
{
    public static Scenes.ComponentRegistry Register(Scenes.ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.Register<Rigidbody2D>().Register<BoxCollider2D>().Register<CircleCollider2D>().Register<TilemapCollider2D>();
    }
}

/// <summary>The "physics.2d" engine module. 2D physics: registers the 2D rigidbody and collider components; each scene starts its physics world on first use.</summary>
public sealed class Physics2DModule : Scenes.IEngineModule
{
    public string Id => "physics.2d";

    public void Register(Scenes.ComponentRegistry components) => Physics2DComponents.Register(components);
}
