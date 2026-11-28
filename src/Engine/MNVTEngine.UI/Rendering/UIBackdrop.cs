// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Graphics;

namespace MNVTEngine.UI;

/// <summary>
/// The blurred frame behind frosted UI: what the target shows so far, copied down a chain of halving targets with
/// linear filtering (each step averages 2x2 pixels), so the last one, sampled bilinearly at full size, is a soft
/// blur. No shader: it works wherever the backend can copy (<see cref="IGraphicsDevice.CanCopyColor"/>).
/// </summary>
internal sealed class UIBackdrop : IDisposable
{
    /// <summary>Halvings: 4 gives a sixteenth-size image, a soft frost of about 16 pixels.</summary>
    public const int Steps = 4;

    private readonly RenderTarget2D?[] _levels = new RenderTarget2D?[Steps];
    private int _width, _height;

    /// <summary>The blurred image, once <see cref="Resize"/> made one.</summary>
    public RenderTarget2D? Result => _levels[^1];

    /// <summary>Backdrop texels per framebuffer pixel.</summary>
    public float Scale => _width > 0 && Result is { } result ? (float)result.Width / _width : 0f;

    /// <summary>Makes the chain for a framebuffer size (again only when it changed).</summary>
    // shortcut: one chain per renderer; a host drawing UI into targets of different sizes each frame would rebuild it every time
    public void Resize(IGraphicsDevice device, int width, int height)
    {
        if (width == _width && height == _height && Result is not null)
        {
            return;
        }

        Dispose();
        (_width, _height) = (width, height);
        for (var i = 0; i < Steps; i++)
        {
            var (w, h) = (Math.Max(1, width >> (i + 1)), Math.Max(1, height >> (i + 1)));
            _levels[i] = device.CreateRenderTarget(w, h, SamplerState.LinearClamp, $"UI.Backdrop{i}");
        }
    }

    /// <summary>Copies what the current target shows now down the chain.</summary>
    public void Capture(IGraphicsDevice device)
    {
        RenderTarget2D? previous = null;
        foreach (var level in _levels)
        {
            device.CopyColor(previous, level!);
            previous = level;
        }
    }

    public void Dispose()
    {
        for (var i = 0; i < _levels.Length; i++)
        {
            _levels[i]?.Dispose();
            _levels[i] = null;
        }
    }
}
