using Microsoft.Extensions.Logging;

namespace MNVTEngine;

/// <summary>DontDestroyOnLoad: root objects that hosts carry from the running scene into the next one.</summary>
public sealed partial class Scene
{
    /// <summary>Root objects marked with <see cref="GameObject.DontDestroyOnLoad"/>.</summary>
    public IEnumerable<GameObject> PersistentObjects => _roots.Where(r => r.PersistsAcrossLoads && !r.IsDestroyed);

    internal void DontDestroyOnLoad(GameObject gameObject)
    {
        if (gameObject.Scene != this || gameObject.IsDestroyed)
        {
            return;
        }

        if (gameObject.Transform.Parent is not null)
        {
            LogNotRoot(Logger, gameObject.Name);
            return;
        }

        gameObject.PersistsAcrossLoads = true;
    }

    /// <summary>
    /// Moves every persistent root (with its children) into <paramref name="target"/>, keeping the same objects and
    /// component instances: their state, lifecycle (no second Awake/OnEnable; a pending Start still runs) and queued
    /// destroys. Each moved component then gets <see cref="Component.OnMovedToScene"/>. An object whose id is already
    /// used in the target (the same scene loaded again) gets a new id. Hosts call this between setting up the new scene's
    /// services and unloading this one.
    /// </summary>
    /// <returns>The moved roots.</returns>
    public IReadOnlyList<GameObject> MovePersistentObjectsTo(Scene target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target == this)
        {
            return [];
        }

        var roots = PersistentObjects.ToList();
        var moved = new List<GameObject>();
        foreach (var root in roots)
        {
            _roots.Remove(root);
            target._roots.Add(root);
            CollectSubtree(root, moved);
        }

        foreach (var gameObject in moved)
        {
            MoveObject(gameObject, target);
        }

        // Destroys queued this frame still happen, in the new scene.
        foreach (var queued in _destroyQueue.ToList())
        {
            var owner = queued switch
            {
                GameObject g => g,
                Component c => c.GameObject,
                _ => null,
            };

            if (owner?.Scene == target)
            {
                _destroyQueue.Remove(queued);
                target._destroyQueue.Add(queued);
            }
        }

        var previous = SceneContext.Enter(target);
        try
        {
            foreach (var component in moved.SelectMany(g => g.Components).ToList())
            {
                target.Invoke(component, c => c.OnMovedToScene(this), nameof(Component.OnMovedToScene));
            }
        }
        finally
        {
            SceneContext.Exit(previous);
        }

        return roots;
    }

    private static void CollectSubtree(GameObject node, List<GameObject> into)
    {
        into.Add(node);
        foreach (var child in node.Transform.Children)
        {
            CollectSubtree(child.GameObject, into);
        }
    }

    private void MoveObject(GameObject gameObject, Scene target)
    {
        // Everything the entity holds in this world: the component objects, and any struct components systems added.
        var entries = new List<(Type Type, object Value)>();
        foreach (var pool in World.Pools)
        {
            if (pool.Has(gameObject.Entity.Index))
            {
                entries.Add((pool.ComponentType, pool.GetBoxed(gameObject.Entity.Index)));
            }
        }

        World.DestroyEntity(gameObject.Entity);
        _gameObjects.Remove(gameObject);
        _byId.Remove(gameObject.Id);
        foreach (var component in gameObject.Components)
        {
            _allComponents.Remove(component);
            _updateList.Remove(component);
            _fixedUpdateList.Remove(component);
            _lateUpdateList.Remove(component);
            _pendingStart.Remove(component);
        }

        if (target._byId.ContainsKey(gameObject.Id))
        {
            gameObject.Id = Guid.NewGuid();
            gameObject.Transform.Id = gameObject.Id;
        }

        gameObject.Scene = target;
        gameObject.Entity = target.World.CreateEntity();
        target._gameObjects.Add(gameObject);
        target._byId.Add(gameObject.Id, gameObject);
        foreach (var (type, value) in entries)
        {
            target.World.AddBoxed(gameObject.Entity, type, value);
        }

        foreach (var component in gameObject.Components)
        {
            target._allComponents.Add(component);
            var info = Scenes.ComponentTypeInfo.Get(component.GetType());
            if (info.HasUpdate)
            {
                target._updateList.Add(component);
            }

            if (info.HasFixedUpdate)
            {
                target._fixedUpdateList.Add(component);
            }

            if (info.HasLateUpdate)
            {
                target._lateUpdateList.Add(component);
            }

            if (component.StartQueued && !component.Started)
            {
                target._pendingStart.Add(component);
            }
        }
    }

    [LoggerMessage(EventId = 1210, Level = LogLevel.Warning, Message = "DontDestroyOnLoad only works for root GameObjects: '{Name}' has a parent")]
    private static partial void LogNotRoot(ILogger logger, string name);
}
