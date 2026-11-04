using MNVTEngine.Graphics.Materials;
using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Graphics.Rendering3D;
using MNVTEngine.Mathematics;
using static MNVTEngine.Graphics.Vulkan.Tests.VulkanHarness;

namespace MNVTEngine.Graphics.Vulkan.Tests;

/// <summary>The Standard shader under lights and the directional shadow map, on a real device.</summary>
[Collection(nameof(VulkanGroup))]
public class VulkanLightingTests
{
    /// <summary>A floor (white Standard material) seen from straight above, filling the view.</summary>
    private sealed class Floor : IDisposable
    {
        private readonly IGraphicsDevice _device;
        private readonly GpuMeshCache _meshes;
        private readonly Shader _standard;
        private readonly Shader _caster;
        private readonly Texture2D _white;
        private readonly Texture2D _flat;
        private readonly Renderer3D _renderer;
        private readonly ShadowMap _shadows;

        public Floor(IGraphicsDevice device, Color baseColor)
        {
            _device = device;
            _meshes = new GpuMeshCache(device);
            _standard = device.CreateShader(MaterialShaders.Standard);
            _caster = device.CreateShader(MaterialShaders.ShadowCaster);
            _white = device.CreateTexture(1, 1, [255, 255, 255, 255], SamplerState.PointClamp);
            _flat = device.CreateTexture(1, 1, [128, 128, 255, 255], SamplerState.PointClamp);
            _renderer = new Renderer3D(device);
            _shadows = new ShadowMap(device, 512);
            Material = new Material(_standard, BlendMode.Opaque, "Floor") { Cull = CullMode.Back };
            Material.SetColor("_BaseColor", baseColor);
            MaterialShaders.ApplyDefaults(Material, MaterialShaders.StandardProperties, kind => kind == DefaultTexture.FlatNormal ? _flat : _white);
        }

        public Material Material { get; }

        public static View3D View { get; } = new(Matrix4x4.LookAt(new Vector3(0f, 2f, 0f), Vector3.Zero, Vector3.Forward), Matrix4x4.PerspectiveZeroToOne(60f, 1f, 0.1f, 50f), new Vector3(0f, 2f, 0f));

        public MeshDrawItem Item(Mesh mesh, Matrix4x4 world) => new(_meshes.Get(mesh), 0, Material, _white, world);

        /// <summary>Renders the floor (and extra items) and returns the frame.</summary>
        public byte[] Render(SceneLighting? lighting, params MeshDrawItem[] extra)
        {
            List<MeshDrawItem> items = [Item(MeshPrimitives.Plane(), Matrix4x4.Identity), .. extra];
            ShadowState? shadow = null;
            if (lighting?.Lights.Select((l, i) => (l, i)).FirstOrDefault(p => p.l.Shadows) is { l.Shadows: true } caster)
            {
                shadow = _shadows.Render(caster.l, caster.i, View.ViewProjection, 0.1f, 50f, items, _caster);
                _device.SetRenderTarget(null);
            }

            _device.Clear(Color.Black);
            _device.ClearDepth();
            _renderer.Render(View, items, lighting, shadow);
            var pixels = new byte[Size * Size * 4];
            _device.ReadPixels(pixels);
            return pixels;
        }

        public void Dispose()
        {
            _shadows.Dispose();
            _meshes.Dispose();
            _standard.Dispose();
            _caster.Dispose();
            _white.Dispose();
            _flat.Dispose();
        }
    }

    private static SceneLighting Lit(params LightData[] lights) => new() { Ambient = Color.Black, Lights = lights };

    private static LightData Sun(Vector3 direction, bool shadows = false) =>
        new(LightKind.Directional, Vector3.Zero, direction.Normalized, Color.White, 1f, Shadows: shadows, ShadowDistance: 10f);

    private static float Brightness(byte[] pixels, int x, int y) => Pixel(pixels, Size, x, y).R;

    [Fact]
    public void WithoutLights_TheStandardShaderShowsTheBaseColor_AsBefore() => Run(device =>
    {
        using var floor = new Floor(device, new Color(0.5f, 0.25f, 0.75f, 1f));
        AssertColor(new Color(0.5f, 0.25f, 0.75f, 1f), Pixel(floor.Render(null), Size, Size / 2, Size / 2));
    });

    [Fact]
    public void DirectionalLight_LightsSurfacesFacingIt_AndNotThoseFacingAway() => Run(device =>
    {
        using var floor = new Floor(device, Color.White);
        Assert.True(Brightness(floor.Render(Lit(Sun(-Vector3.Up))), Size / 2, Size / 2) > 0.9f, "a white floor under a white sun is (nearly) white");
        Assert.Equal(0f, Brightness(floor.Render(Lit(Sun(Vector3.Up))), Size / 2, Size / 2)); // lit from below, no ambient

        var grazing = Brightness(floor.Render(Lit(Sun(new Vector3(1f, -0.3f, 0f)))), Size / 2, Size / 2);
        Assert.InRange(grazing, 0.1f, 0.85f); // Lambert falls off with the angle
    });

    [Fact]
    public void AmbientLight_LightsEverySide() => Run(device =>
    {
        using var floor = new Floor(device, Color.White);
        var lighting = new SceneLighting { Ambient = new Color(0.5f, 0.5f, 0.5f), Lights = [] };
        Assert.InRange(Brightness(floor.Render(lighting), Size / 2, Size / 2), 0.4f, 0.6f);
    });

    [Fact]
    public void PointAndSpotLights_FadeOutWithRangeAndCone() => Run(device =>
    {
        using var floor = new Floor(device, Color.White);
        var point = floor.Render(Lit(new LightData(LightKind.Point, new Vector3(0f, 0.5f, 0f), Vector3.Zero, Color.White, 2f, Range: 1f)));
        Assert.True(Brightness(point, Size / 2, Size / 2) > 0.5f, "under the light");
        Assert.Equal(0f, Brightness(point, 1, 1)); // the view's corner is beyond the range

        var spot = floor.Render(Lit(new LightData(LightKind.Spot, new Vector3(0f, 1f, 0f), -Vector3.Up, Color.White, 3f, Range: 5f, SpotAngle: 30f, InnerSpotAngle: 20f)));
        Assert.True(Brightness(spot, Size / 2, Size / 2) > 0.5f, "inside the cone");
        Assert.Equal(0f, Brightness(spot, 1, 1)); // outside the cone
    });

    [Fact]
    public void DirectionalShadows_DarkenWhatIsBehindAnOccluder_AndOnlyThat() => Run(device =>
    {
        using var floor = new Floor(device, Color.White);

        // A 1 m block above the camera (outside its view) between the sun and the floor's center.
        var blocker = floor.Item(MeshPrimitives.Cube(1f), Matrix4x4.TRS(new Vector3(0f, 4f, 0f), Quaternion.Identity, Vector3.One));
        var withoutShadows = floor.Render(Lit(Sun(-Vector3.Up)), blocker);
        var withShadows = floor.Render(Lit(Sun(-Vector3.Up, shadows: true)), blocker);

        Assert.True(Brightness(withoutShadows, Size / 2, Size / 2) > 0.9f);
        Assert.Equal(0f, Brightness(withShadows, Size / 2, Size / 2)); // full-strength shadow, no ambient
        Assert.True(Brightness(withShadows, 2, 2) > 0.9f, "outside the shadow the floor stays lit (no acne)");
        Assert.True(Brightness(withShadows, Size / 2, 6) > 0.9f);
    });
}
