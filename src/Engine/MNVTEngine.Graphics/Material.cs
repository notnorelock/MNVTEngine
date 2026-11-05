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

/// <summary>
/// A shader with its fixed-function state (blend, culling, depth, render queue), named uniform values and named
/// textures. Shared by everything drawn with it; <see cref="CreateInstance"/> makes an independent copy for per-object
/// changes.
/// </summary>
public sealed class Material
{
    /// <summary>Render queue of opaque geometry; queues at or above <see cref="TransparentQueue"/> draw back to front without depth writes.</summary>
    public const int GeometryQueue = 2000;

    /// <summary>Alpha-tested (cutout) geometry: opaque, drawn after plain geometry.</summary>
    public const int AlphaTestQueue = 2450;

    public const int TransparentQueue = 3000;

    /// <summary>Names the renderer supplies; materials cannot set them.</summary>
    public static readonly IReadOnlySet<string> ReservedNames = new HashSet<string>(StringComparer.Ordinal) { "uViewProjection", "uTexture", "uModel", "uNormalMatrix" };

    /// <summary>
    /// Values a renderer sets on materials every pass (lighting, see <c>LightingUniforms</c>): not material properties,
    /// so they are not listed by <see cref="Shader.Properties"/> or saved in material files.
    /// </summary>
    public static IReadOnlySet<string> RendererNames { get; } = new HashSet<string>(Rendering3D.LightingUniforms.Names, StringComparer.Ordinal);

    private readonly Dictionary<string, UniformValue> _uniforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.Ordinal);

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

    /// <summary>Face culling for 3D drawing (2D drawing never culls).</summary>
    public CullMode Cull { get; set; } = CullMode.Back;

    /// <summary>3D: test against the depth buffer.</summary>
    public bool DepthTest { get; set; } = true;

    /// <summary>3D: write depth. Off for transparent materials by convention.</summary>
    public bool DepthWrite { get; set; } = true;

    /// <summary>Draw order between materials (see <see cref="GeometryQueue"/>); 3000 and above is transparent.</summary>
    public int RenderQueue { get; set; } = GeometryQueue;

    /// <summary>Drawn in the transparent pass (back to front, blended).</summary>
    public bool IsTransparent => RenderQueue >= TransparentQueue;

    public IReadOnlyDictionary<string, UniformValue> Uniforms => _uniforms;

    /// <summary>Textures by sampler name (e.g. <c>_BaseColorMap</c>); <c>uTexture</c> comes from the draw instead.</summary>
    public IReadOnlyDictionary<string, Texture2D> Textures => _textures;

    /// <summary>
    /// Takes every value, texture and render state of <paramref name="source"/>, replacing its own. Values are kept by
    /// name, so a shader variant with the same properties (a skinned twin) draws the same way.
    /// </summary>
    public void CopyFrom(Material source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _uniforms.Clear();
        foreach (var (name, value) in source._uniforms)
        {
            _uniforms[name] = value;
        }

        _textures.Clear();
        foreach (var (name, texture) in source._textures)
        {
            _textures[name] = texture;
        }

        (BlendMode, Cull, DepthTest, DepthWrite, RenderQueue) = (source.BlendMode, source.Cull, source.DepthTest, source.DepthWrite, source.RenderQueue);
    }

    /// <summary>Names a game cannot set as a material property: per-draw values and the renderer's lighting uniforms.</summary>
    public static bool IsRendererOwned(string name) => ReservedNames.Contains(name) || RendererNames.Contains(name);

    /// <exception cref="ArgumentException"><paramref name="name"/> is reserved for the renderer.</exception>
    public Material SetTexture(string name, Texture2D? texture)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (ReservedNames.Contains(name))
        {
            throw new ArgumentException($"'{name}' is set by the renderer and cannot be a material texture.", nameof(name));
        }

        if (texture is null)
        {
            _textures.Remove(name);
        }
        else
        {
            _textures[name] = texture;
        }

        return this;
    }

    public Texture2D? GetTexture(string name) => _textures.GetValueOrDefault(name);

    public bool TryGetUniform(string name, out UniformValue value) => _uniforms.TryGetValue(name, out value);

    /// <summary>An independent copy (same shader and textures, own values), e.g. for one object's color.</summary>
    public Material CreateInstance(string? name = null)
    {
        var copy = new Material(Shader, BlendMode, name ?? Name + " (instance)")
        {
            Cull = Cull,
            DepthTest = DepthTest,
            DepthWrite = DepthWrite,
            RenderQueue = RenderQueue,
        };
        foreach (var (key, value) in _uniforms)
        {
            copy._uniforms[key] = value;
        }

        foreach (var (key, value) in _textures)
        {
            copy._textures[key] = value;
        }

        return copy;
    }

    public Material SetFloat(string name, float value) => Set(name, UniformValue.FromFloat(value));
    public Material SetInt(string name, int value) => Set(name, UniformValue.FromInt(value));
    public Material SetVector(string name, Vector2 value) => Set(name, UniformValue.FromVector2(value));
    public Material SetVector(string name, Vector4 value) => Set(name, UniformValue.FromVector4(value));
    public Material SetColor(string name, Color value) => Set(name, UniformValue.FromVector4(value));
    public Material SetMatrix(string name, Matrix4x4 value) => Set(name, UniformValue.FromMatrix(value));

    private Material Set(string name, UniformValue value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (ReservedNames.Contains(name))
        {
            throw new ArgumentException($"'{name}' is set by the renderer and cannot be a material uniform.", nameof(name));
        }

        _uniforms[name] = value;
        return this;
    }
}
