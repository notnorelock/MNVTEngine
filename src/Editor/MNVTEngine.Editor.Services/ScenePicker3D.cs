using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.Services;

/// <summary>
/// What a click in the 3D Scene view selects: the nearest triangle of a mesh renderer along the picking ray (tested in
/// the mesh's local space; skinned meshes by their bind pose), or a light, camera or empty GameObject whose icon is
/// within a few pixels of the ray. UI-free.
/// </summary>
public static class ScenePicker3D
{
    /// <summary>Icon pick radius in pixels.</summary>
    public const float IconRadius = 12f;

    /// <param name="meshOf">The CPU mesh a renderer draws (primitive or model mesh), or null.</param>
    /// <param name="unitsPerPixel">World units per pixel at a point (icon sizes are in pixels).</param>
    public static GameObject? Pick(Scene scene, Ray ray, Func<MeshRenderer, Mesh?> meshOf, Func<Vector3, float> unitsPerPixel)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(meshOf);
        ArgumentNullException.ThrowIfNull(unitsPerPixel);
        GameObject? best = null;
        var bestDistance = float.MaxValue;
        foreach (var renderer in scene.FindObjectsOfType<MeshRenderer>())
        {
            if (!renderer.GameObject.ActiveInHierarchy || meshOf(renderer) is not { } mesh)
            {
                continue;
            }

            if (Intersect(ray, mesh, renderer.Transform.LocalToWorldMatrix) is { } distance && distance < bestDistance)
            {
                (best, bestDistance) = (renderer.GameObject, distance);
            }
        }

        // Terrain worlds: their element surfaces (clicking anywhere on one selects the whole terrain object).
        foreach (var terrain in scene.FindObjectsOfType<WorldTerrain>())
        {
            if (terrain.GameObject.ActiveInHierarchy && terrain.Raycast(ray) is { } distance && distance < bestDistance)
            {
                (best, bestDistance) = (terrain.GameObject, distance);
            }
        }

        // Objects drawn as icons: lights, cameras, and anything else without a mesh.
        foreach (var gameObject in scene.GameObjects)
        {
            if (!gameObject.ActiveInHierarchy || gameObject.GetComponent<MeshRenderer>() is not null || gameObject.GetComponent<WorldTerrain>() is not null || !HasIcon(gameObject))
            {
                continue;
            }

            var center = gameObject.Transform.Position;
            var along = Vector3.Dot(center - ray.Origin, ray.Direction);
            if (along <= 0f || along >= bestDistance)
            {
                continue;
            }

            var miss = Vector3.Distance(ray.GetPoint(along), center);
            if (miss <= IconRadius * unitsPerPixel(center))
            {
                (best, bestDistance) = (gameObject, along);
            }
        }

        return best;
    }

    /// <summary>Lights and cameras show icons in the 3D view (and can be clicked).</summary>
    public static bool HasIcon(GameObject gameObject) =>
        gameObject.GetComponent<Light>() is not null || gameObject.GetComponent<AmbientLight>() is not null || gameObject.GetComponent<Camera>() is not null;

    /// <summary>Distance along the ray to the nearest triangle of a mesh drawn with <paramref name="world"/>, or null.</summary>
    public static float? Intersect(Ray ray, Mesh mesh, Matrix4x4 world)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!Matrix4x4.TryInvert(world, out var toLocal))
        {
            return null;
        }

        // Test in local space: transform the ray, then convert hit distances back to world units.
        var origin = toLocal.MultiplyPoint3x4(ray.Origin);
        var far = toLocal.MultiplyPoint3x4(ray.GetPoint(1f));
        var local = new Ray(origin, far - origin);
        var scale = (far - origin).Magnitude; // local units per world unit along the ray
        if (!mesh.Bounds.IntersectRay(local, out _) && !Contains(mesh.Bounds, origin))
        {
            return null;
        }

        var positions = mesh.Positions;
        var indices = mesh.IndexSpan;
        float? nearest = null;
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            if (local.IntersectTriangle(positions[(int)indices[i]], positions[(int)indices[i + 1]], positions[(int)indices[i + 2]], out var t) && t >= 0f && (nearest is null || t < nearest))
            {
                nearest = t;
            }
        }

        return nearest / scale;
    }

    private static bool Contains(Bounds bounds, Vector3 point) => bounds.ClosestPoint(point) == point;
}
