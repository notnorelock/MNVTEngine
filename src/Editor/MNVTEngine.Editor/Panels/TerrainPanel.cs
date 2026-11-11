using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Serialization;
using MNVTEngine.Terrain.Elements;
using MNVTEngine.Terrain.Export;
using MNVTEngine.Terrain.Foliage;
using MNVTEngine.Terrain.Meshes;
using MNVTEngine.Terrain.Meshing;
using MNVTEngine.Terrain.Project;
using SysVector3 = System.Numerics.Vector3;
using TerrainTile = MNVTEngine.Terrain.Elements.Terrain;
using Vec3 = MNVTEngine.Terrain.Geometry.Vec3;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// The terrain tools (TerrainHeaven's toolbar and panels): start editing the selected <see cref="WorldTerrain"/>, pick a
/// tool, set brushes, edit the selected element's properties, textures and UVs, foliage presets, props, project
/// settings, presets and history, and export for Unity. The Scene view does the placing and painting.
/// </summary>
internal sealed class TerrainPanel(EditorContext context)
{
    private static readonly (TerrainTool Tool, string Icon, string Label)[] Tools =
    [
        (TerrainTool.Select, Icons.MousePointerClick, "Select and move nodes (Ctrl+drag moves an element, Shift+drag moves vertically)"),
        (TerrainTool.Road, Icons.Waypoints, "Road (drag; Ctrl curves, Shift snaps 45°)"),
        (TerrainTool.Intersection, Icons.Plus, "Intersection"),
        (TerrainTool.Tile, Icons.Grid3x3, "Terrain tile"),
        (TerrainTool.Polygon, Icons.Shapes, "Terrain polygon (click points, Enter finishes)"),
        (TerrainTool.River, Icons.Wind, "River"),
        (TerrainTool.CutSpline, Icons.Spline, "Terrain cut spline"),
        (TerrainTool.CutPoint, Icons.Crosshair, "Terrain cut point"),
        (TerrainTool.Fence, Icons.Hammer, "Fence"),
        (TerrainTool.Stairs, Icons.Pyramid, "Stairs"),
        (TerrainTool.Height, Icons.Mountain, "Height brush (Ctrl inverts)"),
        (TerrainTool.Texture, Icons.Paintbrush, "Texture brush"),
        (TerrainTool.Foliage, Icons.Sparkles, "Foliage brush (Ctrl erases)"),
        (TerrainTool.Props, Icons.Box, "Props brush (Ctrl erases)"),
    ];

    private string _presetName = "";
    private bool _presetsLoaded;
    private int _exportLods = 4;
    private double _exportScale = 1;

    private TerrainEditor Editor => context.TerrainEditor;

    public void Draw()
    {
        if (!_presetsLoaded)
        {
            LoadPresets();
        }

        if (context.InspectorDockId != 0)
        {
            ImGui.SetNextWindowDockID(context.InspectorDockId, ImGuiCond.FirstUseEver);
        }

        if (!ImGui.Begin(EditorWindows.Title("Terrain")))
        {
            ImGui.End();
            return;
        }

        if (context.OpenTerrainRequest is { } requested && !context.PlayMode.IsPlaying)
        {
            context.OpenTerrainRequest = null;
            OpenWorld(requested);
        }

        var selected = context.Selected?.GetComponent<WorldTerrain>();
        if (Editor.Target is { } target && (target.GameObject?.Scene != context.ActiveScene || context.PlayMode.IsPlaying))
        {
            Editor.End(); // the scene changed or Play started: stop editing the old world
        }

        if (Editor.Target is null)
        {
            DrawStart(selected);
        }
        else
        {
            DrawEditing();
        }

        ImGui.End();
    }

    private void DrawStart(WorldTerrain? selected)
    {
        if (selected is null)
        {
            Gui.TextWrapped("Select a GameObject with a WorldTerrain component (Add Component > WorldTerrain) to edit its terrain world.");
            return;
        }

        if (context.PlayMode.IsPlaying)
        {
            Gui.Muted("Stop Play Mode to edit the terrain.");
            return;
        }

        if (selected.Project.IsNone)
        {
            Gui.TextWrapped("This WorldTerrain has no world asset yet.");
            if (ImGui.Button($"{Icons.Plus}  Create world asset"))
            {
                CreateWorld(selected);
            }

            return;
        }

        if (selected.LoadError is { } error)
        {
            Gui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.4f, 1f), error);
        }

        if (ImGui.Button($"{Icons.Mountain}  Edit terrain in Scene view") && FileOf(selected.Project.Guid) is { } file)
        {
            if (Editor.Begin(selected, file))
            {
                Editor.Tool = TerrainTool.Select;
            }
        }
    }

    private void DrawEditing()
    {
        var project = Editor.Project!;
        Gui.Muted(Path.GetFileName(Editor.FilePath) ?? "");
        ImGui.SameLine();
        if (Gui.IconButton(Icons.Undo2, "Undo (Ctrl+Z)") && Editor.History!.CanUndo)
        {
            Editor.Undo();
        }

        ImGui.SameLine(0f, 2f);
        if (Gui.IconButton(Icons.Redo2, "Redo (Ctrl+Y)") && Editor.History!.CanRedo)
        {
            Editor.Redo();
        }

        ImGui.SameLine(0f, 2f);
        if (Gui.IconButton(Icons.X, "Stop editing"))
        {
            Editor.End();
            return;
        }

        foreach (var (tool, icon, label) in Tools)
        {
            if (Gui.IconButton(icon, label, Editor.Tool == tool))
            {
                Editor.Tool = tool;
            }

            ImGui.SameLine(0f, 2f);
        }

        ImGui.NewLine();
        Gui.Muted(Tools.First(t => t.Tool == Editor.Tool).Label);

        switch (Editor.Tool)
        {
            case TerrainTool.Height:
                DrawHeightBrush();
                break;
            case TerrainTool.Texture:
                DrawTextureBrush(project);
                break;
            case TerrainTool.Foliage:
                DrawFoliage(project);
                break;
            case TerrainTool.Props:
                DrawProps(project);
                break;
            default:
                break;
        }

        if (Editor.SelectedNode is { } node && project.World.CutPoints.GetProperties(node) is { } cutPoint)
        {
            DrawProperties(cutPoint);
        }
        else if (Editor.SelectedElement is { } element)
        {
            DrawElement(element);
        }

        if (ImGui.CollapsingHeader("World settings"))
        {
            DrawSettings(project);
        }

        if (ImGui.CollapsingHeader("History"))
        {
            DrawHistory();
        }

        if (ImGui.CollapsingHeader("Export for Unity"))
        {
            DrawExport(project);
        }
    }

    private void DrawHeightBrush()
    {
        var mode = (int)Editor.HeightMode;
        ImGui.RadioButton("Raise", ref mode, 0);
        ImGui.SameLine();
        ImGui.RadioButton("Lower", ref mode, 1);
        ImGui.SameLine();
        ImGui.RadioButton("Smooth", ref mode, 2);
        Editor.HeightMode = (HeightBrushMode)mode;
        Editor.HeightRadius = Slider("Radius", Editor.HeightRadius, 0.5, 30);
        Editor.HeightStrength = Slider("Strength", Editor.HeightStrength, 0.01, 2);
        if (Editor.SelectedElement is TerrainTile tile && ImGui.Button("Clear painted height of the selected tile") && tile.ClearPaintedHeight())
        {
            tile.Update();
            Editor.Commit("Clear Painted Terrain Height");
        }
    }

    private void DrawTextureBrush(TerrainProject project)
    {
        Editor.TextureRadius = Slider("Radius", Editor.TextureRadius, 0.5, 30);
        Editor.TextureOpacity = Slider("Opacity", Editor.TextureOpacity, 0.01, 1);
        Editor.TextureHardness = Slider("Hardness", Editor.TextureHardness, 0, 1);
        Editor.TextureSpacing = Slider("Spacing", Editor.TextureSpacing, 0.05, 1);
        Editor.TextureRotation = Slider("Rotation", Editor.TextureRotation, 0, 360);
        var shape = (int)Editor.TextureShape;
        var names = Enum.GetValues<TextureBrushShape>().Select(TerrainTexturePaint.ShapeName).ToArray();
        if (ImGui.Combo("Shape", ref shape, names, names.Length))
        {
            Editor.TextureShape = (TextureBrushShape)shape;
        }

        ImGui.SeparatorText("Layers (of every tile)");
        var tiles = project.World.Terrains.ToList();
        var first = tiles.FirstOrDefault();
        for (var layer = 0; layer < TerrainTexturePaint.LayerCount; layer++)
        {
            ImGui.PushID(layer);
            if (ImGui.RadioButton($"##layer{layer}", Editor.TextureLayer == layer))
            {
                Editor.TextureLayer = layer;
            }

            ImGui.SameLine();
            var texture = first?.EffectiveLayerTexture(layer) ?? "";
            if (TextureSlot($"Layer {layer + 1}", texture) is { } dropped)
            {
                tiles.ForEach(t => t.TextureLayers[layer].TexturePath = dropped);
                Editor.Commit("Set Paint Layer Texture");
            }

            var tiling = first?.TextureLayers[layer].Tiling ?? 8;
            if (Gui.InputNumber("Tiling", ref tiling))
            {
                tiles.ForEach(t => t.SetTextureLayerTiling(layer, tiling));
                Editor.Commit("Set Paint Layer Tiling");
            }

            ImGui.PopID();
        }

        if (Editor.SelectedElement is TerrainTile tile && ImGui.Button("Clear texture paint of the selected tile"))
        {
            tile.ClearTexturePaint();
            tile.Update();
            Editor.Commit("Clear Texture Paint");
        }
    }

    private void DrawFoliage(TerrainProject project)
    {
        Editor.FoliageRadius = Slider("Radius", Editor.FoliageRadius, 0.1, 25);
        var density = Editor.FoliageDensity;
        if (ImGui.SliderInt("Density", ref density, 1, 100))
        {
            Editor.FoliageDensity = density;
        }

        Editor.FoliageSpacing = Slider("Min. spacing", Editor.FoliageSpacing, 0, 5);
        Editor.FoliageMaxHeightDifference = Slider("Max. height difference", Editor.FoliageMaxHeightDifference, 0, 10);

        ImGui.SeparatorText("Presets (checked ones are painted)");
        var store = project.Foliage;
        for (var index = 0; index < store.Types.Count; index++)
        {
            ImGui.PushID(index);
            var painted = Editor.FoliageTypes.Contains(index);
            if (ImGui.Checkbox("##paint", ref painted))
            {
                _ = painted ? Editor.FoliageTypes.Add(index) : Editor.FoliageTypes.Remove(index);
            }

            ImGui.SameLine();
            if (ImGui.TreeNode($"{store.Types[index].DisplayName} ({store.GetInstances(index).Count})###type"))
            {
                DrawFoliageType(store, index);
                ImGui.TreePop();
            }

            ImGui.PopID();
        }

        if (ImGui.Button($"{Icons.Plus}  Add preset"))
        {
            Editor.FoliageTypes.Add(store.AddType(FoliageTypeData.CreateDefault($"Foliage {store.Types.Count + 1}")));
            Editor.Commit("Add Foliage Preset");
        }
    }

    private void DrawFoliageType(FoliageStore store, int index)
    {
        var type = store.Types[index];
        var changed = false;
        var name = type.DisplayName;
        if (ImGui.InputText("Name", ref name, 64) && name.Length > 0)
        {
            store.UpdateType(index, t => t.DisplayName = name);
            changed = true;
        }

        if (TextureSlot("Texture", type.TexturePath) is { } texture)
        {
            store.UpdateType(index, t => t.TexturePath = texture);
            changed = true;
        }

        changed |= Number("Min size", type.MinSize, v => store.UpdateType(index, t => t.MinSize = Math.Max(0.01, v)));
        changed |= Number("Max size", type.MaxSize, v => store.UpdateType(index, t => t.MaxSize = Math.Max(0.01, v)));
        changed |= Number("Length factor", type.LengthFactor, v => store.UpdateType(index, t => t.LengthFactor = Math.Max(0.01, v)));
        changed |= ColorEdit("Color A", type.ColorA, c => store.UpdateType(index, t => t.ColorA = c));
        changed |= ColorEdit("Color B", type.ColorB, c => store.UpdateType(index, t => t.ColorB = c));
        changed |= Number("Wind effectiveness", type.WindEffectiveness, v => store.UpdateType(index, t => t.WindEffectiveness = v));
        changed |= Number("Wind height offset", type.WindHeightOffset, v => store.UpdateType(index, t => t.WindHeightOffset = v));
        changed |= Number("Wind base offset", type.WindBaseOffset, v => store.UpdateType(index, t => t.WindBaseOffset = v));
        changed |= Number("Max draw distance", type.MaxDrawDistance, v => store.UpdateType(index, t => t.MaxDrawDistance = Math.Max(1, v)));
        changed |= Number("Alpha cutoff", type.AlphaCutoff, v => store.UpdateType(index, t => t.AlphaCutoff = Math.Clamp(v, 0, 1)));
        var shadows = type.CastShadows;
        if (ImGui.Checkbox("Cast shadows", ref shadows))
        {
            store.UpdateType(index, t => t.CastShadows = shadows);
            changed = true;
        }

        if (ImGui.SmallButton("Duplicate"))
        {
            store.DuplicateType(index);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("Remove"))
        {
            store.RemoveType(index);
            Editor.FoliageTypes.Clear();
            changed = true;
        }

        if (changed)
        {
            Editor.Commit("Edit Foliage Preset");
        }
    }

    private void DrawProps(TerrainProject project)
    {
        Editor.PropRadius = Slider("Erase radius", Editor.PropRadius, 0.1, 25);
        Editor.PropSpacing = Slider("Spacing", Editor.PropSpacing, 0.1, 20);
        var random = Editor.PropRandomRotation;
        if (ImGui.Checkbox("Random rotation", ref random))
        {
            Editor.PropRandomRotation = random;
        }

        ImGui.SeparatorText("Prop assets (drop a model from the Project panel)");
        var store = project.Meshes;
        for (var index = 0; index < store.Assets.Count; index++)
        {
            var asset = store.Assets[index];
            ImGui.PushID(index);
            if (ImGui.RadioButton($"{asset.DisplayName} ({store.GetInstances(index).Count})", Editor.PropAsset == index))
            {
                Editor.PropAsset = index;
            }

            if (Editor.PropAsset == index)
            {
                var changed = Number("Base scale", asset.BaseScale, v => store.UpdateAsset(index, baseScale: v));
                var offset = asset.PositionOffset.ToVec3();
                var value = new SysVector3((float)offset.X, (float)offset.Y, (float)offset.Z);
                if (ImGui.DragFloat3("Pivot offset", ref value, 0.01f))
                {
                    store.UpdateAsset(index, positionOffset: new Vec3(value.X, value.Y, value.Z));
                    changed = true;
                }

                if (ImGui.SmallButton("Center on ground") && PropGroups(asset) is { } groups)
                {
                    store.UpdateAsset(index, positionOffset: MeshCalibration.ComputeGroundedCenterOffset(MeshCalibration.MeasureBounds(groups), asset.BaseScale));
                    changed = true;
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                {
                    store.RemoveAsset(index);
                    Editor.PropAsset = -1;
                    changed = true;
                }

                if (changed)
                {
                    Editor.Commit("Edit Prop Asset");
                }
            }

            ImGui.PopID();
        }

        ImGui.Button("Drop a model here", new System.Numerics.Vector2(-1f, 0f));
        if (Gui.AcceptStringPayload(Gui.AssetPayload) is { } path && context.ResolveAsset(path) is { Type: AssetTypes.Model } model)
        {
            Editor.PropAsset = store.AddAsset(MeshAssetData.Create(Path.GetFileNameWithoutExtension(path), new AssetRef(model.Guid).ToString()));
            Editor.Commit("Add Prop Asset");
        }
    }

    /// <summary>A prop's LOD0 groups (one per submesh) from its model, for measuring.</summary>
    private List<MeshGroup>? PropGroups(MeshAssetData asset) =>
        AssetRef.TryParse(asset.LibraryPath, out var reference) && context.Resources.GetModelMesh(reference) is { } model
            ? [new MeshGroup(model.Name, [.. model.Mesh.Positions.SelectMany(p => (double[])[p.X, p.Y, p.Z])], [], Vec3.One, null)]
            : null;

    private void DrawElement(WorldElement element)
    {
        DrawProperties(element.GetProperties());
        if (ImGui.TreeNode("Textures and UVs"))
        {
            foreach (var group in element.GetGroupNames())
            {
                ImGui.PushID(group);
                var current = element.GetGroupTextureReferences().GetValueOrDefault(group) ?? "";
                if (TextureSlot(group, current) is { } texture)
                {
                    element.SetGroupTextureReference(group, texture);
                    Editor.Commit("Set Texture");
                }

                if (current.Length > 0)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton(Icons.X))
                    {
                        element.ClearGroupTextureReference(group);
                        Editor.Commit("Clear Texture");
                    }
                }

                var rotation = element.TextureRotations.GetValueOrDefault(group);
                if (Gui.InputNumber("Rotation", ref rotation))
                {
                    element.SetTextureRotation(group, rotation);
                    Editor.Commit("Rotate Texture");
                }

                if (element.GetUVGroups().Contains(group))
                {
                    var uv = element.GetUVTransform(group);
                    var values = new System.Numerics.Vector4((float)uv.OffsetX, (float)uv.OffsetY, (float)uv.ScaleX, (float)uv.ScaleY);
                    if (ImGui.DragFloat4("Offset / scale", ref values, 0.01f))
                    {
                        element.SetUVTransform(group, new UVTransform { OffsetX = values.X, OffsetY = values.Y, ScaleX = values.Z, ScaleY = values.W });
                        Changed();
                    }

                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        Editor.Commit("Edit UVs");
                    }
                }

                ImGui.PopID();
            }

            ImGui.TreePop();
        }

        if (ImGui.Button($"{Icons.Trash2}  Delete"))
        {
            Editor.DeleteSelection();
            return;
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy"))
        {
            Editor.Copy();
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy properties"))
        {
            Editor.Clipboard.CopyProperties(element);
        }

        if (Editor.Clipboard.CanPastePropertiesOnto(element))
        {
            ImGui.SameLine();
            if (ImGui.Button("Paste properties"))
            {
                Editor.Clipboard.PastePropertiesOnto(element);
                Editor.Commit("Paste Properties");
            }
        }

        if (ImGui.TreeNode("Presets"))
        {
            ImGui.InputText("Name", ref _presetName, 64);
            ImGui.SameLine();
            if (ImGui.Button("Save preset"))
            {
                Editor.Presets.SavePreset(_presetName, element);
                _presetName = "";
            }

            foreach (var preset in Editor.Presets.GetPresets().Where(p => p.ElementType == element.GetType().Name))
            {
                ImGui.PushID(preset.Id);
                if (ImGui.SmallButton("Apply") && Editor.Presets.ApplyPreset(preset.Id, element))
                {
                    Editor.Commit("Apply Preset");
                }

                ImGui.SameLine();
                if (ImGui.SmallButton(Editor.Presets.IsDefault(preset) ? $"{Icons.Check} Default" : "Make default"))
                {
                    Editor.Presets.SetDefault(preset.Id);
                }

                ImGui.SameLine();
                if (ImGui.SmallButton(Icons.Trash2))
                {
                    Editor.Presets.DeletePreset(preset.Id);
                }

                ImGui.SameLine();
                ImGui.TextUnformatted(preset.Name);
                ImGui.PopID();
            }

            ImGui.TreePop();
        }
    }

    /// <summary>An element's inspector, generated from its property definition (TerrainHeaven's PropertiesPanel).</summary>
    private void DrawProperties(PropertyDefinition definition)
    {
        ImGui.SeparatorText(definition.Title);
        foreach (var section in definition.Sections)
        {
            if (!ImGui.TreeNodeEx(section.Label, ImGuiTreeNodeFlags.DefaultOpen))
            {
                continue;
            }

            foreach (var property in section.Properties)
            {
                ImGui.PushID(property.Label);
                DrawProperty(property);
                ImGui.PopID();
            }

            ImGui.TreePop();
        }
    }

    private void DrawProperty(PropertyItem property)
    {
        switch (property)
        {
            case NumberProperty number:
                var value = number.Get();
                if (number.Min is { } min && number.Max is { } max && max > min)
                {
                    var f = (float)value;
                    if (ImGui.SliderFloat(number.Label, ref f, (float)min, (float)max, "%.3g"))
                    {
                        number.Set(f);
                        Changed();
                    }
                }
                else if (Gui.InputNumber(number.Label, ref value))
                {
                    number.Set(number.Min is { } low ? Math.Max(low, value) : value);
                    Changed();
                }

                break;
            case BooleanProperty boolean:
                var flag = boolean.Get();
                if (ImGui.Checkbox(boolean.Label, ref flag))
                {
                    boolean.Set(flag);
                    Changed();
                }

                break;
            case SelectProperty select:
                var options = select.Options;
                var index = Math.Max(0, options.ToList().FindIndex(o => o.Value == select.Get()));
                var labels = options.Select(o => o.Label).ToArray();
                if (ImGui.Combo(select.Label, ref index, labels, labels.Length))
                {
                    select.Set(options[index].Value);
                    Changed();
                }

                break;
            case Vector3Property vector:
                var v = vector.Get();
                var sys = new SysVector3((float)v.X, (float)v.Y, (float)v.Z);
                if (ImGui.DragFloat3(vector.Label, ref sys, 0.05f))
                {
                    vector.Set(new Vec3(sys.X, sys.Y, sys.Z));
                    Changed();
                }

                break;
            case ButtonProperty button:
                if (ImGui.Button(button.Label))
                {
                    button.OnClick();
                    Changed();
                    Editor.Commit(button.Label);
                }

                return;
            default:
                return;
        }

        if (ImGui.IsItemDeactivatedAfterEdit() || (property is BooleanProperty or SelectProperty && ImGui.IsItemEdited()))
        {
            Editor.Commit($"Edit {property.Label}");
        }
    }

    private void DrawSettings(TerrainProject project)
    {
        var settings = project.Settings;
        var lod = Editor.Target!.LodLevel;
        if (ImGui.SliderInt("LOD preview", ref lod, 0, 3))
        {
            Editor.Target.LodLevel = lod;
            project.World.Update();
            Editor.Target.MarkChanged();
        }

        for (var i = 0; i < 3; i++)
        {
            var distance = settings.LodDistances[i];
            if (Gui.InputNumber($"LOD {i}→{i + 1} distance", ref distance))
            {
                settings.SetLodDistance(i, distance);
                Editor.Commit("LOD Distance");
            }
        }

        var sky = settings.SkyColor;
        if (ImGui.InputText("Sky color", ref sky, 16))
        {
            settings.SkyColor = sky;
            Editor.Commit("Sky Color");
        }

        var enhanced = settings.EnhancedVisuals;
        if (ImGui.Checkbox("Enhanced visuals", ref enhanced))
        {
            settings.EnhancedVisuals = enhanced;
            Editor.Commit("Enhanced Visuals");
        }

        var cycle = settings.DayNightCycle;
        if (ImGui.Checkbox("Day/night cycle", ref cycle))
        {
            settings.DayNightCycle = cycle;
            Editor.Commit("Day/Night Cycle");
        }

        settings.Hour = Slider("Hour", settings.Hour, 0, 24);
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            Editor.Commit("Time of Day");
        }

        Gui.Muted("These settings travel with the world and the Unity export; the scene's lights stay the scene's.");
    }

    private void DrawHistory()
    {
        var history = Editor.History!;
        for (var i = history.Labels.Count - 1; i >= 0; i--)
        {
            if (ImGui.Selectable($"{i + 1}. {history.Labels[i]}##h{i}", i == history.Index))
            {
                history.JumpTo(i);
            }
        }
    }

    private void DrawExport(TerrainProject project)
    {
        ImGui.SliderInt("LOD levels", ref _exportLods, 1, 4);
        Gui.InputNumber("Scale", ref _exportScale);
        var folder = Path.Combine(context.Session.Project.RootDirectory, "Exports", Path.GetFileNameWithoutExtension(Editor.FilePath ?? "Terrain"));
        Gui.Muted(folder);
        if (ImGui.Button($"{Icons.ExternalLink}  Export"))
        {
            try
            {
                var manifest = new UnityExporter(context.Logger).Export(project, folder, new UnityExportOptions(_exportLods, _exportScale), new ExportSource(context, Editor.Target!.Project));
                var message = $"Terrain exported for Unity: {manifest.Objects.Count} objects, {manifest.Textures.Count} textures in {folder}";
                EditorLog.Info(context.Logger, message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                EditorLog.ActionFailed(context.Logger, ex, "Export terrain");
            }
        }
    }

    /// <summary>Texture bytes and prop meshes for the exporter, from the project's assets.</summary>
    private sealed class ExportSource(EditorContext context, AssetRef terrain) : ITerrainExportSource
    {
        public byte[]? ReadTexture(string projectPath) =>
            context.Resources.ResolveTerrainReference(terrain, projectPath) is { IsNone: false } asset ? context.Session.Assets.ReadAllBytes(asset.Guid) : null;

        public IReadOnlyList<MeshGroup>? LoadMesh(MeshAssetData asset)
        {
            if (context.Resources.ResolveTerrainReference(terrain, asset.LibraryPath) is not { IsNone: false } reference || context.Resources.GetModelMesh(reference) is not { } model)
            {
                return null;
            }

            var mesh = model.Mesh;
            return [.. mesh.SubMeshes.Select((sub, i) =>
            {
                var indices = Enumerable.Range(sub.IndexStart, sub.IndexCount).Select(k => (int)mesh.Indices[k]).ToList();
                return new MeshGroup(
                    $"{model.Name}_{i}",
                    [.. indices.SelectMany(v => (double[])[mesh.Positions[v].X, mesh.Positions[v].Y, mesh.Positions[v].Z])],
                    [.. indices.SelectMany(v => mesh.UV0 is { } uv ? (double[])[uv[v].X, 1 - uv[v].Y] : [0, 0])],
                    Vec3.One,
                    null);
            })];
        }
    }

    private void CreateWorld(WorldTerrain terrain)
    {
        var assets = context.Session.Assets;
        var name = string.Concat(terrain.GameObject.Name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '_' or '-')).Trim();
        var relative = $"Terrain/{(name.Length > 0 ? name : "World")}.mnvtterrain";
        for (var n = 2; File.Exists(Path.Combine(assets.AssetsDirectory, relative)); n++)
        {
            relative = $"Terrain/{(name.Length > 0 ? name : "World")} {n}.mnvtterrain";
        }

        var full = Path.Combine(assets.AssetsDirectory, relative);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var project = new TerrainProject();
            project.World.Add(new TerrainTile(Vec3.Zero, 60, 60));
            File.WriteAllText(full, project.Save());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EditorLog.ActionFailed(context.Logger, ex, "Create terrain world");
            return;
        }

        assets.Refresh();
        if (assets.TryGetGuid(relative, out var guid))
        {
            terrain.Project = new AssetRef(guid);
            context.Changed("Create Terrain World");
        }
    }

    /// <summary>Selects the WorldTerrain of the open scene that uses a world asset and starts editing it.</summary>
    private void OpenWorld(Guid world)
    {
        if (context.ActiveScene.FindObjectsOfType<WorldTerrain>().FirstOrDefault(t => t.Project.Guid == world) is not { } terrain)
        {
            var message = $"No WorldTerrain in the open scene uses '{context.Session.Assets.Get(world)?.Path}'. Add a GameObject with a WorldTerrain component and set its Project.";
            EditorLog.Warning(context.Logger, message);
            return;
        }

        context.Session.Selection.Select(terrain.GameObject);
        if (FileOf(world) is { } file && Editor.Begin(terrain, file))
        {
            Editor.Tool = TerrainTool.Select;
        }
    }

    /// <summary>Element presets are per project, in <c>Library/Editor/terrain-presets.json</c>, saved on every change.</summary>
    private void LoadPresets()
    {
        _presetsLoaded = true;
        var file = Path.Combine(context.Session.Project.RootDirectory, "Library", "Editor", "terrain-presets.json");
        try
        {
            if (File.Exists(file) && !Editor.Presets.Load(File.ReadAllText(file)))
            {
                EditorLog.Warning(context.Logger, "Terrain presets file is not a version 1 preset store; starting with no presets.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EditorLog.ActionFailed(context.Logger, ex, "Load terrain presets");
        }

        Editor.Presets.Changed += () =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, Editor.Presets.ToJson());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                EditorLog.ActionFailed(context.Logger, ex, "Save terrain presets");
            }
        };
    }

    private string? FileOf(Guid guid) => context.Session.Assets.Get(guid) is { } record ? Path.Combine(context.Session.Assets.AssetsDirectory, record.Path) : null;

    /// <summary>A texture field: shows the reference and takes a texture dropped from the Project panel (stored as an <c>asset:</c> reference).</summary>
    private string? TextureSlot(string label, string current)
    {
        var shown = AssetRef.TryParse(current, out var reference) && context.Session.Assets.Get(reference.Guid) is { } record ? record.Name : current.Length > 0 ? current : "None (drop a texture)";
        ImGui.Button($"{label}: {shown}##{label}", new System.Numerics.Vector2(-1f, 0f));
        return Gui.AcceptStringPayload(Gui.AssetPayload) is { } path && context.ResolveAsset(path) is { Type: AssetTypes.Texture } texture
            ? new AssetRef(texture.Guid).ToString()
            : null;
    }

    private void Changed()
    {
        Editor.Project?.World.FlushDirty();
        Editor.Target?.MarkChanged();
    }

    private static double Slider(string label, double value, double min, double max)
    {
        var f = (float)value;
        return ImGui.SliderFloat(label, ref f, (float)min, (float)max, "%.2f") ? f : value;
    }

    private static bool Number(string label, double value, Action<double> set)
    {
        if (!Gui.InputNumber(label, ref value))
        {
            return false;
        }

        set(value);
        return true;
    }

    private static bool ColorEdit(string label, FoliageColor color, Action<FoliageColor> set)
    {
        var c = new SysVector3((float)color.R, (float)color.G, (float)color.B);
        if (!ImGui.ColorEdit3(label, ref c))
        {
            return false;
        }

        set(new FoliageColor { R = c.X, G = c.Y, B = c.Z, A = color.A });
        return true;
    }
}
