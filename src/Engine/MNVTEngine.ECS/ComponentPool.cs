namespace MNVTEngine.ECS;

/// <summary>Untyped view of a pool, for structural operations that only know the component <see cref="Type"/>.</summary>
public interface IComponentPool
{
    Type ComponentType { get; }

    int Count { get; }

    bool Has(int entityIndex);

    /// <summary>Entity index stored at a dense slot in [0, Count).</summary>
    int EntityIndexAt(int slot);

    bool Remove(int entityIndex);

    void AddBoxed(int entityIndex, object component);

    object GetBoxed(int entityIndex);
}

/// <summary>
/// Sparse set: components are packed densely for cache-friendly iteration, with an index-based lookup
/// table for O(1) has/get/add/remove. Removal swaps the last element into the hole.
/// </summary>
public sealed class ComponentPool<T> : IComponentPool
{
    private const int Absent = -1;

    private T[] _dense = new T[16];
    private int[] _denseEntities = new int[16];
    private int[] _sparse = [];

    public Type ComponentType => typeof(T);

    public int Count { get; private set; }

    /// <summary>Entity indices in dense order. Valid for indices below <see cref="Count"/>.</summary>
    public ReadOnlySpan<int> EntityIndices => _denseEntities.AsSpan(0, Count);

    public Span<T> Components => _dense.AsSpan(0, Count);

    public int EntityIndexAt(int slot) => _denseEntities[slot];

    public bool Has(int entityIndex) => (uint)entityIndex < (uint)_sparse.Length && _sparse[entityIndex] != Absent;

    /// <exception cref="InvalidOperationException">The entity already has this component.</exception>
    public void Add(int entityIndex, T component)
    {
        if (Has(entityIndex))
        {
            throw new InvalidOperationException($"Entity {entityIndex} already has a {typeof(T).Name}.");
        }

        EnsureSparse(entityIndex);
        if (Count == _dense.Length)
        {
            Array.Resize(ref _dense, Count * 2);
            Array.Resize(ref _denseEntities, Count * 2);
        }

        _dense[Count] = component;
        _denseEntities[Count] = entityIndex;
        _sparse[entityIndex] = Count;
        Count++;
    }

    /// <exception cref="KeyNotFoundException">The entity has no such component.</exception>
    public ref T Get(int entityIndex)
    {
        if (!Has(entityIndex))
        {
            throw new KeyNotFoundException($"Entity {entityIndex} has no {typeof(T).Name}.");
        }

        return ref _dense[_sparse[entityIndex]];
    }

    public bool Remove(int entityIndex)
    {
        if (!Has(entityIndex))
        {
            return false;
        }

        var slot = _sparse[entityIndex];
        var last = Count - 1;
        if (slot != last)
        {
            _dense[slot] = _dense[last];
            _denseEntities[slot] = _denseEntities[last];
            _sparse[_denseEntities[slot]] = slot;
        }

        _dense[last] = default!;
        _sparse[entityIndex] = Absent;
        Count--;
        return true;
    }

    public void AddBoxed(int entityIndex, object component) => Add(entityIndex, (T)component);

    public object GetBoxed(int entityIndex) => Get(entityIndex)!;

    private void EnsureSparse(int entityIndex)
    {
        if (entityIndex < _sparse.Length)
        {
            return;
        }

        var old = _sparse.Length;
        Array.Resize(ref _sparse, Math.Max(entityIndex + 1, Math.Max(16, old * 2)));
        _sparse.AsSpan(old).Fill(Absent);
    }
}
