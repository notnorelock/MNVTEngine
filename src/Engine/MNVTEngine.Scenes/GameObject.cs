using MNVTEngine.ECS;
using MNVTEngine.Scenes;

namespace MNVTEngine;

/// <summary>
/// Named object in a <see cref="Scene"/>: an ECS entity plus its components. Every GameObject has a
/// <see cref="Transform"/>. Create through <see cref="Scene.CreateGameObject"/>.
/// </summary>
public sealed class GameObject
{
    private readonly List<Component> _components = [];
    private bool _activeSelf = true;

    internal GameObject(Scene scene, string name, Guid id, Entity entity)
    {
        Scene = scene;
        Name = name;
        Id = id;
        Entity = entity;
        Transform = new Transform { Id = id, GameObject = this };
    }

    /// <summary>Stable identity, persisted in scene files. The Transform shares this id.</summary>
    public Guid Id { get; }

    public string Name { get; set; }

    public Scene Scene { get; }

    /// <summary>ECS handle, for adding struct components and querying from systems.</summary>
    public Entity Entity { get; }

    public Transform Transform { get; }

    public bool IsDestroyed { get; internal set; }

    public bool ActiveSelf => _activeSelf;

    public bool ActiveInHierarchy => _activeSelf && (Transform.Parent?.GameObject.ActiveInHierarchy ?? true);

    /// <summary>All components in attach order, starting with the Transform.</summary>
    public IReadOnlyList<Component> Components => _components;

    public void SetActive(bool active)
    {
        if (_activeSelf == active)
        {
            return;
        }

        _activeSelf = active;
        Scene.SyncHierarchyLifecycle(this);
    }

    public T AddComponent<T>()
        where T : Component, new() => (T)AddComponent(new T());

    /// <exception cref="InvalidOperationException">A component of the same type is already attached.</exception>
    public Component AddComponent(Type componentType)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        if (!typeof(Component).IsAssignableFrom(componentType) || componentType.IsAbstract || componentType == typeof(Transform))
        {
            throw new ArgumentException($"{componentType.Name} is not an instantiable component type.", nameof(componentType));
        }

        var component = (Component?)Activator.CreateInstance(componentType)
            ?? throw new InvalidOperationException($"Could not create {componentType.Name}.");
        return AddComponent(component);
    }

    /// <summary>First component assignable to <typeparamref name="T"/> (class or interface), or null.</summary>
    public T? GetComponent<T>()
        where T : class
    {
        foreach (var component in _components)
        {
            if (component is T match && !component.IsDestroyed)
            {
                return match;
            }
        }

        return null;
    }

    public bool TryGetComponent<T>(out T component)
        where T : class
    {
        component = GetComponent<T>()!;
        return component is not null;
    }

    public IEnumerable<T> GetComponents<T>()
        where T : class => _components.Where(c => !c.IsDestroyed).OfType<T>();

    /// <summary>Depth-first search of this object and its descendants.</summary>
    public T? GetComponentInChildren<T>()
        where T : class
    {
        if (GetComponent<T>() is { } own)
        {
            return own;
        }

        foreach (var child in Transform.Children)
        {
            if (child.GameObject.GetComponentInChildren<T>() is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Destroys this object and its children at the end of the frame (immediately in edit mode).</summary>
    public void Destroy() => Scene.Destroy(this);

    public override string ToString() => $"GameObject '{Name}'";

    internal Component AddComponent(Component component, Guid? id = null)
    {
        ObjectDisposedException.ThrowIf(IsDestroyed, this);
        var type = component.GetType();
        if (component is not MissingComponent && _components.Any(c => c.GetType() == type && !c.IsDestroyed))
        {
            throw new InvalidOperationException($"'{Name}' already has a {type.Name}. A GameObject holds at most one component of each type.");
        }

        component.Id = id ?? Guid.NewGuid();
        component.GameObject = this;
        _components.Add(component);
        Scene.RegisterComponent(component);
        return component;
    }

    internal void AttachTransform() => _components.Insert(0, Transform);

    internal void RemoveComponent(Component component) => _components.Remove(component);
}
