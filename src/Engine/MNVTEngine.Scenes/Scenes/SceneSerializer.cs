using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Scenes;

public sealed record SceneLoadResult(Scene Scene, IReadOnlyList<SerializationDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

public sealed record PrefabInstance(GameObject Root, IReadOnlyList<SerializationDiagnostic> Diagnostics);

/// <summary>
/// Reads and writes <c>.mnvt</c> scenes and <c>.mnvtprefab</c> prefabs (same format; a prefab holds one root
/// subtree). Loading never throws for problems inside individual objects: those become diagnostics and the rest
/// still loads. Only unreadable documents throw.
/// </summary>
/// <remarks>
/// Format (schema 2): <c>{ schemaVersion, name, gameObjects: [ { id, name, active, parent, transform: { localPosition,
/// localRotation, localScale }, components: [ { type, id, enabled, data } ] } ] }</c>. GameObjects are written
/// parents-first in sibling order; on load, sibling order follows file order. Asset references are
/// <c>"asset:&lt;guid&gt;"</c> strings. Schema 1 stored sprite textures as paths; they are converted on load.
/// </remarks>
public sealed class SceneSerializer
{
    public const int CurrentSchemaVersion = 2;

    private static readonly HashSet<string> RootKeys = ["schemaVersion", "name", "gameObjects"];
    private static readonly HashSet<string> ObjectKeys = ["id", "name", "active", "parent", "transform", "components"];
    private static readonly HashSet<string> ComponentKeys = ["type", "id", "enabled", "data"];

    private readonly ComponentRegistry _registry;

    /// <param name="assets">Resolves asset paths when upgrading old files; optional.</param>
    public SceneSerializer(ComponentRegistry registry, IAssetLookup? assets = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        Migrations.Add(1, (document, diagnostics) => MigrateTexturePaths(document, diagnostics, assets));
    }

    /// <summary>Register upgrades here when <see cref="CurrentSchemaVersion"/> increases.</summary>
    public JsonMigrationPipeline Migrations { get; } = new(CurrentSchemaVersion);

    public string Serialize(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var serializer = CreateSerializer(new SceneReferenceConverter(null, null, null));
        var objects = new JArray();
        foreach (var root in scene.RootObjects)
        {
            Write(root, objects, serializer);
        }

        return Document(scene.Name, objects);
    }

    /// <summary>Serializes <paramref name="root"/> and its descendants as a prefab. The root is written without a parent.</summary>
    public string SerializePrefab(GameObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var serializer = CreateSerializer(new SceneReferenceConverter(null, null, null));
        var objects = new JArray();
        Write(root, objects, serializer);
        objects[0]["parent"] = null;
        return Document(root.Name, objects);
    }

    /// <exception cref="SerializationException">Malformed JSON, unsupported schema version, or missing object list.</exception>
    public SceneLoadResult Deserialize(string json, string sourceName = "<memory>")
    {
        var diagnostics = new List<SerializationDiagnostic>();
        var (root, items) = Parse(json, sourceName, diagnostics);
        var scene = new Scene(root.Value<string>("name") ?? Path.GetFileNameWithoutExtension(sourceName));
        LoadObjects(scene, items, diagnostics);
        return new SceneLoadResult(scene, diagnostics);
    }

    /// <summary>
    /// Adds a copy of a prefab to <paramref name="scene"/>. Every object and component gets a fresh id and references
    /// inside the prefab are rewritten to the copies, so a prefab can be instantiated any number of times. In a running
    /// scene, lifecycle callbacks run after all data has been loaded.
    /// </summary>
    /// <exception cref="SerializationException">Unreadable prefab, or it does not contain exactly one root object.</exception>
    public PrefabInstance InstantiatePrefab(string json, Scene scene, Transform? parent = null, string sourceName = "<prefab>")
    {
        ArgumentNullException.ThrowIfNull(scene);
        var diagnostics = new List<SerializationDiagnostic>();
        var (_, items) = Parse(json, sourceName, diagnostics);
        RemapIds(items);

        var created = LoadObjects(scene, items, diagnostics);
        var roots = created.Where(g => g.Transform.Parent is null).ToList();
        if (roots.Count != 1)
        {
            foreach (var gameObject in roots)
            {
                scene.Destroy(gameObject);
            }

            throw new SerializationException(sourceName, $"A prefab must contain exactly one root object, found {roots.Count}.", diagnostics);
        }

        if (parent is not null)
        {
            roots[0].Transform.SetParent(parent, worldPositionStays: false);
        }

        return new PrefabInstance(roots[0], diagnostics);
    }

    private static string Document(string name, JArray objects) => new JObject
    {
        ["schemaVersion"] = CurrentSchemaVersion,
        ["name"] = name,
        ["gameObjects"] = objects,
    }.ToString(Formatting.Indented);

    private (JObject Root, JArray Items) Parse(string json, string sourceName, List<SerializationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(json);
        JObject root;
        try
        {
            root = JObject.Parse(json, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            Migrations.Migrate(root, diagnostics);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new SerializationException(sourceName, ex.Message, diagnostics, ex);
        }

        WarnUnknownKeys(root, RootKeys, diagnostics);
        return root["gameObjects"] is JArray items
            ? (root, items)
            : throw new SerializationException(sourceName, "'gameObjects' must be an array.", diagnostics);
    }

    /// <summary>Creates the objects in <paramref name="items"/> inside <paramref name="scene"/> and returns them in file order.</summary>
    private List<GameObject> LoadObjects(Scene scene, JArray items, List<SerializationDiagnostic> diagnostics)
    {
        var componentsById = scene.GameObjects.SelectMany(g => g.Components).Where(c => c is not Transform).ToDictionary(c => c.Id);
        var serializer = CreateSerializer(new SceneReferenceConverter(scene, componentsById, diagnostics));
        var created = new List<(GameObject GameObject, bool Active)>();
        var pendingData = new List<(Component Component, JObject Data)>();
        var pendingParents = new List<(GameObject Child, JToken Parent)>();

        // Pass 1: create objects (inactive, so a running scene runs no callbacks yet) and their components.
        foreach (var token in items)
        {
            if (token is not JObject item)
            {
                Error(diagnostics, token, "GameObject entry must be an object; skipped.");
                continue;
            }

            WarnUnknownKeys(item, ObjectKeys, diagnostics);
            var gameObject = scene.CreateGameObject(item.Value<string>("name") ?? "GameObject", ReadId(item, scene, componentsById, diagnostics));
            gameObject.SetActive(false);
            created.Add((gameObject, item["active"] is not { Type: JTokenType.Boolean } active || active.Value<bool>()));

            if (item["parent"] is { Type: not JTokenType.Null } parent)
            {
                pendingParents.Add((gameObject, parent));
            }

            ReadTransform(gameObject.Transform, item["transform"], serializer, diagnostics);
            ReadComponents(gameObject, item["components"], componentsById, pendingData, diagnostics);
        }

        // Pass 2: hierarchy, in file order so sibling order is preserved.
        foreach (var (child, parentToken) in pendingParents)
        {
            if (!Guid.TryParse(parentToken.Value<string>(), out var parentId) || scene.FindById(parentId) is not { } parent)
            {
                Error(diagnostics, parentToken, $"Parent '{parentToken}' of '{child.Name}' not found; kept at root.");
                continue;
            }

            try
            {
                child.Transform.SetParent(parent.Transform, worldPositionStays: false);
            }
            catch (InvalidOperationException ex)
            {
                Error(diagnostics, parentToken, ex.Message);
            }
        }

        // Pass 3: component data, now that references can resolve.
        foreach (var (component, data) in pendingData)
        {
            Populate(component, data, serializer, diagnostics);
        }

        // Pass 4: activation, parents first; in a running scene this is where Awake/OnEnable run.
        foreach (var (gameObject, active) in created)
        {
            gameObject.SetActive(active);
        }

        return [.. created.Select(c => c.GameObject)];
    }

    /// <summary>Gives every object and component in a prefab a fresh id and rewrites all references to them.</summary>
    private static void RemapIds(JArray items)
    {
        var map = new Dictionary<Guid, Guid>();
        foreach (var item in items.OfType<JObject>())
        {
            AddId(item);
            foreach (var component in (item["components"] as JArray ?? []).OfType<JObject>())
            {
                AddId(component);
            }
        }

        foreach (var value in items.Descendants().OfType<JValue>())
        {
            if (value.Type == JTokenType.String && Guid.TryParse((string?)value.Value, out var old) && map.TryGetValue(old, out var replacement))
            {
                value.Value = replacement.ToString();
            }
        }

        void AddId(JObject owner)
        {
            if (Guid.TryParse(owner.Value<string>("id"), out var id))
            {
                map.TryAdd(id, Guid.NewGuid());
            }
        }
    }

    private static void MigrateTexturePaths(JObject document, ICollection<SerializationDiagnostic> diagnostics, IAssetLookup? assets)
    {
        var sprites = (document["gameObjects"] as JArray ?? []).OfType<JObject>()
            .SelectMany(g => (g["components"] as JArray ?? []).OfType<JObject>())
            .Where(c => c.Value<string>("type") == "MNVTEngine.SpriteRenderer" && c["data"] is JObject);

        foreach (var sprite in sprites)
        {
            if (sprite["data"]!["texture"] is not JValue { Type: JTokenType.String } texture)
            {
                continue;
            }

            var path = (string)texture.Value!;
            if (assets is not null && assets.TryGetGuid(path, out var guid))
            {
                texture.Value = new AssetRef(guid).ToString();
            }
            else
            {
                diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Warning, texture.Path, $"Texture path '{path}' is not a known asset; reference cleared."));
                texture.Value = null;
            }
        }
    }

    private void Write(GameObject gameObject, JArray output, JsonSerializer serializer)
    {
        var transform = gameObject.Transform;
        var components = new JArray();
        foreach (var component in gameObject.Components)
        {
            switch (component)
            {
                case Transform:
                    continue;
                case MissingComponent missing:
                    components.Add(new JObject { ["type"] = missing.TypeId, ["id"] = missing.Id.ToString(), ["enabled"] = missing.Enabled, ["data"] = missing.Data.DeepClone() });
                    break;
                default:
                    components.Add(new JObject
                    {
                        ["type"] = _registry.GetId(component.GetType()),
                        ["id"] = component.Id.ToString(),
                        ["enabled"] = component.Enabled,
                        ["data"] = JObject.FromObject(component, serializer),
                    });
                    break;
            }
        }

        output.Add(new JObject
        {
            ["id"] = gameObject.Id.ToString(),
            ["name"] = gameObject.Name,
            ["active"] = gameObject.ActiveSelf,
            ["parent"] = transform.Parent?.GameObject.Id.ToString(),
            ["transform"] = new JObject
            {
                ["localPosition"] = JToken.FromObject(transform.LocalPosition, serializer),
                ["localRotation"] = transform.LocalRotation,
                ["localScale"] = JToken.FromObject(transform.LocalScale, serializer),
            },
            ["components"] = components,
        });

        foreach (var child in transform.Children)
        {
            Write(child.GameObject, output, serializer);
        }
    }

    private static Guid ReadId(JObject item, Scene scene, Dictionary<Guid, Component> componentsById, List<SerializationDiagnostic> diagnostics)
    {
        if (!Guid.TryParse(item.Value<string>("id"), out var id))
        {
            Warn(diagnostics, item, "Missing or invalid 'id'; a new id was assigned. References to this object will not resolve.");
            return Guid.NewGuid();
        }

        if (scene.FindById(id) is not null || componentsById.ContainsKey(id))
        {
            Error(diagnostics, item["id"]!, $"Duplicate id {id}; a new id was assigned.");
            return Guid.NewGuid();
        }

        return id;
    }

    private static void ReadTransform(Transform transform, JToken? token, JsonSerializer serializer, List<SerializationDiagnostic> diagnostics)
    {
        if (token is not JObject data)
        {
            return;
        }

        try
        {
            if (data["localPosition"] is { } position)
            {
                transform.LocalPosition = position.ToObject<Vector2>(serializer);
            }

            if (data["localRotation"] is { } rotation)
            {
                transform.LocalRotation = rotation.ToObject<float>(serializer);
            }

            if (data["localScale"] is { } scale)
            {
                transform.LocalScale = scale.ToObject<Vector2>(serializer);
            }
        }
        catch (JsonException ex)
        {
            Error(diagnostics, data, $"Transform could not be read: {ex.Message}");
        }
    }

    private void ReadComponents(GameObject gameObject, JToken? token, Dictionary<Guid, Component> componentsById, List<(Component, JObject)> pendingData, List<SerializationDiagnostic> diagnostics)
    {
        if (token is not JArray entries)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (entry is not JObject componentToken || componentToken.Value<string>("type") is not { Length: > 0 } typeId)
            {
                Error(diagnostics, entry, "Component entry needs a 'type'; skipped.");
                continue;
            }

            WarnUnknownKeys(componentToken, ComponentKeys, diagnostics);
            var data = componentToken["data"] as JObject ?? [];
            var enabled = componentToken.Value<bool?>("enabled") ?? true;
            var id = Guid.TryParse(componentToken.Value<string>("id"), out var parsed) && !componentsById.ContainsKey(parsed) && gameObject.Scene.FindById(parsed) is null
                ? parsed
                : Guid.NewGuid();

            Component component;
            if (_registry.TryGetType(typeId, out var type) && type != typeof(Transform))
            {
                component = (Component)Activator.CreateInstance(type)!;
                pendingData.Add((component, data));
            }
            else
            {
                Warn(diagnostics, componentToken, $"Unknown component type '{typeId}' on '{gameObject.Name}'; kept as MissingComponent.");
                component = new MissingComponent(typeId, (JObject)data.DeepClone());
            }

            component.Enabled = enabled;
            try
            {
                gameObject.AddComponent(component, id);
                componentsById[component.Id] = component;
            }
            catch (InvalidOperationException ex)
            {
                Error(diagnostics, componentToken, ex.Message);
                pendingData.RemoveAll(p => ReferenceEquals(p.Item1, component));
            }
        }
    }

    private static void Populate(Component component, JObject data, JsonSerializer serializer, List<SerializationDiagnostic> diagnostics)
    {
        var known = ComponentTypeInfo.Get(component.GetType()).SerializableMembers.Select(ComponentTypeInfo.JsonName).ToHashSet(StringComparer.Ordinal);
        foreach (var property in data.Properties().Where(p => !known.Contains(p.Name)))
        {
            Warn(diagnostics, property, $"{component.GetType().Name} has no serialized member '{property.Name}'; value ignored.");
        }

        try
        {
            using var reader = data.CreateReader();
            serializer.Populate(reader, component);
        }
        catch (JsonException ex)
        {
            Error(diagnostics, data, $"{component.GetType().Name} on '{component.GameObject.Name}' could not be fully read: {ex.Message}");
        }
    }

    private static JsonSerializer CreateSerializer(SceneReferenceConverter references)
    {
        var settings = MnvtJson.CreateSettings();
        settings.ContractResolver = new ComponentContractResolver(references);
        settings.MissingMemberHandling = MissingMemberHandling.Ignore; // reported explicitly with paths instead
        settings.Converters.Add(new AssetRefConverter());
        return JsonSerializer.Create(settings);
    }

    private static void WarnUnknownKeys(JObject obj, HashSet<string> allowed, List<SerializationDiagnostic> diagnostics)
    {
        foreach (var property in obj.Properties().Where(p => !allowed.Contains(p.Name)))
        {
            Warn(diagnostics, property, $"Unknown property '{property.Name}' ignored.");
        }
    }

    private static void Warn(List<SerializationDiagnostic> diagnostics, JToken token, string message) =>
        diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Warning, token.Path, message));

    private static void Error(List<SerializationDiagnostic> diagnostics, JToken token, string message) =>
        diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Error, token.Path, message));
}
