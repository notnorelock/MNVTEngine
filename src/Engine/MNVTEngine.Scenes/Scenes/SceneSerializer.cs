using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Scenes;

public sealed record SceneLoadResult(Scene Scene, IReadOnlyList<SerializationDiagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// Reads and writes <c>.mnvt</c> scene files. Loading never throws for problems inside individual objects:
/// those become diagnostics and the rest of the scene still loads. Only unreadable documents throw.
/// </summary>
/// <remarks>
/// Format (schema 1): <c>{ schemaVersion, name, gameObjects: [ { id, name, active, parent, transform: { localPosition,
/// localRotation, localScale }, components: [ { type, id, enabled, data } ] } ] }</c>. GameObjects are written
/// parents-first in sibling order; on load, sibling order follows file order.
/// </remarks>
public sealed class SceneSerializer
{
    public const int CurrentSchemaVersion = 1;

    private static readonly HashSet<string> RootKeys = ["schemaVersion", "name", "gameObjects"];
    private static readonly HashSet<string> ObjectKeys = ["id", "name", "active", "parent", "transform", "components"];
    private static readonly HashSet<string> ComponentKeys = ["type", "id", "enabled", "data"];

    private readonly ComponentRegistry _registry;

    public SceneSerializer(ComponentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
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

        var document = new JObject
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["name"] = scene.Name,
            ["gameObjects"] = objects,
        };
        return document.ToString(Formatting.Indented);
    }

    /// <exception cref="SerializationException">Malformed JSON, unsupported schema version, or missing object list.</exception>
    public SceneLoadResult Deserialize(string json, string sourceName = "<memory>")
    {
        ArgumentNullException.ThrowIfNull(json);
        var diagnostics = new List<SerializationDiagnostic>();
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
        if (root["gameObjects"] is not JArray items)
        {
            throw new SerializationException(sourceName, "'gameObjects' must be an array.", diagnostics);
        }

        var scene = new Scene(root.Value<string>("name") ?? Path.GetFileNameWithoutExtension(sourceName));
        var componentsById = new Dictionary<Guid, Component>();
        var serializer = CreateSerializer(new SceneReferenceConverter(scene, componentsById, diagnostics));
        var pendingData = new List<(Component Component, JObject Data)>();
        var pendingParents = new List<(GameObject Child, JToken Parent)>();

        // Pass 1: create objects and components so every id exists before data (and references) is read.
        foreach (var token in items)
        {
            if (token is not JObject item)
            {
                Error(diagnostics, token, "GameObject entry must be an object; skipped.");
                continue;
            }

            WarnUnknownKeys(item, ObjectKeys, diagnostics);
            var gameObject = scene.CreateGameObject(item.Value<string>("name") ?? "GameObject", ReadId(item, scene, componentsById, diagnostics));
            if (item["active"] is { Type: JTokenType.Boolean } active)
            {
                gameObject.SetActive(active.Value<bool>());
            }

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

        return new SceneLoadResult(scene, diagnostics);
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
