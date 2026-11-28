// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

/// <summary>A color at a place along a gradient, 0 at its start and 1 at its end.</summary>
public readonly record struct GradientStop(float Offset, Color Color);

/// <summary>
/// A linear gradient: colors at stops along a direction, <see cref="Angle"/> degrees clockwise from left-to-right (90 is
/// top to bottom, 45 top-left to bottom-right). Painted by <see cref="UIPainter.FillGradient(Rect, Gradient, float)"/>
/// or as an element's <see cref="UIStyle.BackgroundGradient"/>.
/// </summary>
public sealed class Gradient : IEquatable<Gradient>
{
    private readonly GradientStop[] _stops;

    /// <exception cref="ArgumentException">Fewer than two stops.</exception>
    public Gradient(IEnumerable<GradientStop> stops, float angle = 0f)
    {
        ArgumentNullException.ThrowIfNull(stops);
        _stops = [.. stops.Select(s => s with { Offset = Math.Clamp(s.Offset, 0f, 1f) }).OrderBy(s => s.Offset)];
        if (_stops.Length < 2)
        {
            throw new ArgumentException("A gradient needs at least two stops.", nameof(stops));
        }

        Angle = angle;
    }

    public float Angle { get; }

    public ReadOnlySpan<GradientStop> Stops => _stops;

    /// <summary>From one color to another.</summary>
    public static Gradient Linear(Color from, Color to, float angle = 0f) => new([new(0f, from), new(1f, to)], angle);

    /// <summary>The color at <paramref name="t"/> (0 to 1) along <paramref name="stops"/>, sorted by offset.</summary>
    public static Color Sample(ReadOnlySpan<GradientStop> stops, float t)
    {
        if (t <= stops[0].Offset)
        {
            return stops[0].Color;
        }

        for (var i = 1; i < stops.Length; i++)
        {
            if (t <= stops[i].Offset)
            {
                var span = stops[i].Offset - stops[i - 1].Offset;
                return span <= 0f ? stops[i].Color : Color.Lerp(stops[i - 1].Color, stops[i].Color, (t - stops[i - 1].Offset) / span);
            }
        }

        return stops[^1].Color;
    }

    public Color Sample(float t) => Sample(_stops, t);

    /// <summary>The gradient between this one and <paramref name="other"/>: stop colors blended where both have the same stops, otherwise sampled at this one's.</summary>
    public Gradient Lerp(Gradient other, float t)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new Gradient(_stops.Select(s => s with { Color = Color.Lerp(s.Color, other.Sample(s.Offset), t) }), Angle + ((other.Angle - Angle) * t));
    }

    public bool Equals(Gradient? other) => other is not null && Angle == other.Angle && _stops.AsSpan().SequenceEqual(other._stops);

    public override bool Equals(object? obj) => Equals(obj as Gradient);

    public override int GetHashCode() => HashCode.Combine(Angle, _stops.Length, _stops[0], _stops[^1]);
}
