// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

namespace MNVTEngine.UI;

/// <summary>
/// Fitting the interface to the window: designed at <see cref="ReferenceWidth"/> window pixels wide, it shrinks with a
/// narrower window (down to <see cref="MinFit"/>) so the same layout stays on screen, and grows with a wider one up to
/// <see cref="MaxFit"/>.
/// </summary>
public sealed partial class UIContext
{
    private float _fit = 1f;
    private float? _referenceWidth;

    /// <summary>The window width the interface was designed at; null (the default) never fits, only <see cref="Scale"/> applies.</summary>
    public float? ReferenceWidth
    {
        get => _referenceWidth;
        set
        {
            if (value is { } width)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            }

            _referenceWidth = value;
            MarkLayoutDirty();
        }
    }

    /// <summary>The smallest fit: 1 / 2.2 keeps text readable in a small window.</summary>
    public float MinFit { get; set; } = 1f / 2.2f;

    /// <summary>The largest fit: 1 (the default) never grows past the design size.</summary>
    public float MaxFit { get; set; } = 1f;

    /// <summary>The window width over the reference, clamped to the limits; 1 without a reference.</summary>
    public static float FitScale(float windowWidth, float? referenceWidth, float min, float max) =>
        referenceWidth is { } reference && windowWidth > 0f ? Math.Clamp(windowWidth / reference, min, Math.Max(min, max)) : 1f;
}
