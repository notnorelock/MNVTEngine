using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Graphics.Rendering3D;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Runtime;

/// <summary>
/// The 3D pass of <see cref="SceneRenderer"/>: every active <see cref="MeshRenderer"/> seen through the scene's
/// camera, drawn with depth before sprites. Primitive meshes are generated once and uploaded once per renderer.
/// </summary>
public sealed partial class SceneRenderer
{
    private static readonly Lock PrimitivesLock = new();
    private static readonly Dictionary<PrimitiveMesh, Mesh> Primitives = [];
    private readonly List<MeshDrawItem> _items = [];
    private readonly ConditionalWeakTable<MeshRenderer, Material> _rendererMaterials = [];
    private Renderer3D? _renderer3D;
    private GpuMeshCache? _meshes;
    private Shader? _unlit;
    private Texture2D? _white;
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
            if (meshRenderer.IsActiveAndEnabled && meshRenderer.Primitive != PrimitiveMesh.None)
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

    private MeshDrawItem Item(MeshRenderer meshRenderer)
    {
        var device = renderer.Device;
        _meshes ??= new GpuMeshCache(device);
        _white ??= device.CreateTexture(1, 1, [255, 255, 255, 255], SamplerState.PointClamp, "Mesh white");
        var mesh = _meshes.Get(GetPrimitive(meshRenderer.Primitive));
        var texture = meshRenderer.Texture.IsNone ? _white : resources.GetTexture(meshRenderer.Texture);
        var cull = meshRenderer.DoubleSided ? CullMode.None : CullMode.Back;
        var world = meshRenderer.Transform.LocalToWorldMatrix;
        if (resources.GetMaterial(meshRenderer.Material) is { } custom)
        {
            return new MeshDrawItem(mesh, 0, custom, texture, world, custom.BlendMode != BlendMode.Opaque, cull);
        }

        // The built-in material: one per renderer, because the color is a uniform.
        _unlit ??= device.CreateShader(BuiltinShaders.Unlit3D);
        var material = _rendererMaterials.GetValue(meshRenderer, _ => new Material(_unlit, BlendMode.Opaque, "Unlit3D"));
        var transparent = meshRenderer.Color.A < 1f;
        material.BlendMode = transparent ? BlendMode.Alpha : BlendMode.Opaque;
        material.SetColor("uColor", meshRenderer.Color);
        return new MeshDrawItem(mesh, 0, material, texture, world, transparent, cull);
    }

    private void Dispose3D()
    {
        _meshes?.Dispose();
        _meshes = null;
        _unlit?.Dispose();
        _unlit = null;
        _white?.Dispose();
        _white = null;
    }

    [LoggerMessage(EventId = 2104, Level = LogLevel.Warning, Message = "Render target '{Target}' has no depth buffer: 3D meshes are not drawn into it")]
    private static partial void LogNoDepth(ILogger logger, string target);
}
