namespace MNVTEngine.Graphics;

/// <summary>
/// Shader program source: GLSL <c>#version 450 core</c> in GL style (loose <c>uniform</c>s, <c>in</c>/<c>out</c> without
/// locations). The Vulkan backend compiles it to SPIR-V under Vulkan relaxed rules and links stages by name.
/// Reserved names: <c>uViewProjection</c> (mat4) and <c>uTexture</c> (sampler2D, binding 0, the only texture).
/// </summary>
public sealed record ShaderSource(string Name, string VertexGlsl, string FragmentGlsl);

public abstract class Shader : GraphicsResource
{
    protected Shader(GraphicsResourceTracker? tracker, string debugName)
        : base(tracker, debugName)
    {
    }

    /// <summary>True if the linked program has an active uniform with this name.</summary>
    public abstract bool HasUniform(string name);
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

    /// <summary>
    /// Unlit 3D mesh shader for <see cref="Meshes.MeshVertex"/>: texture (UV0) × vertex color × <c>uColor</c>. No
    /// lighting: lit (PBR) shading arrives with the light system.
    /// </summary>
    public static ShaderSource Unlit3D { get; } = new(
        "Builtin/Unlit3D",
        """
        #version 450 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 3) in vec2 aTexCoord;
        layout(location = 5) in vec4 aColor;

        uniform mat4 uViewProjection;
        uniform mat4 uModel;

        out vec2 vTexCoord;
        out vec4 vColor;

        void main()
        {
            vTexCoord = aTexCoord;
            vColor = aColor;
            gl_Position = uViewProjection * uModel * vec4(aPosition, 1.0);
        }
        """,
        """
        #version 450 core
        in vec2 vTexCoord;
        in vec4 vColor;

        layout(binding = 0) uniform sampler2D uTexture;
        uniform vec4 uColor;

        out vec4 fragColor;

        void main()
        {
            fragColor = texture(uTexture, vTexCoord) * vColor * uColor;
        }
        """);
}
