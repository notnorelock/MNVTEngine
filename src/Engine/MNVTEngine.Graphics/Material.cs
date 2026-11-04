using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics;

public enum BlendMode
{
    /// <summary>No blending.</summary>
    Opaque,

    /// <summary>Straight (non-premultiplied) alpha: src * a + dst * (1 - a).</summary>
    Alpha,

    /// <summary>Premultiplied alpha: src + dst * (1 - a).</summary>
    Premultiplied,

    /// <summary>src * a + dst. Glows and particles.</summary>
    Additive,

    /// <summary>src * dst. Shadows and tinting.</summary>
    Multiply,
}

public enum UniformKind
{
    Float,
    Int,
    Vector2,
    Vector4,
    Matrix4x4,
}

public readonly struct UniformValue
{
    private UniformValue(UniformKind kind, Vector4 vector, Matrix4x4 matrix, int integer)
    {
        Kind = kind;
        Vector = vector;
        Matrix = matrix;
        Integer = integer;
    }

    public UniformKind Kind { get; }

    /// <summary>Float (X), Vector2 (X, Y) and Vector4 values.</summary>
    public Vector4 Vector { get; }

    public Matrix4x4 Matrix { get; }

    public int Integer { get; }

    public static UniformValue FromFloat(float value) => new(UniformKind.Float, new Vector4(value, 0f, 0f, 0f), default, 0);
    public static UniformValue FromInt(int value) => new(UniformKind.Int, default, default, value);
    public static UniformValue FromVector2(Vector2 value) => new(UniformKind.Vector2, new Vector4(value.X, value.Y, 0f, 0f), default, 0);
    public static UniformValue FromVector4(Vector4 value) => new(UniformKind.Vector4, value, default, 0);
    public static UniformValue FromMatrix(Matrix4x4 value) => new(UniformKind.Matrix4x4, default, value, 0);
}

/// <summary>A shader plus fixed-function blend state and named uniform values.</summary>
public sealed class Material
{
    private readonly Dictionary<string, UniformValue> _uniforms = new(StringComparer.Ordinal);

    public Material(Shader shader, BlendMode blendMode = BlendMode.Alpha, string name = "Material")
    {
        ArgumentNullException.ThrowIfNull(shader);
        Shader = shader;
        BlendMode = blendMode;
        Name = name;
    }

    public string Name { get; }

    public Shader Shader { get; }

    public BlendMode BlendMode { get; set; }

    public IReadOnlyDictionary<string, UniformValue> Uniforms => _uniforms;

    public Material SetFloat(string name, float value) => Set(name, UniformValue.FromFloat(value));
    public Material SetInt(string name, int value) => Set(name, UniformValue.FromInt(value));
    public Material SetVector(string name, Vector2 value) => Set(name, UniformValue.FromVector2(value));
    public Material SetVector(string name, Vector4 value) => Set(name, UniformValue.FromVector4(value));
    public Material SetColor(string name, Color value) => Set(name, UniformValue.FromVector4(value));
    public Material SetMatrix(string name, Matrix4x4 value) => Set(name, UniformValue.FromMatrix(value));

    private Material Set(string name, UniformValue value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name is "uViewProjection" or "uTexture" or "uModel" or "uNormalMatrix")
        {
            throw new ArgumentException($"'{name}' is set by the renderer and cannot be a material uniform.", nameof(name));
        }

        _uniforms[name] = value;
        return this;
    }
}
