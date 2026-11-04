namespace MNVTEngine.Graphics;

/// <summary>
/// Shader program source: GLSL <c>#version 450 core</c> in GL style (loose <c>uniform</c>s, <c>in</c>/<c>out</c> without
/// locations). The Vulkan backend compiles it to SPIR-V under Vulkan relaxed rules and links stages by name.
/// Reserved names: <c>uViewProjection</c> (mat4) and <c>uTexture</c> (sampler2D, binding 0, the only texture).
/// </summary>
public sealed record ShaderSource(string Name, string VertexGlsl, string FragmentGlsl);

public enum ShaderPropertyKind
{
    Float,
    Int,
    Vector2,
    Vector4,
    Matrix4x4,
    Texture,
}

/// <summary>A value a material can set on a shader: a uniform or a sampler (by name).</summary>
public sealed record ShaderProperty(string Name, ShaderPropertyKind Kind);

public abstract class Shader : GraphicsResource
{
    protected Shader(GraphicsResourceTracker? tracker, string debugName)
        : base(tracker, debugName)
    {
    }

    /// <summary>True if the linked program has an active uniform with this name.</summary>
    public abstract bool HasUniform(string name);

    /// <summary>Uniforms and samplers materials can set (without the renderer's own, see <see cref="Material.ReservedNames"/>).</summary>
    public abstract IReadOnlyList<ShaderProperty> Properties { get; }
}

public sealed class ShaderCompilationException(string shaderName, string stage, string log)
    : Exception($"Shader '{shaderName}' failed to {(stage == "link" ? "link" : $"compile ({stage} stage)")}:{Environment.NewLine}{log}")
{
    public string ShaderName { get; } = shaderName;

    /// <summary>"vertex", "fragment" or "link".</summary>
    public string Stage { get; } = stage;

    public string Log { get; } = log;
}

public static class BuiltinShaders
{
    /// <summary>Textured, vertex-colored sprite shader used by <see cref="SpriteBatch"/>. Matches <see cref="SpriteVertex.Layout"/>.</summary>
    public static ShaderSource Sprite { get; } = new(
        "Builtin/Sprite",
        """
        #version 450 core
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in vec2 aTexCoord;
        layout(location = 2) in vec4 aColor;

        uniform mat4 uViewProjection;

        out vec2 vTexCoord;
        out vec4 vColor;

        void main()
        {
            vTexCoord = aTexCoord;
            vColor = aColor;
            gl_Position = uViewProjection * vec4(aPosition, 0.0, 1.0);
        }
        """,
        """
        #version 450 core
        in vec2 vTexCoord;
        in vec4 vColor;

        layout(binding = 0) uniform sampler2D uTexture;

        out vec4 fragColor;

        void main()
        {
            fragColor = texture(uTexture, vTexCoord) * vColor;
        }
        """);
}
