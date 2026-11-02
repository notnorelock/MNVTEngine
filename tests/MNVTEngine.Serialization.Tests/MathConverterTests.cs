using MNVTEngine.Mathematics;
using Newtonsoft.Json;

namespace MNVTEngine.Serialization.Tests;

public class MathConverterTests
{
    public sealed class Sample
    {
        public Vector2 Position { get; set; }
        public Vector3 Offset { get; set; }
        public Vector4 Tint4 { get; set; }
        public Quaternion Rotation { get; set; }
        public Color Color { get; set; }
        public Rect Area { get; set; }
        public Bounds Bounds { get; set; }
        public Matrix4x4 Matrix { get; set; }
    }

    private static string Compact(object value)
    {
        var settings = MnvtJson.CreateSettings();
        settings.Formatting = Formatting.None;
        return JsonConvert.SerializeObject(value, settings);
    }

    [Fact]
    public void AllMathTypes_RoundTrip()
    {
        var original = new Sample
        {
            Position = new Vector2(1.5f, -2.25f),
            Offset = new Vector3(0.1f, 0.2f, 0.3f),
            Tint4 = new Vector4(1f, 2f, 3f, 4f),
            Rotation = Quaternion.Euler(10f, 20f, 30f),
            Color = new Color(0.1f, 0.5f, 0.9f, 0.75f),
            Area = new Rect(1f, 2f, 3f, 4f),
            Bounds = new Bounds(new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f)),
            Matrix = Matrix4x4.TRS(new Vector3(1f, 2f, 3f), Quaternion.Euler(0f, 0f, 45f), Vector3.One),
        };

        var restored = MnvtJson.Deserialize<Sample>(MnvtJson.Serialize(original));

        // Exact equality: float -> JSON -> float must be lossless.
        Assert.Equal(original.Position, restored.Position);
        Assert.Equal(original.Offset, restored.Offset);
        Assert.Equal(original.Tint4, restored.Tint4);
        Assert.Equal(original.Rotation, restored.Rotation);
        Assert.Equal(original.Color, restored.Color);
        Assert.Equal(original.Area, restored.Area);
        Assert.Equal(original.Bounds, restored.Bounds);
        Assert.Equal(original.Matrix, restored.Matrix);
    }

    [Fact]
    public void Vector2_UsesReadableObjectFormat()
    {
        var json = Compact(new Vector2(1f, 2.5f));
        Assert.Equal("""{"x":1.0,"y":2.5}""", json);
    }

    [Fact]
    public void Matrix_IsColumnMajorArray()
    {
        var json = Compact(Matrix4x4.Translate(new Vector3(7f, 8f, 9f)));
        Assert.EndsWith("7.0,8.0,9.0,1.0]", json, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyOrder_DoesNotMatter() =>
        Assert.Equal(new Vector3(1f, 2f, 3f), MnvtJson.Deserialize<Vector3>("""{"z":3,"x":1,"y":2}"""));

    [Theory]
    [InlineData("""{"x":1}""", "missing required properties: y")]
    [InlineData("""{"x":1,"y":2,"q":3}""", "Unknown property 'q'")]
    [InlineData("""{"x":1,"x":2,"y":3}""", "Duplicate property 'x'")]
    [InlineData("""{"x":"1","y":2}""", "Expected a number")]
    [InlineData("""[1,2]""", "Expected an object")]
    [InlineData("""{"x":1e999,"y":2}""", "Non-finite")]
    public void Vector2_MalformedInputFailsWithDiagnostic(string json, string expectedMessage)
    {
        var ex = Assert.ThrowsAny<JsonException>(() => MnvtJson.Deserialize<Vector2>(json));
        Assert.Contains(expectedMessage, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_IncludesPathAndLine()
    {
        const string json = "{\n  \"position\": {\"x\": 1}\n}";
        var ex = Assert.Throws<JsonSerializationException>(() => MnvtJson.Deserialize<Sample>(json));
        Assert.Contains("position", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, ex.LineNumber);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("[1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17]")]
    public void Matrix_WrongElementCountFails(string json) =>
        Assert.ThrowsAny<JsonException>(() => MnvtJson.Deserialize<Matrix4x4>(json));

    [Fact]
    public void Null_IsRejectedForValueTypes() =>
        Assert.ThrowsAny<JsonException>(() => MnvtJson.Deserialize<Vector2>("null"));
}
