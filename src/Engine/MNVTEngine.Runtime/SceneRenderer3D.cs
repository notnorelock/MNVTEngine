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
    private readonly ConditionalWeakTable<MeshRenderer, Dictionary<Material, Material>> _overriddenMaterials = [];
    private readonly ConditionalWeakTable<Material, Material> _skinnedMaterials = [];
    private readonly ConditionalWeakTable<MeshRenderer, HashSet<string>> _warnedOverrides = [];
    private Renderer3D? _renderer3D;
    private GpuMeshCache? _meshes;
    private ShadowMap? _shadowMap;
    private readonly ShadowMap?[] _spotShadowMaps = new ShadowMap?[ShadowSet.MaxSpotShadows];
    private PointShadowMap? _pointShadowMap;
    private Material? _skyMaterial;
    private readonly Dictionary<MeshRenderer, SkinState> _skins = new(ReferenceEqualityComparer.Instance);
    private int _frame;
    private bool _warnedNoDepth;
    private bool _warnedTooManyLights;

    /// <summary>Mesh renderers drawn by the last 3D pass.</summary>
    public int MeshesDrawn { get; private set; }

    /// <summary>
    /// Skins meshes on the GPU (default): the skinned shader variants move the vertices by the joint matrices, so the
    /// CPU only computes the matrices. Meshes with blend shapes, more than <see cref="MaterialShaders.MaxJoints"/> joints,
    /// or materials other than the built-in Standard and Unlit are skinned on the CPU. Off skins every mesh on the CPU.
    /// </summary>
    public bool GpuSkinning { get; set; } = true;

    /// <summary>Skinned meshes the GPU skinned in the last 3D pass.</summary>
    public int GpuSkinnedMeshes { get; private set; }

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
        MeshesDrawn = MeshesCulled = MeshTriangles = GpuSkinnedMeshes = 0;
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
        var lighting = CollectLighting(scene, camera, view.Position);
        var shadows = RenderShadows(lighting, view, camera, device, target);
        PruneSkins();
        (LightsUsed, ShadowsRendered) = (lighting.Lights.Count, shadows.Directional is not null);
        device.ClearDepth();
        AddSky(scene, camera, view.Position);
        var stats = _renderer3D.Render(view, _items, lighting, shadows);
        (MeshesDrawn, MeshesCulled, MeshTriangles, MaterialSwitches, SkinnedMeshes) = (stats.Visible, stats.Culled, stats.Triangles, stats.MaterialSwitches, _skins.Count);
        Core.Diagnostics.Profiler.Count("Meshes drawn", MeshesDrawn);
        Core.Diagnostics.Profiler.Count("Meshes culled", MeshesCulled);
        Core.Diagnostics.Profiler.Count("Mesh triangles", MeshTriangles);
        Core.Diagnostics.Profiler.Count("Material switches", MaterialSwitches);
        Core.Diagnostics.Profiler.Count("Lights 3D", LightsUsed);
        Core.Diagnostics.Profiler.Count("Skinned meshes", SkinnedMeshes);
        Core.Diagnostics.Profiler.Count("GPU skinned meshes", GpuSkinnedMeshes);
    }

    /// <summary>One item per submesh of a model mesh, with the renderer's material or the model's material of each submesh.</summary>
    private void AddModelItems(MeshRenderer meshRenderer)
    {
        if (resources.GetModelMesh(meshRenderer.Mesh) is not { } modelMesh)
        {
            return;
        }

        var custom = resources.GetMaterial(meshRenderer.Material);
        var materials = new Material[modelMesh.Mesh.SubMeshes.Count];
        for (var i = 0; i < materials.Length; i++)
        {
            var index = i < modelMesh.SubMeshMaterials.Count ? modelMesh.SubMeshMaterials[i] : -1;
            materials[i] = WithOverrides(meshRenderer, custom ?? resources.GetModelMaterial(meshRenderer.Mesh.Guid, index) ?? BuiltinMaterial(meshRenderer));
        }

        var gpuSkin = GpuSkinning && modelMesh.Joints is not null && modelMesh.BlendShapes.Count == 0 && materials.All(m => SkinnedVariant(m) is not null);
        var (mesh, world, joints, bounds) = (modelMesh.Joints is not null || modelMesh.BlendShapes.Count > 0) && Deform(meshRenderer, modelMesh, gpuSkin) is { } deformed
            ? (deformed.Mesh, deformed.WorldSpace ? Matrix4x4.Identity : meshRenderer.Transform.LocalToWorldMatrix, deformed.Joints, deformed.Bounds) // skinned vertices are in world space
            : (Meshes().Get(modelMesh.Mesh), meshRenderer.Transform.LocalToWorldMatrix, null, (Bounds?)null);
        var white = resources.GetDefaultTexture(DefaultTexture.White);
        CullMode? cull = meshRenderer.DoubleSided ? CullMode.None : null;
        if (joints is not null)
        {
            GpuSkinnedMeshes++;
        }

        for (var i = 0; i < mesh.SubMeshes.Count; i++)
        {
            var material = joints is not null ? SkinnedVariant(materials[i])! : materials[i];
            _items.Add(new MeshDrawItem(mesh, i, material, white, world, cull, joints ?? default(ReadOnlyMemory<Matrix4x4>), bounds));
        }
    }

    /// <summary>
    /// The GPU-skinning twin of a built-in Standard or Unlit material: the skinned shader with the material's values,
    /// copied every frame (inspector edits show). Null for other shaders, which only the CPU can skin.
    /// </summary>
    private Material? SkinnedVariant(Material material)
    {
        var id = ReferenceEquals(material.Shader, resources.GetBuiltinShader(Serialization.MaterialDefinition.BuiltinStandard)) ? MaterialShaders.StandardSkinnedId
            : ReferenceEquals(material.Shader, resources.GetBuiltinShader(Serialization.MaterialDefinition.BuiltinUnlit)) ? MaterialShaders.UnlitSkinnedId : null;
        if (id is null)
        {
            return null;
        }

        var shader = resources.GetBuiltinShader(id);
        if (!_skinnedMaterials.TryGetValue(material, out var twin) || !ReferenceEquals(twin.Shader, shader))
        {
            twin = new Material(shader, material.BlendMode, material.Name + " (skinned)");
            _skinnedMaterials.AddOrUpdate(material, twin);
        }

        twin.CopyFrom(material);
        return twin;
    }

    /// <summary>
    /// Deforms a skinned model mesh with the current pose of its joints (the GameObjects of the model's joint nodes, by
    /// name under the model's root) into the renderer's own dynamic mesh. Null when the mesh has no skin in its model or
    /// the root cannot be found; the mesh then draws in its bind pose.
    /// </summary>
    /// <summary>
    /// The renderer's own deformed copy of a model mesh: blend shapes first (the renderer's weights over the model's
    /// defaults), then skinning with the joints' current pose. Recomputed when the pose or the weights change. Null when
    /// a skin's joints cannot be found (the mesh then draws undeformed).
    /// </summary>
    private (GpuMesh Mesh, bool WorldSpace, Matrix4x4[]? Joints, Bounds? Bounds)? Deform(MeshRenderer meshRenderer, ModelMesh modelMesh, bool gpu)
    {
        if (!_skins.TryGetValue(meshRenderer, out var state) || !ReferenceEquals(state.Source, modelMesh) || state.Joints.Any(j => j is null || j.GameObject.IsDestroyed))
        {
            state?.Dispose();
            _skins.Remove(meshRenderer);
            if (BindDeformation(meshRenderer, modelMesh) is not { } bound)
            {
                return null;
            }

            state = bound;
            _skins[meshRenderer] = state;
        }

        state.LastFrame = _frame;
        var skinned = state.Skin is not null;
        if (gpu && skinned && state.Joints.Length <= MaterialShaders.MaxJoints)
        {
            // The GPU moves the vertices: only the joint matrices, and bounds for culling, are computed here.
            Bounds? box = null;
            for (var j = 0; j < state.Joints.Length; j++)
            {
                state.Matrices[j] = state.Joints[j]!.LocalToWorldMatrix * state.Skin!.InverseBindMatrices[j];
                var moved = Renderer3D.TransformBounds(modelMesh.Mesh.Bounds, state.Matrices[j]);
                if (box is { } b)
                {
                    b.Encapsulate(moved);
                    box = b;
                }
                else
                {
                    box = moved;
                }
            }

            // shortcut: one GPU copy per renderer (crowds of one model upload it once each); share it per model mesh if that matters.
            state.GpuSkinned ??= GpuMesh.UploadSkinned(renderer.Device, modelMesh.Mesh, modelMesh.Joints, modelMesh.Weights);
            return (state.GpuSkinned, true, state.Matrices, box);
        }

        var weightsChanged = state.WeightsVersion != meshRenderer.BlendShapeVersion || state.SkinnedFrame < 0;
        if (state.SkinnedFrame != _frame && (skinned || weightsChanged))
        {
            ReadOnlySpan<MeshVertex> source = state.BindPose;
            Bounds bounds = default;
            if (state.Shapes.Count > 0)
            {
                for (var s = 0; s < state.ShapeWeights.Length; s++)
                {
                    state.ShapeWeights[s] = meshRenderer.GetBlendShapeWeightOrDefault(s, modelMesh.DefaultBlendShapeWeights);
                }

                bounds = Skinning.Morph(state.BindPose, state.Shapes, state.ShapeWeights, state.Morphed);
                source = state.Morphed;
                state.WeightsVersion = meshRenderer.BlendShapeVersion;
            }

            if (skinned)
            {
                for (var j = 0; j < state.Joints.Length; j++)
                {
                    state.Matrices[j] = state.Joints[j]!.LocalToWorldMatrix * state.Skin!.InverseBindMatrices[j];
                }

                bounds = Skinning.Apply(source, modelMesh.Joints, modelMesh.Weights, state.Matrices, state.Output);
                state.Gpu.UpdateVertices(state.Output, bounds);
            }
            else
            {
                state.Gpu.UpdateVertices(state.Morphed, bounds);
            }

            state.SkinnedFrame = _frame;
        }
        else if (!skinned)
        {
            state.Gpu.UpdateVertices(state.Morphed, state.Gpu.Bounds); // dynamic buffers are uploaded each frame they are drawn
        }

        return (state.Gpu, skinned, null, null);
    }

    private SkinState? BindDeformation(MeshRenderer meshRenderer, ModelMesh modelMesh)
    {
        if (resources.GetModel(meshRenderer.Mesh.Guid) is not { } model)
        {
            return null;
        }

        var shapes = modelMesh.BlendShapes.Select(s => (s.PositionDeltas, s.NormalDeltas, s.TangentDeltas)).ToList();
        var meshIndex = model.Meshes.IndexOf(modelMesh);
        var nodeIndex = model.Nodes.FindIndex(n => n.Mesh == meshIndex && n.Skin >= 0);
        if (modelMesh.Weights is null || nodeIndex < 0)
        {
            if (shapes.Count == 0)
            {
                return null;
            }

            var unskinned = GpuMesh.Interleave(modelMesh.Mesh);
            return new SkinState(modelMesh, null, [], GpuMesh.CreateDynamic(renderer.Device, modelMesh.Mesh), unskinned, new MeshVertex[unskinned.Length], [], shapes);
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
        return new SkinState(modelMesh, skin, joints, GpuMesh.CreateDynamic(renderer.Device, modelMesh.Mesh), bindPose, new MeshVertex[bindPose.Length], new Matrix4x4[joints.Length], shapes);

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

    private sealed class SkinState(ModelMesh source, ModelSkin? skin, Transform?[] joints, GpuMesh gpu, MeshVertex[] bindPose, MeshVertex[] output, Matrix4x4[] matrices,
        List<(Vector3[] Positions, Vector3[]? Normals, Vector3[]? Tangents)> shapes) : IDisposable
    {
        public ModelMesh Source { get; } = source;

        /// <summary>Null for meshes with blend shapes only.</summary>
        public ModelSkin? Skin { get; } = skin;

        public List<(Vector3[] Positions, Vector3[]? Normals, Vector3[]? Tangents)> Shapes { get; } = shapes;

        public float[] ShapeWeights { get; } = new float[shapes.Count];

        /// <summary>The blend-shaped vertices (the bind pose for skinning).</summary>
        public MeshVertex[] Morphed { get; } = new MeshVertex[bindPose.Length];

        public int WeightsVersion { get; set; } = -1;

        public Transform?[] Joints { get; } = joints;

        public GpuMesh Gpu { get; } = gpu;

        public MeshVertex[] BindPose { get; } = bindPose;

        public MeshVertex[] Output { get; } = output;

        public Matrix4x4[] Matrices { get; } = matrices;

        public int LastFrame { get; set; }

        public int SkinnedFrame { get; set; } = -1;

        /// <summary>The bind pose with joints and weights, for GPU skinning (created on first use).</summary>
        public GpuMesh? GpuSkinned { get; set; }

        public void Dispose()
        {
            Gpu.Dispose();
            GpuSkinned?.Dispose();
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
        return new MeshDrawItem(mesh, 0, WithOverrides(meshRenderer, resources.GetMaterial(meshRenderer.Material) ?? BuiltinMaterial(meshRenderer)), white, world, cull);
    }

    /// <summary>
    /// The material a renderer draws with: <paramref name="material"/> itself, or, when the renderer overrides
    /// properties, its own copy of it, refreshed from the material every frame (inspector edits show) and then overridden.
    /// </summary>
    private Material WithOverrides(MeshRenderer meshRenderer, Material material)
    {
        if (meshRenderer.MaterialOverrides.Count == 0)
        {
            return material;
        }

        var copies = _overriddenMaterials.GetOrCreateValue(meshRenderer);
        if (!copies.TryGetValue(material, out var copy))
        {
            copy = new Material(material.Shader, material.BlendMode, material.Name + " (overridden)");
            copies[material] = copy;
        }

        copy.CopyFrom(material);
        foreach (var (name, value) in meshRenderer.MaterialOverrides)
        {
            if (Material.IsRendererOwned(name))
            {
                if (_warnedOverrides.GetOrCreateValue(meshRenderer).Add(name))
                {
                    LogRendererOwnedOverride(logger, meshRenderer.GameObject.Name, name);
                }

                continue;
            }

            if (value.IsTexture)
            {
                copy.SetTexture(name, resources.GetTexture(value.Texture));
            }
            else
            {
                copy.SetVector(name, value.Value);
            }
        }

        return copy;
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

    /// <summary>
    /// Shadow maps for the lights that cast them: the first directional light, the two nearest spot lights and the
    /// nearest point light (lights are ordered by importance already). Restores the main target afterwards.
    /// </summary>
    private ShadowSet RenderShadows(SceneLighting lighting, View3D view, Camera camera, IGraphicsDevice device, RenderTarget2D? target)
    {
        var casters = lighting.Lights.Select((light, index) => (light, index)).Where(l => l.light.Shadows).ToList();
        if (casters.Count == 0)
        {
            return ShadowSet.None;
        }

        using var sample = Core.Diagnostics.Profiler.Sample("Render.Shadows");
        var shader = resources.GetBuiltinShader(MaterialShaders.ShadowCasterId);
        ShadowState? directional = null;
        var spots = new List<ShadowState>();
        PointShadowState? point = null;
        var count = 0;
        foreach (var (light, index) in casters)
        {
            switch (light.Kind)
            {
                case LightKind.Directional when directional is null:
                    _shadowMap ??= new ShadowMap(device) { SkinnedCasterShader = resources.GetBuiltinShader(MaterialShaders.ShadowCasterSkinnedId) };
                    directional = _shadowMap.Render(light, index, view.ViewProjection, camera.NearClip, camera.FarClip, _items, shader);
                    count += _shadowMap.CasterCount;
                    break;
                case LightKind.Spot when spots.Count < ShadowSet.MaxSpotShadows:
                    var map = _spotShadowMaps[spots.Count] ??= new ShadowMap(device, 1024) { SkinnedCasterShader = resources.GetBuiltinShader(MaterialShaders.ShadowCasterSkinnedId) };
                    spots.Add(map.RenderSpot(light, index, _items, shader));
                    count += map.CasterCount;
                    break;
                case LightKind.Point when point is null:
                    _pointShadowMap ??= new PointShadowMap(device) { SkinnedCasterShader = resources.GetBuiltinShader(MaterialShaders.ShadowCasterSkinnedId) };
                    point = _pointShadowMap.Render(light, index, _items, shader);
                    count += _pointShadowMap.CasterCount;
                    break;
            }
        }

        device.SetRenderTarget(target);
        Core.Diagnostics.Profiler.Count("Shadow casters", count);
        return new ShadowSet(directional, spots, point);
    }

    /// <summary>The skybox: a large cube around the camera with the sky shader, drawn first (background queue) without depth writes.</summary>
    private void AddSky(Scene scene, Camera camera, Vector3 position)
    {
        if (FindEnvironment(scene) is not { DrawSkybox: true } environment || resources.GetEnvironmentTexture(environment.Sky) is null)
        {
            return;
        }

        _skyMaterial ??= new Material(resources.GetBuiltinShader(MaterialShaders.SkyId), BlendMode.Opaque, "Skybox")
        {
            RenderQueue = 1000, // before all geometry
            DepthWrite = false,
            DepthTest = false,
            Cull = CullMode.None,
        };
        var size = camera.FarClip * 0.5f;
        _items.Add(new MeshDrawItem(Meshes().Get(GetPrimitive(PrimitiveMesh.Cube)), 0, _skyMaterial, resources.GetDefaultTexture(DefaultTexture.White),
            Matrix4x4.TRS(position, Quaternion.Identity, new Vector3(size, size, size))));
    }

    private static SceneEnvironment? FindEnvironment(Scene scene)
    {
        foreach (var environment in scene.World.Pool<SceneEnvironment>().Components)
        {
            if (environment.IsActiveAndEnabled && !environment.Sky.IsNone)
            {
                return environment;
            }
        }

        return null;
    }

    /// <summary>
    /// The scene's lights for this camera, with its tone mapping and the scene's environment; unlit when it has no
    /// <see cref="Light"/>, <see cref="AmbientLight"/> or <see cref="SceneEnvironment"/>.
    /// </summary>
    private SceneLighting CollectLighting(Scene scene, Camera viewCamera, Vector3 camera)
    {
        var all = new List<LightData>();
        foreach (var light in scene.World.Pool<Light>().Components)
        {
            if (light.IsActiveAndEnabled && light.Intensity > 0f)
            {
                var kind = light.Type switch { LightType.Point => LightKind.Point, LightType.Spot => LightKind.Spot, _ => LightKind.Directional };
                all.Add(new LightData(kind, light.Transform.Position, light.Transform.Forward, light.Color, light.Intensity, light.Range,
                    light.SpotAngle, light.InnerSpotAngle, light.Shadows, light.ShadowStrength, light.ShadowDistance));
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

        var environment = FindEnvironment(scene);
        if (all.Count == 0 && ambient is null && environment is null)
        {
            return SceneLighting.Unlit;
        }

        var (lights, dropped) = SceneLighting.Select(all, camera);
        if (dropped > 0 && !_warnedTooManyLights)
        {
            LogTooManyLights(logger, scene.Name, all.Count, SceneLighting.MaxLights);
            _warnedTooManyLights = true;
        }

        // With an environment and no ambient light, the environment is the ambient light.
        var ambientColor = ambient?.Color ?? (environment is null ? new SceneLighting().Ambient : Color.Black);
        return new SceneLighting
        {
            Lights = lights,
            Ambient = ambientColor,
            AmbientIntensity = ambient?.Intensity ?? 1f,
            ToneMapping = viewCamera.ToneMapping == ToneMappingMode.Aces ? Graphics.Rendering3D.ToneMapping.Aces : Graphics.Rendering3D.ToneMapping.None,
            Exposure = viewCamera.Exposure,
            Environment = environment is null ? null : resources.GetEnvironmentTexture(environment.Sky),
            EnvironmentIntensity = environment?.Intensity ?? 1f,
            EnvironmentRotation = environment?.Rotation ?? 0f,
        };
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
        for (var i = 0; i < _spotShadowMaps.Length; i++)
        {
            _spotShadowMaps[i]?.Dispose();
            _spotShadowMaps[i] = null;
        }

        _pointShadowMap?.Dispose();
        _pointShadowMap = null;
        if (_meshes is not null)
        {
            resources.MeshesEvicted -= OnMeshesEvicted;
        }

        _meshes?.Dispose();
        _meshes = null;
    }

    [LoggerMessage(EventId = 2105, Level = LogLevel.Warning, Message = "Scene '{Scene}' has {Count} lights; only the {Max} that matter most for the camera are used")]
    private static partial void LogTooManyLights(ILogger logger, string scene, int count, int max);

    [LoggerMessage(EventId = 2106, Level = LogLevel.Warning, Message = "'{GameObject}': the material property '{Property}' is set by the renderer; the override is ignored")]
    private static partial void LogRendererOwnedOverride(ILogger logger, string gameObject, string property);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Warning, Message = "Render target '{Target}' has no depth buffer: 3D meshes are not drawn into it")]
    private static partial void LogNoDepth(ILogger logger, string target);
}
