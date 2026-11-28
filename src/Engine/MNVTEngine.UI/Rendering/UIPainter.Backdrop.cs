// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Graphics;
using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

/// <summary>Frosted glass: the blurred frame behind the UI (<see cref="UIRenderer"/> makes it), drawn inside a rounded box.</summary>
public sealed partial class UIPainter
{
    /// <summary>The blurred copy of what was drawn before this context, or null when the backend cannot copy.</summary>
    internal Texture2D? Backdrop { get; init; }

    /// <summary>Backdrop texels per framebuffer pixel.</summary>
    internal float BackdropScale { get; init; }

    /// <summary>Whether anything was drawn from the backdrop (the renderer then refreshes it before drawing).</summary>
    internal bool UsedBackdrop { get; private set; }

    /// <summary>
    /// Fills a rounded box with what is behind it, blurred. The corners are cut in horizontal strips (no anti-aliasing):
    /// the translucent background drawn over it carries the smooth edge.
    /// </summary>
    public void FillBackdrop(Rect logical, float cornerRadius = 0f)
    {
        if (Backdrop is not { } texture)
        {
            return;
        }

        var r = Snap(Scale(logical));
        if (r.Width <= 0 || r.Height <= 0)
        {
            return;
        }

        UsedBackdrop = true;
        var radius = MathF.Max(0f, MathF.Min(MathF.Round(cornerRadius * PixelScale), MathF.Floor(MathF.Min(r.Width, r.Height) / 2f)));
        var (right, bottom) = (r.X + r.Width, r.Y + r.Height);
        Strip(texture, r.X, r.Y + radius, right, bottom - radius);
        const float Step = 2f;
        for (var y = 0f; y < radius; y += Step)
        {
            var h = MathF.Min(Step, radius - y);
            var dy = radius - (y + (h / 2f));
            var inset = radius - MathF.Sqrt(MathF.Max(0f, (radius * radius) - (dy * dy)));
            Strip(texture, r.X + inset, r.Y + y, right - inset, r.Y + y + h);
            Strip(texture, r.X + inset, bottom - y - h, right - inset, bottom - y);
        }
    }

    private void Strip(Texture2D texture, float x0, float y0, float x1, float y1)
    {
        if (x1 > x0 && y1 > y0)
        {
            var k = BackdropScale;
            Quad(texture, new Rect(x0, y0, x1 - x0, y1 - y0), new Rect(x0 * k, y0 * k, (x1 - x0) * k, (y1 - y0) * k), Color.White);
        }
    }
}
