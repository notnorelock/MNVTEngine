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

    /// <summary>How lit colors map to the screen: none clips at 1; ACES rolls highlights off like film.</summary>
    public ToneMapping ToneMapping { get; init; }

    /// <summary>Multiplies the lit color before tone mapping (1 = as computed; 2 = one stop brighter).</summary>
    public float Exposure { get; init; } = 1f;

    /// <summary>
    /// Image-based lighting: an equirectangular (2:1 latitude-longitude) environment image with mipmaps. Surfaces reflect
    /// it (sharper when smoother) and are lit by its blurred colors; null for none.
    /// </summary>
    public Texture2D? Environment { get; init; }

    public float EnvironmentIntensity { get; init; } = 1f;

    /// <summary>Turns the environment around the vertical axis (degrees).</summary>
    public float EnvironmentRotation { get; init; }

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

public enum ToneMapping
{
    None,

    /// <summary>The ACES filmic curve (Narkowicz fit).</summary>
    Aces,
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
///   directional shadow map, 1–2 a spot shadow map, 3 the point shadow atlas), shadow strength.</item>
/// <item>Shadow maps (depth packed in RGBA8, see <see cref="ShadowMap"/>): <c>mat4 uShadowMatrix</c>, <c>vec4
/// uShadowParams</c> (enabled, depth bias, texel size, unused) and <c>sampler2D uShadowMap</c> for the directional light,
/// the same with suffixes 1 and 2 for spot lights, and <c>mat4 uPointShadowMatrix0</c>…<c>5</c> (cube faces +X, -X, +Y,
/// -Y, +Z, -Z), <c>vec4 uPointShadowParams</c> and <c>sampler2D uPointShadowMap</c> (a 3 × 2 face atlas) for one point light.</item>
/// <item><c>vec4 uToneMapping</c> (mode: 0 none, 1 ACES; exposure) and <c>vec4 uEnvironment</c> (enabled, intensity, mip
/// levels, rotation in radians) with <c>sampler2D uEnvironmentMap</c>.</item>
/// </list>
/// </summary>
public static class LightingUniforms
{
    public static readonly IReadOnlyList<string> Names = Material.LightingUniformNames;

    private static readonly string[] SlotSuffix = ["", "1", "2"];

    private static readonly string[] LightNames = ["uLight0", "uLight1", "uLight2", "uLight3", "uLight4", "uLight5", "uLight6", "uLight7"];

    /// <summary>True when the material's shader reads the lighting uniforms.</summary>
    public static bool Uses(Material material)
    {
        ArgumentNullException.ThrowIfNull(material);
        return material.Shader.HasUniform("uAmbient") || material.Shader.HasUniform("uLightCount") || material.Shader.HasUniform("uEnvironment");
    }

    /// <summary>Sets every lighting uniform on a material (unused lights are zeroed so stale data never shows).</summary>
    /// <param name="noShadow">Bound to every unused sampler (they must be bound; they are not read).</param>
    public static void Apply(Material material, SceneLighting lighting, Vector3 camera, ShadowSet shadows, Texture2D noShadow)
    {
        ArgumentNullException.ThrowIfNull(shadows);
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
            material.SetMatrix(LightNames[i], i < count ? Pack(lighting.Lights[i], shadows.SlotOf(i)) : default);
        }

        for (var slot = 0; slot < SlotSuffix.Length; slot++)
        {
            var shadow = shadows.Slot(slot);
            material.SetMatrix("uShadowMatrix" + SlotSuffix[slot], shadow?.Matrix ?? Matrix4x4.Identity);
            material.SetVector("uShadowParams" + SlotSuffix[slot], shadow is { Map: { } map } s ? new Vector4(1f, s.Bias, 1f / map.Width, 0f) : default);
            material.SetTexture("uShadowMap" + SlotSuffix[slot], shadow?.Map ?? noShadow);
        }

        var point = shadows.Point;
        for (var face = 0; face < 6; face++)
        {
            material.SetMatrix("uPointShadowMatrix" + face, point is { } p ? p.Faces[face] : Matrix4x4.Identity);
        }

        material.SetVector("uPointShadowParams", point is { Map: { } atlas } ps ? new Vector4(1f, ps.Bias, 1f / atlas.Width, 1f / atlas.Height) : default);
        material.SetTexture("uPointShadowMap", point?.Map ?? noShadow);
        material.SetVector("uToneMapping", new Vector4(lighting.ToneMapping == ToneMapping.Aces ? 1f : 0f, lighting.Exposure, 0f, 0f));
        var environment = lighting.IsUnlit ? null : lighting.Environment;
        var mips = environment is null ? 0f : MathF.Floor(MathF.Log2(MathF.Max(environment.Width, environment.Height)));
        material.SetVector("uEnvironment", new Vector4(environment is null ? 0f : 1f, lighting.EnvironmentIntensity, mips, lighting.EnvironmentRotation * MathF.PI / 180f));
        material.SetTexture("uEnvironmentMap", environment ?? noShadow);
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

/// <summary>A shadow map of a pass: the index of the light that casts it, its matrix and texture; <see cref="None"/> when off.</summary>
public readonly record struct ShadowState(int LightIndex, Matrix4x4 Matrix, RenderTarget2D? Map, float Bias)
{
    public static ShadowState None => new(-1, Matrix4x4.Identity, null, 0f);
}

/// <summary>The point light shadow of a pass: six face matrices (+X, -X, +Y, -Y, +Z, -Z) into a 3 × 2 atlas.</summary>
public sealed record PointShadowState(int LightIndex, IReadOnlyList<Matrix4x4> Faces, RenderTarget2D Map, float Bias);

/// <summary>Every shadow map of a pass: the directional light's (slot 0), up to two spot lights' (slots 1 and 2), one point light's.</summary>
public sealed record ShadowSet(ShadowState? Directional = null, IReadOnlyList<ShadowState>? Spots = null, PointShadowState? Point = null)
{
    public const int MaxSpotShadows = 2;

    public static ShadowSet None { get; } = new();

    public static implicit operator ShadowSet(ShadowState directional) => new(directional);

    public ShadowState? Slot(int slot) => slot == 0 ? Directional is { Map: not null } d ? d : null
        : Spots is { } spots && slot - 1 < spots.Count && spots[slot - 1].Map is not null ? spots[slot - 1] : null;

    /// <summary>The shader's shadow index for light <paramref name="lightIndex"/>: 0–2 a slot, 3 the point atlas, -1 none.</summary>
    public int SlotOf(int lightIndex)
    {
        for (var slot = 0; slot <= MaxSpotShadows; slot++)
        {
            if (Slot(slot) is { } shadow && shadow.LightIndex == lightIndex)
            {
                return slot;
            }
        }

        return Point is { } point && point.LightIndex == lightIndex ? 3 : -1;
    }
}

/// <summary>
/// Draws shadow casters for a light view: opaque and alpha-cutout meshes (cutout materials discard the same pixels as
/// in the main pass, so leaves and fences cast their real shapes). Transparent materials do not cast. Each source
/// material gets one caster material that mirrors its base color map, color and cutoff.
/// </summary>
public sealed class ShadowCasters(IGraphicsDevice device)
{
    private readonly Dictionary<Material, Material> _casters = new(ReferenceEqualityComparer.Instance);

    /// <summary>Meshes drawn by the last <see cref="Draw"/> calls since <see cref="ResetCount"/>.</summary>
    public int Count { get; private set; }

    public void ResetCount() => Count = 0;

    /// <summary>
    /// The caster shader for GPU-skinned items (<see cref="MeshDrawItem.Joints"/>), <see cref="MaterialShaders.ShadowCasterSkinned"/>.
    /// Without one they cast their bind pose.
    /// </summary>
    public Shader? SkinnedCasterShader { get; set; }

    /// <summary>Draws the casters inside <paramref name="viewProjection"/> into the current target and viewport.</summary>
    public void Draw(Matrix4x4 viewProjection, IReadOnlyList<MeshDrawItem> items, Shader casterShader)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(casterShader);
        var frustum = Frustum.FromViewProjection(viewProjection);
        foreach (var item in items)
        {
            var source = item.Material;
            if (source.IsTransparent || !source.DepthWrite || !frustum.Intersects(item.CullBounds))
            {
                continue;
            }

            var skinned = !item.Joints.IsEmpty && SkinnedCasterShader is not null;
            var caster = skinned ? SkinnedCasterFor(source, item.Texture) : CasterFor(source, casterShader, item.Texture);
            var sub = item.Mesh.SubMeshes[item.SubMesh];
            Count++;
            device.Draw(new DrawCommand(caster, item.Texture, item.Mesh.Vertices, item.Mesh.Indices, sub.IndexStart, sub.IndexCount, viewProjection,
                State: new RenderState(true, true, item.Cull ?? source.Cull), World: item.World, Joints: skinned ? item.Joints : default));
        }
    }

    private readonly Dictionary<Material, Material> _skinnedCasters = new(ReferenceEqualityComparer.Instance);

    private Material SkinnedCasterFor(Material source, Texture2D white)
    {
        var shader = SkinnedCasterShader!;
        if (!_skinnedCasters.TryGetValue(source, out var caster) || !ReferenceEquals(caster.Shader, shader))
        {
            caster = new Material(shader, BlendMode.Opaque, source.Name + " (skinned shadow caster)");
            _skinnedCasters[source] = caster;
        }

        return Mirror(caster, source, white);
    }

    private Material CasterFor(Material source, Shader casterShader, Texture2D white)
    {
        if (!_casters.TryGetValue(source, out var caster) || !ReferenceEquals(caster.Shader, casterShader))
        {
            caster = new Material(casterShader, BlendMode.Opaque, source.Name + " (shadow caster)");
            _casters[source] = caster;
        }

        return Mirror(caster, source, white);
    }

    /// <summary>Mirrored every draw: the source material may change (inspector edits, scripts).</summary>
    private static Material Mirror(Material caster, Material source, Texture2D white)
    {
        caster.SetTexture("_BaseColorMap", source.GetTexture("_BaseColorMap") ?? white);
        caster.SetVector("_BaseColor", source.TryGetUniform("_BaseColor", out var color) ? color.Vector : new Vector4(1f, 1f, 1f, 1f));
        caster.SetFloat("_AlphaCutoff", source.TryGetUniform("_AlphaCutoff", out var cutoff) ? cutoff.Vector.X : 0f);
        caster.SetFloat("_BaseColorUVSet", source.TryGetUniform("_BaseColorUVSet", out var uvSet) ? uvSet.Vector.X : 0f);
        return caster;
    }
}

/// <summary>
/// A directional or spot light shadow map: depth from the light, packed into an RGBA8 render target (8 bits per channel,
/// 32 in all) so it works on every render target format; sampled with point filtering and 3×3 percentage-closer
/// filtering in the Standard shader.
/// <list type="bullet">
/// <item>Directional: an orthographic view enclosing the camera frustum up to the light's shadow distance (a bounding
/// sphere, so it does not change size as the camera turns), moved in whole texels so edges do not shimmer.</item>
/// <item>Spot: the light's cone as a perspective view, out to its range.</item>
/// </list>
/// </summary>
public sealed class ShadowMap(IGraphicsDevice device, int size = 2048) : IDisposable
{
    private static readonly Color Far = Color.White; // packs depth >= 1: nothing in front
    private readonly ShadowCasters _casters = new(device);
    private RenderTarget2D? _target;

    public int Size { get; } = size;

    /// <inheritdoc cref="ShadowCasters.SkinnedCasterShader"/>
    public Shader? SkinnedCasterShader { get => _casters.SkinnedCasterShader; set => _casters.SkinnedCasterShader = value; }

    /// <summary>Meshes drawn into the shadow map by the last render.</summary>
    public int CasterCount => _casters.Count;

    /// <summary>Renders the casters for a directional light and returns the state the main pass needs.</summary>
    public ShadowState Render(in LightData light, int lightIndex, Matrix4x4 cameraViewProjection, float cameraNear, float cameraFar, IReadOnlyList<MeshDrawItem> items, Shader casterShader)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(casterShader);
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
        var viewProjection = Matrix4x4.OrthoZeroToOne(-radius, radius, -radius, radius, 0f, radius * 3f) * Matrix4x4.LookAt(eye, center, up);
        Draw(viewProjection, items, casterShader);

        // Depth bias grows with the texel size: big shadow maps over large areas need more.
        return new ShadowState(lightIndex, viewProjection, _target, MathF.Max(0.0005f, texel / (radius * 3f) * 1.5f));
    }

    /// <summary>Renders the casters for a spot light (its cone, out to its range).</summary>
    public ShadowState RenderSpot(in LightData light, int lightIndex, IReadOnlyList<MeshDrawItem> items, Shader casterShader)
    {
        ArgumentNullException.ThrowIfNull(items);
        var direction = light.Direction.Normalized;
        var up = MathF.Abs(Vector3.Dot(direction, Vector3.Up)) > 0.99f ? Vector3.Forward : Vector3.Up;
        var near = MathF.Max(0.05f, light.Range * 0.002f);
        var viewProjection = Matrix4x4.PerspectiveZeroToOne(MathF.Min(light.SpotAngle + 2f, 175f), 1f, near, light.Range)
            * Matrix4x4.LookAt(light.Position, light.Position + direction, up);
        Draw(viewProjection, items, casterShader);
        return new ShadowState(lightIndex, viewProjection, _target, 0.0002f);
    }

    private void Draw(Matrix4x4 viewProjection, IReadOnlyList<MeshDrawItem> items, Shader casterShader)
    {
        _target ??= device.CreateRenderTarget(Size, Size, SamplerState.PointClamp, "Shadow map", depth: true);
        device.SetRenderTarget(_target);
        device.Clear(Far);
        device.ClearDepth();
        _casters.ResetCount();
        _casters.Draw(viewProjection, items, casterShader);
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

/// <summary>
/// The point light shadow: six 90-degree views (+X, -X, +Y, -Y, +Z, -Z) out to the light's range, rendered into one
/// 3 × 2 atlas (faces left to right, top row first) and sampled by face in the Standard shader.
/// </summary>
public sealed class PointShadowMap(IGraphicsDevice device, int faceSize = 512) : IDisposable
{
    private static readonly (Vector3 Direction, Vector3 Up)[] Faces =
    [
        (Vector3.UnitX, Vector3.Up), (-Vector3.UnitX, Vector3.Up), (Vector3.UnitY, Vector3.Forward),
        (-Vector3.UnitY, -Vector3.Forward), (Vector3.UnitZ, Vector3.Up), (-Vector3.UnitZ, Vector3.Up),
    ];

    private readonly ShadowCasters _casters = new(device);
    private RenderTarget2D? _atlas;

    public int FaceSize { get; } = faceSize;

    /// <inheritdoc cref="ShadowCasters.SkinnedCasterShader"/>
    public Shader? SkinnedCasterShader { get => _casters.SkinnedCasterShader; set => _casters.SkinnedCasterShader = value; }

    public int CasterCount => _casters.Count;

    public PointShadowState Render(in LightData light, int lightIndex, IReadOnlyList<MeshDrawItem> items, Shader casterShader)
    {
        ArgumentNullException.ThrowIfNull(items);
        _atlas ??= device.CreateRenderTarget(FaceSize * 3, FaceSize * 2, SamplerState.PointClamp, "Point shadow atlas", depth: true);
        device.SetRenderTarget(_atlas);
        device.Clear(Color.White);
        device.ClearDepth();
        _casters.ResetCount();
        var near = MathF.Max(0.05f, light.Range * 0.002f);
        var projection = Matrix4x4.PerspectiveZeroToOne(90f, 1f, near, light.Range);
        var matrices = new Matrix4x4[6];
        for (var face = 0; face < 6; face++)
        {
            var (direction, up) = Faces[face];
            matrices[face] = projection * Matrix4x4.LookAt(light.Position, light.Position + direction, up);
            // Viewport Y counts from the bottom (GL style): faces 0-2 go in the top row of the image, as the shader reads them.
            device.SetViewport((face % 3) * FaceSize, (1 - (face / 3)) * FaceSize, FaceSize, FaceSize);
            _casters.Draw(matrices[face], items, casterShader);
        }

        return new PointShadowState(lightIndex, matrices, _atlas, 0.0004f);
    }

    public void Dispose()
    {
        _atlas?.Dispose();
        _atlas = null;
    }
}
