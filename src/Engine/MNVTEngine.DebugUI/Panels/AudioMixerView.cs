using System.Globalization;
using ImGuiNET;
using MNVTEngine.Audio;
using MNVTEngine.DebugUI.ImGuiBackend;
using SysVector2 = System.Numerics.Vector2;
using SysVector4 = System.Numerics.Vector4;

namespace MNVTEngine.DebugUI.Panels;

/// <summary>
/// A mixing console for an <see cref="AudioMixer"/>, in the style of a DAW's mixer: one channel strip per group and one
/// for Master, each with its insert effects (add, toggle, edit, reorder, remove), a mute button, the fader in dB, and
/// a stereo meter with peak (light) and RMS (solid) bars coloured green, yellow and red, a peak-hold readout that turns
/// red after clipping (click to reset). Meters need a device (Play mode or a running game); without one they stay empty.
/// Used by the editor's Mixer window and the AudioMixer inspector, and by the in-game Mixer panel.
/// </summary>
public sealed class AudioMixerView
{
    private const float StripWidth = 96f;
    private const float MinDb = -60f, MaxDb = 6f;
    private const float FallDbPerSecond = 26f;

    private static readonly SysVector4 Green = new(0.36f, 0.85f, 0.35f, 1f), Yellow = new(0.95f, 0.8f, 0.2f, 1f), Red = new(0.95f, 0.25f, 0.2f, 1f);
    private readonly Dictionary<string, (float Left, float Right)> _shown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Draws the console. True when the mixer was edited (the caller records the change).</summary>
    /// <param name="height">Height of the strips; 0 fills the space left.</param>
    public bool Draw(AudioMixer mixer, IAudioDevice? device, float height = 0f)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        var changed = false;
        var size = new SysVector2(0f, height <= 0f ? Math.Max(260f, ImGui.GetContentRegionAvail().Y) : height);
        if (ImGui.BeginChild("##console", size, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
        {
            var groups = mixer.Groups;
            var top = ImGui.GetCursorPosY();
            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                var (volume, muted) = (group.Volume, group.Muted);
                changed |= Strip(i.ToString(CultureInfo.InvariantCulture), group.Name, group.Parent.Length == 0 ? AudioMixer.MasterGroup : group.Parent, ref volume, ref muted, group.Effects, device);
                (group.Volume, group.Muted) = (volume, muted);
                ImGui.SameLine();
                ImGui.SetCursorPosY(top); // strips start level whatever the previous strip's last text baseline was
            }

            var (masterVolume, masterMuted) = (mixer.MasterVolume, mixer.MasterMuted);
            changed |= Strip("master", AudioMixer.MasterGroup, null, ref masterVolume, ref masterMuted, mixer.MasterEffects, device);
            (mixer.MasterVolume, mixer.MasterMuted) = (masterVolume, masterMuted);
        }

        ImGui.EndChild();
        return changed;
    }

    /// <summary>Linear gain to dB, with silence as negative infinity.</summary>
    public static float ToDb(float gain) => gain <= 1e-6f ? float.NegativeInfinity : 20f * MathF.Log10(gain);

    public static string FormatDb(float db) => float.IsNegativeInfinity(db) || db < -99f ? "-inf" : db.ToString("0.0", CultureInfo.InvariantCulture);

    private bool Strip(string id, string name, string? parent, ref float volume, ref bool muted, List<AudioEffectSettings> effects, IAudioDevice? device)
    {
        var changed = false;
        ImGui.PushID(id);
        ImGui.BeginGroup();
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var headerColor = parent is null ? new SysVector4(0.85f, 0.85f, 0.88f, 1f) : Color(name);
        draw.AddRectFilled(start, start + new SysVector2(StripWidth, ImGui.GetTextLineHeight() + 4f), ImGui.GetColorU32(headerColor), 3f);
        draw.AddText(start + new SysVector2(4f, 2f), ImGui.GetColorU32(new SysVector4(0.08f, 0.08f, 0.1f, 1f)), Fit(name, StripWidth - 8f)); // drawn, not laid out: SameLine baselines would shift it

        ImGui.SetCursorScreenPos(start + new SysVector2(0f, ImGui.GetTextLineHeight() + 8f));
        Gui.Muted(parent is null ? "out" : Fit("> " + parent, StripWidth));
        changed |= Effects(effects);

        // Mute, fader dB and peak hold.
        using (Gui.StyleColor(ImGuiCol.Button, new SysVector4(0.95f, 0.78f, 0.2f, 1f), !muted))
        {
            using (Gui.StyleColor(ImGuiCol.Text, new SysVector4(0.08f, 0.08f, 0.1f, 1f), !muted))
            {
                if (ImGui.Button(muted ? "Muted" : "On", new SysVector2(StripWidth, 0f)))
                {
                    muted = !muted;
                    changed = true;
                }
            }
        }

        Hint(muted ? "Muted: click to hear this channel" : "Click to mute this channel");
        var meter = device is not null && device.TryGetMeter(name, out var reading) ? reading : (AudioMeter?)null;
        var half = (StripWidth - 4f) / 2f;
        ImGui.Button(FormatDb(ToDb(volume)) + "##volume", new SysVector2(half, 0f));
        Hint("Fader (dB)");
        ImGui.SameLine(0f, 4f);
        var hold = meter is { } m ? FormatDb(ToDb(m.HoldPeak)) : "-inf";
        using (Gui.StyleColor(ImGuiCol.Button, Red, meter?.Clipped == true))
        {
            if (ImGui.Button(hold + "##peak", new SysVector2(half, 0f)) && device is not null)
            {
                device.ResetMeter(name);
            }
        }

        Hint(meter?.Clipped == true ? "Clipped: the peak went over 0 dB. Click to reset." : "Peak hold (dB). Click to reset.");

        // Fader beside the stereo meter.
        var faderHeight = Math.Max(120f, ImGui.GetContentRegionAvail().Y - 6f);
        var db = Math.Clamp(float.IsNegativeInfinity(ToDb(volume)) ? MinDb : ToDb(volume), MinDb, MaxDb);
        if (ImGui.VSliderFloat("##fader", new SysVector2(28f, faderHeight), ref db, MinDb, MaxDb, ""))
        {
            volume = db <= MinDb + 0.01f ? 0f : MathF.Pow(10f, db / 20f);
            changed = true;
        }

        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            volume = 1f; // double-click: 0 dB
            changed = true;
        }

        Hint("Fader: drag for dB, double-click for 0 dB");
        ImGui.SameLine(0f, 6f);
        Meter(name, meter, new SysVector2(StripWidth - 34f, faderHeight));
        ImGui.EndGroup();
        ImGui.PopID();
        return changed;
    }

    private static bool Effects(List<AudioEffectSettings> effects)
    {
        var changed = false;
        int? remove = null, moveUp = null;
        for (var i = 0; i < effects.Count; i++)
        {
            var effect = effects[i];
            var definition = AudioEffects.Find(effect.Type);
            ImGui.PushID(i);
            var enabled = effect.Enabled;
            if (ImGui.Checkbox("##on", ref enabled))
            {
                effect.Enabled = enabled;
                changed = true;
            }

            ImGui.SameLine(0f, 2f);
            if (ImGui.Button(Fit(definition?.DisplayName ?? effect.Type + " (unknown)", StripWidth - 34f), new SysVector2(StripWidth - ImGui.GetFrameHeight() - 2f, 0f)))
            {
                ImGui.OpenPopup("effect");
            }

            if (ImGui.BeginPopup("effect"))
            {
                ImGui.TextUnformatted(definition?.DisplayName ?? effect.Type);
                ImGui.Separator();
                if (definition is null)
                {
                    Gui.Muted("This engine has no such effect: it is skipped.");
                }
                else
                {
                    foreach (var parameter in definition.Parameters)
                    {
                        var value = effect.Get(parameter);
                        ImGui.SetNextItemWidth(200f);
                        if (ImGui.SliderFloat(parameter.Label, ref value, parameter.Min, parameter.Max, Format(parameter.Unit), parameter.Logarithmic ? ImGuiSliderFlags.Logarithmic : ImGuiSliderFlags.None))
                        {
                            effect.Parameters[parameter.Name] = value;
                            changed = true;
                        }
                    }
                }

                ImGui.Separator();
                if (i > 0 && ImGui.Button("Move up"))
                {
                    moveUp = i;
                }

                ImGui.SameLine();
                if (ImGui.Button("Remove"))
                {
                    remove = i;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }

            ImGui.PopID();
        }

        if (ImGui.Button("+ FX", new SysVector2(StripWidth, 0f)))
        {
            ImGui.OpenPopup("add");
        }

        if (ImGui.BeginPopup("add"))
        {
            foreach (var definition in AudioEffects.All)
            {
                if (ImGui.Selectable(definition.DisplayName))
                {
                    effects.Add(definition.CreateSettings());
                    changed = true;
                }
            }

            ImGui.EndPopup();
        }

        if (remove is { } r)
        {
            effects.RemoveAt(r);
            changed = true;
        }

        if (moveUp is { } u)
        {
            (effects[u - 1], effects[u]) = (effects[u], effects[u - 1]);
            changed = true;
        }

        return changed;
    }

    /// <summary>Two bars (left, right): RMS solid, peak lighter, with a falling display and a dB scale from -60 to +6.</summary>
    private void Meter(string bus, AudioMeter? meter, SysVector2 size)
    {
        var origin = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        ImGui.Dummy(size);
        var barWidth = 8f;
        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);
        var previous = _shown.TryGetValue(bus, out var shown) ? shown : (Left: MinDb, Right: MinDb);
        var peak = (Left: Falling(previous.Left, meter?.PeakLeft ?? 0f, dt), Right: Falling(previous.Right, meter?.PeakRight ?? 0f, dt));
        _shown[bus] = peak;
        for (var channel = 0; channel < 2; channel++)
        {
            var x = origin.X + (channel * (barWidth + 3f));
            draw.AddRectFilled(new SysVector2(x, origin.Y), new SysVector2(x + barWidth, origin.Y + size.Y), ImGui.GetColorU32(new SysVector4(0.1f, 0.1f, 0.12f, 1f)));
            var rms = meter is { } m ? ToDb(channel == 0 ? m.RmsLeft : m.RmsRight) : float.NegativeInfinity;
            Bar(draw, x, origin.Y, barWidth, size.Y, channel == 0 ? peak.Left : peak.Right, 0.45f);
            Bar(draw, x, origin.Y, barWidth, size.Y, rms, 1f);
        }

        // Scale: 0, -6, -12, -24, -48 dB.
        var labelX = origin.X + (2 * (barWidth + 3f)) + 2f;
        foreach (var mark in (float[])[6f, 0f, -6f, -12f, -24f, -48f])
        {
            var y = origin.Y + (size.Y * (1f - Fraction(mark)));
            draw.AddLine(new SysVector2(origin.X, y), new SysVector2(labelX - 2f, y), ImGui.GetColorU32(new SysVector4(1f, 1f, 1f, 0.12f)));
            draw.AddText(new SysVector2(labelX, y - (ImGui.GetTextLineHeight() / 2f)), ImGui.GetColorU32(new SysVector4(0.6f, 0.62f, 0.66f, 1f)), mark.ToString("0", CultureInfo.InvariantCulture));
        }
    }

    private static float Falling(float shownDb, float peak, float dt)
    {
        var db = ToDb(peak);
        var fallen = shownDb - (FallDbPerSecond * dt);
        return Math.Max(float.IsNegativeInfinity(db) ? MinDb : db, Math.Max(fallen, MinDb));
    }

    private static float Fraction(float db) => Math.Clamp((db - MinDb) / (MaxDb - MinDb), 0f, 1f);

    /// <summary>A vertical bar up to <paramref name="db"/>: green to -6 dB, yellow to 0 dB, red above.</summary>
    private static void Bar(ImDrawListPtr draw, float x, float top, float width, float height, float db, float alpha)
    {
        if (float.IsNegativeInfinity(db) || db <= MinDb)
        {
            return;
        }

        var bottom = top + height;
        var level = bottom - (height * Fraction(db));
        foreach (var (from, to, color) in ((float, float, SysVector4)[])[(MinDb, -6f, Green), (-6f, 0f, Yellow), (0f, MaxDb, Red)])
        {
            var y0 = bottom - (height * Fraction(from));
            var y1 = Math.Max(level, bottom - (height * Fraction(to)));
            if (y1 < y0)
            {
                draw.AddRectFilled(new SysVector2(x, y1), new SysVector2(x + width, y0), ImGui.GetColorU32(color with { W = alpha }));
            }
        }
    }

    private static void Hint(string text)
    {
        if (ImGui.IsItemHovered())
        {
            Gui.Tooltip(text);
        }
    }

    private static string Format(string unit) => unit switch
    {
        "Hz" => "%.0f Hz",
        "dB" => "%.1f dB",
        "ms" => "%.1f ms",
        ":1" => "%.1f:1",
        _ => "%.2f",
    };

    private static string Fit(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
        {
            return text;
        }

        while (text.Length > 1 && ImGui.CalcTextSize(text + "..").X > width)
        {
            text = text[..^1];
        }

        return text + "..";
    }

    /// <summary>A stable colour per group name, like a DAW's track colours.</summary>
    private static SysVector4 Color(string name)
    {
        var hue = (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(name) % 360 / 360f;
        ImGui.ColorConvertHSVtoRGB(hue, 0.45f, 0.95f, out var r, out var g, out var b);
        return new SysVector4(r, g, b, 1f);
    }
}

/// <summary>The in-game mixer: the running scene's AudioMixer with live meters (changes last for this run).</summary>
public sealed class MixerPanel() : DebugPanel("Mixer")
{
    private readonly AudioMixerView _view = new();

    protected override SysVector2 DefaultSize => new(560, 420);

    protected override void Draw()
    {
        if (Scene is not { } scene || AudioSystem.For(scene) is not { } audio)
        {
            Gui.Muted("No audio in this scene.");
            return;
        }

        if (audio.Mixer is not { } mixer)
        {
            Gui.Muted("The scene has no AudioMixer: everything plays through Master.");
            return;
        }

        ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture, $"{audio.Device.Name}, {audio.VoiceCount} voices"));
        _view.Draw(mixer, audio.Device);
    }
}
