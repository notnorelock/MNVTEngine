using ImGuiNET;
using MNVTEngine.Runtime;
using MNVTEngine.Assets;
using MNVTEngine.Assets.Models;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Materials;
using MNVTEngine.Graphics.Meshes;
using MNVTEngine.Graphics.Rendering3D;
using MNVTEngine.Mathematics;
using MNVTEngine.Serialization;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// Inspector for an imported model (FBX, glTF, GLB), like Unity's model import inspector:
/// <list type="bullet">
/// <item>a preview of the model with its materials (drag to orbit);</item>
/// <item>import settings with Apply and Revert;</item>
/// <item>materials: imported, extracted or remapped, with Extract Materials, per-material Remap and Reset;</item>
/// <item>textures: project, embedded or missing, with Extract Textures;</item>
/// <item>meshes, skins, animations, lights and cameras; Add to scene;</item>
/// <item>relinking references to meshes renamed in the source (mesh aliases).</item>
/// </list>
/// Every change goes through <see cref="ModelWorkflow"/>, which writes the model's meta and reimports it.
/// </summary>
internal sealed class ModelInspector(EditorContext context) : IDisposable
{
    private bool _usedPreview;
    private const int PreviewSize = 220;
    private static readonly SysVector4 Warning = new(1f, 0.75f, 0.35f, 1f);
    private ModelWorkflow? _workflow;
    private ModelData? _model;
    private string? _modelHash;
    private string? _error;
    private ModelImportSettings _settings = new();
    private ModelImportSettings _applied = new();
    private List<string> _report = [];
    private IReadOnlyList<MissingMeshReference>? _missing;
    private ViewTarget? _preview;
    private GpuMeshCache? _meshes;
    private Renderer3D? _renderer;
    private (Vector3 Center, float Radius) _bounds;
    private float _yaw = 30f;
    private float _pitch = 20f;
    private float _zoom = 1f;
    private bool _visible;

    public void Draw(Guid guid)
    {
        _visible = true;
        Sync(guid);
        if (_error is not null)
        {
            Gui.TextColored(new SysVector4(1f, 0.45f, 0.45f, 1f), _error);
        }

        if (_workflow is not { } workflow)
        {
            return;
        }

        if (ImGui.Button($"{Icons.Plus}  Add to scene"))
        {
            context.InstantiateModel(guid, null);
        }

        if (_model is { } model)
        {
            DrawPreview();
            DrawSettings(workflow);
            DrawMaterials(workflow, model);
            DrawTextures(workflow, model);
            DrawContents(model);
            DrawRelink(workflow, model);
        }
        else
        {
            Gui.Muted(context.Session.Assets.Get(guid)?.Status == AssetImportStatus.Importing
                ? "The model is importing in the background; it can be previewed and extracted when it is done."
                : "The model has not imported yet; fix the errors above, then it can be previewed and extracted.");
            DrawSettings(workflow);
        }

        if (_report.Count > 0 && ImGui.CollapsingHeader("Last operation", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var line in _report)
            {
                Gui.Bullet(line);
            }
        }
    }

    /// <summary>Draws the preview (render phase), only while the inspector shows a model.</summary>
    public void Render()
    {
        var visible = _visible;
        _visible = false;
        if (!visible || _preview is null)
        {
            return;
        }

        RenderPreview();
    }

    // A method of its own: the preview uses world.3d's renderer, so a 2D project that never shows a model never loads it.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void RenderPreview()
    {
        _usedPreview = true;
        if (_model is null || _workflow is null || _preview is null)
        {
            return;
        }

        _preview.EnsureSize();
        var device = context.Device;
        device.SetRenderTarget(_preview.Target);
        device.Clear(new Color(0.16f, 0.17f, 0.2f, 1f));
        device.ClearDepth();
        if (_meshes is null)
        {
            _meshes = new GpuMeshCache(device);
            context.Resources.Models.MeshesEvicted += OnMeshesEvicted;
        }

        _renderer ??= new Renderer3D(device);
        var white = context.Resources.GetDefaultTexture(DefaultTexture.White);
        var items = new List<MeshDrawItem>();
        var world = WorldMatrices(_model);
        for (var i = 0; i < _model.Nodes.Count; i++)
        {
            if (_model.Nodes[i].Mesh is var meshIndex && meshIndex < 0)
            {
                continue;
            }

            var source = _model.Meshes[meshIndex];
            var mesh = _meshes.Get(source.Mesh);
            for (var s = 0; s < mesh.SubMeshes.Count; s++)
            {
                var material = context.Resources.GetModelMaterial(_workflow.Guid, s < source.SubMeshMaterials.Count ? source.SubMeshMaterials[s] : -1) ?? DefaultMaterial();
                items.Add(new MeshDrawItem(mesh, s, material, white, world[i]));
            }
        }

        var (center, radius) = _bounds;
        var distance = radius / MathF.Sin(17.5f * MathF.PI / 180f) * 1.1f * _zoom;
        var rotation = Quaternion.Euler(-_pitch, _yaw, 0f);
        var eye = center + (rotation * new Vector3(0f, 0f, distance));
        var view = new View3D(Matrix4x4.LookAt(eye, center, Vector3.Up), Matrix4x4.PerspectiveZeroToOne(35f, 1f, MathF.Max(distance - (radius * 2f), distance * 0.01f), distance + (radius * 2f)), eye);
        _renderer.Render(view, items, SceneLighting.Preview);
        device.SetRenderTarget(null);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void DisposePreviewMeshes()
    {
        if (_meshes is not null)
        {
            context.Resources.Models.MeshesEvicted -= OnMeshesEvicted;
        }

        _meshes?.Dispose();
    }

    public void Dispose()
    {
        if (_usedPreview)
        {
            DisposePreviewMeshes(); // world.3d's types are touched only if a model preview was drawn
        }

        _preview?.Dispose();
        _default = null;
    }

    private Material? _default;

    private Material DefaultMaterial()
    {
        if (_default is null)
        {
            _default = new Material(context.Resources.GetBuiltinShader(MaterialDefinition.BuiltinStandard), BlendMode.Opaque, "Model preview default");
            MaterialProperties.ApplyDefaults(_default, MaterialShaders.StandardProperties, context.Resources.GetDefaultTexture);
        }

        return _default;
    }

    private void OnMeshesEvicted(IReadOnlyCollection<Mesh> meshes)
    {
        foreach (var mesh in meshes)
        {
            _meshes?.Remove(mesh);
        }
    }

    /// <summary>Reloads the workflow, settings and model when another model is selected or this one reimported.</summary>
    private void Sync(Guid guid)
    {
        var record = context.Session.Assets.Get(guid);
        var hash = $"{record?.SourceHash}|{record?.SettingsHash}|{record?.Status}";
        if (_workflow?.Guid == guid && hash == _modelHash)
        {
            return;
        }

        if (_workflow?.Guid != guid)
        {
            _report = [];
            _missing = null;
            (_yaw, _pitch, _zoom) = (30f, 20f, 1f);
        }

        _modelHash = hash;
        _error = null;
        _model = null;
        try
        {
            _workflow = new ModelWorkflow(context.Session.Assets, guid);
            _applied = _settings = _workflow.Settings;
            _model = _workflow.Load() is { } model ? context.Resources.GetModel(guid) ?? model : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or ArgumentException)
        {
            _error = ex.Message;
            return;
        }

        if (_model is not null)
        {
            _bounds = Bounds(_model);
            _preview ??= new ViewTarget(context, "Model preview");
            _preview.Request(PreviewSize, PreviewSize);
        }
    }

    private void DrawPreview()
    {
        if (_preview is null)
        {
            return;
        }

        // An image is not interactive: an invisible button over it receives the drag and the wheel.
        var at = ImGui.GetCursorScreenPos();
        Gui.Image(_preview.Id, _preview.Target, new SysVector2(PreviewSize, PreviewSize));
        ImGui.SetCursorScreenPos(at);
        ImGui.InvisibleButton("##model_preview", new SysVector2(PreviewSize, PreviewSize));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var delta = ImGui.GetIO().MouseDelta;
            _yaw -= delta.X * 0.5f;
            _pitch = Math.Clamp(_pitch + (delta.Y * 0.5f), -85f, 85f);
        }

        if (ImGui.IsItemHovered() && ImGui.GetIO().MouseWheel != 0f)
        {
            _zoom = Math.Clamp(_zoom * MathF.Pow(0.85f, ImGui.GetIO().MouseWheel), 0.2f, 5f);
        }

        Gui.Muted("Drag to orbit, wheel to zoom. Lit by a preview key light and fill.");
    }

    private void DrawSettings(ModelWorkflow workflow)
    {
        if (!ImGui.CollapsingHeader("Import settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var scale = _settings.Scale;
        if (ImGui.DragFloat("Scale", ref scale, 0.01f, 0.0001f, 10000f, "%.4f"))
        {
            _settings = _settings with { Scale = Math.Clamp(scale, 0.0001f, 10000f) };
        }

        Toggle("Generate missing normals", _settings.GenerateMissingNormals, v => _settings = _settings with { GenerateMissingNormals = v });
        Toggle("Generate tangents", _settings.GenerateTangents, v => _settings = _settings with { GenerateTangents = v });
        Toggle("Optimize meshes (FBX)", _settings.OptimizeMeshes, v => _settings = _settings with { OptimizeMeshes = v });
        Toggle("Import materials", _settings.ImportMaterials, v => _settings = _settings with { ImportMaterials = v });
        Toggle("Import skins", _settings.ImportSkins, v => _settings = _settings with { ImportSkins = v });
        Toggle("Import animations", _settings.ImportAnimations, v => _settings = _settings with { ImportAnimations = v });
        Toggle("Import lights", _settings.ImportLights, v => _settings = _settings with { ImportLights = v });
        Toggle("Import cameras", _settings.ImportCameras, v => _settings = _settings with { ImportCameras = v });

        var dirty = _settings.ToJson().ToString() != _applied.ToJson().ToString();
        ImGui.BeginDisabled(!dirty);
        if (ImGui.Button($"{Icons.Check}  Apply"))
        {
            Run("Apply import settings", () => workflow.ApplySettings(_settings));
        }

        ImGui.SameLine();
        if (ImGui.Button($"{Icons.Undo2}  Revert"))
        {
            _settings = _applied;
        }

        ImGui.EndDisabled();
    }

    private static void Toggle(string label, bool value, Action<bool> set)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
        }
    }

    private void DrawMaterials(ModelWorkflow workflow, ModelData model)
    {
        if (!ImGui.CollapsingHeader($"Materials ({model.Materials.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            return;
        }

        var assets = context.Session.Assets;
        foreach (var material in model.Materials)
        {
            ImGui.PushID(material.Name);
            ImGui.TextUnformatted(material.Name);
            ImGui.SameLine();
            Gui.Muted(material.SourceModel);
            var current = material.Override is { } o ? assets.TryGetPath(o, out var path) ? path : $"missing ({o})" : "Imported";
            if (ImGui.Button($"{current}##remap", new SysVector2(ImGui.GetContentRegionAvail().X, 0f)))
            {
                ImGui.OpenPopup("remap");
            }

            if (ImGui.IsItemHovered())
            {
                Gui.Tooltip("Click to remap this material to a project material, or drop a .mnvtmat here.");
            }

            if (Gui.DropTarget(Gui.AssetPayload) is { } dropped && context.ResolveAsset(dropped) is { Type: AssetTypes.Material } target)
            {
                Run($"Remap {material.Name}", () => workflow.RemapMaterial(material.Name, target.Guid));
            }

            if (ImGui.BeginPopup("remap"))
            {
                if (ImGui.MenuItem("Imported (no remap)", null, material.Override is null))
                {
                    Run($"Remap {material.Name}", () => workflow.RemapMaterial(material.Name, null));
                }

                ImGui.Separator();
                foreach (var candidate in assets.Assets.Where(a => a.Type == AssetTypes.Material).OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase))
                {
                    if (ImGui.MenuItem(candidate.Path, null, candidate.Guid == material.Override))
                    {
                        Run($"Remap {material.Name}", () => workflow.RemapMaterial(material.Name, candidate.Guid));
                    }
                }

                ImGui.EndPopup();
            }

            ImGui.PopID();
        }

        if (ImGui.Button($"{Icons.WandSparkles}  Extract Materials"))
        {
            Run("Extract materials", () => _report = [.. workflow.ExtractMaterials(workflow.DefaultFolder("Materials"), workflow.DefaultFolder("Textures"))]);
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(model.Materials.TrueForAll(m => m.Override is null));
        if (ImGui.Button($"{Icons.Undo2}  Reset remaps"))
        {
            Run("Reset material remaps", workflow.ResetMaterialRemaps);
        }

        ImGui.EndDisabled();
        Gui.Muted($"Extracted materials go to {workflow.DefaultFolder("Materials")}/; existing files are reused, never overwritten.");
    }

    private void DrawTextures(ModelWorkflow workflow, ModelData model)
    {
        if (!ImGui.CollapsingHeader($"Textures ({model.Textures.Count})"))
        {
            return;
        }

        var assets = context.Session.Assets;
        foreach (var texture in model.Textures)
        {
            var state = texture.ProjectAsset is { } guid ? assets.TryGetPath(guid, out var path) ? path : $"missing ({guid})"
                : texture.Embedded is not null ? "embedded in the model" : $"not found: {texture.SourcePath}";
            Gui.Bullet($"{texture.Name}: {state}");
            if (texture.ProjectAsset is null && texture.Embedded is null)
            {
                ImGui.SameLine();
                Gui.TextColored(Warning, "(!)");
            }
        }

        ImGui.BeginDisabled(model.Textures.TrueForAll(t => t.ProjectAsset is not null));
        if (ImGui.Button($"{Icons.WandSparkles}  Extract Textures"))
        {
            Run("Extract textures", () => _report = [.. workflow.ExtractTextures(workflow.DefaultFolder("Textures"))]);
        }

        ImGui.EndDisabled();
        Gui.Muted("Writes embedded textures, and textures found outside Assets/, into the project.");
    }

    private static void DrawContents(ModelData model)
    {
        if (!ImGui.CollapsingHeader($"Meshes ({model.Meshes.Count}), skins ({model.Skins.Count}), animations ({model.Animations.Count}), lights ({model.Lights.Count}), cameras ({model.Cameras.Count})"))
        {
            return;
        }

        foreach (var mesh in model.Meshes)
        {
            Gui.Bullet($"mesh/{mesh.Name}: {mesh.Mesh.VertexCount} vertices, {mesh.Mesh.TriangleCount} triangles, {mesh.Mesh.SubMeshes.Count} submesh(es){(mesh.Joints is null ? "" : ", skinned")}");
        }

        foreach (var skin in model.Skins)
        {
            Gui.Bullet($"skin {skin.Name}: {skin.Joints.Count} joints");
        }

        foreach (var animation in model.Animations)
        {
            Gui.Bullet($"animation {animation.Name}: {animation.Duration:0.##} s, {animation.Channels.Count} channels");
        }

        foreach (var light in model.Lights)
        {
            Gui.Bullet($"light {light.Name}: {light.Type}, intensity {light.Intensity:0.###} on {model.Nodes[light.Node].Name}");
        }

        foreach (var camera in model.Cameras)
        {
            Gui.Bullet($"camera {camera.Name}: {(camera.Orthographic ? $"orthographic, size {camera.OrthographicSize:0.##}" : $"perspective, {camera.FieldOfView:0.#} degrees")} on {model.Nodes[camera.Node].Name}");
        }
    }

    private void DrawRelink(ModelWorkflow workflow, ModelData model)
    {
        if (!ImGui.CollapsingHeader($"Relink meshes ({model.MeshAliases.Count} alias(es))"))
        {
            return;
        }

        Gui.TextWrapped("A mesh renamed in the source file breaks the scene references to its old name; they draw nothing. Find them and point the old name at a current mesh.");
        if (ImGui.Button($"{Icons.Search}  Find missing references"))
        {
            Run("Find missing mesh references", () => _missing = workflow.FindMissingMeshReferences());
        }

        if (ImGui.IsItemHovered())
        {
            Gui.Tooltip("Scans the saved scenes and prefabs of the project; save open scenes first.");
        }

        if (_missing is { Count: 0 })
        {
            Gui.Muted("Every saved reference to this model's meshes resolves.");
        }

        foreach (var reference in _missing ?? [])
        {
            ImGui.PushID(reference.Mesh);
            Gui.TextColored(Warning, $"mesh/{reference.Mesh}");
            ImGui.SameLine();
            Gui.Muted($"used by {string.Join(", ", reference.UsedBy)}");
            if (ImGui.Button($"{Icons.Link}  Point at...", new SysVector2(ImGui.GetContentRegionAvail().X, 0f)))
            {
                ImGui.OpenPopup("relink");
            }

            if (ImGui.BeginPopup("relink"))
            {
                foreach (var mesh in model.Meshes)
                {
                    if (ImGui.MenuItem($"mesh/{mesh.Name}"))
                    {
                        Run($"Relink {reference.Mesh}", () =>
                        {
                            _report = [.. workflow.Relink(reference.Mesh, mesh.Name)];
                            _missing = workflow.FindMissingMeshReferences();
                        });
                    }
                }

                ImGui.EndPopup();
            }

            ImGui.PopID();
        }

        foreach (var (alias, current) in model.MeshAliases)
        {
            ImGui.PushID(alias);
            Gui.Bullet($"mesh/{alias} -> mesh/{current}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"{Icons.Trash2}  Remove"))
            {
                Run($"Remove the mesh alias {alias}", () => _report = [.. workflow.Relink(alias, null)]);
            }

            ImGui.PopID();
        }
    }

    /// <summary>Runs a workflow step; failures are shown and logged, never thrown into the UI loop.</summary>
    private void Run(string action, Action step)
    {
        try
        {
            step();
            _modelHash = null; // reload after the reimport
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or ArgumentException)
        {
            _report = [$"{action} failed: {ex.Message}"];
            EditorLog.ActionFailed(context.Logger, ex, action);
        }
    }

    private static Matrix4x4[] WorldMatrices(ModelData model)
    {
        var world = new Matrix4x4[model.Nodes.Count];
        for (var i = 0; i < model.Nodes.Count; i++)
        {
            var n = model.Nodes[i];
            var local = Matrix4x4.TRS(n.Position, n.Rotation, n.Scale);
            world[i] = n.Parent < 0 ? local : world[n.Parent] * local;
        }

        return world;
    }

    private static (Vector3 Center, float Radius) Bounds(ModelData model)
    {
        var world = WorldMatrices(model);
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (var i = 0; i < model.Nodes.Count; i++)
        {
            if (model.Nodes[i].Mesh is var m && m < 0)
            {
                continue;
            }

            foreach (var p in model.Meshes[m].Mesh.Positions)
            {
                var w = world[i].MultiplyPoint(p);
                min = Vector3.Min(min, w);
                max = Vector3.Max(max, w);
            }
        }

        return min.X > max.X ? (Vector3.Zero, 1f) : ((min + max) * 0.5f, MathF.Max((max - min).Magnitude * 0.5f, 0.001f));
    }
}
