using MNVTEngine.Mathematics;
using Newtonsoft.Json;

namespace MNVTEngine.Serialization.Converters;

public sealed class Vector2Converter() : FloatComponentsConverter<Vector2>("x", "y")
{
    protected override Vector2 Create(ReadOnlySpan<float> v) => new(v[0], v[1]);

    protected override void Decompose(Vector2 value, Span<float> v) => (v[0], v[1]) = (value.X, value.Y);
}

public sealed class Vector3Converter() : FloatComponentsConverter<Vector3>("x", "y", "z")
{
    protected override Vector3 Create(ReadOnlySpan<float> v) => new(v[0], v[1], v[2]);

    protected override void Decompose(Vector3 value, Span<float> v) => (v[0], v[1], v[2]) = (value.X, value.Y, value.Z);
}

public sealed class Vector4Converter() : FloatComponentsConverter<Vector4>("x", "y", "z", "w")
{
    protected override Vector4 Create(ReadOnlySpan<float> v) => new(v[0], v[1], v[2], v[3]);

    protected override void Decompose(Vector4 value, Span<float> v) => (v[0], v[1], v[2], v[3]) = (value.X, value.Y, value.Z, value.W);
}

public sealed class QuaternionConverter() : FloatComponentsConverter<Quaternion>("x", "y", "z", "w")
{
    protected override Quaternion Create(ReadOnlySpan<float> v) => new(v[0], v[1], v[2], v[3]);

    protected override void Decompose(Quaternion value, Span<float> v) => (v[0], v[1], v[2], v[3]) = (value.X, value.Y, value.Z, value.W);
}

public sealed class ColorConverter() : FloatComponentsConverter<Color>("r", "g", "b", "a")
{
    protected override Color Create(ReadOnlySpan<float> v) => new(v[0], v[1], v[2], v[3]);

    protected override void Decompose(Color value, Span<float> v) => (v[0], v[1], v[2], v[3]) = (value.R, value.G, value.B, value.A);
}

public sealed class RectConverter() : FloatComponentsConverter<Rect>("x", "y", "width", "height")
{
    protected override Rect Create(ReadOnlySpan<float> v) => new(v[0], v[1], v[2], v[3]);

    protected override void Decompose(Rect value, Span<float> v) => (v[0], v[1], v[2], v[3]) = (value.X, value.Y, value.Width, value.Height);
}

/// <summary>Bounds as a flat object: center (cx, cy, cz) and extents (ex, ey, ez).</summary>
public sealed class BoundsConverter() : FloatComponentsConverter<Bounds>("cx", "cy", "cz", "ex", "ey", "ez")
{
    protected override Bounds Create(ReadOnlySpan<float> v) =>
        new() { Center = new Vector3(v[0], v[1], v[2]), Extents = new Vector3(v[3], v[4], v[5]) };

    protected override void Decompose(Bounds value, Span<float> v) =>
        (v[0], v[1], v[2], v[3], v[4], v[5]) = (value.Center.X, value.Center.Y, value.Center.Z, value.Extents.X, value.Extents.Y, value.Extents.Z);
}

/// <summary>Matrix as a JSON array of exactly 16 numbers in column-major order (same as GPU layout).</summary>
public sealed class Matrix4x4Converter : JsonConverter<Matrix4x4>
{
    public override void WriteJson(JsonWriter writer, Matrix4x4 value, JsonSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartArray();
        foreach (var element in Matrix4x4.AsSpan(in value))
        {
            writer.WriteValue(element);
        }

        writer.WriteEndArray();
    }

    public override Matrix4x4 ReadJson(JsonReader reader, Type objectType, Matrix4x4 existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.TokenType != JsonToken.StartArray)
        {
            throw JsonErrors.At(reader, $"Expected an array of 16 numbers for Matrix4x4, got {reader.TokenType}.");
        }

        var result = default(Matrix4x4);
        var count = 0;
        while (reader.Read() && reader.TokenType != JsonToken.EndArray)
        {
            if (count == 16)
            {
                throw JsonErrors.At(reader, "Matrix4x4 array has more than 16 elements.");
            }

            // Column-major index -> (row, column).
            result[count % 4, count / 4] = JsonErrors.ReadFiniteFloat(reader, "Matrix4x4");
            count++;
        }

        return count == 16 && reader.TokenType == JsonToken.EndArray
            ? result
            : throw JsonErrors.At(reader, $"Matrix4x4 array must have exactly 16 elements, got {count}.");
    }
}
