using MNVTEngine.Core.Timing;

namespace MNVTEngine.ECS;

/// <summary>
/// Data-oriented logic that runs over component queries each frame. Systems run after component
/// behaviours in the same phase, ordered by <see cref="Order"/> (ties keep registration order).
/// </summary>
public abstract class GameSystem
{
    /// <summary>Lower runs first.</summary>
    public virtual int Order => 0;

    public bool Enabled { get; set; } = true;

    public virtual void FixedUpdate(World world, float fixedDeltaTime)
    {
    }

    public virtual void Update(World world, FrameTime time)
    {
    }

    public virtual void LateUpdate(World world, FrameTime time)
    {
    }
}

/// <summary>Ordered set of systems for one world.</summary>
public sealed class SystemScheduler
{
    private readonly List<GameSystem> _systems = [];

    public IReadOnlyList<GameSystem> Systems => _systems;

    public void Add(GameSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (_systems.Contains(system))
        {
            throw new InvalidOperationException($"{system.GetType().Name} is already registered.");
        }

        // Insert after every system with Order <= the new one: stable by registration.
        var index = _systems.FindLastIndex(s => s.Order <= system.Order) + 1;
        _systems.Insert(index, system);
    }

    public bool Remove(GameSystem system) => _systems.Remove(system);

    public T? Get<T>()
        where T : GameSystem => _systems.OfType<T>().FirstOrDefault();

    public void FixedUpdate(World world, float fixedDeltaTime)
    {
        foreach (var system in _systems.ToArray())
        {
            if (system.Enabled)
            {
                system.FixedUpdate(world, fixedDeltaTime);
            }
        }
    }

    public void Update(World world, FrameTime time)
    {
        foreach (var system in _systems.ToArray())
        {
            if (system.Enabled)
            {
                system.Update(world, time);
            }
        }
    }

    public void LateUpdate(World world, FrameTime time)
    {
        foreach (var system in _systems.ToArray())
        {
            if (system.Enabled)
            {
                system.LateUpdate(world, time);
            }
        }
    }
}
