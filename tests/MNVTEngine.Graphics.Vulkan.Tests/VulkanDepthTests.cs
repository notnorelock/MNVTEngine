using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;
using static MNVTEngine.Graphics.Vulkan.Tests.VulkanHarness;

namespace MNVTEngine.Graphics.Vulkan.Tests;

/// <summary>Depth buffers, depth state, face culling and per-draw world matrices.</summary>
[Collection(nameof(VulkanGroup))]
public class VulkanDepthTests
{
    private static readonly ShaderSource WorldColor = new(
        "Test/WorldColor",
        """
        #version 450 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 5) in vec4 aColor;
        uniform mat4 uViewProjection;
        uniform mat4 uModel;
        out vec4 vColor;
        void main()
        {
            vColor = aColor;
            gl_Position = uViewProjection * uModel * vec4(aPosition, 1.0);
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

    private sealed class Scene3D(IGraphicsDevice device) : IDisposable
    {
        private readonly GpuMeshCache _meshes = new(device);
        private readonly Shader _shader = device.CreateShader(WorldColor);
        private readonly Texture2D _white = device.CreateTexture(1, 1, [255, 255, 255, 255], SamplerState.PointClamp);

        public Matrix4x4 ViewProjection { get; } =
            Matrix4x4.PerspectiveZeroToOne(60f, 1f, 0.1f, 100f) * Matrix4x4.LookAt(new Vector3(0f, 0f, 5f), Vector3.Zero, Vector3.Up);

        public void Draw(Mesh mesh, Matrix4x4 world, RenderState state)
        {
            var gpu = _meshes.Get(mesh);
            device.Draw(new DrawCommand(new Material(_shader, BlendMode.Opaque), _white, gpu.Vertices, gpu.Indices, 0, gpu.Indices.Count, ViewProjection, State: state, World: world));
        }

        public void Dispose()
        {
            _meshes.Dispose();
            _shader.Dispose();
            _white.Dispose();
        }
    }

    private static Mesh Solid(Mesh mesh, Color color)
    {
        mesh.Colors = Enumerable.Repeat(color, mesh.VertexCount).ToArray();
        return mesh;
    }

    /// <summary>Each cube face colored by its normal, so the visible face tells which side was drawn.</summary>
    private static Mesh NormalColoredCube()
    {
        var cube = MeshPrimitives.Cube(2f);
        cube.Colors = [.. cube.Normals!.Select(n => new Color((n.X * 0.5f) + 0.5f, (n.Y * 0.5f) + 0.5f, (n.Z * 0.5f) + 0.5f, 1f))];
        return cube;
    }

    private static Color Center(VulkanGraphicsDevice device)
    {
        var pixels = new byte[Size * Size * 4];
        device.ReadPixels(pixels);
        return Pixel(pixels, Size, Size / 2, Size / 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NearerGeometryWins_InEitherDrawOrder(bool nearFirst)
    {
        Run(device =>
        {
            using var scene = new Scene3D(device);
            var near = Solid(MeshPrimitives.Quad(2f, 2f), Color.Red);
            var far = Solid(MeshPrimitives.Quad(12f, 12f), Color.Green); // fills the view at distance 7
            device.Clear(Color.Black);
            device.ClearDepth();
            var draws = new[] { (near, Matrix4x4.Translate(new Vector3(0f, 0f, 1f))), (far, Matrix4x4.Translate(new Vector3(0f, 0f, -2f))) };
            foreach (var (mesh, world) in nearFirst ? draws : draws.Reverse())
            {
                scene.Draw(mesh, world, RenderState.Opaque3D);
            }

            AssertColor(Color.Red, Center(device));
            var pixels = new byte[Size * Size * 4];
            device.ReadPixels(pixels);
            AssertColor(Color.Green, Pixel(pixels, Size, 4, 4)); // only the far quad reaches the corner
        });
    }

    [Fact]
    public void WithoutDepthTesting_TheLastDrawWins()
    {
        Run(device =>
        {
            using var scene = new Scene3D(device);
            device.Clear(Color.Black);
            device.ClearDepth();
            scene.Draw(Solid(MeshPrimitives.Quad(2f, 2f), Color.Red), Matrix4x4.Translate(new Vector3(0f, 0f, 1f)), default);
            scene.Draw(Solid(MeshPrimitives.Quad(4f, 4f), Color.Green), Matrix4x4.Translate(new Vector3(0f, 0f, -2f)), default);
            AssertColor(Color.Green, Center(device));
        });
    }

    [Fact]
    public void BackFaceCulling_ShowsTheOutside_FrontCullingTheInside()
    {
        Run(device =>
        {
            using var scene = new Scene3D(device);
            var cube = NormalColoredCube();
            device.Clear(Color.Black);
            device.ClearDepth();
            scene.Draw(cube, Matrix4x4.Identity, RenderState.Opaque3D);
            AssertColor(new Color(0.5f, 0.5f, 1f, 1f), Center(device)); // the +Z face, towards the camera

            device.Clear(Color.Black);
            device.ClearDepth();
            scene.Draw(cube, Matrix4x4.Identity, RenderState.Opaque3D with { Cull = CullMode.Front });
            AssertColor(new Color(0.5f, 0.5f, 0f, 1f), Center(device)); // the far -Z face, seen from inside
        });
    }

    [Fact]
    public void RenderTargetsWithDepth_DepthTest_AndOverManyFrames()
    {
        Run(device =>
        {
            using var scene = new Scene3D(device);
            using var target = device.CreateRenderTarget(Size, Size, SamplerState.PointClamp, "3D", depth: true);
            using var flat = device.CreateRenderTarget(Size, Size, SamplerState.PointClamp, "2D");
            Assert.True(target.HasDepth);
            Assert.False(flat.HasDepth);
            var cube = NormalColoredCube();
            for (var frame = 0; frame < 4; frame++) // the depth image is reused across frames in flight
            {
                device.SetRenderTarget(target);
                device.Clear(Color.Black);
                device.ClearDepth();
                scene.Draw(cube, Matrix4x4.Rotate(Quaternion.Euler(0f, frame * 10f, 0f)), RenderState.Opaque3D);
                scene.Draw(cube, Matrix4x4.Translate(new Vector3(0f, 0f, -3f)), RenderState.Opaque3D); // hidden behind
                device.SetRenderTarget(null);
                device.Present();
            }

            device.SetRenderTarget(target);
            Assert.True(Center(device).B > 0.9f); // the front cube's +Z face, not the one behind

            device.SetRenderTarget(flat);
            Assert.Throws<InvalidOperationException>(() => device.ClearDepth());
            Assert.Throws<InvalidOperationException>(() => scene.Draw(cube, Matrix4x4.Identity, RenderState.Opaque3D));
            device.SetRenderTarget(null);
        });
    }

    [Fact]
    public void TheWorldMatrix_PlacesEachDraw()
    {
        Run(device =>
        {
            using var scene = new Scene3D(device);
            var quad = Solid(MeshPrimitives.Quad(0.5f, 0.5f), Color.White);
            device.Clear(Color.Black);
            device.ClearDepth();
            scene.Draw(quad, Matrix4x4.Translate(new Vector3(-1.5f, 0f, 0f)), RenderState.Opaque3D);
            var pixels = new byte[Size * Size * 4];
            device.ReadPixels(pixels);
            AssertColor(Color.Black, Pixel(pixels, Size, Size / 2, Size / 2));
            var left = scene.ViewProjection.MultiplyPoint(new Vector3(-1.5f, 0f, 0f));
            var x = (int)((left.X + 1f) * 0.5f * Size);
            AssertColor(Color.White, Pixel(pixels, Size, x, Size / 2));
        });
    }
}
