using System.Reflection;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace MNVTEngine.Scenes;

/// <summary>
/// Component members follow <see cref="ComponentTypeInfo.SerializableMembers"/>. Members typed as
/// <see cref="GameObject"/> or a component (or collections of them) are written as ids and resolved on load.
/// One instance per load/save, so contract caches never outlive the operation.
/// </summary>
internal sealed class ComponentContractResolver : DefaultContractResolver
{
    private readonly SceneReferenceConverter _references;

    public ComponentContractResolver(SceneReferenceConverter references)
    {
        _references = references;
        NamingStrategy = new CamelCaseNamingStrategy();
    }

    public static bool IsReferenceType(Type type) => type == typeof(GameObject) || typeof(Component).IsAssignableFrom(type);

    protected override List<MemberInfo> GetSerializableMembers(Type objectType) =>
        typeof(Component).IsAssignableFrom(objectType)
            ? [.. ComponentTypeInfo.Get(objectType).SerializableMembers]
            : base.GetSerializableMembers(objectType);

    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
    {
        var property = base.CreateProperty(member, memberSerialization);
        if (member.DeclaringType is { } declaring && typeof(Component).IsAssignableFrom(declaring))
        {
            property.PropertyName = ComponentTypeInfo.JsonName(member);
            property.Readable = true;
            property.Writable = true;
            property.Ignored = false;
        }

        var type = property.PropertyType!;
        if (IsReferenceType(type))
        {
            property.Converter = _references;
        }
        else if (ElementType(type) is { } element && IsReferenceType(element))
        {
            property.ItemConverter = _references;
        }

        return property;
    }

    private static Type? ElementType(Type type) =>
        type.IsArray ? type.GetElementType()
        : type.IsGenericType && type.GetGenericArguments() is [var single] && typeof(System.Collections.IEnumerable).IsAssignableFrom(type) ? single
        : null;
}

/// <summary>Writes scene object references as id strings; on read, resolves them against the scene being loaded.</summary>
internal sealed class SceneReferenceConverter(Scene? scene, Dictionary<Guid, Component>? componentsById, List<SerializationDiagnostic>? diagnostics) : JsonConverter
{
    public override bool CanConvert(Type objectType) => ComponentContractResolver.IsReferenceType(objectType);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        switch (value)
        {
            case null:
                writer.WriteNull();
                break;
            case GameObject { IsDestroyed: false } gameObject:
                writer.WriteValue(gameObject.Id.ToString());
                break;
            case Component { IsDestroyed: false } component:
                writer.WriteValue(component.Id.ToString()); // A Transform's id is its GameObject's id.
                break;
            default:
                writer.WriteNull(); // Destroyed objects persist as missing references.
                break;
        }
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonToken.String || !Guid.TryParse((string)reader.Value!, out var id))
        {
            Report(reader, $"Expected an object id string for {objectType.Name}, got {reader.TokenType}.");
            JToken.ReadFrom(reader);
            return null;
        }

        object? resolved = null;
        if (scene?.FindById(id) is { } gameObject)
        {
            resolved = objectType == typeof(GameObject) ? gameObject : gameObject.Components.FirstOrDefault(objectType.IsInstanceOfType);
        }
        else if (componentsById?.GetValueOrDefault(id) is { } component && objectType.IsInstanceOfType(component))
        {
            resolved = component;
        }

        if (resolved is null)
        {
            Report(reader, $"Reference {id} to a {objectType.Name} was not found; set to null.");
        }

        return resolved;
    }

    private void Report(JsonReader reader, string message) =>
        diagnostics?.Add(new SerializationDiagnostic(DiagnosticSeverity.Warning, reader.Path, message));
}
