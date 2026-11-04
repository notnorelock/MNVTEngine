using MNVTEngine.Graphics.Materials;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Rendering3D;

public enum LightKind
{
    Directional,
    Point,
    Spot,
}

/// <summary>One light for a 3D pass, in world space.</summary>
/// <param name="Direction">Where the light shines (directional and spot), normalized.</param>
/// <param name="Color">sRGB color; multiplied by <paramref name="Intensity"/> in linear space by the shader.</param>
/// <param name="SpotAngle">Full outer cone angle in degrees.</param>
/// <param name="InnerSpotAngle">Full inner (fully lit) cone angle in degrees.</param>
public readonly record struct LightData(
    LightKind Kind,
    Vector3 Position,
    Vector3 Direction,
    Color Color,
    float Intensity = 1f,
    float Range = 10f,
    float SpotAngle = 30f,
    float InnerSpotAngle = 21f,
    bool Shadows = false,
    float ShadowStrength = 1f,
    float ShadowDistance = 40f);

/// <summary>
/// The lights of a 3D pass. <see cref="Unlit"/> (a scene without lights) shows surfaces at full brightness, exactly as
/// before lighting existed; otherwise the Standard shader adds ambient light and up to <see cref="MaxLights"/> lights.
/// </summary>
public sealed class SceneLighting
{
    public const int MaxLights = 8;

    /// <summary>A scene without lights: base color × occlusion + emission.</summary>
    public static SceneLighting Unlit { get; } = new() { IsUnlit = true };

    /// <summary>Editor previews (materials, models): a key light from the upper left and a soft fill.</summary>
    public static SceneLighting Preview { get; } = new()
    {
        Ambient = new Color(0.35f, 0.36f, 0.4f),
        Lights =
        [
            new LightData(LightKind.Directional, Vector3.Zero, new Vector3(0.5f, -0.7f, -0.5f).Normalized, Color.White, 1.1f),
            new LightData(LightKind.Directional, Vector3.Zero, new Vector3(-0.6f, -0.2f, 0.7f).Normalized, new Color(0.6f, 0.7f, 0.9f), 0.35f),
        ],
    };

    public bool IsUnlit { get; private init; }

    /// <summary>Ambient light (sRGB color × intensity).</summary>
    public Color Ambient { get; init; } = new(0.25f, 0.26f, 0.3f);

    public float AmbientIntensity { get; init; } = 1f;

    /// <summary>Directional lights first, then the rest by priority; only the first <see cref="MaxLights"/> are used.</summary>
    public IReadOnlyList<LightData> Lights { get; init; } = [];

    /// <summary>
    /// Orders lights for a camera: directional lights, then point and spot lights by how close their range comes to the
    /// camera. Returns at most <see cref="MaxLights"/> and how many were dropped.
    /// </summary>
    public static (IReadOnlyList<LightData> Lights, int Dropped) Select(IEnumerable<LightData> lights, Vector3 camera)
    {
        ArgumentNullException.ThrowIfNull(lights);
        var ordered = lights
            .OrderBy(l => l.Kind == LightKind.Directional ? 0 : 1)
            .ThenBy(l => l.Kind == LightKind.Directional ? 0f : Vector3.Distance(l.Position, camera) - l.Range)
            .ToList();
        return (ordered.Take(MaxLights).ToList(), Math.Max(0, ordered.Count - MaxLights));
    }
}

/// <summary>
/// Writes the lighting uniforms a 3D pass gives every material whose shader declares them. Names are reserved for the
/// renderer (see <c>MaterialDefinition.ReservedUniforms</c>):
/// <list type="bullet">
/// <item><c>vec4 uCameraPosition</c>: xyz = camera position.</item>
/// <item><c>vec4 uAmbient</c>: rgb = ambient (sRGB × intensity); w = 1 when lit, 0 for an unlit scene.</item>
/// <item><c>int uLightCount</c>, <c>mat4 uLight0</c> … <c>uLight7</c>; columns:
///   0 = position.xyz and type (0 directional, 1 point, 2 spot); 1 = direction.xyz and range;
///   2 = color.rgb (sRGB) and intensity; 3 = cos(inner half angle), cos(outer half angle), shadow index (-1 none, 0 the
///   directional shadow map), shadow strength.</item>
/// <item><c>mat4 uShadowMatrix</c> (world to shadow clip space), <c>vec4 uShadowParams</c> (enabled, depth bias, texel
/// size, unused) and <c>sampler2D uShadowMap</c> (depth packed in RGBA8, see <see cref="ShadowMap"/>).</item>
/// </list>
/// </summary>
public static class LightingUniforms
{
    public static readonly IReadOnlyList<string> Names =
    [
        "uCameraPosition", "uAmbient", "uLightCount", "uLight0", "uLight1", "uLight2", "uLight3", "uLight4", "uLight5",
        "uLight6", "uLight7", "uShadowMatrix", "uShadowParams", "uShadowMap",
    ];

    private static readonly string[] LightNames = ["uLight0", "uLight1", "uLight2", "uLight3", "uLight4", "uLight5", "uLight6", "uLight7"];

    /// <summary>True when the material's shader reads the lighting uniforms.</summary>
    public static bool Uses(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        return material.Shader.HasUniform("uAmbient") || material.Shader.HasUniform("uLightCount");
    }

    /// <summary>Sets every lighting uniform on a material (unused lights are zeroed so stale data never shows).</summary>
    public static void Apply(Material material, SceneLighting lighting, Vector3 camera, in ShadowState shadow, Texture2D noShadow)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(lighting);
        material.SetVector("uCameraPosition", new Vector4(camera.X, camera.Y, camera.Z, 1f));
        var ambient = lighting.IsUnlit ? new Vector4(1f, 1f, 1f, 0f)
            : new Vector4(lighting.Ambient.R * lighting.AmbientIntensity, lighting.Ambient.G * lighting.AmbientIntensity, lighting.Ambient.B * lighting.AmbientIntensity, 1f);
        material.SetVector("uAmbient", ambient);
        var count = lighting.IsUnlit ? 0 : Math.Min(lighting.Lights.Count, SceneLighting.MaxLights);
        material.SetInt("uLightCount", count);
        for (var i = 0; i < LightNames.Length; i++)
        {
            material.SetMatrix(LightNames[i], i < count ? Pack(lighting.Lights[i], shadow.Map is not null && shadow.LightIndex == i ? 0 : -1) : default);
        }

        material.SetMatrix("uShadowMatrix", shadow.Matrix);
        material.SetVector("uShadowParams", new Vector4(shadow.Map is null ? 0f : 1f, shadow.Bias, shadow.Map is null ? 0f : 1f / shadow.Map.Width, 0f));
        material.SetTexture("uShadowMap", shadow.Map ?? noShadow);
    }

    /// <summary>A light as the <c>mat4</c> the shader reads (column c = <c>M0c..M3c</c>).</summary>
    public static Matrix4x4 Pack(in LightData light, int shadowIndex)
    {
        var halfOuter = light.SpotAngle * 0.5f * MathF.PI / 180f;
        var halfInner = MathF.Min(light.InnerSpotAngle, light.SpotAngle) * 0.5f * MathF.PI / 180f;
        var direction = light.Direction.Normalized;
        return new Matrix4x4
        {
            M00 = light.Position.X, M10 = light.Position.Y, M20 = light.Position.Z, M30 = (float)light.Kind,
            M01 = direction.X, M11 = direction.Y, M21 = direction.Z, M31 = light.Range,
            M02 = light.Color.R, M12 = light.Color.G, M22 = light.Color.B, M32 = light.Intensity,
            M03 = MathF.Cos(halfInner), M13 = MathF.Cos(halfOuter), M23 = shadowIndex, M33 = light.ShadowStrength,
        };
    }
}

/// <summary>The shadow map of a pass: the index of the light that casts it, its matrix and texture; <see cref="None"/> when off.</summary>
public readonly record struct ShadowState(int LightIndex, Matrix4x4 Matrix, RenderTarget2D? Map, float Bias)
{
    public static ShadowState None => new(-1, Matrix4x4.Identity, null, 0f);
}

/// <summary>
/// The directional shadow map: depth from the light, packed into an RGBA8 render target (8 bits per channel, 32 in all)
/// so it works on every render target format; sampled with point filtering and 3×3 percentage-closer filtering in the
/// Standard shader. The light's orthographic view encloses the camera frustum up to the light's shadow distance (a
/// bounding sphere, so it does not change size as the camera turns) and moves in whole texels, so shadow edges do not
/// shimmer. Casters are opaque meshes; transparent and alpha-cutout surfaces do not cast shadows yet.
/// </summary>
public sealed class ShadowMap(IGraphicsDevice device, int size = 2048) : IDisposable
{
    private static readonly Color Far = Color.White; // packs depth >= 1: nothing in front
    private RenderTarget2D? _target;
    private Material? _caster;

    public int Size { get; } = size;

    /// <summary>Renders the casters for a directional light and returns the state the main pass needs.</summary>
    public ShadowState Render(in LightData light, int lightIndex, Matrix4x4 cameraViewProjection, float cameraNear, float cameraFar, IReadOnlyList<MeshDrawItem> items, Shader casterShader)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(casterShader);
        _target ??= device.CreateRenderTarget(Size, Size, SamplerState.PointClamp, "Shadow map", depth: true);
        _caster ??= new Material(casterShader, BlendMode.Opaque, "Shadow caster");

        var (center, radius) = FrustumSphere(cameraViewProjection, cameraNear, cameraFar, light.ShadowDistance);
        var direction = light.Direction.Normalized;
        var up = MathF.Abs(Vector3.Dot(direction, Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;

        // Snap the center to the shadow map's texel grid in light space, so moving the camera does not shimmer edges.
        var lightView = Matrix4x4.LookAt(Vector3.Zero, direction, up);
        var texel = radius * 2f / Size;
        var local = lightView.MultiplyPoint3x4(center);
        local = new Vector3(MathF.Floor(local.X / texel) * texel, MathF.Floor(local.Y / texel) * texel, local.Z);
        Matrix4x4.TryInvert(lightView, out var lightToWorld);
        center = lightToWorld.MultiplyPoint3x4(local);

        var eye = center - (direction * (radius * 2f));
        var view = Matrix4x4.LookAt(eye, center, up);
        var projection = Matrix4x4.OrthoZeroToOne(-radius, radius, -radius, radius, 0f, radius * 3f);
        var viewProjection = projection * view;

        device.SetRenderTarget(_target);
        device.Clear(Far);
        device.ClearDepth();
        var frustum = Frustum.FromViewProjection(viewProjection);
        foreach (var item in items)
        {
            if (item.Material.IsTransparent || !item.Material.DepthWrite || !frustum.Intersects(Renderer3D.TransformBounds(item.Mesh.Bounds, item.World)))
            {
                continue;
            }

            var sub = item.Mesh.SubMeshes[item.SubMesh];
            device.Draw(new DrawCommand(_caster, item.Texture, item.Mesh.Vertices, item.Mesh.Indices, sub.IndexStart, sub.IndexCount, viewProjection,
                State: new RenderState(true, true, item.Cull ?? item.Material.Cull), World: item.World));
        }

        // Depth bias grows with the texel size: big shadow maps over large areas need more.
        return new ShadowState(lightIndex, viewProjection, _target, MathF.Max(0.0005f, texel / (radius * 3f) * 1.5f));
    }

    /// <summary>A sphere around the camera frustum from its near plane to <paramref name="distance"/> (or its far plane).</summary>
    public static (Vector3 Center, float Radius) FrustumSphere(Matrix4x4 viewProjection, float near, float far, float distance)
    {
        Matrix4x4.TryInvert(viewProjection, out var inverse);
        var t = Math.Clamp((MathF.Min(distance, far) - near) / MathF.Max(far - near, 1e-4f), 0f, 1f);
        Span<Vector3> corners = stackalloc Vector3[8];
        var i = 0;
        foreach (var (x, y) in new[] { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) })
        {
            var nearPoint = Unproject(inverse, x, y, 0f);
            var farPoint = Unproject(inverse, x, y, 1f);
            corners[i++] = nearPoint;
            corners[i++] = nearPoint + ((farPoint - nearPoint) * t);
        }

        var center = Vector3.Zero;
        foreach (var c in corners)
        {
            center += c;
        }

        center /= 8f;
        var radius = 0f;
        foreach (var c in corners)
        {
            radius = MathF.Max(radius, Vector3.Distance(center, c));
        }

        return (center, MathF.Max(radius, 0.01f));
    }

    private static Vector3 Unproject(Matrix4x4 inverse, float x, float y, float z) => inverse.MultiplyPoint(new Vector3(x, y, z));

    public void Dispose()
    {
        _target?.Dispose();
        _target = null;
    }
}
