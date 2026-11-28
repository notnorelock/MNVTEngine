// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

/// <summary>Gradient fills: no shader, colors blend across quads (one per piece of the rounded box between stops).</summary>
public sealed partial class UIPainter
{
    private readonly List<float> _xs = [], _ys = [];

    public void FillGradient(Rect logical, Gradient gradient, float cornerRadius = 0f)
    {
        ArgumentNullException.ThrowIfNull(gradient);
        FillGradient(logical, gradient.Stops, gradient.Angle, cornerRadius);
    }

    /// <summary>
    /// Fills a rounded box with a linear gradient; <paramref name="stops"/> sorted by offset, <paramref name="angle"/> as
    /// <see cref="Gradient.Angle"/>. Takes a span so animated gradients (moving stops) need no allocation.
    /// </summary>
    public void FillGradient(Rect logical, ReadOnlySpan<GradientStop> stops, float angle, float cornerRadius = 0f)
    {
        if (stops.Length == 0)
        {
            return;
        }

        var r = Snap(Scale(logical));
        if (r.Width <= 0 || r.Height <= 0)
        {
            return;
        }

        var radius = (int)MathF.Max(0f, MathF.Min(MathF.Round(cornerRadius * PixelScale), MathF.Floor(MathF.Min(r.Width, r.Height) / 2f)));
        var (sin, cos) = MathF.SinCos(angle * MathF.PI / 180f);
        var (right, bottom) = (r.X + r.Width, r.Y + r.Height);
        var center = new Vector2(r.X + (r.Width / 2f), r.Y + (r.Height / 2f));
        var reach = (MathF.Abs(cos) * r.Width / 2f) + (MathF.Abs(sin) * r.Height / 2f); // the corners project to ±reach
        float T(float x, float y) => reach <= 0f ? 0f : (((x - center.X) * cos) + ((y - center.Y) * sin) + reach) / (2f * reach);

        Breaks(_xs, r.X, right, radius, stops, MathF.Abs(sin) < 1e-4f ? cos : 0f, MathF.Abs(sin) >= 1e-4f && MathF.Abs(cos) >= 1e-4f);
        Breaks(_ys, r.Y, bottom, radius, stops, MathF.Abs(cos) < 1e-4f ? sin : 0f, false);
        var circle = radius > 0 ? Circle(radius, 0) : default;
        var white = _atlas.White;
        for (var j = 0; j + 1 < _ys.Count; j++)
        {
            var (y0, y1) = (_ys[j], _ys[j + 1]);
            var cornerY = y1 <= r.Y + radius ? y0 - r.Y : y0 >= bottom - radius ? radius + (y0 - (bottom - radius)) : -1f;
            for (var i = 0; i + 1 < _xs.Count; i++)
            {
                var (x0, x1) = (_xs[i], _xs[i + 1]);
                var cornerX = x1 <= r.X + radius ? x0 - r.X : x0 >= right - radius ? radius + (x0 - (right - radius)) : -1f;
                var dest = new Rect(x0, y0, x1 - x0, y1 - y0);
                var (tl, tr) = (Gradient.Sample(stops, T(x0, y0)), Gradient.Sample(stops, T(x1, y0)));
                var (br, bl) = (Gradient.Sample(stops, T(x1, y1)), Gradient.Sample(stops, T(x0, y1)));
                if (radius > 0 && cornerX >= 0f && cornerY >= 0f)
                {
                    var s = circle.Source;
                    Quad(circle.Page, dest, new Rect(s.X + cornerX, s.Y + cornerY, dest.Width, dest.Height), tl, tr, br, bl);
                }
                else
                {
                    Quad(white.Page, dest, white.Source, tl, tr, br, bl);
                }
            }
        }
    }

    /// <summary>
    /// Where one axis of the box splits: its ends, the corner bands, and where the stops fall when the gradient runs
    /// along this axis (<paramref name="along"/> ±1), so each piece blends linearly between exact colors. Diagonal
    /// gradients with more than two stops split into eighths instead.
    /// </summary>
    private static void Breaks(List<float> breaks, float start, float end, int radius, ReadOnlySpan<GradientStop> stops, float along, bool diagonal)
    {
        breaks.Clear();
        breaks.Add(start);
        breaks.Add(end);
        if (radius > 0)
        {
            breaks.Add(start + radius);
            breaks.Add(end - radius);
        }

        if (MathF.Abs(along) > 0.5f)
        {
            foreach (var stop in stops)
            {
                breaks.Add(along > 0f ? start + (stop.Offset * (end - start)) : end - (stop.Offset * (end - start)));
            }
        }
        else if (diagonal && stops.Length > 2)
        {
            for (var k = 1; k < 8; k++)
            {
                breaks.Add(start + ((end - start) * k / 8f)); // shortcut: diagonal multi-stop gradients are approximated in eighths; split exactly if banding shows
            }
        }

        breaks.Sort();
        for (var i = breaks.Count - 1; i > 0; i--)
        {
            if (breaks[i] - breaks[i - 1] < 0.01f || breaks[i] < start || breaks[i] > end)
            {
                breaks.RemoveAt(i);
            }
        }
    }
}
