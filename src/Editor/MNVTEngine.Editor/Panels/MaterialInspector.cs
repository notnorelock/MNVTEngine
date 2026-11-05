using ImGuiNET;
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
/// Inspector for <c>.mnvtmat</c> assets:
/// <list type="bullet">
/// <item>a live preview sphere, drawn with the engine's 3D renderer;</item>
/// <item>the shader (built-in or a custom asset), and render settings: blend, culling, depth, render queue;</item>
/// <item>the shader's properties: colors, sliders, numbers, and texture slots that accept textures dragged from the
/// Project panel.</item>
/// </list>
/// Edits are written to the file when each edit ends, so the scene updates at once; Undo/Redo step through them.
/// </summary>
internal sealed class MaterialInspector(EditorContext context) : IDisposable
{
    private const int PreviewSize = 192;
    private static readonly string[] ShaderNames = ["Standard", "Unlit", "Sprite (2D)", "Custom shader"];
    private static readonly string[] DefaultOptions = ["Default", "On", "Off"];
    private static readonly string[] Blends = [.. MaterialDefinition.BlendModes];
    private static readonly string[] Culls = ["Default", .. MaterialDefinition.CullModes];
    private ViewTarget? _preview;
    private GpuMeshCache? _meshes;
    private Renderer3D? _renderer;
    private MaterialDocument? _document;
    private Material? _material;
    private Shader? _ownedShader;
    private string? _builtJson;
    private string? _error;
    private bool _visible;
    private string _fragmentPath = "";
    private string _vertexPath = "";

    public void Draw(Guid guid)
    {
        _visible = true;
        if (_document?.Guid != guid)
        {
            Open(guid);
        }

        if (_error is not null)
        {
            Gui.TextColored(new SysVector4(1f, 0.45f, 0.45f, 1f), _error);
        }

        if (_document is not { } document)
        {
            return;
        }

        ImGui.Separator();
        DrawPreview(document);
        ImGui.BeginDisabled(!document.CanUndo);
        if (Gui.IconButton(Icons.Undo2, "Undo"))
        {
            document.Undo();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!document.CanRedo);
        if (Gui.IconButton(Icons.Redo2, "Redo"))
        {
            document.Redo();
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        Gui.Muted("Changes are saved as you edit.");
        DrawShader(document);
        if (ImGui.CollapsingHeader("Render settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawRenderSettings(document);
        }

        if (ImGui.CollapsingHeader("Properties", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawProperties(document);
        }

        if (!ImGui.IsAnyItemActive() && !ImGui.IsAnyMouseDown())
        {
            try
            {
                document.Commit();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _error = $"Could not save: {ex.Message}";
                EditorLog.ActionFailed(context.Logger, ex, $"Save material {document.Path}");
            }
        }
    }

    /// <summary>Draws the preview sphere (render phase). Only while the inspector shows a material.</summary>
    public void Render()
    {
        var visible = _visible;
        _visible = false;
        if (!visible || _document is not { } document || _preview is null)
        {
            return;
        }

        var json = document.Definition.ToJson();
        if (json != _builtJson)
        {
            Rebuild(document, json);
        }

        _preview.EnsureSize();
        var device = context.Device;
        device.SetRenderTarget(_preview.Target);
        device.Clear(new Color(0.16f, 0.17f, 0.2f, 1f));
        if (_material is null || !IsMeshMaterial(document.Definition))
        {
            device.SetRenderTarget(null);
            return;
        }

        device.ClearDepth();
        _meshes ??= new GpuMeshCache(device);
        _renderer ??= new Renderer3D(device);
        var sphere = _meshes.Get(MeshPrimitives.Sphere(0.5f, 48, 24));
        var eye = new Vector3(0f, 0.4f, 1.9f);
        var view = new View3D(Matrix4x4.LookAt(eye, Vector3.Zero, Vector3.Up), Matrix4x4.PerspectiveZeroToOne(35f, 1f, 0.05f, 20f), eye);
        _renderer.Render(view, [new MeshDrawItem(sphere, 0, _material, context.Resources.GetDefaultTexture(DefaultTexture.White), Matrix4x4.Identity)], SceneLighting.Preview);
        device.SetRenderTarget(null);
    }

    public void Dispose()
    {
        _ownedShader?.Dispose();
        _meshes?.Dispose();
        _preview?.Dispose();
    }

    private static bool IsMeshMaterial(MaterialDefinition definition) =>
        definition.Builtin is MaterialDefinition.BuiltinStandard or MaterialDefinition.BuiltinUnlit || definition.VertexShader is not null;

    private void Open(Guid guid)
    {
        _error = null;
        _document = null;
        _builtJson = null;
        try
        {
            _document = new MaterialDocument(context.Session.Assets, guid);
            _fragmentPath = AssetPath(_document.Definition.Shader);
            _vertexPath = AssetPath(_document.Definition.VertexShader);
        }
        catch (Exception ex) when (ex is SerializationException or IOException or KeyNotFoundException)
        {
            _error = ex.Message;
        }

        _preview ??= new ViewTarget(context, "Material preview");
        _preview.Request(PreviewSize, PreviewSize);
    }

    private void Rebuild(MaterialDocument document, string json)
    {
        _builtJson = json;
        _ownedShader?.Dispose();
        _ownedShader = null;
        try
        {
            _material = context.Resources.BuildMaterial(document.Definition, document.Path, out _ownedShader);
            _error = null;
        }
        catch (Exception ex) when (ex is ShaderCompilationException or IOException or KeyNotFoundException or ArgumentException)
        {
            _material = null;
            _error = ex is ShaderCompilationException compile ? $"Shader: {compile.Log}" : ex.Message;
        }
    }

    private void DrawPreview(MaterialDocument document)
    {
        if (_preview is null)
        {
            return;
        }

        if (IsMeshMaterial(document.Definition))
        {
            Gui.Image(_preview.Id, _preview.Target, new SysVector2(PreviewSize, PreviewSize));
            Gui.Muted("Lit by a preview key light and fill.");
        }
        else
        {
            Gui.Muted("2D (sprite) material: it is previewed on sprites in the scene.");
        }
    }

    private void DrawShader(MaterialDocument document)
    {
        var definition = document.Definition;
        var current = definition.Builtin switch
        {
            MaterialDefinition.BuiltinStandard => 0,
            MaterialDefinition.BuiltinUnlit => 1,
            MaterialDefinition.BuiltinSprite => 2,
            _ => 3,
        };
        if (ImGui.Combo("Shader", ref current, ShaderNames, ShaderNames.Length))
        {
            if (current == 3)
            {
                _error = "Enter the fragment shader's asset path below (and a vertex shader for meshes), then press Enter.";
            }
            else
            {
                document.SetShader(current switch { 0 => MaterialDefinition.BuiltinStandard, 1 => MaterialDefinition.BuiltinUnlit, _ => MaterialDefinition.BuiltinSprite }, null);
            }
        }

        if (current == 3 || definition.Shader is not null)
        {
            if (ImGui.InputText("Fragment shader", ref _fragmentPath, 260, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                if (ShaderAsset(_fragmentPath) is { } fragment)
                {
                    document.SetShader(null, fragment);
                    _error = null;
                }
            }

            if (ImGui.InputText("Vertex shader", ref _vertexPath, 260, ImGuiInputTextFlags.EnterReturnsTrue) && definition.Shader is not null)
            {
                definition.VertexShader = _vertexPath.Trim().Length == 0 ? null : ShaderAsset(_vertexPath);
            }

            if (ImGui.IsItemHovered())
            {
                Gui.Tooltip("Optional. Without one the sprite vertex stage is used; meshes need a vertex shader reading the mesh layout.");
            }
        }
    }

    private void DrawRenderSettings(MaterialDocument document)
    {
        var definition = document.Definition;
        var blend = Math.Max(0, Array.IndexOf(Blends, definition.Blend));
        if (ImGui.Combo("Blend", ref blend, Blends, Blends.Length))
        {
            definition.Blend = Blends[blend];
        }

        var cull = definition.Cull is null ? 0 : Array.IndexOf(Culls, definition.Cull);
        if (ImGui.Combo("Cull", ref cull, Culls, Culls.Length))
        {
            definition.Cull = cull == 0 ? null : Culls[cull];
        }

        definition.DepthTest = TriState("Depth test", definition.DepthTest);
        definition.DepthWrite = TriState("Depth write", definition.DepthWrite);
        var overrideQueue = definition.RenderQueue is not null;
        if (ImGui.Checkbox("Override render queue", ref overrideQueue))
        {
            definition.RenderQueue = overrideQueue ? _material?.RenderQueue ?? Material.GeometryQueue : null;
        }

        if (definition.RenderQueue is { } queue)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100f);
            if (ImGui.DragInt("##queue", ref queue, 1f, 0, 5000))
            {
                definition.RenderQueue = queue;
            }
        }

        if (_material is { } m)
        {
            Gui.Muted($"In effect: queue {m.RenderQueue}{(m.IsTransparent ? " (transparent)" : "")}, cull {m.Cull}, depth test {(m.DepthTest ? "on" : "off")}, write {(m.DepthWrite ? "on" : "off")}");
        }
    }

    private static bool? TriState(string label, bool? value)
    {
        var index = value is null ? 0 : value.Value ? 1 : 2;
        return ImGui.Combo(label, ref index, DefaultOptions, DefaultOptions.Length) ? index switch { 1 => true, 2 => false, _ => null } : value;
    }

    private void DrawProperties(MaterialDocument document)
    {
        var definition = document.Definition;
        var documented = MaterialShaders.PropertiesOf(definition.Builtin);
        if (documented.Count > 0)
        {
            foreach (var info in documented)
            {
                DrawDocumented(document, info);
            }

            return;
        }

        if (_material is null)
        {
            Gui.Muted("The shader did not build; see the error above.");
            return;
        }

        if (_material.Shader.Properties.Count == 0)
        {
            Gui.Muted("This shader has no properties.");
        }

        foreach (var property in _material.Shader.Properties)
        {
            DrawReflected(document, property);
        }
    }

    private void DrawDocumented(MaterialDocument document, MaterialPropertyInfo info)
    {
        ImGui.PushID(info.Name);
        var definition = document.Definition;
        var stored = definition.Properties.TryGetValue(info.Name, out var value);
        switch (info.UI)
        {
            case MaterialPropertyUI.Texture:
                DrawTextureSlot(definition, info.Name, info.Label);
                break;
            case MaterialPropertyUI.Color or MaterialPropertyUI.HdrColor:
                var color = stored && value.Numbers is { Length: 4 } n ? new SysVector4(n[0], n[1], n[2], n[3]) : new SysVector4(info.Default.X, info.Default.Y, info.Default.Z, info.Default.W);
                var flags = info.UI == MaterialPropertyUI.HdrColor ? ImGuiColorEditFlags.HDR | ImGuiColorEditFlags.Float : ImGuiColorEditFlags.AlphaPreviewHalf;
                if (ImGui.ColorEdit4(info.Label, ref color, flags))
                {
                    definition.Properties[info.Name] = MaterialValue.Vector(color.X, color.Y, color.Z, color.W);
                }

                break;
            default:
                var number = stored && value.Numbers is { Length: >= 1 } v ? v[0] : info.Default.X;
                var changed = info.UI == MaterialPropertyUI.Range ? ImGui.SliderFloat(info.Label, ref number, info.Min, info.Max) : ImGui.DragFloat(info.Label, ref number, 0.01f);
                if (changed)
                {
                    definition.Properties[info.Name] = MaterialValue.Number(number);
                }

                break;
        }

        if (info.Tooltip is { } tooltip && ImGui.IsItemHovered())
        {
            Gui.Tooltip(tooltip);
        }

        ResetButton(document, info.Name, stored);
        ImGui.PopID();
    }

    private void DrawReflected(MaterialDocument document, ShaderProperty property)
    {
        ImGui.PushID(property.Name);
        var definition = document.Definition;
        var stored = definition.Properties.TryGetValue(property.Name, out var value);
        var numbers = stored ? value.Numbers : null;
        float At(int i) => numbers is { } n && i < n.Length ? n[i] : 0f;
        switch (property.Kind)
        {
            case ShaderPropertyKind.Texture:
                DrawTextureSlot(definition, property.Name, property.Name);
                break;
            case ShaderPropertyKind.Float or ShaderPropertyKind.Int:
                var f = At(0);
                if (ImGui.DragFloat(property.Name, ref f, 0.01f))
                {
                    definition.Properties[property.Name] = MaterialValue.Number(property.Kind == ShaderPropertyKind.Int ? MathF.Round(f) : f);
                }

                break;
            case ShaderPropertyKind.Vector2:
                var v2 = new SysVector2(At(0), At(1));
                if (ImGui.DragFloat2(property.Name, ref v2, 0.01f))
                {
                    definition.Properties[property.Name] = MaterialValue.Vector(v2.X, v2.Y);
                }

                break;
            case ShaderPropertyKind.Vector4:
                var v4 = new SysVector4(At(0), At(1), At(2), At(3));
                var edited = property.Name.Contains("Color", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Tint", StringComparison.OrdinalIgnoreCase)
                    ? ImGui.ColorEdit4(property.Name, ref v4, ImGuiColorEditFlags.Float)
                    : ImGui.DragFloat4(property.Name, ref v4, 0.01f);
                if (edited)
                {
                    definition.Properties[property.Name] = MaterialValue.Vector(v4.X, v4.Y, v4.Z, v4.W);
                }

                break;
            default:
                Gui.Muted($"{property.Name}: {property.Kind} (set from code)");
                break;
        }

        ResetButton(document, property.Name, stored);
        ImGui.PopID();
    }

    /// <summary>Thumbnail, asset name and a drop target for textures from the Project panel; the X clears the slot.</summary>
    private void DrawTextureSlot(MaterialDefinition definition, string name, string label)
    {
        var assets = context.Session.Assets;
        var current = definition.Properties.TryGetValue(name, out var value) ? value.Texture : null;
        if (current is { } guid && assets.GetAssetType(guid) == AssetTypes.Texture)
        {
            var texture = context.Resources.GetTexture(new AssetRef(guid));
            Gui.Image(context.ImGui.Register(texture), texture, new SysVector2(32f, 32f));
        }
        else
        {
            ImGui.Dummy(new SysVector2(32f, 32f));
        }

        ImGui.SameLine();
        var text = current is { } g ? assets.TryGetPath(g, out var path) ? Path.GetFileName(path) : "(missing)" : "None";
        ImGui.Button($"{text}##slot", new SysVector2(Math.Max(120f, ImGui.GetContentRegionAvail().X - 170f), 32f));
        if (Gui.DropTarget(Gui.AssetPayload) is { } dropped && context.ResolveAsset(dropped) is { Type: AssetTypes.Texture } asset)
        {
            definition.Properties[name] = MaterialValue.TextureAsset(asset.Guid);
        }

        if (ImGui.IsItemHovered())
        {
            Gui.Tooltip("Drag a texture here from the Project panel.");
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        if (current is not null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton(Icons.X))
            {
                definition.Properties.Remove(name);
            }
        }
    }

    private static void ResetButton(MaterialDocument document, string name, bool stored)
    {
        if (!stored)
        {
            return;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton($"{Icons.Undo2}  Reset"))
        {
            document.ResetProperty(name);
        }
    }

    private string AssetPath(Guid? guid) => guid is { } g && context.Session.Assets.TryGetPath(g, out var path) ? path : "";

    private Guid? ShaderAsset(string path)
    {
        if (context.Session.Assets.TryGetGuid(path.Trim(), out var guid) && context.Session.Assets.GetAssetType(guid) == AssetTypes.Shader)
        {
            return guid;
        }

        _error = $"'{path}' is not a shader asset (.frag, .vert, .glsl).";
        return null;
    }
}
