// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Mathematics;
using MNVTEngine.Reactivity;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.UI;

/// <summary>Color input: a swatch, a hex field and hue, saturation and brightness sliders.</summary>
public static partial class V
{
    /// <summary>
    /// Edits <paramref name="value"/>: type a hex color (<c>#RRGGBB</c>, <c>#RGB</c> or with alpha; applied when it
    /// parses) or drag the hue, saturation and brightness sliders. Alpha is kept. Changes from elsewhere show at once.
    /// </summary>
    public static UIElement ColorField(Signal<Color> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var hex = Signal.Create(value.Peek().ToHex());
        var (h0, s0, v0) = ToHsv(value.Peek());
        var (hue, saturation, brightness) = (Signal.Create(h0), Signal.Create(s0), Signal.Create(v0));
        void FromSliders() => value.Value = FromHsv(hue.Peek(), saturation.Peek(), brightness.Peek()) with { A = value.Peek().A };

        var swatch = new UIElement().Size(Design.Current.Peek().ControlHeight, Design.Current.Peek().ControlHeight)
            .Radius(Design.Current.Peek().RadiusSmall).Border(Design.Current.Peek().LineStrong).Background(() => value.Value);
        var field = TextField(hex, "#RRGGBB", maxLength: 9).Grow();
        field.TextChanged += t =>
        {
            if (Color.TryParseHex(t, out var parsed))
            {
                value.Value = parsed;
            }
        };

        Slider Channel(Signal<float> channel, float max)
        {
            var slider = Slider(channel, 0f, max);
            slider.ValueChanged += _ => FromSliders();
            return slider;
        }

        return Column(
                Row(swatch, field).Gap(Design.Current.Peek().Space2),
                Channel(hue, 360f).FillWidth().Tooltip("Hue"),
                Channel(saturation, 1f).FillWidth().Tooltip("Saturation"),
                Channel(brightness, 1f).FillWidth().Tooltip("Brightness"))
            .Gap(Design.Current.Peek().Space1).FillWidth()
            .Effect(() =>
            {
                var color = value.Value;
                Reactive.Untrack(() =>
                {
                    if (!Color.TryParseHex(hex.Value, out var shown) || shown != color)
                    {
                        hex.Value = color.ToHex();
                    }

                    if (FromHsv(hue.Value, saturation.Value, brightness.Value).ToHex() != (color with { A = 1f }).ToHex())
                    {
                        (hue.Value, saturation.Value, brightness.Value) = ToHsv(color); // a grey keeps no hue; the sliders' own hue stays then
                    }
                });
            });
    }

    /// <summary>Hue in degrees (0 to 360), saturation and value 0 to 1.</summary>
    public static (float Hue, float Saturation, float Value) ToHsv(Color c)
    {
        var max = MathF.Max(c.R, MathF.Max(c.G, c.B));
        var delta = max - MathF.Min(c.R, MathF.Min(c.G, c.B));
        var hue = delta <= 0f ? 0f
            : max == c.R ? 60f * ((((c.G - c.B) / delta) % 6f + 6f) % 6f)
            : max == c.G ? 60f * (((c.B - c.R) / delta) + 2f)
            : 60f * (((c.R - c.G) / delta) + 4f);
        return (hue, max <= 0f ? 0f : delta / max, max);
    }

    public static Color FromHsv(float hue, float saturation, float value)
    {
        hue = ((hue % 360f) + 360f) % 360f;
        var c = value * Math.Clamp(saturation, 0f, 1f);
        var x = c * (1f - MathF.Abs((hue / 60f % 2f) - 1f));
        var m = value - c;
        var (r, g, b) = (int)(hue / 60f) switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return new Color(r + m, g + m, b + m, 1f);
    }
}
