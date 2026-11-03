namespace MNVTEngine.ECS;

// Queries iterate the first type's pool from the end and filter by the others.
// Put the rarest component first for the fewest checks. Use with foreach:
//     foreach (var e in world.Query<Position, Velocity>()) world.Get<Position>(e).Value += ...

public readonly struct Query<T1>(World world)
{
    public QueryEnumerator GetEnumerator() => new(world, world.Pool<T1>(), null, null);
}

public readonly struct Query<T1, T2>(World world)
{
    public QueryEnumerator GetEnumerator() => new(world, world.Pool<T1>(), world.Pool<T2>(), null);
}

public readonly struct Query<T1, T2, T3>(World world)
{
    public QueryEnumerator GetEnumerator() => new(world, world.Pool<T1>(), world.Pool<T2>(), world.Pool<T3>());
}

public struct QueryEnumerator
{
    private readonly World _world;
    private readonly IComponentPool _primary;
    private readonly IComponentPool? _second;
    private readonly IComponentPool? _third;
    private int _slot;

    internal QueryEnumerator(World world, IComponentPool primary, IComponentPool? second, IComponentPool? third)
    {
        _world = world;
        _primary = primary;
        _second = second;
        _third = third;
        _slot = primary.Count;
        Current = Entity.Null;
    }

    public Entity Current { get; private set; }

    public bool MoveNext()
    {
        while (--_slot >= 0)
        {
            // The pool may have shrunk if the previous entity was destroyed; clamp to the live range.
            if (_slot >= _primary.Count)
            {
                continue;
            }

            var index = _primary.EntityIndexAt(_slot);
            if ((_second is null || _second.Has(index)) && (_third is null || _third.Has(index)))
            {
                Current = _world.EntityAt(index);
                return true;
            }
        }

        return false;
    }
}
