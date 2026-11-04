using System.Globalization;
using Newtonsoft.Json;

namespace MNVTEngine;

/// <summary>
/// Persistent reference to a project asset by its GUID, so references survive renames and moves.
/// Persisted as <c>"asset:&lt;guid&gt;"</c>, or null for <see cref="None"/>.
/// </summary>
public readonly record struct AssetRef(Guid Guid)
{
    public const string Prefix = "asset:";

    public static AssetRef None => default;

    public bool IsNone => Guid == Guid.Empty;

    public static bool TryParse(string? text, out AssetRef value)
    {
        value = None;
        return text is not null && text.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParse(text.AsSpan(Prefix.Length), out var guid)
            && (value = new AssetRef(guid)) != None;
    }

    public override string ToString() => IsNone ? "None" : Prefix + Guid.ToString("D", CultureInfo.InvariantCulture);
}

/// <summary>Restricts an <see cref="AssetRef"/> member to one asset type (see <c>MNVTEngine.Serialization.AssetTypes</c>).</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class AssetTypeAttribute(string type) : Attribute
{
    public string Type { get; } = type;
}

internal sealed class AssetRefConverter : JsonConverter<AssetRef>
{
    public override void WriteJson(JsonWriter writer, AssetRef value, JsonSerializer serializer)
    {
        if (value.IsNone)
        {
            writer.WriteNull();
        }
        else
        {
            writer.WriteValue(value.ToString());
        }
    }

    public override AssetRef ReadJson(JsonReader reader, Type objectType, AssetRef existingValue, bool hasExistingValue, JsonSerializer serializer) =>
        reader.TokenType switch
        {
            JsonToken.Null => AssetRef.None,
            JsonToken.String when AssetRef.TryParse((string?)reader.Value, out var value) => value,
            _ => throw new JsonSerializationException($"Expected \"{AssetRef.Prefix}<guid>\" or null for an asset reference at '{reader.Path}', got {reader.TokenType} '{reader.Value}'."),
        };
}

/// <summary>Implemented by the host (runtime, editor) to create prefab instances.</summary>
public interface IPrefabProvider
{
    /// <exception cref="InvalidOperationException">The asset is missing or is not a readable prefab.</exception>
    GameObject Instantiate(Scene scene, AssetRef prefab, Transform? parent);
}

/// <summary>Implemented by the host (runtime, editor) to give components read access to text assets (e.g. scripts).</summary>
public interface ITextAssetReader
{
    /// <exception cref="InvalidOperationException">The asset is missing, unreadable, or not of <paramref name="assetType"/>.</exception>
    string ReadText(AssetRef asset, string assetType);

    /// <summary>The asset's build-time compiled form (e.g. script bytecode) in exported games; null when the host has none and the source must be used.</summary>
    /// <exception cref="InvalidOperationException">The host requires compiled assets and this one is missing or unreadable.</exception>
    byte[]? ReadCompiled(AssetRef asset, string assetType) => null;
}

/// <summary>Prefab instantiation for gameplay code.</summary>
public static class Prefab
{
    /// <summary>Creates a copy of the prefab in the current scene at <paramref name="position"/> (world space).</summary>
    public static GameObject Instantiate(AssetRef prefab, Mathematics.Vector3 position, Transform? parent = null)
    {
        var instance = SceneContext.Current.Instantiate(prefab, parent);
        instance.Transform.Position = position;
        return instance;
    }
}
