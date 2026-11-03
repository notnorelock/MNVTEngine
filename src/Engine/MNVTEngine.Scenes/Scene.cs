using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MNVTEngine.Core.Timing;
using MNVTEngine.ECS;
using MNVTEngine.Scenes;

namespace MNVTEngine;

/// <summary>
/// A set of GameObjects over an ECS <see cref="World"/>. In edit mode (not running) the scene is plain
/// data: no lifecycle callbacks run and destruction is immediate. <see cref="Start"/> switches to running
/// mode, after which the host drives <see cref="FixedUpdate"/> and <see cref="Update"/>.
/// </summary>
public sealed partial class Scene
{
    private readonly List<GameObject> _gameObjects = [];
    private readonly List<GameObject> _roots = [];
    private readonly Dictionary<Guid, GameObject> _byId = [];
    private readonly List<Component> _updateList = [];
    private readonly List<Component> _fixedUpdateList = [];
    private readonly List<Component> _lateUpdateList = [];
    private readonly List<Component> _allComponents = [];
    private readonly List<Component> _pendingStart = [];
    private readonly List<object> _destroyQueue = [];

    public Scene(string name = "Scene")
    {
        Name = name;
    }

    public string Name { get; set; }

    public World World { get; } = new();

    public SystemScheduler Systems { get; } = new();

    public bool IsRunning { get; private set; }

    public ILogger Logger { get; set; } = NullLogger.Instance;

    /// <summary>Input source for the <see cref="MNVTEngine.Input"/> facade. Null reads as no input.</summary>
    public InputState? Input { get; set; }

    /// <summary>Host service for <see cref="SceneManager.LoadScene(string)"/>.</summary>
    public ISceneLoader? Loader { get; set; }

    /// <summary>Host service for <see cref="Instantiate"/> and <see cref="Prefab.Instantiate"/>.</summary>
    public IPrefabProvider? Prefabs { get; set; }

    /// <summary>Host service for components that read text assets, such as mission scripts.</summary>
    public ITextAssetReader? TextAssets { get; set; }

    /// <summary>Exceptions from component callbacks are logged and the frame continues; set true to rethrow (tests, debugging).</summary>
    public bool RethrowScriptExceptions { get; set; }

    /// <summary>All live GameObjects in creation order.</summary>
    public IReadOnlyList<GameObject> GameObjects => _gameObjects;

    /// <summary>Top-level GameObjects in sibling order.</summary>
    public IReadOnlyList<GameObject> RootObjects => _roots;

    /// <summary>First enabled <see cref="Camera"/> on an active object, in creation order.</summary>
    public Camera? MainCamera => FindObjectsOfType<Camera>().FirstOrDefault(c => c.IsActiveAndEnabled);

    internal FrameTime FrameTime { get; private set; }

    internal float FixedDeltaTime { get; private set; } = 1f / 60f;

    internal bool InFixedUpdate { get; private set; }

    public GameObject CreateGameObject(string name = "GameObject", Transform? parent = null)
    {
        var gameObject = CreateGameObject(name, Guid.NewGuid());
        if (parent is not null)
        {
            gameObject.Transform.SetParent(parent, worldPositionStays: false);
        }

        return gameObject;
    }

    /// <summary>Creates a copy of a prefab asset in this scene, optionally under <paramref name="parent"/>.</summary>
    /// <exception cref="InvalidOperationException">No prefab provider, or the prefab cannot be loaded.</exception>
    public GameObject Instantiate(AssetRef prefab, Transform? parent = null)
    {
        var provider = Prefabs ?? throw new InvalidOperationException("This scene has no prefab provider; it must be hosted by the runtime or editor.");
        return provider.Instantiate(this, prefab, parent);
    }

    public GameObject? Find(string name) => _gameObjects.FirstOrDefault(g => g.Name == name);

    public GameObject? FindById(Guid id) => _byId.GetValueOrDefault(id);

    public IEnumerable<T> FindObjectsOfType<T>()
        where T : class => _gameObjects.SelectMany(g => g.GetComponents<T>());

    /// <summary>Destroys at the end of the frame while running; immediately in edit mode.</summary>
    public void Destroy(GameObject gameObject)
    {
        ArgumentNullException.ThrowIfNull(gameObject);
        if (gameObject.Scene != this || gameObject.IsDestroyed)
        {
            return;
        }

        if (IsRunning)
        {
            _destroyQueue.Add(gameObject);
        }
        else
        {
            DestroyNow(gameObject);
        }
    }

    /// <exception cref="InvalidOperationException">Attempt to destroy a Transform; destroy the GameObject instead.</exception>
    public void Destroy(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (component is Transform)
        {
            throw new InvalidOperationException("A Transform cannot be removed; destroy the GameObject instead.");
        }

        if (component.GameObject.Scene != this || component.IsDestroyed)
        {
            return;
        }

        if (IsRunning)
        {
            _destroyQueue.Add(component);
        }
        else
        {
            DestroyComponentNow(component);
        }
    }

    /// <summary>Enters running mode: Awake on every active object, then OnEnable. Start runs before the first update.</summary>
    public void Start()
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("The scene is already running.");
        }

        var previous = SceneContext.Enter(this);
        try
        {
            IsRunning = true;
            foreach (var component in _allComponents.ToArray())
            {
                if (component.GameObject.ActiveInHierarchy && !component.Awoken)
                {
                    component.Awoken = true;
                    Invoke(component, static c => c.Awake(), nameof(Component.Awake));
                }
            }

            foreach (var component in _allComponents.ToArray())
            {
                SyncLifecycle(component);
            }
        }
        finally
        {
            SceneContext.Exit(previous);
        }
    }

    public void FixedUpdate(float fixedDeltaTime)
    {
        EnsureRunning();
        var previous = SceneContext.Enter(this);
        try
        {
            FixedDeltaTime = fixedDeltaTime;
            RunPendingStarts();
            InFixedUpdate = true;
            using (Core.Diagnostics.Profiler.Sample("Scripts.FixedUpdate"))
            {
                Run(_fixedUpdateList, static c => c.FixedUpdate(), nameof(Component.FixedUpdate));
            }

            Systems.FixedUpdate(World, fixedDeltaTime);
        }
        finally
        {
            InFixedUpdate = false;
            SceneContext.Exit(previous);
        }
    }

    /// <summary>Start for new components, Update, systems, LateUpdate, late systems, then pending destruction.</summary>
    public void Update(FrameTime time)
    {
        EnsureRunning();
        var previous = SceneContext.Enter(this);
        try
        {
            FrameTime = time;
            using (Core.Diagnostics.Profiler.Sample("Scripts.Start"))
            {
                RunPendingStarts();
            }

            using (Core.Diagnostics.Profiler.Sample("Scripts.Update"))
            {
                Run(_updateList, static c => c.Update(), nameof(Component.Update));
            }

            Systems.Update(World, time);
            using (Core.Diagnostics.Profiler.Sample("Scripts.LateUpdate"))
            {
                Run(_lateUpdateList, static c => c.LateUpdate(), nameof(Component.LateUpdate));
            }

            Systems.LateUpdate(World, time);
            using (Core.Diagnostics.Profiler.Sample("Destroy"))
            {
                FlushDestroyQueue();
            }

            Core.Diagnostics.Profiler.Count("Game objects", _gameObjects.Count);
        }
        finally
        {
            SceneContext.Exit(previous);
        }
    }

    /// <summary>Raised at the end of <see cref="Unload"/>, after every object is destroyed; scene-level services release their resources.</summary>
    public event Action<Scene>? Unloaded;

    /// <summary>Destroys every object (running OnDisable/OnDestroy when running) and returns to edit mode.</summary>
    public void Unload()
    {
        var previous = SceneContext.Enter(this);
        try
        {
            _destroyQueue.Clear();
            foreach (var root in _roots.ToArray())
            {
                DestroyNow(root);
            }

            IsRunning = false;
        }
        finally
        {
            SceneContext.Exit(previous);
        }

        Unloaded?.Invoke(this);
    }

    internal GameObject CreateGameObject(string name, Guid id)
    {
        if (_byId.ContainsKey(id))
        {
            throw new InvalidOperationException($"A GameObject with id {id} already exists in scene '{Name}'.");
        }

        var gameObject = new GameObject(this, name, id, World.CreateEntity());
        _gameObjects.Add(gameObject);
        _roots.Add(gameObject);
        _byId.Add(id, gameObject);
        gameObject.AttachTransform();
        RegisterComponent(gameObject.Transform);
        return gameObject;
    }

    internal int RootIndexOf(GameObject gameObject) => _roots.IndexOf(gameObject);

    internal void RegisterComponent(Component component)
    {
        if (component is not MissingComponent)
        {
            World.AddBoxed(component.GameObject.Entity, component.GetType(), component);
        }

        _allComponents.Add(component);
        var info = ComponentTypeInfo.Get(component.GetType());
        if (info.HasUpdate)
        {
            _updateList.Add(component);
        }

        if (info.HasFixedUpdate)
        {
            _fixedUpdateList.Add(component);
        }

        if (info.HasLateUpdate)
        {
            _lateUpdateList.Add(component);
        }

        if (IsRunning)
        {
            var previous = SceneContext.Enter(this);
            try
            {
                SyncLifecycle(component);
            }
            finally
            {
                SceneContext.Exit(previous);
            }
        }
    }

    /// <summary>Brings a component's Awake/OnEnable/OnDisable state in line with its enabled and active flags.</summary>
    internal void SyncLifecycle(Component component)
    {
        if (!IsRunning || component.IsDestroyed)
        {
            return;
        }

        var active = component.GameObject.ActiveInHierarchy;
        if (active && !component.Awoken)
        {
            component.Awoken = true;
            Invoke(component, static c => c.Awake(), nameof(Component.Awake));
        }

        var shouldRun = active && component.Enabled && !component.IsDestroyed;
        if (shouldRun && !component.OnEnableCalled)
        {
            component.OnEnableCalled = true;
            Invoke(component, static c => c.OnEnable(), nameof(Component.OnEnable));
            if (!component.Started && !component.StartQueued)
            {
                component.StartQueued = true;
                _pendingStart.Add(component);
            }
        }
        else if (!shouldRun && component.OnEnableCalled)
        {
            component.OnEnableCalled = false;
            Invoke(component, static c => c.OnDisable(), nameof(Component.OnDisable));
        }
    }

    internal void SyncHierarchyLifecycle(GameObject gameObject)
    {
        if (gameObject.Transform.Parent is null != _roots.Contains(gameObject))
        {
            if (gameObject.Transform.Parent is null)
            {
                _roots.Add(gameObject);
            }
            else
            {
                _roots.Remove(gameObject);
            }
        }

        if (!IsRunning)
        {
            return;
        }

        var previous = SceneContext.Enter(this);
        try
        {
            SyncSubtree(gameObject);
        }
        finally
        {
            SceneContext.Exit(previous);
        }

        void SyncSubtree(GameObject node)
        {
            foreach (var component in node.Components.ToArray())
            {
                SyncLifecycle(component);
            }

            foreach (var child in node.Transform.Children.ToArray())
            {
                SyncSubtree(child.GameObject);
            }
        }
    }

    private void RunPendingStarts()
    {
        // Components enabled during a Start are appended and started in the same pass.
        for (var i = 0; i < _pendingStart.Count; i++)
        {
            var component = _pendingStart[i];
            component.StartQueued = false;
            if (!component.Started && component.IsActiveAndEnabled)
            {
                component.Started = true;
                Invoke(component, static c => c.Start(), nameof(Component.Start));
            }
        }

        _pendingStart.RemoveAll(c => c.Started || c.IsDestroyed);
    }

    private void Run(List<Component> list, Action<Component> callback, string name)
    {
        // Components added during the loop are appended and run from the next frame (after their Start).
        var count = list.Count;
        for (var i = 0; i < count; i++)
        {
            var component = list[i];
            if (component.Started && component.IsActiveAndEnabled)
            {
                Invoke(component, callback, name);
            }
        }
    }

    /// <summary>
    /// Runs an engine callback on a component (e.g. physics contacts) inside this scene's context, with the same
    /// exception handling as lifecycle callbacks: logged with context, unless <see cref="RethrowScriptExceptions"/>.
    /// </summary>
    public void InvokeCallback(Component component, Action<Component> callback, string name)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(callback);
        var previous = SceneContext.Enter(this);
        try
        {
            Invoke(component, callback, name);
        }
        finally
        {
            SceneContext.Exit(previous);
        }
    }

    private void Invoke(Component component, Action<Component> callback, string name)
    {
        try
        {
            callback(component);
        }
        catch (Exception ex) when (!RethrowScriptExceptions)
        {
            LogScriptException(Logger, ex, component.GetType().Name, name, component.GameObject.Name);
        }
    }

    private void FlushDestroyQueue()
    {
        // Destroy callbacks may queue more destruction; loop until stable.
        while (_destroyQueue.Count > 0)
        {
            var batch = _destroyQueue.ToArray();
            _destroyQueue.Clear();
            foreach (var item in batch)
            {
                if (item is GameObject gameObject)
                {
                    DestroyNow(gameObject);
                }
                else
                {
                    DestroyComponentNow((Component)item);
                }
            }
        }
    }

    private void DestroyNow(GameObject gameObject)
    {
        if (gameObject.IsDestroyed)
        {
            return;
        }

        foreach (var child in gameObject.Transform.Children.ToArray())
        {
            DestroyNow(child.GameObject);
        }

        // Transform last, so OnDestroy of other components can still read it.
        foreach (var component in gameObject.Components.Reverse().ToArray())
        {
            TearDown(component);
        }

        gameObject.IsDestroyed = true;
        gameObject.Transform.Parent?.DetachChild(gameObject.Transform);
        gameObject.Transform.ClearParentForDestroy();
        _roots.Remove(gameObject);
        _gameObjects.Remove(gameObject);
        _byId.Remove(gameObject.Id);
        World.DestroyEntity(gameObject.Entity);
        PruneDestroyed();
    }

    private void DestroyComponentNow(Component component)
    {
        if (component.IsDestroyed)
        {
            return;
        }

        TearDown(component);
        component.GameObject.RemoveComponent(component);
        if (component is not MissingComponent)
        {
            World.Remove(component.GameObject.Entity, component.GetType());
        }

        PruneDestroyed();
    }

    private void TearDown(Component component)
    {
        if (component.OnEnableCalled)
        {
            component.OnEnableCalled = false;
            Invoke(component, static c => c.OnDisable(), nameof(Component.OnDisable));
        }

        if (component.Awoken)
        {
            Invoke(component, static c => c.OnDestroy(), nameof(Component.OnDestroy));
        }

        component.IsDestroyed = true;
    }

    private void PruneDestroyed()
    {
        _allComponents.RemoveAll(static c => c.IsDestroyed);
        _updateList.RemoveAll(static c => c.IsDestroyed);
        _fixedUpdateList.RemoveAll(static c => c.IsDestroyed);
        _lateUpdateList.RemoveAll(static c => c.IsDestroyed);
        _pendingStart.RemoveAll(static c => c.IsDestroyed);
    }

    private void EnsureRunning()
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException("Call Start before updating the scene.");
        }
    }

    [LoggerMessage(EventId = 2000, Level = LogLevel.Error, Message = "{Component}.{Callback} on '{GameObject}' threw; continuing")]
    private static partial void LogScriptException(ILogger logger, Exception exception, string component, string callback, string gameObject);
}
