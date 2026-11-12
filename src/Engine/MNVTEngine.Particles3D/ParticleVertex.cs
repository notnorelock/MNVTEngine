using System.Runtime.InteropServices;
using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles3D;

/// <summary>A 3D particle quad corner: world position, texture coordinate and packed color (24 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ParticleVertex(Vector3 Position, Vector2 TexCoord, uint Color)
{
    public static VertexLayout Layout { get; } = new(24,
    [
        new VertexElement(0, 3, VertexElementType.Float32, 0),
        new VertexElement(1, 2, VertexElementType.Float32, 12),
        new VertexElement(2, 4, VertexElementType.UNormByte, 20),
    ]);

    /// <summary>Textured, vertex-colored quads in world space (the sprite shader with a 3D position).</summary>
    public static ShaderSource Shader { get; } = new(
        "Builtin/Particle3D",
        """
        #version 450 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec2 aTexCoord;
        layout(location = 2) in vec4 aColor;

        uniform mat4 uViewProjection;

        out vec2 vTexCoord;
        out vec4 vColor;

        void main()
        {
            vTexCoord = aTexCoord;
            vColor = aColor;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
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
