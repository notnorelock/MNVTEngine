using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Graphics.Rendering3D;

/// <summary>Camera data for one 3D pass (Vulkan depth range; see <c>Matrix4x4.PerspectiveZeroToOne</c>).</summary>
public readonly record struct View3D(Matrix4x4 View, Matrix4x4 Projection, Vector3 Position)
{
    public Matrix4x4 ViewProjection => Projection * View;
}

/// <summary>
/// One submesh to draw with a material. The material decides depth testing and writing, face culling, the render
/// queue and transparency.
/// </summary>
/// <param name="Texture">The draw's <c>uTexture</c> (custom shaders); built-in shaders take their maps from the material.</param>
/// <param name="Cull">Overrides the material's culling (e.g. a double-sided renderer).</param>
public readonly record struct MeshDrawItem(GpuMesh Mesh, int SubMesh, Material Material, Texture2D Texture, Matrix4x4 World, CullMode? Cull = null);

/// <param name="MaterialSwitches">Draws that used a different material than the draw before (pipeline and descriptor changes).</param>
public readonly record struct Render3DStatistics(int Visible, int Culled, int DrawCalls, int Triangles, int MaterialSwitches = 0);

/// <summary>
/// Draws meshes for one camera, independent of the graphics backend:
/// <list type="bullet">
/// <item>frustum-culls each item by its world-space bounds;</item>
/// <item>draws opaque materials (render queue below 3000) by queue, then front to back in distance bands, and within a
/// band by material and mesh, so draws that share state follow each other (fewer pipeline and descriptor changes);</item>
/// <item>then transparent materials by queue, then back to front.</item>
/// </list>
/// Materials whose shader reads the lighting uniforms (the Standard shader) get the pass's lights and shadow map
/// (<see cref="LightingUniforms"/>); without lighting they show unlit. The caller sets the target (with a depth buffer)
/// and clears it.
/// </summary>
public sealed class Renderer3D(IGraphicsDevice device)
{
    private readonly List<(MeshDrawItem Item, float Distance)> _opaque = [];
    private readonly List<(MeshDrawItem Item, float Distance)> _transparent = [];
    private readonly HashSet<Material> _lit = new(ReferenceEqualityComparer.Instance);

    /// <param name="lighting">The pass's lights; null draws lit materials unlit (<see cref="SceneLighting.Unlit"/>).</param>
    /// <param name="shadows">The shadow maps rendered for this pass, if any.</param>
    public Render3DStatistics Render(in View3D view, IReadOnlyList<MeshDrawItem> items, SceneLighting? lighting = null, ShadowSet? shadows = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ApplyLighting(items, lighting ?? SceneLighting.Unlit, view.Position, shadows ?? ShadowSet.None);
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
            (item.Material.IsTransparent ? _transparent : _opaque).Add((item, distance));
        }

        // Front to back saves fragment work; within a distance band (a factor of about 1.4 in distance) draws with the
        // same material and mesh are grouped, which saves state changes without losing much early depth rejection.
        _opaque.Sort((a, b) => a.Item.Material.RenderQueue != b.Item.Material.RenderQueue ? a.Item.Material.RenderQueue.CompareTo(b.Item.Material.RenderQueue)
            : Band(a.Distance) != Band(b.Distance) ? Band(a.Distance).CompareTo(Band(b.Distance))
            : MaterialOrder(a.Item.Material) != MaterialOrder(b.Item.Material) ? MaterialOrder(a.Item.Material).CompareTo(MaterialOrder(b.Item.Material))
            : MeshOrder(a.Item.Mesh) != MeshOrder(b.Item.Mesh) ? MeshOrder(a.Item.Mesh).CompareTo(MeshOrder(b.Item.Mesh))
            : a.Distance.CompareTo(b.Distance));
        _transparent.Sort((a, b) => a.Item.Material.RenderQueue != b.Item.Material.RenderQueue ? a.Item.Material.RenderQueue.CompareTo(b.Item.Material.RenderQueue) : b.Distance.CompareTo(a.Distance));
        var triangles = 0;
        var switches = 0;
        Material? previous = null;
        foreach (var (item, _) in _opaque.Concat(_transparent))
        {
            switches += ReferenceEquals(item.Material, previous) ? 0 : 1;
            previous = item.Material;
            triangles += Draw(item, viewProjection);
        }

        return new Render3DStatistics(_opaque.Count + _transparent.Count, culled, _opaque.Count + _transparent.Count, triangles, switches);
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

    private int Draw(in MeshDrawItem item, Matrix4x4 viewProjection)
    {
        var material = item.Material;
        var state = new RenderState(material.DepthTest, material.DepthWrite, item.Cull ?? material.Cull);
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

    private static int MeshOrder(GpuMesh mesh) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(mesh);

    /// <summary>Distance bands (the distances are squared): each band spans about 1.4 times the distance of the previous.</summary>
    private static int Band(float squaredDistance) => (int)MathF.Log2(1f + squaredDistance);

    /// <summary>Sets the lighting uniforms once per lit material (uniforms are copied at each draw, so passes do not interfere).</summary>
    private void ApplyLighting(IReadOnlyList<MeshDrawItem> items, SceneLighting lighting, Vector3 camera, ShadowSet shadows)
    {
        _lit.Clear();
        foreach (var item in items)
        {
            // The draw's texture stands in for the shadow map when there is none: the sampler must be bound, and is not read.
            if (_lit.Add(item.Material) && LightingUniforms.Uses(item.Material))
            {
                LightingUniforms.Apply(item.Material, lighting, camera, shadows, item.Texture);
            }
        }
    }
}
