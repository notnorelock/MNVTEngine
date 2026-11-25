// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

/// <summary>
/// Nine-slice layout: a texture cut by four lines into corners (drawn at their size), edges (stretched along one axis)
/// and a centre (stretched both ways), so a small frame fits any box without distorting its corners. Pixel art scales
/// its corners by a whole <c>scale</c> to keep them crisp.
/// </summary>
public static class NineSlice
{
    /// <summary>
    /// The pieces to draw: destination rectangles in <paramref name="dest"/>'s units and source rectangles in texels.
    /// <paramref name="slice"/> is the frame's width on each side, in texels; <paramref name="scale"/> is how many
    /// destination units one texel of the frame covers. Corners shrink evenly when the box is smaller than the frame;
    /// empty pieces are left out.
    /// </summary>
    public static IReadOnlyList<(Rect Dest, Rect Source)> Pieces(Rect dest, float textureWidth, float textureHeight, Thickness slice, float scale = 1f)
    {
        var (left, right) = Fit(slice.Left * scale, slice.Right * scale, dest.Width);
        var (top, bottom) = Fit(slice.Top * scale, slice.Bottom * scale, dest.Height);
        float[] dx = [dest.X, dest.X + left, dest.X + dest.Width - right, dest.X + dest.Width];
        float[] dy = [dest.Y, dest.Y + top, dest.Y + dest.Height - bottom, dest.Y + dest.Height];
        float[] sx = [0f, slice.Left, textureWidth - slice.Right, textureWidth];
        float[] sy = [0f, slice.Top, textureHeight - slice.Bottom, textureHeight];
        var pieces = new List<(Rect, Rect)>(9);
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var target = new Rect(dx[column], dy[row], dx[column + 1] - dx[column], dy[row + 1] - dy[row]);
                var source = new Rect(sx[column], sy[row], sx[column + 1] - sx[column], sy[row + 1] - sy[row]);
                if (target.Width > 0f && target.Height > 0f && source.Width > 0f && source.Height > 0f)
                {
                    pieces.Add((target, source));
                }
            }
        }

        return pieces;
    }

    private static (float Start, float End) Fit(float start, float end, float length)
    {
        var total = start + end;
        return total <= length || total <= 0f ? (start, end) : (start / total * length, end / total * length);
    }
}
