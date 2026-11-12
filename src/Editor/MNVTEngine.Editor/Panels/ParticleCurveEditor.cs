using ImGuiNET;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Mathematics;
using MNVTEngine.Particles;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.Editor.Panels;

/// <summary>ImGui editors for particle curves and gradients: a graph with draggable keys and key tables, a gradient bar with color and alpha keys.</summary>
internal static class ParticleCurveEditor
{
    private const float GraphHeight = 90f;
    private const int Samples = 64;

    /// <summary>Draws <paramref name="curve"/> as a graph (drag a key to move it) with its keys below; true when changed.</summary>
    public static bool Curve(string id, ParticleCurve curve)
    {
        ImGui.PushID(id);
        var changed = Graph(curve);
        var keys = curve.Keys;
        if (ImGui.TreeNode("##keys", $"{keys.Count} keys"))
        {
            var remove = -1;
            for (var i = 0; i < keys.Count; i++)
            {
                ImGui.PushID(i);
                var k = keys[i];
                var values = new System.Numerics.Vector4(k.Time, k.Value, k.InTangent, k.OutTangent);
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 28f);
                if (ImGui.DragFloat4("##key", ref values, 0.01f, 0f, 0f, "%.3g"))
                {
                    keys[i] = new CurveKey(values.X, values.Y, values.Z, values.W);
                    changed = true;
                }

                if (ImGui.IsItemHovered())
                {
                    Gui.Tooltip("Time, value, in tangent, out tangent");
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("x") && keys.Count > 1)
                {
                    remove = i;
                }

                ImGui.PopID();
            }

            if (remove >= 0)
            {
                keys.RemoveAt(remove);
                changed = true;
            }

            if (ImGui.SmallButton("Add key"))
            {
                keys.Add(new CurveKey(0.5f, curve.Evaluate(0.5f)));
                changed = true;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("0 to 1"))
            {
                curve.Keys = ParticleCurve.Linear(0f, 1f).Keys;
                changed = true;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("1 to 0"))
            {
                curve.Keys = ParticleCurve.Linear(1f, 0f).Keys;
                changed = true;
            }

            ImGui.TreePop();
        }

        if (changed)
        {
            curve.Keys = curve.Keys; // re-sort after edits
        }

        ImGui.PopID();
        return changed;
    }

    /// <summary>Draws <paramref name="gradient"/> as a bar with its color and alpha keys below; true when changed.</summary>
    public static bool Gradient(string id, ParticleGradient gradient)
    {
        ImGui.PushID(id);
        Bar(gradient);
        var changed = false;
        if (ImGui.TreeNode("##gradient", $"{gradient.ColorKeys.Count} color keys, {gradient.AlphaKeys.Count} alpha keys"))
        {
            var mode = (int)gradient.Mode;
            if (ImGui.Combo("Mode", ref mode, "Blend\0Fixed\0"))
            {
                gradient.Mode = (GradientMode)mode;
                changed = true;
            }

            changed |= KeyRows("color", gradient.ColorKeys, (i, key) =>
            {
                var time = key.Time;
                var rgb = new System.Numerics.Vector3(key.Color.R, key.Color.G, key.Color.B);
                var edited = TimeField(ref time);
                ImGui.SameLine();
                edited |= ImGui.ColorEdit3("##c", ref rgb, ImGuiColorEditFlags.NoInputs);
                return edited ? new GradientColorKey(time, new Color(rgb.X, rgb.Y, rgb.Z)) : null;
            }, () => new GradientColorKey(0.5f, gradient.Evaluate(0.5f)));
            changed |= KeyRows("alpha", gradient.AlphaKeys, (i, key) =>
            {
                var time = key.Time;
                var alpha = key.Alpha;
                var edited = TimeField(ref time);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 28f);
                edited |= ImGui.SliderFloat("##a", ref alpha, 0f, 1f, "alpha %.2f");
                return edited ? new GradientAlphaKey(time, alpha) : null;
            }, () => new GradientAlphaKey(0.5f, gradient.Evaluate(0.5f).A));
            ImGui.TreePop();
        }

        if (changed)
        {
            gradient.ColorKeys = gradient.ColorKeys;
            gradient.AlphaKeys = gradient.AlphaKeys;
        }

        ImGui.PopID();
        return changed;
    }

    /// <summary>A gradient preview over the frame width (alpha shown against a dark and light checker).</summary>
    public static void Bar(ParticleGradient gradient)
    {
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetFrameHeight();
        for (var i = 0; i < Samples; i++)
        {
            var x0 = start.X + (width * i / Samples);
            var x1 = start.X + (width * (i + 1) / Samples);
            var checker = ((i / 4) % 2 == 0) ? 0.35f : 0.6f;
            var c = gradient.Evaluate((i + 0.5f) / Samples);
            var r = (c.R * c.A) + (checker * (1f - c.A));
            var g = (c.G * c.A) + (checker * (1f - c.A));
            var b = (c.B * c.A) + (checker * (1f - c.A));
            draw.AddRectFilled(new SysVector2(x0, start.Y), new SysVector2(x1 + 1f, start.Y + height), ImGui.GetColorU32(new SysVector4(r, g, b, 1f)));
        }

        ImGui.Dummy(new SysVector2(width, height));
    }

    private static bool KeyRows<T>(string name, List<T> keys, Func<int, T, T?> row, Func<T> create)
        where T : struct
    {
        var changed = false;
        var remove = -1;
        Gui.TextDisabled(name + " keys");
        for (var i = 0; i < keys.Count; i++)
        {
            ImGui.PushID(name + i);
            if (row(i, keys[i]) is { } edited)
            {
                keys[i] = edited;
                changed = true;
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("x") && keys.Count > 1)
            {
                remove = i;
            }

            ImGui.PopID();
        }

        if (remove >= 0)
        {
            keys.RemoveAt(remove);
            changed = true;
        }

        if (ImGui.SmallButton($"Add {name} key"))
        {
            keys.Add(create());
            changed = true;
        }

        return changed;
    }

    private static bool TimeField(ref float time)
    {
        ImGui.SetNextItemWidth(70f);
        var changed = ImGui.DragFloat("##t", ref time, 0.005f, 0f, 1f, "t %.2f");
        time = Math.Clamp(time, 0f, 1f);
        return changed;
    }

    /// <summary>The curve over 0 to 1 with its keys as handles; dragging a handle moves the key in time and value.</summary>
    private static bool Graph(ParticleCurve curve)
    {
        var keys = curve.Keys;
        var (min, max) = Range(curve);
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var size = new SysVector2(ImGui.GetContentRegionAvail().X, GraphHeight);
        draw.AddRectFilled(start, start + size, ImGui.GetColorU32(ImGuiCol.FrameBg));
        SysVector2 ToScreen(float t, float v) => new(start.X + (t * size.X), start.Y + size.Y - ((v - min) / (max - min) * size.Y));

        if (min < 0f && max > 0f)
        {
            draw.AddLine(ToScreen(0f, 0f), ToScreen(1f, 0f), ImGui.GetColorU32(ImGuiCol.Border));
        }

        var previous = ToScreen(0f, curve.Evaluate(0f));
        for (var i = 1; i <= Samples; i++)
        {
            var t = i / (float)Samples;
            var point = ToScreen(t, curve.Evaluate(t));
            draw.AddLine(previous, point, ImGui.GetColorU32(new SysVector4(0.45f, 0.75f, 1f, 1f)), 1.5f);
            previous = point;
        }

        ImGui.InvisibleButton("##graph", size);
        var changed = false;
        var hovered = -1;
        var mouse = ImGui.GetIO().MousePos;
        for (var i = 0; i < keys.Count; i++)
        {
            var handle = ToScreen(keys[i].Time, keys[i].Value);
            if (SysVector2.Distance(handle, mouse) < 7f)
            {
                hovered = i;
            }

            draw.AddCircleFilled(handle, 4f, ImGui.GetColorU32(ImGuiCol.Text));
        }

        var storage = ImGui.GetStateStorage();
        var dragId = ImGui.GetID("##dragging");
        if (ImGui.IsItemActivated())
        {
            storage.SetInt(dragId, hovered);
        }

        var dragging = ImGui.IsItemActive() ? storage.GetInt(dragId, -1) : -1;
        if (dragging >= 0 && dragging < keys.Count && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
        {
            var time = Math.Clamp((mouse.X - start.X) / size.X, 0f, 1f);
            var value = min + ((start.Y + size.Y - mouse.Y) / size.Y * (max - min));
            var key = keys[dragging];
            keys[dragging] = key with { Time = time, Value = value };
            changed = true;
        }

        draw.AddText(start + new SysVector2(4f, 2f), ImGui.GetColorU32(ImGuiCol.TextDisabled), max.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        draw.AddText(start + new SysVector2(4f, size.Y - ImGui.GetTextLineHeight() - 2f), ImGui.GetColorU32(ImGuiCol.TextDisabled), min.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        return changed;
    }

    /// <summary>Value range shown by the graph: the keys' and the curve's, padded, never empty.</summary>
    private static (float Min, float Max) Range(ParticleCurve curve)
    {
        float min = 0f, max = 1f;
        for (var i = 0; i <= 16; i++)
        {
            var v = curve.Evaluate(i / 16f);
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        foreach (var key in curve.Keys)
        {
            min = Math.Min(min, key.Value);
            max = Math.Max(max, key.Value);
        }

        var pad = (max - min) * 0.1f;
        return (min - pad, max + pad);
    }
}
