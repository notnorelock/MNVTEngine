namespace MNVTEngine.ECS;

/// <summary>
/// Entity registry and component storage. Components may be structs (plain data) or classes
/// (object components such as scene behaviours). Not thread-safe.
/// </summary>
/// <remarks>
/// Iteration safety: queries walk pools from the end, so while iterating you may destroy the current
/// entity, remove its components, or create entities. Removing components from <em>other</em> entities
/// during a query is not supported; defer it (the scene layer defers all destruction to end of frame).
/// </remarks>
public sealed class World
{
    private readonly Dictionary<Type, IComponentPool> _pools = [];
    private readonly Stack<int> _freeIndices = new();
    private int[] _generations = new int[64];
    private int _nextIndex;

    public int EntityCount { get; private set; }

    public IEnumerable<IComponentPool> Pools => _pools.Values;

    public Entity CreateEntity()
    {
        var index = _freeIndices.Count > 0 ? _freeIndices.Pop() : _nextIndex++;
        if (index >= _generations.Length)
        {
            Array.Resize(ref _generations, _generations.Length * 2);
        }

        // Generation 0 is reserved for Entity.Null; a live slot always has a positive generation.
        if (_generations[index] <= 0)
        {
            _generations[index] = -_generations[index] + 1;
        }

        EntityCount++;
        return new Entity(index, _generations[index]);
    }

    public bool IsAlive(Entity entity) =>
        !entity.IsNull && (uint)entity.Index < (uint)_nextIndex && _generations[entity.Index] == entity.Generation;

    /// <summary>Removes the entity and all its components. Returns false for stale or null handles.</summary>
    public bool DestroyEntity(Entity entity)
    {
        if (!IsAlive(entity))
        {
            return false;
        }

        foreach (var pool in _pools.Values)
        {
            pool.Remove(entity.Index);
        }

        // Negative = free; the next CreateEntity on this slot bumps it past the old generation.
        _generations[entity.Index] = -_generations[entity.Index];
        _freeIndices.Push(entity.Index);
        EntityCount--;
        return true;
    }

    public void Add<T>(Entity entity, T component)
    {
        CheckAlive(entity);
        Pool<T>().Add(entity.Index, component);
    }

    /// <summary>Adds a component whose type is only known at runtime (deserialization, scripting).</summary>
    public void AddBoxed(Entity entity, Type componentType, object component)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        ArgumentNullException.ThrowIfNull(component);
        CheckAlive(entity);
        if (!componentType.IsInstanceOfType(component))
        {
            throw new ArgumentException($"Component is {component.GetType().Name}, not {componentType.Name}.", nameof(component));
        }

        PoolFor(componentType).AddBoxed(entity.Index, component);
    }

    public ref T Get<T>(Entity entity)
    {
        CheckAlive(entity);
        return ref Pool<T>().Get(entity.Index);
    }

    public bool TryGet<T>(Entity entity, out T component)
    {
        if (IsAlive(entity) && _pools.TryGetValue(typeof(T), out var pool) && pool.Has(entity.Index))
        {
            component = ((ComponentPool<T>)pool).Get(entity.Index);
            return true;
        }

        component = default!;
        return false;
    }

    public bool Has<T>(Entity entity) =>
        IsAlive(entity) && _pools.TryGetValue(typeof(T), out var pool) && pool.Has(entity.Index);

    public bool Remove<T>(Entity entity) => Remove(entity, typeof(T));

    public bool Remove(Entity entity, Type componentType) =>
        IsAlive(entity) && _pools.TryGetValue(componentType, out var pool) && pool.Remove(entity.Index);

    public ComponentPool<T> Pool<T>()
    {
        if (!_pools.TryGetValue(typeof(T), out var pool))
        {
            pool = new ComponentPool<T>();
            _pools.Add(typeof(T), pool);
        }

        return (ComponentPool<T>)pool;
    }

    /// <summary>Live entity handle for an index known to be alive (e.g. from a pool's dense array).</summary>
    public Entity EntityAt(int index) => new(index, _generations[index]);

    public Query<T1> Query<T1>() => new(this);

    public Query<T1, T2> Query<T1, T2>() => new(this);

    public Query<T1, T2, T3> Query<T1, T2, T3>() => new(this);

    private IComponentPool PoolFor(Type componentType)
    {
        if (!_pools.TryGetValue(componentType, out var pool))
        {
            pool = (IComponentPool)Activator.CreateInstance(typeof(ComponentPool<>).MakeGenericType(componentType))!;
            _pools.Add(componentType, pool);
        }

        return pool;
    }

    private void CheckAlive(Entity entity)
    {
        if (!IsAlive(entity))
        {
            throw new InvalidOperationException($"{entity} is not alive in this world.");
        }
    }
}
