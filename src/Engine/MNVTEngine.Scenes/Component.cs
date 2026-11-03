namespace MNVTEngine;

/// <summary>
/// Base class for gameplay behaviour attached to a <see cref="GameObject"/>. Override the lifecycle
/// methods you need; they only run while the scene is running (Play), never in edit mode.
/// </summary>
/// <remarks>
/// Lifecycle: <see cref="Awake"/> once when the object first becomes active; <see cref="OnEnable"/> /
/// <see cref="OnDisable"/> whenever the component becomes active-and-enabled or stops being so;
/// <see cref="Start"/> once before the first update after the first enable; <see cref="FixedUpdate"/>,
/// <see cref="Update"/>, <see cref="LateUpdate"/> every step while active-and-enabled; <see cref="OnDestroy"/>
/// once at destruction (end of frame), if <see cref="Awake"/> ran.
/// A GameObject holds at most one component of each concrete type.
/// </remarks>
public abstract class Component
{
    private bool _enabled = true;

    /// <summary>Stable identity, persisted in scene files and used by references.</summary>
    public Guid Id { get; internal set; }

    public GameObject GameObject { get; internal set; } = null!;

    public Transform Transform => GameObject.Transform;

    /// <summary>Whether this component wants to run. It actually runs only if its GameObject is also active in the hierarchy.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            GameObject?.Scene.SyncLifecycle(this);
        }
    }

    public bool IsActiveAndEnabled => _enabled && !IsDestroyed && GameObject.ActiveInHierarchy;

    public bool IsDestroyed { get; internal set; }

    internal bool Awoken { get; set; }

    internal bool OnEnableCalled { get; set; }

    internal bool Started { get; set; }

    internal bool StartQueued { get; set; }

    public virtual void Awake()
    {
    }

    public virtual void OnEnable()
    {
    }

    public virtual void Start()
    {
    }

    public virtual void FixedUpdate()
    {
    }

    public virtual void Update()
    {
    }

    public virtual void LateUpdate()
    {
    }

    public virtual void OnDisable()
    {
    }

    public virtual void OnDestroy()
    {
    }

    public T? GetComponent<T>()
        where T : class => GameObject.GetComponent<T>();

    /// <summary>Destroys this component at the end of the frame (immediately in edit mode).</summary>
    public void Destroy() => GameObject.Scene.Destroy(this);

    public override string ToString() => $"{GetType().Name} on '{GameObject?.Name}'";
}

/// <summary>Persists a non-public field or a property of a component. Public fields are persisted by default.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class SerializeFieldAttribute : Attribute;

/// <summary>Marks a string member as a path relative to the project's Assets folder, so tools can offer matching assets.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class AssetPathAttribute(params string[] extensions) : Attribute
{
    /// <summary>Accepted extensions including the dot, e.g. ".png". Empty accepts any file.</summary>
    public IReadOnlyList<string> Extensions { get; } = extensions;
}

/// <summary>Excludes a public field from persistence.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class DontSerializeAttribute : Attribute;

/// <summary>
/// Optional: the type id written to scene files. Without it the id is the full type name (<c>Namespace.Class</c>),
/// which is what game code normally wants. After renaming or moving a component, add
/// <see cref="ComponentAliasAttribute"/> with the old name instead, so existing scenes keep loading.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ComponentIdAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

/// <summary>
/// An id this component was saved under before a rename or namespace move. Scenes written with the old id still load;
/// they are saved with the current id from then on. Repeat for several old names.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class ComponentAliasAttribute(string formerId) : Attribute
{
    public string FormerId { get; } = formerId;
}

/// <summary>Saved, but not listed in the editor's Inspector (the member has a dedicated editor UI).</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class HideInInspectorAttribute : Attribute;
