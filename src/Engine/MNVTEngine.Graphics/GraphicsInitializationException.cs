// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-04.

namespace MNVTEngine.Graphics;

/// <summary>The graphics backend cannot start on this machine (missing runtime, no suitable GPU, unsupported API version).</summary>
public sealed class GraphicsInitializationException : Exception
{
    public GraphicsInitializationException()
    {
    }

    public GraphicsInitializationException(string message)
        : base(message)
    {
    }

    public GraphicsInitializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
