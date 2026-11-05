using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using MNVTEngine.Assets.Models;
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
    private ShadowMap? _shadowMap;
    private readonly Dictionary<MeshRenderer, SkinState> _skins = new(ReferenceEqualityComparer.Instance);
    private int _frame;
    private bool _warnedNoDepth;
    private bool _warnedTooManyLights;

    /// <summary>Mesh renderers drawn by the last 3D pass.</summary>
    public int MeshesDrawn { get; private set; }

    /// <summary>Mesh renderers outside the camera's view in the last 3D pass.</summary>
    public int MeshesCulled { get; private set; }

    /// <summary>Triangles drawn by the last 3D pass.</summary>
    public int MeshTriangles { get; private set; }

    /// <summary>Lights used by the last 3D pass (at most <see cref="SceneLighting.MaxLights"/>).</summary>
    public int LightsUsed { get; private set; }

    /// <summary>Whether the last 3D pass rendered a directional shadow map.</summary>
    public bool ShadowsRendered { get; private set; }

    /// <summary>Material changes between consecutive mesh draws in the last 3D pass.</summary>
    public int MaterialSwitches { get; private set; }

    /// <summary>Skinned meshes deformed in the last 3D pass.</summary>
    public int SkinnedMeshes { get; private set; }

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
        _frame++;
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
        _renderer3D ??= new Renderer3D(device);
        var view = new View3D(camera.ViewMatrix, camera.ProjectionMatrix, camera.Transform.Position);
        var lighting = CollectLighting(scene, view.Position);
        ShadowState? shadow = null;
        if (lighting.Lights.Select((light, index) => (light, index)).FirstOrDefault(l => l.light.Kind == LightKind.Directional && l.light.Shadows) is { light.Shadows: true } caster)
        {
            using (Core.Diagnostics.Profiler.Sample("Render.Shadows"))
            {
                _shadowMap ??= new ShadowMap(device);
                shadow = _shadowMap.Render(caster.light, caster.index, view.ViewProjection, camera.NearClip, camera.FarClip, _items,
                    resources.GetBuiltinShader(MaterialShaders.ShadowCasterId));
                device.SetRenderTarget(target);
                Core.Diagnostics.Profiler.Count("Shadow casters", _shadowMap.CasterCount);
            }
        }

        PruneSkins();
        (LightsUsed, ShadowsRendered) = (lighting.Lights.Count, shadow is not null);
        device.ClearDepth();
        var stats = _renderer3D.Render(view, _items, lighting, shadow);
        (MeshesDrawn, MeshesCulled, MeshTriangles, MaterialSwitches, SkinnedMeshes) = (stats.Visible, stats.Culled, stats.Triangles, stats.MaterialSwitches, _skins.Count);
        Core.Diagnostics.Profiler.Count("Meshes drawn", MeshesDrawn);
        Core.Diagnostics.Profiler.Count("Meshes culled", MeshesCulled);
        Core.Diagnostics.Profiler.Count("Mesh triangles", MeshTriangles);
        Core.Diagnostics.Profiler.Count("Material switches", MaterialSwitches);
        Core.Diagnostics.Profiler.Count("Lights 3D", LightsUsed);
        Core.Diagnostics.Profiler.Count("Skinned meshes", SkinnedMeshes);
    }

    /// <summary>One item per submesh of a model mesh, with the renderer's material or the model's material of each submesh.</summary>
    private void AddModelItems(MeshRenderer meshRenderer)
    {
        if (resources.GetModelMesh(meshRenderer.Mesh) is not { } modelMesh)
        {
            return;
        }

        var (mesh, world) = modelMesh.Joints is not null && Skin(meshRenderer, modelMesh) is { } skinned
            ? (skinned, Matrix4x4.Identity) // skinned vertices are already in world space
            : (Meshes().Get(modelMesh.Mesh), meshRenderer.Transform.LocalToWorldMatrix);
        var white = resources.GetDefaultTexture(DefaultTexture.White);
        CullMode? cull = meshRenderer.DoubleSided ? CullMode.None : null;
        var custom = resources.GetMaterial(meshRenderer.Material);
        for (var i = 0; i < mesh.SubMeshes.Count; i++)
        {
            var index = i < modelMesh.SubMeshMaterials.Count ? modelMesh.SubMeshMaterials[i] : -1;
            var material = custom ?? resources.GetModelMaterial(meshRenderer.Mesh.Guid, index) ?? BuiltinMaterial(meshRenderer);
            _items.Add(new MeshDrawItem(mesh, i, material, white, world, cull));
        }
    }

    /// <summary>
    /// Deforms a skinned model mesh with the current pose of its joints (the GameObjects of the model's joint nodes, by
    /// name under the model's root) into the renderer's own dynamic mesh. Null when the mesh has no skin in its model or
    /// the root cannot be found; the mesh then draws in its bind pose.
    /// </summary>
    private GpuMesh? Skin(MeshRenderer meshRenderer, ModelMesh modelMesh)
    {
        if (!_skins.TryGetValue(meshRenderer, out var state) || !ReferenceEquals(state.Source, modelMesh) || state.Joints.Any(j => j is null || j.GameObject.IsDestroyed))
        {
            state?.Dispose();
            _skins.Remove(meshRenderer);
            if (BindSkin(meshRenderer, modelMesh) is not { } bound)
            {
                return null;
            }

            state = bound;
            _skins[meshRenderer] = state;
        }

        state.LastFrame = _frame;
        if (state.SkinnedFrame != _frame)
        {
            for (var j = 0; j < state.Joints.Length; j++)
            {
                state.Matrices[j] = state.Joints[j]!.LocalToWorldMatrix * state.Skin.InverseBindMatrices[j];
            }

            var bounds = Skinning.Apply(state.BindPose, modelMesh.Joints, modelMesh.Weights, state.Matrices, state.Output);
            state.Gpu.UpdateVertices(state.Output, bounds);
            state.SkinnedFrame = _frame;
        }

        return state.Gpu;
    }

    private SkinState? BindSkin(MeshRenderer meshRenderer, ModelMesh modelMesh)
    {
        if (resources.GetModel(meshRenderer.Mesh.Guid) is not { } model || modelMesh.Weights is null)
        {
            return null;
        }

        var meshIndex = model.Meshes.IndexOf(modelMesh);
        var nodeIndex = model.Nodes.FindIndex(n => n.Mesh == meshIndex && n.Skin >= 0);
        if (nodeIndex < 0)
        {
            return null;
        }

        // The model's root is as many parents up as the mesh node is deep in the model.
        var root = meshRenderer.Transform;
        for (var n = nodeIndex; model.Nodes[n].Parent >= 0; n = model.Nodes[n].Parent)
        {
            if (root.Parent is null)
            {
                return null;
            }

            root = root.Parent;
        }

        var byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
        Collect(root, byName);
        var skin = model.Skins[model.Nodes[nodeIndex].Skin];
        var joints = skin.Joints.Select(j => j == 0 ? root : byName.GetValueOrDefault(model.Nodes[j].Name)).ToArray();
        if (joints.Any(j => j is null))
        {
            return null;
        }

        var bindPose = GpuMesh.Interleave(modelMesh.Mesh);
        return new SkinState(modelMesh, skin, joints, GpuMesh.CreateDynamic(renderer.Device, modelMesh.Mesh), bindPose, new MeshVertex[bindPose.Length], new Matrix4x4[joints.Length]);

        static void Collect(Transform node, Dictionary<string, Transform> into)
        {
            foreach (var child in node.Children)
            {
                into.TryAdd(child.GameObject.Name, child);
                Collect(child, into);
            }
        }
    }

    /// <summary>Releases the skinned meshes of renderers that were not drawn this frame (destroyed, disabled, changed).</summary>
    private void PruneSkins()
    {
        foreach (var (renderer, state) in _skins.Where(p => p.Value.LastFrame != _frame).ToList())
        {
            state.Dispose();
            _skins.Remove(renderer);
        }
    }

    private sealed class SkinState(ModelMesh source, ModelSkin skin, Transform?[] joints, GpuMesh gpu, MeshVertex[] bindPose, MeshVertex[] output, Matrix4x4[] matrices) : IDisposable
    {
        public ModelMesh Source { get; } = source;

        public ModelSkin Skin { get; } = skin;

        public Transform?[] Joints { get; } = joints;

        public GpuMesh Gpu { get; } = gpu;

        public MeshVertex[] BindPose { get; } = bindPose;

        public MeshVertex[] Output { get; } = output;

        public Matrix4x4[] Matrices { get; } = matrices;

        public int LastFrame { get; set; }

        public int SkinnedFrame { get; set; } = -1;

        public void Dispose() => Gpu.Dispose();
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

    /// <summary>The scene's lights for this camera; unlit when it has neither <see cref="Light"/> nor <see cref="AmbientLight"/>.</summary>
    private SceneLighting CollectLighting(Scene scene, Vector3 camera)
    {
        var all = new List<LightData>();
        foreach (var light in scene.World.Pool<Light>().Components)
        {
            if (light.IsActiveAndEnabled && light.Intensity > 0f)
            {
                var kind = light.Type switch { LightType.Point => LightKind.Point, LightType.Spot => LightKind.Spot, _ => LightKind.Directional };
                all.Add(new LightData(kind, light.Transform.Position, light.Transform.Forward, light.Color, light.Intensity, light.Range,
                    light.SpotAngle, light.InnerSpotAngle, light.Shadows && kind == LightKind.Directional, light.ShadowStrength, light.ShadowDistance));
            }
        }

        AmbientLight? ambient = null;
        foreach (var candidate in scene.World.Pool<AmbientLight>().Components)
        {
            if (candidate.IsActiveAndEnabled)
            {
                ambient = candidate;
                break;
            }
        }

        if (all.Count == 0 && ambient is null)
        {
            return SceneLighting.Unlit;
        }

        var (lights, dropped) = SceneLighting.Select(all, camera);
        if (dropped > 0 && !_warnedTooManyLights)
        {
            LogTooManyLights(logger, scene.Name, all.Count, SceneLighting.MaxLights);
            _warnedTooManyLights = true;
        }

        return ambient is null ? new SceneLighting { Lights = lights } : new SceneLighting { Lights = lights, Ambient = ambient.Color, AmbientIntensity = ambient.Intensity };
    }

    private void Dispose3D()
    {
        foreach (var state in _skins.Values)
        {
            state.Dispose();
        }

        _skins.Clear();
        _shadowMap?.Dispose();
        _shadowMap = null;
        if (_meshes is not null)
        {
            resources.MeshesEvicted -= OnMeshesEvicted;
        }

        _meshes?.Dispose();
        _meshes = null;
    }

    [LoggerMessage(EventId = 2105, Level = LogLevel.Warning, Message = "Scene '{Scene}' has {Count} lights; only the {Max} that matter most for the camera are used")]
    private static partial void LogTooManyLights(ILogger logger, string scene, int count, int max);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Warning, Message = "Render target '{Target}' has no depth buffer: 3D meshes are not drawn into it")]
    private static partial void LogNoDepth(ILogger logger, string target);
}
