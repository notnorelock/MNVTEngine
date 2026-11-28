// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Mathematics;

namespace MNVTEngine.UI.Widgets;

/// <summary>
/// One line of text that slides when it does not fit (a long track title): it rests at the start for
/// <see cref="Pause"/> seconds, glides to its end at <see cref="Speed"/>, rests, and glides back. Text that fits stays
/// still, and so does all text under <see cref="Design.ReducedMotion"/>. It moves its label visually, so sliding
/// costs no layout. Built with <c>V.Marquee</c>.
/// </summary>
public class Marquee : UIElement
{
    public const float Pause = 1.6f;

    private float _clock, _natural;

    public Marquee(Label label)
    {
        ArgumentNullException.ThrowIfNull(label);
        Overflow = Overflow.Clip;
        label.Wrap = false;
        Label = AddChild(label);
    }

    public Label Label { get; }

    /// <summary>Logical pixels per second.</summary>
    public float Speed { get; set; } = 32f;

    /// <summary>How much of the line is hidden: 0 when it fits.</summary>
    public float Excess => Math.Max(0f, _natural - ContentBounds.Width);

    /// <summary>The slide at <paramref name="clock"/> seconds: rest, out, rest, back, again.</summary>
    public static float OffsetAt(float clock, float excess, float speed)
    {
        if (excess <= 0f || speed <= 0f)
        {
            return 0f;
        }

        var travel = excess / speed;
        var t = clock % (2f * (Pause + travel));
        return t < Pause ? 0f
            : t < Pause + travel ? (t - Pause) * speed
            : t < (2f * Pause) + travel ? excess
            : excess - ((t - (2f * Pause) - travel) * speed);
    }

    protected internal override Vector2 MeasureContent(float availableWidth, float availableHeight)
    {
        var size = LayoutEngine.MeasureOuter(Label, float.PositiveInfinity, availableHeight);
        return new Vector2(MathF.Min(size.X, availableWidth), size.Y);
    }

    protected internal override void ArrangeChildren()
    {
        var view = ContentBounds;
        var size = LayoutEngine.Size(Label, float.PositiveInfinity, view.Height, false, false);
        _natural = size.X;
        LayoutEngine.Arrange(Label, new Rect(view.X, view.Y, size.X, size.Y), ChildClip);
    }

    protected internal override bool Tick(float deltaSeconds)
    {
        _clock = Excess > 0f && !Design.ReducedMotion ? _clock + deltaSeconds : 0f;
        var x = -OffsetAt(_clock, Excess, Speed);
        if (Label.Translate.X == x)
        {
            return false;
        }

        Label.Translate = new Vector2(x, 0f);
        return true;
    }
}
