using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;
using static MNVTEngine.Graphics.Vulkan.Tests.VulkanHarness;

namespace MNVTEngine.Graphics.Vulkan.Tests;

/// <summary>Several textures per draw: <c>uTexture</c> from the draw, named samplers from the material.</summary>
[Collection(nameof(VulkanGroup))]
public class VulkanMaterialTextureTests
{
    private static readonly ShaderSource ThreeTextures = new(
        "Test/ThreeTextures",
        """
        #version 450 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 3) in vec2 aTexCoord;
        uniform mat4 uViewProjection;
        uniform mat4 uModel;
        out vec2 vTexCoord;
        void main()
        {
            vTexCoord = aTexCoord;
            gl_Position = uViewProjection * uModel * vec4(aPosition, 1.0);
        }
        """,
        """
        #version 450 core
        in vec2 vTexCoord;
        layout(binding = 0) uniform sampler2D uTexture;
        uniform sampler2D _RedMap;
        uniform sampler2D _GreenMap;
        uniform float _Mix;
        out vec4 fragColor;
        void main()
        {
            // Left third: uTexture; middle: _RedMap; right: _GreenMap scaled by _Mix.
            vec4 color = vTexCoord.x < 0.33 ? texture(uTexture, vTexCoord)
                : vTexCoord.x < 0.66 ? texture(_RedMap, vTexCoord) : texture(_GreenMap, vTexCoord) * _Mix;
            fragColor = vec4(color.rgb, 1.0);
        }
        """);

    [Fact]
    public void EachSampler_ReadsItsOwnTexture_AndPropertiesAreReflected()
    {
        Run(device =>
        {
            using var shader = device.CreateShader(ThreeTextures);
            Assert.Equal(
                [new ShaderProperty("_Mix", ShaderPropertyKind.Float), new ShaderProperty("_GreenMap", ShaderPropertyKind.Texture), new ShaderProperty("_RedMap", ShaderPropertyKind.Texture)],
                shader.Properties.OrderBy(p => p.Kind).ThenBy(p => p.Name, StringComparer.Ordinal));
            using var blue = device.CreateTexture(1, 1, [0, 0, 255, 255], SamplerState.PointClamp);
            using var red = device.CreateTexture(1, 1, [255, 0, 0, 255], SamplerState.PointClamp);
            using var green = device.CreateTexture(1, 1, [0, 255, 0, 255], SamplerState.PointClamp);
            var material = new Material(shader, BlendMode.Opaque).SetTexture("_RedMap", red).SetTexture("_GreenMap", green).SetFloat("_Mix", 0.5f);
            using var meshes = new GpuMeshCache(device);
            var quad = meshes.Get(MeshPrimitives.Quad(2f, 2f));
            var vp = Matrix4x4.OrthoZeroToOne(-1f, 1f, -1f, 1f, -1f, 1f);

            device.Clear(Color.Black);
            device.Draw(new DrawCommand(material, blue, quad.Vertices, quad.Indices, 0, quad.Indices.Count, vp));
            var pixels = new byte[Size * Size * 4];
            device.ReadPixels(pixels);
            AssertColor(Color.Blue, Pixel(pixels, Size, 5, Size / 2));
            AssertColor(Color.Red, Pixel(pixels, Size, Size / 2, Size / 2));
            AssertColor(new Color(0f, 0.5f, 0f, 1f), Pixel(pixels, Size, Size - 5, Size / 2));

            material.SetTexture("_GreenMap", null);
            var error = Assert.Throws<InvalidOperationException>(() => device.Draw(new DrawCommand(material, blue, quad.Vertices, quad.Indices, 0, quad.Indices.Count, vp)));
            Assert.Contains("_GreenMap", error.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ExplicitSamplerBindings_AreReassigned_SoTheyCannotCollide()
    {
        Run(device =>
        {
            // The engine owns set 0's layout: bindings a shader declares (here a shared one) are replaced at link time.
            var shared = ThreeTextures with
            {
                FragmentGlsl = ThreeTextures.FragmentGlsl
                    .Replace("uniform sampler2D _RedMap;", "layout(binding = 5) uniform sampler2D _RedMap;", StringComparison.Ordinal)
                    .Replace("uniform sampler2D _GreenMap;", "layout(binding = 5) uniform sampler2D _GreenMap;", StringComparison.Ordinal),
            };
            using var shader = device.CreateShader(shared);
            Assert.Contains(shader.Properties, p => p.Name == "_RedMap");
            Assert.Contains(shader.Properties, p => p.Name == "_GreenMap");
        });
    }
}
