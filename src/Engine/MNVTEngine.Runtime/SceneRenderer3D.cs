using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Graphics.Materials;
using MNVTEngine.Graphics.Rendering3D;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Runtime;

/// <summary>
/// The 3D pass of <see cref="SceneRenderer"/>: every active <see cref="MeshRenderer"/> seen through the scene's
/// camera, drawn with depth before sprites. Primitive meshes are generated once and uploaded once per renderer; model
/// meshes are uploaded on first use and released when their model is reimported.
/// </summary>
public sealed partial class SceneRenderer
{
    private static readonly Lock PrimitivesLock = new();
    private static readonly Dictionary<PrimitiveMesh, Mesh> Primitives = [];
    private readonly List<MeshDrawItem> _items = [];
    private readonly ConditionalWeakTable<MeshRenderer, Material> _rendererMaterials = [];
    private Renderer3D? _renderer3D;
    private GpuMeshCache? _meshes;
    private bool _warnedNoDepth;

    /// <summary>Mesh renderers drawn by the last 3D pass.</summary>
    public int MeshesDrawn { get; private set; }

    /// <summary>Mesh renderers outside the camera's view in the last 3D pass.</summary>
    public int MeshesCulled { get; private set; }

    /// <summary>Triangles drawn by the last 3D pass.</summary>
    public int MeshTriangles { get; private set; }

    /// <summary>GPU memory of the uploaded meshes.</summary>
    public long MeshBytes => _meshes?.SizeInBytes ?? 0;

    /// <summary>The shared CPU mesh of a primitive (generated on first use).</summary>
    public static Mesh GetPrimitive(PrimitiveMesh primitive)
    {
        lock (PrimitivesLock)
        {
            if (!Primitives.TryGetValue(primitive, out var mesh))
            {
                mesh = primitive switch
                {
                    PrimitiveMesh.Cube => MeshPrimitives.Cube(),
                    PrimitiveMesh.Sphere => MeshPrimitives.Sphere(),
                    PrimitiveMesh.Capsule => MeshPrimitives.Capsule(),
                    PrimitiveMesh.Cylinder => MeshPrimitives.Cylinder(),
                    PrimitiveMesh.Cone => MeshPrimitives.Cone(),
                    PrimitiveMesh.Plane => MeshPrimitives.Plane(),
                    PrimitiveMesh.Quad => MeshPrimitives.Quad(),
                    _ => throw new ArgumentOutOfRangeException(nameof(primitive), primitive, "Not a primitive mesh."),
                };
                Primitives.Add(primitive, mesh);
            }

            return mesh;
        }
    }

    private void Render3D(Scene scene, Camera camera, RenderTarget2D? target)
    {
        MeshesDrawn = MeshesCulled = MeshTriangles = 0;
        _items.Clear();
        foreach (var meshRenderer in scene.World.Pool<MeshRenderer>().Components)
        {
            if (meshRenderer.IsActiveAndEnabled && !meshRenderer.Mesh.IsNone)
            {
                AddModelItems(meshRenderer);
            }
            else if (meshRenderer.IsActiveAndEnabled && meshRenderer.Primitive != PrimitiveMesh.None)
            {
                _items.Add(Item(meshRenderer));
            }
        }

        if (_items.Count == 0)
        {
            return;
        }

        if (target is { HasDepth: false })
        {
            if (!_warnedNoDepth)
            {
                LogNoDepth(logger, target.DebugName);
                _warnedNoDepth = true;
            }

            return;
        }

        using var sample = Core.Diagnostics.Profiler.Sample("Render.Meshes");
        var device = renderer.Device;
        device.ClearDepth();
        _renderer3D ??= new Renderer3D(device);
        var stats = _renderer3D.Render(new View3D(camera.ViewMatrix, camera.ProjectionMatrix, camera.Transform.Position), _items);
        (MeshesDrawn, MeshesCulled, MeshTriangles) = (stats.Visible, stats.Culled, stats.Triangles);
        Core.Diagnostics.Profiler.Count("Meshes drawn", MeshesDrawn);
        Core.Diagnostics.Profiler.Count("Meshes culled", MeshesCulled);
    }

    /// <summary>One item per submesh of a model mesh, with the renderer's material or the model's material of each submesh.</summary>
    private void AddModelItems(MeshRenderer meshRenderer)
    {
        if (resources.GetModelMesh(meshRenderer.Mesh) is not { } modelMesh)
        {
            return;
        }

        var mesh = Meshes().Get(modelMesh.Mesh);
        var white = resources.GetDefaultTexture(DefaultTexture.White);
        CullMode? cull = meshRenderer.DoubleSided ? CullMode.None : null;
        var world = meshRenderer.Transform.LocalToWorldMatrix;
        var custom = resources.GetMaterial(meshRenderer.Material);
        for (var i = 0; i < mesh.SubMeshes.Count; i++)
        {
            var index = i < modelMesh.SubMeshMaterials.Count ? modelMesh.SubMeshMaterials[i] : -1;
            var material = custom ?? resources.GetModelMaterial(meshRenderer.Mesh.Guid, index) ?? BuiltinMaterial(meshRenderer);
            _items.Add(new MeshDrawItem(mesh, i, material, white, world, cull));
        }
    }

    private GpuMeshCache Meshes()
    {
        if (_meshes is null)
        {
            _meshes = new GpuMeshCache(renderer.Device);
            resources.MeshesEvicted += OnMeshesEvicted;
        }

        return _meshes;
    }

    private void OnMeshesEvicted(IReadOnlyCollection<Mesh> meshes)
    {
        foreach (var mesh in meshes)
        {
            _meshes?.Remove(mesh);
        }
    }

    private MeshDrawItem Item(MeshRenderer meshRenderer)
    {
        var mesh = Meshes().Get(GetPrimitive(meshRenderer.Primitive));
        var white = resources.GetDefaultTexture(DefaultTexture.White);
        CullMode? cull = meshRenderer.DoubleSided ? CullMode.None : null;
        var world = meshRenderer.Transform.LocalToWorldMatrix;
        return new MeshDrawItem(mesh, 0, resources.GetMaterial(meshRenderer.Material) ?? BuiltinMaterial(meshRenderer), white, world, cull);
    }

    /// <summary>The built-in material: Standard with the renderer's color and texture, one per renderer.</summary>
    private Material BuiltinMaterial(MeshRenderer meshRenderer)
    {
        var white = resources.GetDefaultTexture(DefaultTexture.White);
        var material = _rendererMaterials.GetValue(meshRenderer, _ => new Material(resources.GetBuiltinShader(Serialization.MaterialDefinition.BuiltinStandard), BlendMode.Opaque, "Default"));
        var transparent = meshRenderer.Color.A < 1f;
        material.BlendMode = transparent ? BlendMode.Alpha : BlendMode.Opaque;
        material.RenderQueue = transparent ? Material.TransparentQueue : Material.GeometryQueue;
        material.DepthWrite = !transparent;
        material.SetColor("_BaseColor", meshRenderer.Color);
        material.SetTexture("_BaseColorMap", meshRenderer.Texture.IsNone ? white : resources.GetTexture(meshRenderer.Texture));
        MaterialShaders.ApplyDefaults(material, MaterialShaders.StandardProperties, resources.GetDefaultTexture);
        return material;
    }

    private void Dispose3D()
    {
        if (_meshes is not null)
        {
            resources.MeshesEvicted -= OnMeshesEvicted;
        }

        _meshes?.Dispose();
        _meshes = null;
    }

    [LoggerMessage(EventId = 2104, Level = LogLevel.Warning, Message = "Render target '{Target}' has no depth buffer: 3D meshes are not drawn into it")]
    private static partial void LogNoDepth(ILogger logger, string target);
}
