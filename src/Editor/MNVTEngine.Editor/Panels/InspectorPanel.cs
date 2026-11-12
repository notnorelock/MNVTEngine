using ImGuiNET;
using Microsoft.Extensions.Logging;
using MNVTEngine.Runtime;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// Edits the selected GameObject, laid out like Unity's Inspector: a header (icon, active state, name), then a foldable
/// section per component (icon, enabled toggle, name, a menu) whose rows have the label on the left and the value on the
/// right; vectors as X/Y/Z fields, colors as swatches, references as object fields with a picker.
/// </summary>
internal sealed partial class InspectorPanel(EditorContext context) : IDisposable
{
    private readonly Dictionary<Type, IReadOnlyList<InspectableMember>> _members = [];
    private readonly AssetInspector _assetInspector = new(context);
    private readonly Dictionary<Guid, (string SourceHash, IReadOnlyList<string> Names)> _modelMeshes = [];
    private Component? _pendingRemove;

    /// <summary>Render phase: asset previews (material spheres).</summary>
    public void Render() => _assetInspector.Render();

    public void Dispose() => _assetInspector.Dispose();

    public void Draw()
    {
        if (ImGui.Begin(EditorWindows.Title("Inspector")) && context.Session.Selection.Asset is { } asset)
        {
            _assetInspector.Draw(asset);
        }
        else if (context.Selected is { } gameObject)
        {
            DrawHeader(gameObject);
            DrawTransform(gameObject.Transform);
            foreach (var component in gameObject.Components.ToArray())
            {
                if (component is not Transform)
                {
                    DrawComponent(component);
                }
            }

            ImGui.Spacing();
            DrawAddComponent(gameObject);
        }
        else
        {
            Gui.Muted("Select an object in the Hierarchy or Scene view, or an asset in the Project panel.");
        }

        ImGui.End();

        if (_pendingRemove is { } removed)
        {
            _pendingRemove = null;
            removed.Destroy();
            context.Changed("Remove Component");
        }
    }

    /// <summary>Drops cached member lists; required after a script reload replaced component types.</summary>
    public void ClearCaches() => _members.Clear();

    private void DrawHeader(GameObject gameObject)
    {
        // A large icon of what the object is, beside the active toggle and the name (two rows high).
        var (icon, iconColor) = HierarchyIcons.Primary(gameObject, open: false);
        var start = ImGui.GetCursorScreenPos();
        var iconSize = ImGui.GetFrameHeight() * 1.6f;
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize() * 1.9f, start + new SysVector2(2f, 2f), ImGui.GetColorU32(iconColor), icon);
        ImGui.SetCursorScreenPos(start + new SysVector2(iconSize + 8f, 0f));

        var active = gameObject.ActiveSelf;
        if (ImGui.Checkbox("##active", ref active))
        {
            gameObject.SetActive(active);
            context.Changed("Toggle Active");
        }

        if (ImGui.IsItemHovered())
        {
            Gui.Tooltip("Active: inactive objects and their children are not updated or drawn.");
        }

        ImGui.SameLine();
        var name = gameObject.Name;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##name", ref name, 128) && name.Length > 0)
        {
            gameObject.Name = name;
            context.Changed("Rename");
        }

        ImGui.SetCursorScreenPos(new SysVector2(start.X + iconSize + 8f, ImGui.GetCursorScreenPos().Y));
        Gui.Muted($"id {gameObject.Id}");
        ImGui.Spacing();
    }

    /// <summary>
    /// A component's foldout header: arrow, icon, enabled toggle (when it can be disabled), name and a menu button. The
    /// header's context menu and the menu button open the same menu. Returns whether it is unfolded.
    /// </summary>
    private bool ComponentHeader(Component component, string title, bool canDisable)
    {
        var open = ImGui.CollapsingHeader("##header", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap | ImGuiTreeNodeFlags.SpanAvailWidth);
        var (min, max) = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        if (ImGui.BeginPopupContextItem("##component_menu"))
        {
            DrawComponentMenu(component);
            ImGui.EndPopup();
        }

        var (icon, color) = HierarchyIcons.For(component);
        var font = ImGui.GetFontSize();
        var y = min.Y + ((max.Y - min.Y - font) * 0.5f);
        var x = min.X + ImGui.GetTreeNodeToLabelSpacing();
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddText(new SysVector2(x, y), ImGui.GetColorU32(color), icon);
        x += font + 6f;
        if (canDisable)
        {
            ImGui.SetCursorScreenPos(new SysVector2(x, min.Y));
            var enabled = component.Enabled;
            using (Gui.StyleVar(ImGuiStyleVar.FramePadding, new SysVector2(2f, 2f)))
            {
                if (ImGui.Checkbox("##enabled", ref enabled))
                {
                    component.Enabled = enabled;
                    context.Changed("Toggle Component");
                }
            }

            x = ImGui.GetItemRectMax().X + 6f;
        }

        // The name in bold: drawn twice, one pixel apart.
        var textColor = ImGui.GetColorU32(ImGuiCol.Text);
        drawList.AddText(new SysVector2(x, y), textColor, title);
        drawList.AddText(new SysVector2(x + 0.6f, y), textColor, title);

        ImGui.SetCursorScreenPos(new SysVector2(max.X - ImGui.GetFrameHeight(), min.Y));
        if (Gui.IconButton(Icons.EllipsisVertical, "Component menu"))
        {
            ImGui.OpenPopup("##component_menu");
        }

        if (ImGui.BeginPopup("##component_menu"))
        {
            DrawComponentMenu(component);
            ImGui.EndPopup();
        }

        ImGui.SetCursorScreenPos(new SysVector2(min.X - ImGui.GetStyle().FramePadding.X + ImGui.GetStyle().FramePadding.X, max.Y + ImGui.GetStyle().ItemSpacing.Y));
        ImGui.Dummy(SysVector2.Zero);
        return open;
    }

    /// <summary>
    /// Starts a property row: the label in the left column (clipped to it), the cursor in the right column with the next
    /// item filling it.
    /// </summary>
    private static void Row(string label, string? tooltip = null)
    {
        var width = MathF.Max(100f, ImGui.GetContentRegionAvail().X * 0.38f);
        var start = ImGui.GetCursorScreenPos();
        ImGui.AlignTextToFramePadding();
        ImGui.PushClipRect(start, start + new SysVector2(width - 6f, ImGui.GetFrameHeight()), true);
        ImGui.TextUnformatted(label);
        ImGui.PopClipRect();
        if (tooltip is not null && ImGui.IsItemHovered())
        {
            Gui.Tooltip(tooltip);
        }

        ImGui.SameLine(ImGui.GetCursorStartPos().X + width + (start.X - ImGui.GetWindowPos().X - ImGui.GetCursorStartPos().X));
        ImGui.SetNextItemWidth(-1f);
    }

    private static readonly SysVector4[] AxisColors = [new(0.95f, 0.45f, 0.42f, 1f), new(0.55f, 0.85f, 0.4f, 1f), new(0.45f, 0.65f, 1f, 1f), new(0.75f, 0.75f, 0.78f, 1f)];

    /// <summary>X/Y/Z(/W) number fields side by side, each with its axis letter; drag a field or type into it.</summary>
    private static bool VectorFields(Span<float> values, float speed)
    {
        var changed = false;
        var letter = ImGui.CalcTextSize("X").X + 4f;
        var spacing = 4f;
        var width = (ImGui.GetContentRegionAvail().X - (values.Length * letter) - ((values.Length - 1) * spacing)) / values.Length;
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(0f, spacing);
            }

            Gui.TextColored(AxisColors[i], "XYZW"[i].ToString());
            ImGui.SameLine(0f, 4f);
            ImGui.SetNextItemWidth(MathF.Max(width, 20f));
            changed |= ImGui.DragFloat($"##axis{i}", ref values[i], speed, 0f, 0f, "%.4g");
        }

        return changed;
    }

    /// <summary>A color as a swatch filling the field; click for a picker.</summary>
    private static bool ColorField(ref SysVector4 color)
    {
        var changed = false;
        if (ImGui.ColorButton("##swatch", color, ImGuiColorEditFlags.AlphaPreviewHalf, new SysVector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight())))
        {
            ImGui.OpenPopup("##picker");
        }

        if (ImGui.BeginPopup("##picker"))
        {
            changed = ImGui.ColorPicker4("##color", ref color, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf);
            ImGui.EndPopup();
        }

        return changed;
    }

    /// <summary>
    /// An object field: the referenced object's icon and name in a field, and a picker button on its right. Returns
    /// whether the picker was clicked (the caller opens its menu, the same one as the field's context menu).
    /// </summary>
    private static bool ObjectField(string icon, string text, bool missing, string? tooltip)
    {
        var picker = ImGui.GetFrameHeight();
        var width = ImGui.GetContentRegionAvail().X - picker - 2f;
        using (Gui.StyleColor(ImGuiCol.Text, new SysVector4(1f, 0.45f, 0.45f, 1f), missing))
        {
            using (Gui.StyleVar(ImGuiStyleVar.ButtonTextAlign, new SysVector2(0f, 0.5f)))
            {
                using (Gui.StyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]))
                {
                    ImGui.Button($"{icon}  {text}##field", new SysVector2(width, 0f));
                }
            }
        }

        if (tooltip is not null && ImGui.IsItemHovered())
        {
            Gui.Tooltip(tooltip);
        }

        return false;
    }

    private void DrawTransform(Transform transform)
    {
        ImGui.PushID("transform");
        if (ComponentHeader(transform, "Transform", canDisable: false))
        {
            Span<float> v = stackalloc float[3];
            (v[0], v[1], v[2]) = (transform.LocalPosition.X, transform.LocalPosition.Y, transform.LocalPosition.Z);
            ImGui.PushID("position");
            Row("Position");
            if (VectorFields(v, 0.05f))
            {
                transform.LocalPosition = new Vector3(v[0], v[1], v[2]);
                context.Changed("Move");
            }

            ImGui.PopID();

            // Euler angles in degrees; the transform remembers them, so editing one does not disturb the others.
            var euler = transform.LocalEulerAngles;
            (v[0], v[1], v[2]) = (euler.X, euler.Y, euler.Z);
            ImGui.PushID("rotation");
            Row("Rotation");
            if (VectorFields(v, 0.5f))
            {
                transform.LocalEulerAngles = new Vector3(v[0], v[1], v[2]);
                context.Changed("Rotate");
            }

            ImGui.PopID();
            (v[0], v[1], v[2]) = (transform.LocalScale.X, transform.LocalScale.Y, transform.LocalScale.Z);
            ImGui.PushID("scale");
            Row("Scale");
            if (VectorFields(v, 0.01f))
            {
                transform.LocalScale = new Vector3(v[0], v[1], v[2]);
                context.Changed("Scale");
            }

            ImGui.PopID();
            ImGui.Spacing();
        }

        ImGui.PopID();
    }

    private void DrawComponent(Component component)
    {
        ImGui.PushID(component.Id.ToString());
        if (component is MissingComponent missing)
        {
            if (ComponentHeader(component, $"Missing: {missing.TypeId}", canDisable: false))
            {
                Gui.TextColored(new System.Numerics.Vector4(1f, 0.7f, 0.3f, 1f), "Type is not registered. Data is kept and saved unchanged.");
                Gui.Muted(missing.Data.ToString(Newtonsoft.Json.Formatting.None));
            }

            ImGui.PopID();
            return;
        }

        if (ComponentHeader(component, InspectableMember.Nicify(component.GetType().Name), canDisable: true))
        {
            foreach (var member in MembersOf(component.GetType()))
            {
                DrawMember(component, member);
            }

            if (component is Tilemap tilemap)
            {
                DrawTilePalette(tilemap);
            }

            if (component is MeshRenderer meshRenderer)
            {
                DrawBlendShapes(meshRenderer);
            }

            if (component is WorldTerrain worldTerrain)
            {
                context.TerrainTools?.DrawInspector(worldTerrain);
            }

            if (component is AudioMixer mixer)
            {
                DrawMixer(mixer);
            }

            ImGui.Spacing();
        }

        ImGui.PopID();
    }

    /// <summary>One row per group: name, parent, volume, mute; Master's settings are the members above.</summary>
    private void DrawMixer(AudioMixer mixer)
    {
        ImGui.SeparatorText("Groups");
        AudioMixerGroup? remove = null;
        foreach (var group in mixer.Groups)
        {
            ImGui.PushID(group.GetHashCode());
            var name = group.Name;
            var width = ImGui.GetContentRegionAvail().X;
            ImGui.SetNextItemWidth((width * 0.5f) - 30f);
            if (ImGui.InputText("##name", ref name, 48, ImGuiInputTextFlags.EnterReturnsTrue) && name.Trim().Length > 0 && mixer.Find(name.Trim()) is null && name.Trim() != AudioMixer.MasterGroup)
            {
                group.Name = name.Trim();
                context.Changed("Rename Mixer Group");
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth((width * 0.5f) - 30f);
            if (ImGui.BeginCombo("##parent", group.Parent.Length == 0 ? AudioMixer.MasterGroup : group.Parent))
            {
                foreach (var parent in mixer.Groups.Where(g => g != group).Select(g => g.Name).Prepend(""))
                {
                    if (ImGui.Selectable(parent.Length == 0 ? AudioMixer.MasterGroup : parent, parent == group.Parent))
                    {
                        group.Parent = parent;
                        context.Changed("Edit Mixer");
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(Icons.X))
            {
                remove = group;
            }

            ImGui.SetNextItemWidth(width - 80f);
            var volume = group.Volume;
            if (ImGui.SliderFloat("##volume", ref volume, 0f, 1f, "%.2f"))
            {
                group.Volume = volume;
                context.Changed("Edit Mixer");
            }

            ImGui.SameLine();
            var muted = group.Muted;
            if (ImGui.Checkbox("Mute", ref muted))
            {
                group.Muted = muted;
                context.Changed("Edit Mixer");
            }

            ImGui.Spacing();
            ImGui.PopID();
        }

        if (remove is not null)
        {
            mixer.Groups.Remove(remove);
            context.Changed("Remove Mixer Group");
        }

        if (ImGui.Button($"{Icons.Plus}  Group"))
        {
            var index = 1;
            while (mixer.Find($"Group {index}") is not null)
            {
                index++;
            }

            mixer.Groups.Add(new AudioMixerGroup { Name = $"Group {index}" });
            context.Changed("Add Mixer Group");
        }

        Gui.Muted("AudioSource.Group picks the group; a group's volume multiplies its parents' up to Master.");
    }

    /// <summary>Paint toggle, eraser and the sheet's tiles as buttons; the Scene view paints with the chosen tile.</summary>
    /// <summary>A slider per blend shape of the renderer's model mesh (0–1), with Reset back to the model's defaults.</summary>
    private void DrawBlendShapes(MeshRenderer renderer)
    {
        if (context.Resources.GetModelMesh(renderer.Mesh) is not { BlendShapes.Count: > 0 } mesh)
        {
            return;
        }

        ImGui.SeparatorText($"Blend shapes ({mesh.BlendShapes.Count})");
        for (var i = 0; i < mesh.BlendShapes.Count; i++)
        {
            var weight = renderer.GetBlendShapeWeightOrDefault(i, mesh.DefaultBlendShapeWeights);
            ImGui.PushID(i);
            if (ImGui.SliderFloat(mesh.BlendShapes[i].Name, ref weight, 0f, 1f, "%.2f"))
            {
                // Unset shapes keep their defaults when a later one is set.
                for (var s = renderer.BlendShapeWeights.Count; s < i; s++)
                {
                    renderer.SetBlendShapeWeight(s, renderer.GetBlendShapeWeightOrDefault(s, mesh.DefaultBlendShapeWeights));
                }

                renderer.SetBlendShapeWeight(i, weight);
            }

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                context.Changed("Blend Shape");
            }

            ImGui.PopID();
        }

        if (ImGui.SmallButton($"{Icons.Undo2}  Reset to model defaults"))
        {
            renderer.ResetBlendShapeWeights();
            context.Changed("Blend Shape");
        }
    }

    private void DrawTilePalette(Tilemap tilemap)
    {
        var brush = context.Tiles;
        var active = brush.Active;
        if (ImGui.Checkbox("Paint in Scene view (B)", ref active))
        {
            brush.Active = active;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("Eraser", brush.Tile < 0))
        {
            brush.Tile = -1;
        }

        Gui.Muted($"{tilemap.Count} tiles. Left drag paints, Ctrl + left drag erases.");
        if (tilemap.Texture.IsNone)
        {
            Gui.Muted("Assign a sheet texture to show the palette.");
            return;
        }

        var texture = context.Resources.GetTexture(tilemap.Texture);
        var id = context.ImGui.Register(texture);
        var rows = Math.Max(1, texture.Height / tilemap.TileHeight);
        var count = Math.Min(rows * tilemap.Columns, 1024);
        var perLine = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / 40f));
        for (var tile = 0; tile < count; tile++)
        {
            var rect = tilemap.SourceRect(tile);
            if (rect.X + rect.Width > texture.Width || rect.Y + rect.Height > texture.Height)
            {
                break;
            }

            if (tile % perLine != 0)
            {
                ImGui.SameLine();
            }

            var uv0 = new SysVector2(rect.X / texture.Width, rect.Y / texture.Height);
            var uv1 = new SysVector2((rect.X + rect.Width) / texture.Width, (rect.Y + rect.Height) / texture.Height);
            using (Gui.StyleColor(ImGuiCol.Button, new System.Numerics.Vector4(1f, 0.6f, 0.1f, 1f), brush.Tile == tile))
            {
                if (ImGui.ImageButton($"tile{tile}", id, new SysVector2(32f, 32f), uv0, uv1))
                {
                    brush.Tile = tile; // changes the push condition: the scope still pops what it pushed
                    brush.Active = true;
                }
            }

            if (ImGui.IsItemHovered())
            {
                Gui.Tooltip($"Tile {tile}");
            }
        }
    }

    private void DrawComponentMenu(Component component)
    {
        if (ImGui.MenuItem($"{Icons.Trash2}  Remove Component", null, false, component is not Transform))
        {
            _pendingRemove = component;
        }
    }

    private void DrawMember(Component component, InspectableMember member)
    {
        var label = $"##{member.DisplayName}";
        var value = member.GetValue(component);
        object? updated = null;
        var changed = false;

        ImGui.PushID(member.DisplayName);
        Row(member.DisplayName);
        switch (member.Kind)
        {
            case MemberKind.Float:
                var f = (float)value!;
                changed = ImGui.DragFloat(label, ref f, 0.05f, 0f, 0f, "%.4g");
                updated = f;
                break;
            case MemberKind.Int:
                var i = (int)value!;
                changed = ImGui.DragInt(label, ref i);
                updated = i;
                break;
            case MemberKind.Bool:
                var b = (bool)value!;
                changed = ImGui.Checkbox(label, ref b);
                updated = b;
                break;
            case MemberKind.String:
                var s = (string?)value ?? "";
                changed = ImGui.InputText(label, ref s, 512);
                updated = s;
                break;
            case MemberKind.AssetPath:
                var path = (string?)value ?? "";
                changed = ImGui.InputText(label, ref path, 512);
                updated = path.Length == 0 ? null : path;
                if (ImGui.BeginDragDropTarget())
                {
                    if (Gui.AcceptStringPayload(Gui.AssetPayload) is { } dropped && member.AcceptsAsset(dropped))
                    {
                        changed = true;
                        updated = dropped;
                    }

                    ImGui.EndDragDropTarget();
                }

                break;
            case MemberKind.Vector2:
                {
                    var v2 = (Vector2)value!;
                    Span<float> xy = [v2.X, v2.Y];
                    changed = VectorFields(xy, 0.05f);
                    updated = new Vector2(xy[0], xy[1]);
                    break;
                }

            case MemberKind.Vector3:
                {
                    var v3 = (Vector3)value!;
                    Span<float> xyz = [v3.X, v3.Y, v3.Z];
                    changed = VectorFields(xyz, 0.05f);
                    updated = new Vector3(xyz[0], xyz[1], xyz[2]);
                    break;
                }

            case MemberKind.Color:
                var c = ((Color)value!).ToSys();
                changed = ColorField(ref c);
                updated = c.ToColor();
                break;
            case MemberKind.Enum:
                var names = Enum.GetNames(member.Type);
                var index = Array.IndexOf(names, value!.ToString());
                if (ImGui.Combo(label, ref index, names, names.Length))
                {
                    changed = true;
                    updated = Enum.Parse(member.Type, names[index]);
                }

                break;
            case MemberKind.AssetReference:
                (changed, updated) = DrawAssetReference(member, (AssetRef)value!);
                break;
            case MemberKind.GameObjectReference or MemberKind.ComponentReference:
                (changed, updated) = DrawReference(member, value);
                break;
            default:
                Gui.TextDisabled(value is System.Collections.ICollection items
                    ? $"{items.Count} items"
                    : $"{value?.ToString() ?? "none"} (not editable)");
                break;
        }

        ImGui.PopID();

        if (changed)
        {
            if (member.TrySetValue(component, updated, out var error))
            {
                context.Changed("Edit Property");
            }
            else
            {
                LogRejected(context.Logger, error!);
            }
        }
    }

    private (bool Changed, object? Value) DrawReference(InspectableMember member, object? value)
    {
        var text = value switch
        {
            GameObject g => g.Name,
            Component c => $"{c.GameObject.Name} ({c.GetType().Name})",
            _ => $"None ({member.Type.Name})",
        };

        var icon = value switch
        {
            GameObject g => HierarchyIcons.Primary(g, open: false).Icon,
            Component c => HierarchyIcons.For(c).Icon,
            _ => Icons.CircleDashed,
        };

        ObjectField(icon, text, missing: false, tooltip: "Drop an object from the Hierarchy; right-click to clear.");
        var result = (false, value);
        if (ImGui.BeginDragDropTarget())
        {
            if (Gui.AcceptGuidPayload() is { } id && context.ActiveScene.FindById(id) is { } dropped)
            {
                if (MemberInspector.ResolveReference(member, dropped) is { } resolved)
                {
                    result = (true, resolved);
                }
                else
                {
                    LogRejected(context.Logger, $"{dropped.Name} has no {member.Type.Name}.");
                }
            }

            ImGui.EndDragDropTarget();
        }

        if (ImGui.BeginPopupContextItem())
        {
            if (ImGui.MenuItem("Clear"))
            {
                result = (true, null);
            }

            ImGui.EndPopup();
        }

        ImGui.SameLine(0f, 2f);
        using (Gui.StyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]))
        {
            Gui.IconButton(Icons.CircleDot, "Drag an object here from the Hierarchy");
        }

        return result;
    }

    /// <summary>Mesh names of an imported model, read from its artifact once per import (none when it never imported).</summary>
    private IReadOnlyList<string> ModelMeshNames(Assets.AssetRecord model)
    {
        if (_modelMeshes.TryGetValue(model.Guid, out var cached) && cached.SourceHash == model.SourceHash)
        {
            return cached.Names;
        }

        IReadOnlyList<string> names = [];
        if (context.Session.Assets.ReadArtifact(model.Guid, Assets.Models.ModelArtifact.Name) is { } artifact)
        {
            try
            {
                names = [.. Assets.Models.ModelArtifact.Read(artifact).Meshes.Select(m => m.Name)];
            }
            catch (InvalidDataException ex)
            {
                EditorLog.ActionFailed(context.Logger, ex, $"Read model {model.Path}");
            }
        }

        _modelMeshes[model.Guid] = (model.SourceHash, names);
        return names;
    }

    private (bool Changed, object? Value) DrawAssetReference(InspectableMember member, AssetRef value)
    {
        var assets = context.Session.Assets;
        var record = value.IsNone ? null : assets.Get(value.Guid);
        var text = value.IsNone ? $"None ({member.AssetType ?? "Asset"})" : (record?.Name ?? $"Missing ({value.Guid})") + (value.SubAsset is { } sub ? $" #{sub}" : "");
        var icon = record is null ? Icons.CircleDashed : ProjectPanel.AssetIcon(record.Type);
        ObjectField(icon, text, record is null && !value.IsNone, record is null ? null : value.SubAsset is { } tip ? $"{record.Path}#{tip}" : record.Path);
        var result = (false, (object?)value);
        if (ImGui.BeginDragDropTarget())
        {
            if (Gui.AcceptStringPayload(Gui.AssetPayload) is { } path && context.ResolveAsset(path) is { } dropped)
            {
                if (member.AcceptsAssetType(dropped.Type))
                {
                    result = (true, new AssetRef(dropped.Guid));
                }
                else
                {
                    LogRejected(context.Logger, $"{member.DisplayName} needs a {member.AssetType}, not a {dropped.Type}.");
                }
            }

            ImGui.EndDragDropTarget();
        }

        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            ImGui.OpenPopup("##pick_asset");
        }

        ImGui.SameLine(0f, 2f);
        using (Gui.StyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]))
        {
            if (Gui.IconButton(Icons.CircleDot, $"Pick a {member.AssetType ?? "asset"}"))
            {
                ImGui.OpenPopup("##pick_asset");
            }
        }

        if (ImGui.BeginPopup("##pick_asset"))
        {
            if (ImGui.MenuItem("Clear"))
            {
                result = (true, AssetRef.None);
            }

            ImGui.Separator();
            foreach (var candidate in assets.Assets.Where(a => member.AcceptsAssetType(a.Type)).OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (candidate.Type == Serialization.AssetTypes.Model)
                {
                    // A model's meshes are sub-assets: "asset:<guid>#mesh/<name>".
                    if (ImGui.BeginMenu(candidate.Path))
                    {
                        foreach (var mesh in ModelMeshNames(candidate))
                        {
                            var part = Runtime.ModelCache.MeshSubAssetPrefix + mesh;
                            if (ImGui.MenuItem(mesh, null, candidate.Guid == value.Guid && value.SubAsset == part))
                            {
                                result = (true, new AssetRef(candidate.Guid, part));
                            }
                        }

                        ImGui.EndMenu();
                    }
                }
                else if (ImGui.MenuItem(candidate.Path, null, candidate.Guid == value.Guid))
                {
                    result = (true, new AssetRef(candidate.Guid));
                }
            }

            ImGui.EndPopup();
        }

        return result;
    }

    private void DrawAddComponent(GameObject gameObject)
    {
        var width = MathF.Min(260f, ImGui.GetContentRegionAvail().X);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((ImGui.GetContentRegionAvail().X - width) * 0.5f));
        if (ImGui.Button($"{Icons.Plus}  Add Component", new SysVector2(width, 0f)))
        {
            ImGui.OpenPopup("add_component");
        }

        if (ImGui.BeginPopup("add_component"))
        {
            foreach (var (id, type) in context.Session.Registry.Types.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                if (type == typeof(Transform))
                {
                    continue;
                }

                var present = gameObject.Components.Any(c => c.GetType() == type);
                if (ImGui.MenuItem(id, null, false, !present))
                {
                    gameObject.AddComponent(type);
                    context.Changed("Add Component");
                }
            }

            ImGui.EndPopup();
        }
    }

    private IReadOnlyList<InspectableMember> MembersOf(Type type)
    {
        if (!_members.TryGetValue(type, out var members))
        {
            members = MemberInspector.GetMembers(type);
            _members.Add(type, members);
        }

        return members;
    }

    [LoggerMessage(EventId = 5000, Level = LogLevel.Warning, Message = "Edit rejected: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);
}
