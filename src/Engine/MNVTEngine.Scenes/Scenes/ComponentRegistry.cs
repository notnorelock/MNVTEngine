using System.Reflection;

namespace MNVTEngine.Scenes;

/// <summary>
/// Explicit mapping between stable component type ids (written to files) and CLR types. Scene files can only
/// instantiate types registered here, so data never chooses arbitrary types to construct.
/// </summary>
public sealed class ComponentRegistry
{
    private readonly Dictionary<string, Type> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = [];
    private readonly Dictionary<string, Type> _aliases = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, Type> Types => _byId;

    /// <summary>Registry with the engine's built-in components.</summary>
    public static ComponentRegistry CreateDefault()
    {
        var registry = new ComponentRegistry();
        registry.Register<Transform>();
        registry.Register<SpriteRenderer>();
        registry.Register<Camera>();
        registry.Register<SpriteAnimator>();
        registry.Register<Tilemap>();
        registry.Register<Light2D>();
        registry.Register<GlobalLight2D>();
        registry.Register<MeshRenderer>();
        registry.Register<Light>();
        registry.Register<AmbientLight>();
        registry.Register<ModelAnimator>();
        registry.Register<ModelAnimatorController>();
        registry.Register<SceneEnvironment>();
        return registry;
    }

    /// <summary>The id written to files: <see cref="ComponentIdAttribute"/> if present, otherwise the full type name.</summary>
    public static string IdOf(Type type) => type.GetCustomAttribute<ComponentIdAttribute>()?.Id ?? type.FullName ?? type.Name;

    public ComponentRegistry Register<T>()
        where T : Component, new() => Register(typeof(T));

    /// <exception cref="ArgumentException">Not a concrete component with a public parameterless constructor.</exception>
    /// <exception cref="InvalidOperationException">The id is already used by a different type.</exception>
    public ComponentRegistry Register(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!typeof(Component).IsAssignableFrom(type) || type.IsAbstract || type == typeof(MissingComponent) || type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new ArgumentException($"{type.FullName} must be a non-abstract Component with a public parameterless constructor.", nameof(type));
        }

        var id = IdOf(type);
        if (_byId.TryGetValue(id, out var existing))
        {
            return existing == type
                ? this
                : throw new InvalidOperationException($"Component id '{id}' is used by both {existing.FullName} and {type.FullName}.");
        }

        _byId.Add(id, type);
        _byType.Add(type, id);
        foreach (var alias in type.GetCustomAttributes<ComponentAliasAttribute>())
        {
            if (_byId.ContainsKey(alias.FormerId) || (_aliases.TryGetValue(alias.FormerId, out var other) && other != type))
            {
                throw new InvalidOperationException($"Component alias '{alias.FormerId}' of {type.FullName} is already used by another component.");
            }

            _aliases[alias.FormerId] = type;
        }

        return this;
    }

    /// <summary>Registers every public concrete component type in <paramref name="assembly"/>.</summary>
    public ComponentRegistry RegisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        foreach (var type in assembly.GetExportedTypes())
        {
            if (typeof(Component).IsAssignableFrom(type) && !type.IsAbstract && type != typeof(MissingComponent) && type.GetConstructor(Type.EmptyTypes) is not null)
            {
                Register(type);
            }
        }

        return this;
    }

    /// <summary>Removes every type from <paramref name="assembly"/> (before unloading or reloading game code).</summary>
    /// <returns>The number of types removed.</returns>
    public int Unregister(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var removed = _byType.Keys.Where(t => t.Assembly == assembly).ToList();
        foreach (var type in removed)
        {
            _byId.Remove(_byType[type]);
            _byType.Remove(type);
            foreach (var alias in _aliases.Where(a => a.Value == type).Select(a => a.Key).ToList())
            {
                _aliases.Remove(alias);
            }
        }

        return removed.Count;
    }

    /// <summary>Resolves a saved id, including former ids declared with <see cref="ComponentAliasAttribute"/>.</summary>
    public bool TryGetType(string id, out Type type) => _byId.TryGetValue(id, out type!) || _aliases.TryGetValue(id, out type!);

    /// <exception cref="InvalidOperationException">The type is not registered.</exception>
    public string GetId(Type type) =>
        _byType.TryGetValue(type, out var id)
            ? id
            : throw new InvalidOperationException($"Component type {type.FullName} is not registered; it cannot be saved.");
}
