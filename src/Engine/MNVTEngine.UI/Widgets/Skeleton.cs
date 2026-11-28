// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Mathematics;

namespace MNVTEngine.UI.Widgets;

/// <summary>The outline of a <see cref="Skeleton"/>: a box with the theme's radius, a circle (avatars) or a text line.</summary>
public enum SkeletonShape
{
    Box,
    Circle,
    Text,
}

/// <summary>
/// A loading placeholder in the shape of what is coming: a base a little above the surface that breathes gently
/// (<see cref="DesignTokens.SkeletonPulse"/>), and a soft highlight sweeping across it
/// (<see cref="DesignTokens.ShimmerSeconds"/>). The sweep runs on the context's clock in screen space, so every
/// skeleton on screen shows one band of light passing over them together, and it leaves fully before it starts again
/// (no jump at the loop). Colors come from the current tokens, so a theme change restyles visible skeletons. Under
/// <see cref="Design.ReducedMotion"/> it stands still. Built with <see cref="V.Skeleton"/>, <see cref="V.SkeletonCircle"/>
/// and <see cref="V.SkeletonText"/>; give it a size.
/// </summary>
public class Skeleton : UIElement
{
    /// <summary>Width of the highlight band, in logical pixels either side of its middle.</summary>
    public const float BandHalfWidth = 140f;

    private readonly GradientStop[] _stops = new GradientStop[5];
    private SkeletonShape _shape;

    public Skeleton() => HitTestVisible = false;

    public SkeletonShape Shape { get => _shape; set => SetVisual(ref _shape, value); }

    /// <summary>The shimmer's middle in context x (logical pixels) at <paramref name="time"/>: from off the left edge to off the right.</summary>
    public static float SweepX(double time, float seconds, float contextWidth)
    {
        var phase = (float)(time % seconds / seconds);
        return -BandHalfWidth + (phase * (contextWidth + (2f * BandHalfWidth)));
    }

    /// <summary>How far the base is toward the highlight at <paramref name="time"/>: 0 to <paramref name="depth"/>, a smooth breath.</summary>
    public static float Breath(double time, float seconds, float depth) =>
        depth * (0.5f - (0.5f * MathF.Cos((float)(time % (seconds * 1.5) / (seconds * 1.5)) * 2f * MathF.PI)));

    protected internal override bool Tick(float deltaSeconds) => !Design.ReducedMotion && Design.Current.Peek().ShimmerSeconds > 0f && Shown();

    protected internal override void Paint(UIPainter painter, in ResolvedStyle style)
    {
        ArgumentNullException.ThrowIfNull(painter);
        var t = Design.Current.Peek();
        var b = Bounds;
        var radius = Shape switch
        {
            SkeletonShape.Circle => MathF.Min(b.Width, b.Height) / 2f,
            SkeletonShape.Text => b.Height / 2f,
            _ => style.CornerRadius > 0f ? style.CornerRadius : t.Radius,
        };

        var time = Context?.Time ?? 0d;
        var still = Design.ReducedMotion || t.ShimmerSeconds <= 0f;
        var body = still ? t.Skeleton : Color.Lerp(t.Skeleton, t.SkeletonHighlight, Breath(time, t.ShimmerSeconds, t.SkeletonPulse));
        if (still || b.Width <= 0f)
        {
            painter.FillRect(b, body, radius);
            return;
        }

        var middle = (SweepX(time, t.ShimmerSeconds, Context?.Size.X ?? b.Width) - b.X) / b.Width;
        var half = BandHalfWidth / b.Width;
        _stops[0] = new(0f, body);
        _stops[1] = new(middle - half, body);
        _stops[2] = new(middle, t.SkeletonHighlight);
        _stops[3] = new(middle + half, body);
        _stops[4] = new(1f, body);
        if (middle + half <= 0f || middle - half >= 1f)
        {
            painter.FillRect(b, body, radius); // the band is elsewhere on screen
            return;
        }

        painter.FillGradient(b, _stops, 0f, radius);
    }

    private bool Shown()
    {
        for (var e = (UIElement?)this; e is not null; e = e.Parent)
        {
            if (!e.Visible)
            {
                return false;
            }
        }

        return Context is not null;
    }
}
