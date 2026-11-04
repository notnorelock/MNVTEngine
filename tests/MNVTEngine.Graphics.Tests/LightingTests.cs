using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Graphics.Rendering3D;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Tests;

public sealed class LightingTests : IDisposable
{
    private readonly FakeGraphicsDevice _device = new();

    public void Dispose() => _device.Dispose();

    [Fact]
    public void Pack_PutsEachLightFieldInTheColumnTheShaderReads()
    {
        var light = new LightData(LightKind.Spot, new Vector3(1f, 2f, 3f), new Vector3(0f, -2f, 0f), new Color(0.1f, 0.2f, 0.3f), 4f, Range: 5f, SpotAngle: 60f, InnerSpotAngle: 30f, ShadowStrength: 0.7f);
        var m = LightingUniforms.Pack(light, shadowIndex: 0);

        Assert.Equal((1f, 2f, 3f, 2f), (m.M00, m.M10, m.M20, m.M30));        // position, type (spot = 2)
        Assert.Equal((0f, -1f, 0f, 5f), (m.M01, m.M11, m.M21, m.M31));       // normalized direction, range
        Assert.Equal((0.1f, 0.2f, 0.3f, 4f), (m.M02, m.M12, m.M22, m.M32));  // color, intensity
        Assert.Equal(MathF.Cos(15f * MathF.PI / 180f), m.M03, 5);            // cos of the inner half angle
        Assert.Equal(MathF.Cos(30f * MathF.PI / 180f), m.M13, 5);            // cos of the outer half angle
        Assert.Equal((0f, 0.7f), (m.M23, m.M33));                            // shadow index, strength
        Assert.Equal(-1f, LightingUniforms.Pack(light, -1).M23);
    }

    [Fact]
    public void Select_KeepsDirectionalLightsFirst_ThenTheNearest_UpToTheLimit()
    {
        var lights = Enumerable.Range(0, 10).Select(i => new LightData(LightKind.Point, new Vector3(i * 10f, 0f, 0f), Vector3.Zero, Color.White, Range: 1f))
            .Append(new LightData(LightKind.Directional, Vector3.Zero, -Vector3.Up, Color.White)).ToList();

        var (selected, dropped) = SceneLighting.Select(lights, new Vector3(95f, 0f, 0f));

        Assert.Equal(SceneLighting.MaxLights, selected.Count);
        Assert.Equal(3, dropped);
        Assert.Equal(LightKind.Directional, selected[0].Kind);
        Assert.Equal(90f, selected[1].Position.X); // nearest to the camera
        Assert.DoesNotContain(selected, l => l.Position.X < 30f && l.Kind == LightKind.Point); // the farthest were dropped
    }

    [Fact]
    public void FrustumSphere_EnclosesTheFrustumUpToTheShadowDistance()
    {
        var viewProjection = Matrix4x4.PerspectiveZeroToOne(60f, 16f / 9f, 0.1f, 1000f) * Matrix4x4.LookAt(Vector3.Zero, Vector3.Forward, Vector3.Up);
        var (center, radius) = ShadowMap.FrustumSphere(viewProjection, 0.1f, 1000f, 20f);

        Assert.InRange(radius, 10f, 40f); // sized by the 20 m shadow distance, not the 1000 m far plane
        Assert.True(Vector3.Distance(center, new Vector3(0f, 0f, -20f)) <= radius + 0.01f, "the far corners' center is inside");
        Assert.True(Vector3.Distance(center, Vector3.Zero) <= radius + 0.01f, "the camera is inside");
    }

    [Fact]
    public void Renderer3D_GivesLitMaterialsTheLightingUniforms_AndUnlitByDefault()
    {
        var mesh = GpuMesh.Upload(_device, MeshPrimitives.Cube());
        var material = new Material(_device.CreateShader(Materials.MaterialShaders.Standard), BlendMode.Opaque);
        var texture = _device.CreateTexture(1, 1, [255, 255, 255, 255], SamplerState.PointClamp);
        var view = new View3D(Matrix4x4.LookAt(new Vector3(0f, 0f, 5f), Vector3.Zero, Vector3.Up), Matrix4x4.PerspectiveZeroToOne(60f, 1f, 0.1f, 100f), new Vector3(0f, 0f, 5f));
        var renderer = new Renderer3D(_device);

        renderer.Render(view, [new MeshDrawItem(mesh, 0, material, texture, Matrix4x4.Identity)]);
        Assert.True(material.TryGetUniform("uAmbient", out var ambient) && ambient.Vector.W == 0f); // unlit
        Assert.Same(texture, material.GetTexture("uShadowMap")); // a bound stand-in, never read

        var lighting = new SceneLighting { Ambient = new Color(0.5f, 0.5f, 0.5f), Lights = [new LightData(LightKind.Point, Vector3.One, Vector3.Zero, Color.White)] };
        renderer.Render(view, [new MeshDrawItem(mesh, 0, material, texture, Matrix4x4.Identity)], lighting);
        Assert.True(material.TryGetUniform("uAmbient", out ambient) && ambient.Vector is { X: 0.5f, W: 1f });
        Assert.True(material.TryGetUniform("uLightCount", out var count) && count.Integer == 1);
        Assert.True(material.TryGetUniform("uLight1", out var unused) && unused.Matrix.Equals(default(Matrix4x4))); // stale lights are cleared
        Assert.True(material.TryGetUniform("uCameraPosition", out var camera) && camera.Vector.Z == 5f);
    }
}
