using MNVTEngine.Browser;
using MNVTEngine.Mathematics;

namespace MNVTEngine;

/// <summary>
/// A browser page on this GameObject's <see cref="MeshRenderer"/> (its base color texture). The mouse ray from the main
/// camera is intersected with the mesh's triangles; the hit's interpolated UV is the page pixel, so any UV-mapped mesh
/// works (a quad, a curved screen, a model's display). Objects in front of the surface do not block the pointer.
/// </summary>
public sealed class BrowserSurface3D : BrowserSurface
{
    private MeshData? _mesh;

    /// <summary>Accept hits on the back of the triangles too.</summary>
    [SerializeField]
    public bool DoubleSided { get; set; }

    /// <summary>
    /// Shown as a lit-independent display (the page is the emissive map, the base color black), like a real screen.
    /// Off: the page is the base color texture and scene lighting shades it.
    /// </summary>
    [SerializeField]
    public bool Emissive { get; set; } = true;

    /// <summary>Emissive brightness (1 shows the page's own colors).</summary>
    [SerializeField]
    public float Brightness { get; set; } = 1f;

    /// <summary>Built-in primitives have their UV origin at the bottom left; models (glTF) at the top left.</summary>
    private bool BottomUpUVs => GameObject.GetComponent<MeshRenderer>() is { } renderer && renderer.Mesh.IsNone;

    protected override void Configure(BrowserInstance browser) => browser.FlipVertically = BottomUpUVs;

    protected override void ShowTexture(AssetRef texture)
    {
        if (GameObject.GetComponent<MeshRenderer>() is not { } renderer)
        {
            Log.Warn("BrowserSurface3D needs a MeshRenderer on the same GameObject.");
            return;
        }

        if (texture.IsNone)
        {
            renderer.ClearMaterialOverride("_BaseColor");
            renderer.ClearMaterialOverride("_EmissiveMap");
            renderer.ClearMaterialOverride("_EmissiveColor");
            renderer.Texture = default;
        }
        else if (Emissive)
        {
            renderer.SetMaterialColor("_BaseColor", Color.Black);
            renderer.SetMaterialTexture("_EmissiveMap", texture);
            renderer.SetMaterialColor("_EmissiveColor", new Color(Brightness, Brightness, Brightness, 1f));
        }
        else
        {
            renderer.Texture = texture;
        }
    }

    protected override Vector2? PageUV(Vector2 screen)
    {
        if (GameObject.GetComponent<MeshRenderer>() is not { } renderer || GameObject.Scene.MainCamera is not { } camera)
        {
            return null;
        }

        _mesh ??= GameObject.Scene.MeshData?.GetMeshData(renderer);
        if (_mesh is null)
        {
            return null;
        }

        var ray = camera.ScreenPointToRay(screen);
        var toLocal = Transform.WorldToLocalMatrix;
        var origin = toLocal.MultiplyPoint3x4(ray.Origin);
        var direction = toLocal.MultiplyPoint3x4(ray.Origin + ray.Direction) - origin;
        return MeshRaycast.HitUV(_mesh, origin, direction, DoubleSided)?.UV is { } uv
            ? new Vector2(uv.X, Browser?.FlipVertically == true ? 1f - uv.Y : uv.Y)
            : null;
    }
}

/// <summary>Ray and triangle mesh intersection with texture coordinates (Möller–Trumbore).</summary>
public static class MeshRaycast
{
    /// <summary>
    /// The nearest triangle hit along the ray (in the mesh's space; <paramref name="direction"/> need not be
    /// normalized): its distance in units of <paramref name="direction"/> and its interpolated UV. Null without a hit
    /// or without texture coordinates.
    /// </summary>
    public static (float Distance, Vector2 UV)? HitUV(MeshData mesh, Vector3 origin, Vector3 direction, bool doubleSided = false)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.TexCoords is not { } uvs || uvs.Length < mesh.Positions.Length)
        {
            return null;
        }

        (float Distance, Vector2 UV)? best = null;
        var positions = mesh.Positions;
        var indices = mesh.Indices;
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            var e1 = positions[b] - positions[a];
            var e2 = positions[c] - positions[a];
            var p = Vector3.Cross(direction, e2);
            var det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-9f || (!doubleSided && det < 0f))
            {
                continue;
            }

            var inverse = 1f / det;
            var t0 = origin - positions[a];
            var u = Vector3.Dot(t0, p) * inverse;
            if (u < 0f || u > 1f)
            {
                continue;
            }

            var q = Vector3.Cross(t0, e1);
            var v = Vector3.Dot(direction, q) * inverse;
            if (v < 0f || u + v > 1f)
            {
                continue;
            }

            var t = Vector3.Dot(e2, q) * inverse;
            if (t < 0f || (best is { } current && t >= current.Distance))
            {
                continue;
            }

            best = (t, (uvs[a] * (1f - u - v)) + (uvs[b] * u) + (uvs[c] * v));
        }

        return best;
    }
}

/// <summary>The "browser.3d" engine module: <see cref="BrowserSurface3D"/>.</summary>
public sealed class Browser3DModule : Scenes.IEngineModule
{
    public string Id => "browser.3d";

    public void Register(Scenes.ComponentRegistry components)
    {
        ArgumentNullException.ThrowIfNull(components);
        components.Register<BrowserSurface3D>();
    }
}
