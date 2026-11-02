using Newtonsoft.Json;

namespace MNVTEngine.Serialization.Converters;

/// <summary>
/// Base for value types stored as a flat JSON object of named numbers, e.g. <c>{"x": 1, "y": 2}</c>.
/// Reading is strict: every component is required, unknown or duplicate properties are errors,
/// and non-finite values are rejected so corrupted files fail loudly with a JSON path.
/// </summary>
public abstract class FloatComponentsConverter<T> : JsonConverter<T>
    where T : struct
{
    private readonly string[] _names;

    protected FloatComponentsConverter(params string[] names) => _names = names;

    protected abstract T Create(ReadOnlySpan<float> v);

    protected abstract void Decompose(T value, Span<float> v);

    public override void WriteJson(JsonWriter writer, T value, JsonSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        Span<float> values = stackalloc float[_names.Length];
        Decompose(value, values);

        writer.WriteStartObject();
        for (var i = 0; i < _names.Length; i++)
        {
            writer.WritePropertyName(_names[i]);
            writer.WriteValue(values[i]);
        }

        writer.WriteEndObject();
    }

    public override T ReadJson(JsonReader reader, Type objectType, T existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.TokenType != JsonToken.StartObject)
        {
            throw JsonErrors.At(reader, $"Expected an object for {typeof(T).Name}, got {reader.TokenType}.");
        }

        Span<float> values = stackalloc float[_names.Length];
        var seen = 0UL;

        while (reader.Read() && reader.TokenType != JsonToken.EndObject)
        {
            if (reader.TokenType != JsonToken.PropertyName)
            {
                throw JsonErrors.At(reader, $"Unexpected token {reader.TokenType} in {typeof(T).Name}.");
            }

            var name = (string)reader.Value!;
            var index = Array.IndexOf(_names, name);
            if (index < 0)
            {
                throw JsonErrors.At(reader, $"Unknown property '{name}' for {typeof(T).Name}. Expected: {string.Join(", ", _names)}.");
            }

            if ((seen & (1UL << index)) != 0)
            {
                throw JsonErrors.At(reader, $"Duplicate property '{name}' for {typeof(T).Name}.");
            }

            reader.Read();
            values[index] = JsonErrors.ReadFiniteFloat(reader, typeof(T).Name);
            seen |= 1UL << index;
        }

        if (reader.TokenType != JsonToken.EndObject)
        {
            throw JsonErrors.At(reader, $"Unexpected end of data while reading {typeof(T).Name}.");
        }

        var missing = _names.Where((_, i) => (seen & (1UL << i)) == 0).ToArray();
        if (missing.Length > 0)
        {
            throw JsonErrors.At(reader, $"{typeof(T).Name} is missing required properties: {string.Join(", ", missing)}.");
        }

        return Create(values);
    }
}
