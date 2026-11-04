using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;
using static MNVTEngine.Graphics.Vulkan.Tests.VulkanHarness;

namespace MNVTEngine.Graphics.Vulkan.Tests;

/// <summary>Static mesh buffers on the GPU: uploaded once, drawn in later frames, with 16- and 32-bit indices.</summary>
[Collection(nameof(VulkanGroup))]
public class VulkanMeshTests
{
    private static readonly ShaderSource VertexColor = new(
        "Test/MeshVertexColor",
        """
        #version 450 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 5) in vec4 aColor;
        uniform mat4 uViewProjection;
        out vec4 vColor;
        void main()
        {
            vColor = aColor;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """,
        """
        #version 450 core
        in vec4 vColor;
        layout(binding = 0) uniform sampler2D uTexture;
        out vec4 fragColor;
        void main()
        {
            fragColor = vColor * texture(uTexture, vec2(0.5));
        }
        """);

    private static Mesh Colored(Mesh mesh, Color color)
    {
        mesh.Colors = Enumerable.Repeat(color, mesh.VertexCount).ToArray();
        return mesh;
    }

    [Fact]
    public void StaticMeshes_DrawInLaterFrames_With16And32BitIndices()
    {
        Run(device =>
        {
            using var shader = device.CreateShader(VertexColor);
            var material = new Material(shader, BlendMode.Opaque);
            using var white = device.CreateTexture(1, 1, [255, 255, 255, 255], SamplerState.PointClamp);
            var left = Colored(MeshPrimitives.Quad(1f, 2f), Color.Red);                 // x -1..0 after the offset below
            var right = Colored(MeshPrimitives.Plane(1f, 2f, subdivisions: 264), Color.Green); // 265^2 = 70,225 vertices
            Assert.Equal(IndexFormat.UInt16, left.IndexFormat);
            Assert.Equal(IndexFormat.UInt32, right.IndexFormat);

            using var cache = new GpuMeshCache(device);
            var gpuLeft = cache.Get(left);
            var gpuRight = cache.Get(right);
            Assert.Equal(IndexFormat.UInt32, gpuRight.Indices.Format);
            Assert.True(gpuLeft.Vertices.IsStatic);
            Assert.Equal(72, gpuLeft.Vertices.Layout.Stride);

            // The quad lies in XY; the plane in XZ, so it is turned to face the camera (y = -z). Vulkan depth range: z = 0
            // maps to depth 0.5, so float noise from the rotation cannot clip it (GL-style Ortho would put z = 0 on the edge).
            var projection = Matrix4x4.OrthoZeroToOne(-1f, 1f, -1f, 1f, -1f, 1f);
            var leftMatrix = projection * Matrix4x4.Translate(new Vector3(-0.5f, 0f, 0f));
            var rightMatrix = projection * Matrix4x4.Translate(new Vector3(0.5f, 0f, 0f)) * Matrix4x4.Rotate(Quaternion.Euler(90f, 0f, 0f));
            for (var frame = 0; frame < 3; frame++) // uploaded once, drawn every frame
            {
                device.Clear(Color.Black);
                foreach (var (gpu, matrix) in new[] { (gpuLeft, leftMatrix), (gpuRight, rightMatrix) })
                {
                    device.Draw(new DrawCommand(material, white, gpu.Vertices, gpu.Indices, 0, gpu.Indices.Count, matrix));
                }

                if (frame < 2)
                {
                    device.Present();
                }
            }

            var pixels = new byte[Size * Size * 4];
            device.ReadPixels(pixels);
            AssertColor(Color.Red, Pixel(pixels, Size, Size / 4, Size / 2));
            AssertColor(Color.Green, Pixel(pixels, Size, 3 * Size / 4, Size / 2));
            Assert.Same(gpuLeft, cache.Get(left)); // shared, not uploaded again
        });
    }

    [Fact]
    public void ChangedMeshes_AreUploadedAgain_AndReleasedBuffersAreTracked()
    {
        Run(device =>
        {
            var mesh = MeshPrimitives.Cube();
            var live = device.Resources.LiveCount;
            using (var cache = new GpuMeshCache(device))
            {
                var first = cache.Get(mesh);
                Assert.Equal(live + 2, device.Resources.LiveCount); // vertex + index buffer
                mesh.Positions = [.. mesh.Positions.Select(p => p * 2f)];
                var second = cache.Get(mesh);
                Assert.NotSame(first, second);
                Assert.True(first.IsDisposed);
                Assert.Equal(new Vector3(2f, 2f, 2f), second.Bounds.Size);
                Assert.Equal(live + 2, device.Resources.LiveCount);
                device.Present();
            }

            Assert.Equal(live, device.Resources.LiveCount);
        });
    }
}
