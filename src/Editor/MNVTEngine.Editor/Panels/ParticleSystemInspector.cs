using System.Reflection;
using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Editor.Services;
using MNVTEngine.Mathematics;
using MNVTEngine.Particles;
using MNVTEngine.Serialization;
using SysVector2 = System.Numerics.Vector2;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// The Inspector section of a <see cref="ParticleSystem"/>: preview controls and statistics, presets, saving to and
/// loading from a <c>.mnvtparticle</c> asset, then one section per module (optional modules with an enable checkbox).
/// Fields are drawn from the module classes' properties; curves and gradients use <see cref="ParticleCurveEditor"/>.
/// The preview is the scene's own system drawn by the project's particle renderer (<see cref="ParticlePreview"/>).
/// </summary>
internal sealed class ParticleSystemInspector(EditorContext context)
{
    private static readonly string[] CurveModes = ["Constant", "Random", "Curve", "Random curves"];
    private static readonly string[] GradientModes = ["Color", "Random colors", "Gradient", "Random gradients", "Random from gradient"];

    private string? _status;

    private bool Planar => context.Session.Project.Profile == Modules.ProjectProfile.TwoD;

    public void Draw(ParticleSystem system)
    {
        DrawPreview(system);
        DrawAssetTools(system);
        var settings = system.Settings;
        var changed = false;
        changed |= Module("Main", settings.Main, null);
        changed |= Module("Emission", settings.Emission, () => settings.Emission.Enabled, v => settings.Emission.Enabled = v);
        changed |= Module("Shape", settings.Shape, () => settings.Shape.Enabled, v => settings.Shape.Enabled = v);
        changed |= Module("Velocity over Lifetime", settings.VelocityOverLifetime, () => settings.VelocityOverLifetime.Enabled, v => settings.VelocityOverLifetime.Enabled = v);
        changed |= Module("Force over Lifetime", settings.ForceOverLifetime, () => settings.ForceOverLifetime.Enabled, v => settings.ForceOverLifetime.Enabled = v);
        changed |= Module("Color over Lifetime", settings.ColorOverLifetime, () => settings.ColorOverLifetime.Enabled, v => settings.ColorOverLifetime.Enabled = v);
        changed |= Module("Size over Lifetime", settings.SizeOverLifetime, () => settings.SizeOverLifetime.Enabled, v => settings.SizeOverLifetime.Enabled = v);
        changed |= Module("Rotation over Lifetime", settings.RotationOverLifetime, () => settings.RotationOverLifetime.Enabled, v => settings.RotationOverLifetime.Enabled = v);
        changed |= Module("Texture Sheet Animation", settings.TextureSheetAnimation, () => settings.TextureSheetAnimation.Enabled, v => settings.TextureSheetAnimation.Enabled = v);
        changed |= Module("Noise", settings.Noise, () => settings.Noise.Enabled, v => settings.Noise.Enabled = v);
        changed |= Module("Collision", settings.Collision, () => settings.Collision.Enabled, v => settings.Collision.Enabled = v);
        changed |= Module("Sub Emitters", settings.SubEmitters, () => settings.SubEmitters.Enabled, v => settings.SubEmitters.Enabled = v);
        changed |= Module("Renderer", settings.Renderer, null);
        if (changed)
        {
            context.Changed("Edit Particle System");
        }
    }

    private bool Module(string title, object module, Func<bool>? enabled, Action<bool>? setEnabled = null)
    {
        ImGui.PushID(title);
        var changed = false;
        if (enabled is not null)
        {
            var on = enabled();
            if (ImGui.Checkbox("##enabled", ref on))
            {
                setEnabled!(on);
                changed = true;
            }

            ImGui.SameLine();
        }

        if (ImGui.CollapsingHeader(title, title is "Main" or "Emission" or "Shape" ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
        {
            foreach (var property in module.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite && p.Name != "Enabled"))
            {
                changed |= Field(module, property);
            }
        }

        ImGui.PopID();
        return changed;
    }

    private bool Field(object owner, PropertyInfo property)
    {
        ImGui.PushID(property.Name);
        var label = property.Name == nameof(MainModule.StartSize3D) ? "Separate Axes" : InspectableMember.Nicify(property.Name);
        var value = property.GetValue(owner);
        object? updated = null;
        var changed = false;
        switch (value)
        {
            case MinMaxCurve curve:
                changed = MinMax(label, curve);
                break;
            case MinMaxGradient gradient:
                changed = MinMax(label, gradient);
                break;
            case List<ParticleBurst> bursts:
                changed = Bursts(bursts);
                break;
            case List<CollisionPlane> planes:
                changed = ObjectList(label, planes);
                break;
            case List<SubEmitter> emitters:
                changed = ObjectList(label, emitters);
                break;
            case string text:
                InspectorPanel.Row(label);
                changed = ImGui.InputText("##v", ref text, 256);
                updated = text;
                break;
            case AssetRef asset:
                InspectorPanel.Row(label);
                (changed, updated) = AssetField(asset, property.Name == "Material" ? AssetTypes.Material : AssetTypes.Texture);
                break;
            default:
                InspectorPanel.Row(label);
                (changed, updated) = Simple(owner, property, value);
                break;
        }

        if (changed && updated is not null)
        {
            property.SetValue(owner, updated);
        }

        ImGui.PopID();
        return changed;
    }

    private (bool, object?) Simple(object owner, PropertyInfo property, object? value)
    {
        switch (value)
        {
            case float f:
                return (ImGui.DragFloat("##v", ref f, 0.05f, 0f, 0f, "%.4g"), f);
            case int i:
                return (ImGui.DragInt("##v", ref i, 1f, 0, int.MaxValue), i);
            case uint u:
                var asInt = (int)Math.Min(u, int.MaxValue);
                return (ImGui.DragInt("##v", ref asInt, 1f, 0, int.MaxValue), (uint)Math.Max(0, asInt));
            case bool b:
                return (ImGui.Checkbox("##v", ref b), b);
            case Vector3 v:
                Span<float> xyz = [v.X, v.Y, v.Z];
                return (InspectorPanel.VectorFields(xyz, 0.05f), new Vector3(xyz[0], xyz[1], xyz[2]));
            case Color c:
                var sys = c.ToSys();
                return (InspectorPanel.ColorField(ref sys), sys.ToColor());
            case Enum e:
                var names = Enum.GetNames(e.GetType()).Where(n => owner is not ShapeModule || property.Name != nameof(ShapeModule.Kind) || Available(Enum.Parse<ParticleShapeKind>(n))).ToArray();
                var index = Array.IndexOf(names, e.ToString());
                return ImGui.Combo("##v", ref index, [.. names.Select(InspectableMember.Nicify)], names.Length) && index >= 0
                    ? (true, Enum.Parse(e.GetType(), names[index]))
                    : (false, null);
            default:
                Gui.TextDisabled(value?.ToString() ?? "none");
                return (false, null);
        }
    }

    /// <summary>Shapes of the project's profile (Point is in both).</summary>
    private bool Available(ParticleShapeKind kind) => kind == ParticleShapeKind.Point || ShapeModule.IsPlanar(kind) == Planar;

    private static bool MinMax(string label, MinMaxCurve curve)
    {
        InspectorPanel.Row(label);
        var mode = (int)curve.Mode;
        ImGui.SetNextItemWidth(110f);
        var changed = ImGui.Combo("##mode", ref mode, CurveModes, CurveModes.Length);
        curve.Mode = (CurveMode)mode;
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        switch (curve.Mode)
        {
            case CurveMode.Constant:
                var max = curve.Max;
                changed |= ImGui.DragFloat("##max", ref max, 0.05f, 0f, 0f, "%.4g");
                curve.Max = max;
                break;
            case CurveMode.RandomBetweenConstants:
                var range = new SysVector2(curve.Min, curve.Max);
                changed |= ImGui.DragFloat2("##range", ref range, 0.05f, 0f, 0f, "%.4g");
                (curve.Min, curve.Max) = (range.X, range.Y);
                break;
            default:
                var multiplier = curve.Multiplier;
                changed |= ImGui.DragFloat("##multiplier", ref multiplier, 0.05f, 0f, 0f, "x %.4g");
                curve.Multiplier = multiplier;
                if (curve.Mode == CurveMode.RandomBetweenCurves)
                {
                    changed |= ParticleCurveEditor.Curve("min", curve.CurveMin ??= ParticleCurve.Constant(0f));
                }

                changed |= ParticleCurveEditor.Curve("max", curve.CurveMax ??= ParticleCurve.Constant(1f));
                break;
        }

        return changed;
    }

    private static bool MinMax(string label, MinMaxGradient gradient)
    {
        InspectorPanel.Row(label);
        var mode = (int)gradient.Mode;
        ImGui.SetNextItemWidth(-1f);
        var changed = ImGui.Combo("##mode", ref mode, GradientModes, GradientModes.Length);
        gradient.Mode = (GradientValueMode)mode;
        switch (gradient.Mode)
        {
            case GradientValueMode.Color:
            case GradientValueMode.RandomBetweenColors:
                if (gradient.Mode == GradientValueMode.RandomBetweenColors)
                {
                    InspectorPanel.Row("  Color A");
                    var a = gradient.ColorMin.ToSys();
                    changed |= InspectorPanel.ColorField(ref a);
                    gradient.ColorMin = a.ToColor();
                }

                InspectorPanel.Row(gradient.Mode == GradientValueMode.Color ? "  Color" : "  Color B");
                var b = gradient.ColorMax.ToSys();
                ImGui.PushID("b");
                changed |= InspectorPanel.ColorField(ref b);
                ImGui.PopID();
                gradient.ColorMax = b.ToColor();
                break;
            default:
                if (gradient.Mode == GradientValueMode.RandomBetweenGradients)
                {
                    changed |= ParticleCurveEditor.Gradient("min", gradient.GradientMin ??= ParticleGradient.Between(Color.White, Color.White));
                }

                changed |= ParticleCurveEditor.Gradient("max", gradient.GradientMax ??= ParticleGradient.Between(Color.White, Color.White));
                break;
        }

        return changed;
    }

    private static bool Bursts(List<ParticleBurst> bursts)
    {
        var changed = false;
        Gui.TextDisabled("Bursts: time, count (min, max), cycles (0 = repeat), interval, probability");
        var remove = -1;
        for (var i = 0; i < bursts.Count; i++)
        {
            var burst = bursts[i];
            ImGui.PushID(i);
            var width = (ImGui.GetContentRegionAvail().X - 30f) / 5f;
            var time = burst.Time;
            ImGui.SetNextItemWidth(width);
            changed |= ImGui.DragFloat("##time", ref time, 0.01f, 0f, 1000f, "%.2f s");
            ImGui.SameLine();
            var count = new SysVector2(burst.Count.Mode == CurveMode.RandomBetweenConstants ? burst.Count.Min : burst.Count.Max, burst.Count.Max);
            ImGui.SetNextItemWidth(width * 1.4f);
            if (ImGui.DragFloat2("##count", ref count, 0.5f, 0f, 100000f, "%.0f"))
            {
                burst.Count = count.X == count.Y ? new MinMaxCurve(count.X) : new MinMaxCurve(count.X, count.Y);
                changed = true;
            }

            ImGui.SameLine();
            var cycles = burst.Cycles;
            ImGui.SetNextItemWidth(width * 0.6f);
            changed |= ImGui.DragInt("##cycles", ref cycles, 0.1f, 0, 1000);
            ImGui.SameLine();
            var interval = burst.Interval;
            ImGui.SetNextItemWidth(width * 0.8f);
            changed |= ImGui.DragFloat("##interval", ref interval, 0.01f, 0.001f, 100f, "%.2f s");
            ImGui.SameLine();
            var probability = burst.Probability;
            ImGui.SetNextItemWidth(width * 0.7f);
            changed |= ImGui.SliderFloat("##p", ref probability, 0f, 1f, "%.2f");
            (burst.Time, burst.Cycles, burst.Interval, burst.Probability) = (time, cycles, interval, probability);
            ImGui.SameLine();
            if (ImGui.SmallButton("x"))
            {
                remove = i;
            }

            ImGui.PopID();
        }

        if (remove >= 0)
        {
            bursts.RemoveAt(remove);
            changed = true;
        }

        if (ImGui.SmallButton("Add burst"))
        {
            bursts.Add(new ParticleBurst());
            changed = true;
        }

        return changed;
    }

    /// <summary>A list of settings objects: one tree node per item with its fields, remove and add buttons.</summary>
    private bool ObjectList<T>(string label, List<T> items)
        where T : class, new()
    {
        var changed = false;
        var remove = -1;
        Gui.TextDisabled($"{label} ({items.Count})");
        for (var i = 0; i < items.Count; i++)
        {
            ImGui.PushID(i);
            var open = ImGui.TreeNode("##item", $"{InspectableMember.Nicify(typeof(T).Name)} {i + 1}");
            ImGui.SameLine();
            if (ImGui.SmallButton("x"))
            {
                remove = i;
            }

            if (open)
            {
                foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
                {
                    changed |= Field(items[i], property);
                }

                ImGui.TreePop();
            }

            ImGui.PopID();
        }

        if (remove >= 0)
        {
            items.RemoveAt(remove);
            changed = true;
        }

        if (ImGui.SmallButton($"Add##{label}"))
        {
            items.Add(new T());
            changed = true;
        }

        return changed;
    }

    private (bool, object?) AssetField(AssetRef value, string type)
    {
        var assets = context.Session.Assets;
        var record = value.IsNone ? null : assets.Get(value.Guid);
        var result = (false, (object?)value);
        if (ImGui.Button(value.IsNone ? $"None ({type})" : record?.Path ?? $"Missing ({value.Guid})", new SysVector2(-1f, 0f)))
        {
            ImGui.OpenPopup("##pick");
        }

        if (ImGui.BeginDragDropTarget())
        {
            if (Gui.AcceptStringPayload(Gui.AssetPayload) is { } path && context.ResolveAsset(path) is { } dropped && dropped.Type == type)
            {
                result = (true, new AssetRef(dropped.Guid));
            }

            ImGui.EndDragDropTarget();
        }

        if (ImGui.BeginPopup("##pick"))
        {
            if (ImGui.MenuItem("None"))
            {
                result = (true, AssetRef.None);
            }

            foreach (var candidate in assets.Assets.Where(a => a.Type == type).OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (ImGui.MenuItem(candidate.Path, null, candidate.Guid == value.Guid))
                {
                    result = (true, new AssetRef(candidate.Guid));
                }
            }

            ImGui.EndPopup();
        }

        return result;
    }

    private void DrawPreview(ParticleSystem system)
    {
        var preview = context.ParticlePreview;
        var simulation = system.Simulation;
        if (!context.PlayMode.IsPlaying)
        {
            if (ImGui.Button(preview.IsPaused ? "Play" : "Pause"))
            {
                if (preview.IsPaused)
                {
                    preview.Play();
                }
                else
                {
                    preview.Pause();
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Restart"))
            {
                preview.Restart();
            }

            ImGui.SameLine();
            if (ImGui.Button("Stop"))
            {
                preview.Stop();
            }

            ImGui.SameLine();
            var speed = preview.Speed;
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.SliderFloat("##speed", ref speed, 0f, 4f, "speed %.2fx"))
            {
                preview.Speed = speed;
            }
        }

        var particles = simulation.Particles;
        Gui.Muted($"Particles {particles.Count} / {system.Main.MaxParticles}   emitted {simulation.TotalEmitted}   cycle {simulation.CycleTime:0.00} s   seed {simulation.Seed}   {particles.MemoryBytes / 1024} KB");
        if (particles.Count > 0)
        {
            var oldest = 0f;
            var average = 0f;
            for (var i = 0; i < particles.Count; i++)
            {
                oldest = Math.Max(oldest, particles.Age[i]);
                average += particles.Lifetime[i];
            }

            Gui.Muted($"Oldest particle {oldest:0.00} s   average lifetime {average / particles.Count:0.00} s   modules: {string.Join(", ", system.Settings.EnabledModules().DefaultIfEmpty("none"))}");
        }
    }

    private void DrawAssetTools(ParticleSystem system)
    {
        ImGui.SetNextItemWidth(130f);
        if (ImGui.BeginCombo("##preset", "Preset..."))
        {
            foreach (var name in ParticlePresets.Names)
            {
                if (ImGui.Selectable(name))
                {
                    var preset = ParticlePresets.Create(name, Planar);
                    preset.Renderer.Texture = system.Settings.Renderer.Texture; // keep the chosen texture
                    system.Settings = preset;
                    context.ParticlePreview.Restart();
                    context.Changed($"Particle Preset {name}");
                }
            }

            ImGui.EndCombo();
        }

        var assets = context.Session.Assets;
        ImGui.SameLine();
        if (system.Asset.IsNone || assets.Get(system.Asset.Guid) is not { } record)
        {
            if (ImGui.Button("Save as Asset"))
            {
                var path = $"Particles/{system.GameObject.Name}{ParticleSystemAsset.Extension}";
                for (var i = 1; assets.TryGetGuid(path, out _); i++)
                {
                    path = $"Particles/{system.GameObject.Name} {i}{ParticleSystemAsset.Extension}";
                }

                system.Asset = new AssetRef(assets.WriteText(path, ParticleSystemAsset.Serialize(system.Settings)));
                context.Changed("Save Particle System");
            }

            return;
        }

        if (ImGui.Button("Apply to Asset"))
        {
            assets.WriteText(record.Path, ParticleSystemAsset.Serialize(system.Settings));
        }

        ImGui.SameLine();
        if (ImGui.Button("Revert to Asset"))
        {
            try
            {
                system.Settings = ParticleSystemAsset.Parse(System.Text.Encoding.UTF8.GetString(assets.ReadAllBytes(record.Guid)), record.Path);
                context.Changed("Revert Particle System");
            }
            catch (SerializationException ex)
            {
                _status = ex.Message;
            }
        }

        if (_status is not null)
        {
            Gui.TextWrapped(_status);
        }
    }
}
