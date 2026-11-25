// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Mathematics;

namespace MNVTEngine.UI;

/// <summary>Easing curves for tweens: map progress 0..1 to eased progress.</summary>
public enum Easing
{
    Linear,
    QuadOut,
    CubicOut,
    CubicInOut,

    /// <summary>Overshoots slightly and settles: opening surfaces.</summary>
    BackOut,
}

/// <summary>
/// How a property moves to a new value: a tween (fixed duration and easing) or a spring (critically damped by default,
/// so it settles without wobbling; it keeps its velocity when retargeted, which makes hover and press feel continuous).
/// A zero motion jumps.
/// </summary>
public readonly record struct Motion
{
    private Motion(bool spring, float duration, Easing easing, float stiffness, float dampingRatio) =>
        (IsSpring, Duration, Easing, Stiffness, DampingRatio) = (spring, duration, easing, stiffness, dampingRatio);

    /// <summary>Jumps to the target at once.</summary>
    public static Motion Instant => default;

    public bool IsSpring { get; }

    /// <summary>Tween length in seconds.</summary>
    public float Duration { get; }

    public Easing Easing { get; }

    /// <summary>Spring stiffness (higher settles faster).</summary>
    public float Stiffness { get; }

    /// <summary>1 is critically damped; below 1 overshoots.</summary>
    public float DampingRatio { get; }

    public bool IsInstant => !IsSpring && Duration <= 0f;

    public static Motion Tween(float seconds, Easing easing = Easing.CubicOut) => new(false, MathF.Max(0f, seconds), easing, 0f, 0f);

    public static Motion Spring(float stiffness, float dampingRatio = 1f) => new(true, 0f, Easing.Linear, MathF.Max(1f, stiffness), MathF.Max(0.05f, dampingRatio));

    public static float Ease(Easing easing, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return easing switch
        {
            Easing.QuadOut => 1f - ((1f - t) * (1f - t)),
            Easing.CubicOut => 1f - MathF.Pow(1f - t, 3f),
            Easing.CubicInOut => t < 0.5f ? 4f * t * t * t : 1f - (MathF.Pow((-2f * t) + 2f, 3f) / 2f),
            Easing.BackOut => 1f + (2.70158f * MathF.Pow(t - 1f, 3f)) + (1.70158f * MathF.Pow(t - 1f, 2f)),
            _ => t,
        };
    }
}

/// <summary>One running animation of one element property (a few floats, so colors and vectors share the stepper).</summary>
internal sealed class UIAnimation
{
    private readonly float[] _from, _to, _value, _velocity;
    private readonly Action<float[]> _apply;
    private readonly Motion _motion;
    private float _time;

    public UIAnimation(string property, float[] from, float[] to, float[] velocity, Motion motion, Action<float[]> apply)
    {
        Property = property;
        (_from, _to, _velocity, _motion, _apply) = (from, to, velocity, motion, apply);
        _value = (float[])from.Clone();
    }

    public string Property { get; }

    /// <summary>The spring velocity, kept when the property is retargeted.</summary>
    public float[] Velocity => _velocity;

    public float[] Target => _to;

    /// <summary>Advances and applies; false once settled (the target has been applied exactly).</summary>
    public bool Step(float deltaSeconds)
    {
        var done = _motion.IsSpring ? StepSpring(deltaSeconds) : StepTween(deltaSeconds);
        _apply(done ? _to : _value);
        return !done;
    }

    private bool StepTween(float dt)
    {
        _time += dt;
        var t = _motion.Duration <= 0f ? 1f : _time / _motion.Duration;
        var eased = Motion.Ease(_motion.Easing, t);
        for (var i = 0; i < _value.Length; i++)
        {
            _value[i] = _from[i] + ((_to[i] - _from[i]) * eased);
        }

        return t >= 1f;
    }

    private bool StepSpring(float dt)
    {
        // Semi-implicit Euler in small steps: stable for stiff springs at low frame rates.
        var omega = MathF.Sqrt(_motion.Stiffness);
        var damping = 2f * _motion.DampingRatio * omega;
        var settled = true;
        for (var remaining = MathF.Min(dt, 0.1f); remaining > 0f; remaining -= 1f / 240f)
        {
            var h = MathF.Min(remaining, 1f / 240f);
            for (var i = 0; i < _value.Length; i++)
            {
                var force = (_motion.Stiffness * (_to[i] - _value[i])) - (damping * _velocity[i]);
                _velocity[i] += force * h;
                _value[i] += _velocity[i] * h;
            }
        }

        for (var i = 0; i < _value.Length; i++)
        {
            var scale = MathF.Max(1f, MathF.Abs(_to[i]));
            settled &= MathF.Abs(_to[i] - _value[i]) < 0.001f * scale && MathF.Abs(_velocity[i]) < 0.01f * scale;
        }

        if (settled)
        {
            Array.Clear(_velocity);
        }

        return settled;
    }
}

/// <summary>Animates element properties: <c>button.AnimateScale(0.96f, theme.Press)</c>.</summary>
public static class UIAnimations
{
    private static int _delays;

    public static T AnimateOpacity<T>(this T element, float target, Motion motion)
        where T : UIElement =>
        Animate(element, "opacity", [element.Opacity ?? 1f], [target], motion, v => element.Opacity = v[0]);

    public static T AnimateScale<T>(this T element, float target, Motion motion)
        where T : UIElement =>
        Animate(element, "scale", [element.RenderScale], [target], motion, v => element.RenderScale = v[0]);

    public static T AnimateTranslate<T>(this T element, Vector2 target, Motion motion)
        where T : UIElement =>
        Animate(element, "translate", [element.Translate.X, element.Translate.Y], [target.X, target.Y], motion, v => element.Translate = new Vector2(v[0], v[1]));

    public static T AnimateBackground<T>(this T element, Color target, Motion motion)
        where T : UIElement
    {
        var from = element.BackgroundColor ?? element.ResolveStyle().Background;
        return Animate(element, "background", [from.R, from.G, from.B, from.A], [target.R, target.G, target.B, target.A], motion,
            v => element.BackgroundColor = new Color(v[0], v[1], v[2], v[3]));
    }

    /// <summary>
    /// An entrance: the element starts transparent and moved by <paramref name="offset"/>, then fades and slides to its
    /// place over <paramref name="seconds"/>, once it joins a UI context (when mounted, or when a region builds it
    /// later) and <paramref name="delay"/> has passed. Increasing delays stagger a list. Already in a context: starts now.
    /// </summary>
    public static T Appear<T>(this T element, Vector2 offset = default, float delay = 0f, float seconds = 0.45f)
        where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(element);
        var opacity = element.Opacity ?? 1f;
        (element.Opacity, element.Translate) = (0f, offset);
        void Play()
        {
            var motion = Motion.Tween(seconds, Easing.CubicOut);
            element.AnimateOpacity(opacity, motion).AnimateTranslate(Vector2.Zero, motion);
        }

        void Start()
        {
            if (delay > 0f)
            {
                element.After(delay, Play);
            }
            else
            {
                Play();
            }
        }

        if (element.Context is null)
        {
            element.WhenAttached += Start;
        }
        else
        {
            Start();
        }

        return element;
    }

    /// <summary>
    /// Runs <paramref name="action"/> after <paramref name="seconds"/> of UI time, unless the element is disposed (or
    /// its animations stopped) first. Without a context it runs at once.
    /// </summary>
    public static T After<T>(this T element, float seconds, Action action)
        where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(action);
        return Animate(element, "after#" + Interlocked.Increment(ref _delays), [0f], [1f], Motion.Tween(MathF.Max(seconds, 1e-4f), Easing.Linear), v =>
        {
            if (v[0] >= 1f)
            {
                action();
            }
        });
    }

    /// <summary>Stops an element's animations (one property, or all) where they are.</summary>
    public static void StopAnimations(this UIElement element, string? property = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.StopAnimations(property);
    }

    /// <summary>Whether the element has a running animation (of one property, or any).</summary>
    public static bool IsAnimating(this UIElement element, string? property = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        return element.Animations is { } running && running.Any(a => property is null || a.Property == property);
    }

    private static T Animate<T>(T element, string property, float[] from, float[] to, Motion motion, Action<float[]> apply)
        where T : UIElement
    {
        ArgumentNullException.ThrowIfNull(element);
        if (motion.IsInstant || element.Context is null)
        {
            element.StopAnimations(property);
            apply(to);
            return element;
        }

        var running = element.Animations?.FirstOrDefault(a => a.Property == property);
        if (running is not null && running.Target.AsSpan().SequenceEqual(to))
        {
            return element; // already heading there
        }

        var velocity = running is not null && motion.IsSpring ? running.Velocity : new float[to.Length];
        element.AddAnimation(new UIAnimation(property, from, to, velocity, motion, apply));
        return element;
    }
}
