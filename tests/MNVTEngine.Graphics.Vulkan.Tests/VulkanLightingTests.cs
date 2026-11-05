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
        private readonly PointShadowMap _points;

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
            _points = new PointShadowMap(device, 256);
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
            ShadowSet? shadow = null;
            if (lighting?.Lights.Select((l, i) => (l, i)).FirstOrDefault(p => p.l.Shadows) is { l.Shadows: true } caster)
            {
                shadow = caster.l.Kind switch
                {
                    LightKind.Spot => new ShadowSet(Spots: [_shadows.RenderSpot(caster.l, caster.i, items, _caster)]),
                    LightKind.Point => new ShadowSet(Point: _points.Render(caster.l, caster.i, items, _caster)),
                    _ => _shadows.Render(caster.l, caster.i, View.ViewProjection, 0.1f, 50f, items, _caster),
                };
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
            _points.Dispose();
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
    public void TextureMaps_SampleTheUVSetTheMaterialChooses() => Run(device =>
    {
        using var floor = new Floor(device, Color.White);
        using var redGreen = device.CreateTexture(2, 1, [255, 0, 0, 255, 0, 255, 0, 255], SamplerState.PointClamp);
        floor.Material.SetTexture("_BaseColorMap", redGreen);
        // UV0 is (0, 0) everywhere (red); UV1 spans the texture, so the two halves of the view differ.
        var plane = MeshPrimitives.Plane();
        plane.UV1 = plane.UV0;
        plane.UV0 = new Vector2[plane.VertexCount];
        var raised = floor.Item(plane, Matrix4x4.Translate(new Vector3(0f, 0.5f, 0f)));

        var uv0 = floor.Render(null, raised);
        AssertColor(Color.Red, Pixel(uv0, Size, Size / 4, Size / 2));
        AssertColor(Color.Red, Pixel(uv0, Size, Size * 3 / 4, Size / 2));

        floor.Material.SetFloat("_BaseColorUVSet", 1f);
        var uv1 = floor.Render(null, raised);
        Assert.NotEqual(Pixel(uv1, Size, Size / 4, Size / 2), Pixel(uv1, Size, Size * 3 / 4, Size / 2));
        Assert.Contains(Color.Green, new[] { Pixel(uv1, Size, Size / 4, Size / 2), Pixel(uv1, Size, Size * 3 / 4, Size / 2) });
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
    /// <summary>A small cube above the floor's center, outside the camera's view; its shadow covers the middle of the view only.</summary>
    private static MeshDrawItem Blocker(Floor floor, float y) => floor.Item(MeshPrimitives.Cube(1f), Matrix4x4.TRS(new Vector3(0f, y, 0f), Quaternion.Identity, new Vector3(0.3f, 0.3f, 0.3f)));

    [Fact]
    public void SpotShadows_DarkenWhatIsBehindAnOccluder() => Run(device =>
    {
        using var floor = new Floor(device, Color.White);
        var spot = new LightData(LightKind.Spot, new Vector3(0f, 6f, 0f), -Vector3.Up, Color.White, 3f, Range: 12f, SpotAngle: 60f, InnerSpotAngle: 50f, Shadows: true);
        var lit = floor.Render(Lit(spot with { Shadows = false }), Blocker(floor, 4f));
        var shadowed = floor.Render(Lit(spot), Blocker(floor, 4f));
        Assert.True(Brightness(lit, Size / 2, Size / 2) > 0.3f);
        Assert.True(Brightness(shadowed, Size / 2, Size / 2) < 0.05f, "under the blocker");
        Assert.True(Brightness(shadowed, 8, Size / 2) > 0.2f, "beside the blocker's shadow");
    });

    [Fact]
    public void PointShadows_DarkenWhatIsBehindAnOccluder_OnEveryFace() => Run(device =>
    {
        using var floor = new Floor(device, Color.White);
        var point = new LightData(LightKind.Point, new Vector3(0f, 6f, 0f), Vector3.Zero, Color.White, 6f, Range: 14f, Shadows: true);
        var shadowed = floor.Render(Lit(point), Blocker(floor, 4f));
        Assert.True(Brightness(shadowed, Size / 2, Size / 2) < 0.05f, "under the blocker (the -Y face)");
        Assert.True(Brightness(shadowed, 4, 4) > 0.2f, "a corner of the view stays lit");
    });

    [Fact]
    public void CutoutMaterials_CastTheirCutShape() => Run(device =>
    {
        using var floor = new Floor(device, Color.White);
        // A 2 x 2 texel texture: left column opaque, right column transparent, on a quad above the floor.
        using var half = device.CreateTexture(2, 2, [255, 255, 255, 255, 255, 255, 255, 0, 255, 255, 255, 255, 255, 255, 255, 0], SamplerState.PointClamp);
        var leaf = floor.Material.CreateInstance("Leaf");
        leaf.SetTexture("_BaseColorMap", half);
        leaf.SetFloat("_AlphaCutoff", 0.5f);
        leaf.Cull = CullMode.None;
        var quad = new MeshDrawItem(floor.Item(MeshPrimitives.Quad(), Matrix4x4.Identity).Mesh, 0, leaf, half,
            Matrix4x4.TRS(new Vector3(0f, 4f, 0f), Quaternion.AngleAxis(-90f, Vector3.UnitX), new Vector3(1.4f, 1.4f, 1f)));
        var frame = floor.Render(Lit(Sun(-Vector3.Up, shadows: true)), quad);
        var left = Brightness(frame, (Size / 2) - 6, Size / 2);
        var right = Brightness(frame, (Size / 2) + 6, Size / 2);
        Assert.True(left < 0.05f, $"the opaque half casts a shadow ({left})");
        Assert.True(right > 0.9f, $"the cut-out half lets the light through ({right})");
    });

    [Fact]
    public void ToneMapping_RollsOffHighlights_AndExposureScales() => Run(device =>
    {
        using var floor = new Floor(device, new Color(0.5f, 0.5f, 0.5f, 1f));
        var bright = new SceneLighting { Ambient = Color.Black, Lights = [Sun(-Vector3.Up) with { Intensity = 4f }] };
        var clipped = Brightness(floor.Render(bright), Size / 2, Size / 2);
        var filmic = Brightness(floor.Render(new SceneLighting { Ambient = Color.Black, Lights = bright.Lights, ToneMapping = ToneMapping.Aces }), Size / 2, Size / 2);
        var darker = Brightness(floor.Render(new SceneLighting { Ambient = Color.Black, Lights = [Sun(-Vector3.Up) with { Intensity = 0.5f }], Exposure = 0.25f }), Size / 2, Size / 2);
        Assert.Equal(1f, clipped, 2);  // 4x light on mid-grey clips to white without tone mapping
        Assert.InRange(filmic, 0.8f, 0.99f); // ACES keeps it just below white
        Assert.InRange(darker, 0.05f, 0.4f);
    });

    [Fact]
    public void Environment_LightsAndIsReflected_ByMetalsAndRoughSurfaces() => Run(device =>
    {
        // A uniformly red environment: a smooth metal floor reflects red; a rough white floor is lit red.
        var red = new byte[16 * 8 * 4];
        for (var i = 0; i < red.Length; i += 4)
        {
            (red[i], red[i + 1], red[i + 2], red[i + 3]) = (230, 20, 20, 255);
        }

        using var environment = device.CreateTexture(16, 8, red, new SamplerState(TextureFilter.Linear, TextureWrap.Repeat, GenerateMipmaps: true));
        using var floor = new Floor(device, Color.White);
        floor.Material.SetFloat("_Metallic", 1f);
        floor.Material.SetFloat("_Roughness", 0.1f);
        var lighting = new SceneLighting { Ambient = Color.Black, Lights = [], Environment = environment };
        var metal = Pixel(floor.Render(lighting), Size, Size / 2, Size / 2);
        Assert.True(metal.R > 0.6f && metal.G < 0.2f, $"the metal reflects the red environment ({metal})");

        floor.Material.SetFloat("_Metallic", 0f);
        floor.Material.SetFloat("_Roughness", 1f);
        var rough = Pixel(floor.Render(lighting), Size, Size / 2, Size / 2);
        Assert.True(rough.R > 0.3f && rough.R > rough.G * 3f, $"the rough surface is lit red ({rough})");
    });
}
