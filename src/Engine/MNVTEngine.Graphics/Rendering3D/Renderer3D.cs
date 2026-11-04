using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Rendering3D;

/// <summary>Camera data for one 3D pass (Vulkan depth range; see <c>Matrix4x4.PerspectiveZeroToOne</c>).</summary>
public readonly record struct View3D(Matrix4x4 View, Matrix4x4 Projection, Vector3 Position)
{
    public Matrix4x4 ViewProjection => Projection * View;
}

/// <summary>One submesh to draw with a material.</summary>
/// <param name="Transparent">Drawn after opaque geometry, back to front, without writing depth.</param>
public readonly record struct MeshDrawItem(GpuMesh Mesh, int SubMesh, Material Material, Texture2D Texture, Matrix4x4 World, bool Transparent = false, CullMode Cull = CullMode.Back);

public readonly record struct Render3DStatistics(int Visible, int Culled, int DrawCalls, int Triangles);

/// <summary>
/// Draws meshes for one camera, independent of the graphics backend:
/// <list type="bullet">
/// <item>frustum-culls each item by its world-space bounds;</item>
/// <item>draws opaque items front to back (grouped by material within similar distances), depth-tested and written;</item>
/// <item>then draws transparent items back to front, depth-tested but not written.</item>
/// </list>
/// The caller sets the target (with a depth buffer) and clears it.
/// </summary>
public sealed class Renderer3D(IGraphicsDevice device)
{
    private readonly List<(MeshDrawItem Item, float Distance)> _opaque = [];
    private readonly List<(MeshDrawItem Item, float Distance)> _transparent = [];

    public Render3DStatistics Render(in View3D view, IReadOnlyList<MeshDrawItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var viewProjection = view.ViewProjection;
        var frustum = Frustum.FromViewProjection(viewProjection);
        _opaque.Clear();
        _transparent.Clear();
        var culled = 0;
        foreach (var item in items)
        {
            var bounds = TransformBounds(item.Mesh.Bounds, item.World);
            if (!frustum.Intersects(bounds))
            {
                culled++;
                continue;
            }

            var distance = Vector3.DistanceSquared(bounds.Center, view.Position);
            (item.Transparent ? _transparent : _opaque).Add((item, distance));
        }

        // Front to back saves fragment work; ties (same distance band) keep materials together.
        _opaque.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : MaterialOrder(a.Item.Material).CompareTo(MaterialOrder(b.Item.Material)));
        _transparent.Sort((a, b) => b.Distance.CompareTo(a.Distance));
        var triangles = 0;
        foreach (var (item, _) in _opaque)
        {
            triangles += Draw(item, viewProjection, RenderState.Opaque3D with { Cull = item.Cull });
        }

        foreach (var (item, _) in _transparent)
        {
            triangles += Draw(item, viewProjection, RenderState.Transparent3D with { Cull = item.Cull });
        }

        return new Render3DStatistics(_opaque.Count + _transparent.Count, culled, _opaque.Count + _transparent.Count, triangles);
    }

    /// <summary>World-space box around a transformed box (its eight corners).</summary>
    public static Bounds TransformBounds(Bounds local, Matrix4x4 world)
    {
        var center = world.MultiplyPoint3x4(local.Center);
        var e = local.Extents;
        // Each world axis extent is the sum of the absolute contributions of the local extents (Arvo's method).
        var extents = new Vector3(
            (MathF.Abs(world.M00) * e.X) + (MathF.Abs(world.M01) * e.Y) + (MathF.Abs(world.M02) * e.Z),
            (MathF.Abs(world.M10) * e.X) + (MathF.Abs(world.M11) * e.Y) + (MathF.Abs(world.M12) * e.Z),
            (MathF.Abs(world.M20) * e.X) + (MathF.Abs(world.M21) * e.Y) + (MathF.Abs(world.M22) * e.Z));
        return new Bounds(center, extents * 2f);
    }

    private int Draw(in MeshDrawItem item, Matrix4x4 viewProjection, RenderState state)
    {
        var mesh = item.Mesh;
        if ((uint)item.SubMesh >= (uint)mesh.SubMeshes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(item), $"Mesh '{mesh.Name}' has {mesh.SubMeshes.Count} submeshes; {item.SubMesh} was requested.");
        }

        var sub = mesh.SubMeshes[item.SubMesh];
        device.Draw(new DrawCommand(item.Material, item.Texture, mesh.Vertices, mesh.Indices, sub.IndexStart, sub.IndexCount, viewProjection, State: state, World: item.World));
        return sub.IndexCount / 3;
    }

    private static int MaterialOrder(Material material) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(material);
}
