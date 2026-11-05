using MNVTEngine.Graphics.Materials;
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
/// <param name="Joints">GPU skinning: the joint matrices of a mesh uploaded with <see cref="GpuMesh.UploadSkinned"/>, drawn with a skinned shader.</param>
/// <param name="WorldBounds">Bounds for culling when the mesh's own bounds moved by <see cref="World"/> do not hold (GPU-skinned meshes).</param>
public readonly record struct MeshDrawItem(GpuMesh Mesh, int SubMesh, Material Material, Texture2D Texture, Matrix4x4 World, CullMode? Cull = null, ReadOnlyMemory<Matrix4x4> Joints = default, Bounds? WorldBounds = null)
{
    /// <summary>Where the item is in world space, for culling.</summary>
    public Bounds CullBounds => WorldBounds ?? Renderer3D.TransformBounds(Mesh.Bounds, World);
}

/// <param name="MaterialSwitches">Draws that used a different material than the draw before (pipeline and descriptor changes).</param>
/// <param name="InstancedItems">Visible items drawn by instanced draws (they share <see cref="DrawCalls"/>).</param>
public readonly record struct Render3DStatistics(int Visible, int Culled, int DrawCalls, int Triangles, int MaterialSwitches = 0, int InstancedItems = 0);

/// <summary>
/// Draws meshes for one camera, independent of the graphics backend:
/// <list type="bullet">
/// <item>frustum-culls each item by its world-space bounds;</item>
/// <item>draws opaque materials (render queue below 3000) by queue, then front to back in distance bands, and within a
/// band by material and mesh, so draws that share state follow each other (fewer pipeline and descriptor changes);</item>
/// <item>then transparent materials by queue, then back to front.</item>
/// </list>
/// With <see cref="InstancedVariant"/>, opaque items that share a mesh, submesh, material and culling are drawn with
/// instanced draws (up to <see cref="MaterialShaders.MaxInstances"/> per draw), placed where the nearest of them is.
/// Materials whose shader reads the lighting uniforms (the Standard shader) get the pass's lights and shadow map
/// (<see cref="LightingUniforms"/>); without lighting they show unlit. The caller sets the target (with a depth buffer)
/// and clears it.
/// </summary>
public sealed class Renderer3D(IGraphicsDevice device)
{
    private readonly List<(MeshDrawItem Item, float Distance)> _opaque = [];
    private readonly List<(MeshDrawItem Item, float Distance)> _transparent = [];
    private readonly HashSet<Material> _lit = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(GpuMesh, int, Material, CullMode?), List<Matrix4x4>> _groups = [];
    private readonly HashSet<(GpuMesh, int, Material, CullMode?)> _drawnGroups = [];

    /// <summary>
    /// The instancing twin of a material (its values on the instanced variant of its shader, copied after lighting), or
    /// null when it has none: those items draw one by one. Null (the default) turns instancing off.
    /// </summary>
    public Func<Material, Material?>? InstancedVariant { get; set; }

    /// <summary>Items that share mesh and material are instanced from this many on.</summary>
    public int InstancingThreshold { get; set; } = 2;

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
            var bounds = item.CullBounds;
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
        var draws = 0;
        var instanced = 0;
        Material? previous = null;
        GroupInstances();
        foreach (var (item, _) in _opaque.Concat(_transparent))
        {
            var key = (item.Mesh, item.SubMesh, item.Material, item.Cull);
            if (_groups.TryGetValue(key, out var worlds))
            {
                if (!_drawnGroups.Add(key))
                {
                    continue; // drawn with its group
                }

                if (InstancedVariant!(item.Material) is { } twin)
                {
                    switches += ReferenceEquals(twin, previous) ? 0 : 1;
                    previous = twin;
                    for (var start = 0; start < worlds.Count; start += MaterialShaders.MaxInstances)
                    {
                        var batch = worlds.GetRange(start, Math.Min(MaterialShaders.MaxInstances, worlds.Count - start)).ToArray();
                        triangles += Draw(item with { Material = twin }, viewProjection, batch);
                        draws++;
                    }

                    instanced += worlds.Count;
                    continue;
                }

                // No twin: drawn one by one below, with the other members as they come.
                _groups.Remove(key);
            }

            switches += ReferenceEquals(item.Material, previous) ? 0 : 1;
            previous = item.Material;
            triangles += Draw(item, viewProjection);
            draws++;
        }

        return new Render3DStatistics(_opaque.Count + _transparent.Count, culled, draws, triangles, switches, instanced);
    }

    /// <summary>Opaque items that can share an instanced draw: the same mesh, submesh, material and culling, not GPU-skinned.</summary>
    private void GroupInstances()
    {
        _groups.Clear();
        _drawnGroups.Clear();
        if (InstancedVariant is null)
        {
            return;
        }

        foreach (var (item, _) in _opaque)
        {
            if (item.Joints.IsEmpty)
            {
                var key = (item.Mesh, item.SubMesh, item.Material, item.Cull);
                if (!_groups.TryGetValue(key, out var worlds))
                {
                    worlds = [];
                    _groups[key] = worlds;
                }

                worlds.Add(item.World);
            }
        }

        foreach (var key in _groups.Where(g => g.Value.Count < Math.Max(2, InstancingThreshold)).Select(g => g.Key).ToList())
        {
            _groups.Remove(key);
        }
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

    private int Draw(in MeshDrawItem item, Matrix4x4 viewProjection, Matrix4x4[]? instances = null)
    {
        var material = item.Material;
        var state = new RenderState(material.DepthTest, material.DepthWrite, item.Cull ?? material.Cull);
        var mesh = item.Mesh;
        if ((uint)item.SubMesh >= (uint)mesh.SubMeshes.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(item), $"Mesh '{mesh.Name}' has {mesh.SubMeshes.Count} submeshes; {item.SubMesh} was requested.");
        }

        var sub = mesh.SubMeshes[item.SubMesh];
        device.Draw(new DrawCommand(item.Material, item.Texture, mesh.Vertices, mesh.Indices, sub.IndexStart, sub.IndexCount, viewProjection, State: state, World: item.World, Joints: item.Joints, Instances: instances));
        return sub.IndexCount / 3 * (instances?.Length ?? 1);
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
