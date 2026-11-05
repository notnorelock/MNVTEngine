using Silk.NET.Core;
using Silk.NET.GLFW;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace MNVTEngine.Platform;

/// <summary>Runtime window properties: mode, size and limits, position, top-most, icon.</summary>
public sealed partial class GameWindow
{
    private WindowMode _mode;
    private Vector2D<int> _windowedPosition, _windowedSize;

    /// <summary>
    /// Changes how the window occupies the screen. Leaving a fullscreen mode restores the last windowed position and size.
    /// The graphics device sees the new size through <see cref="IGameLoop.Resize"/> and recreates its swapchain.
    /// </summary>
    public WindowMode Mode
    {
        get => _mode;
        set
        {
            if (value == _mode)
            {
                return;
            }

            if (_mode is WindowMode.Windowed or WindowMode.Borderless)
            {
                (_windowedPosition, _windowedSize) = (_window.Position, _window.Size);
            }

            _mode = value;
            switch (value)
            {
                case WindowMode.Fullscreen:
                    _window.WindowState = WindowState.Fullscreen;
                    break;
                case WindowMode.BorderlessFullscreen:
                    _window.WindowState = WindowState.Normal;
                    _window.WindowBorder = WindowBorder.Hidden;
                    if (_window.Monitor is { } monitor)
                    {
                        _window.Position = monitor.Bounds.Origin;
                        _window.Size = monitor.Bounds.Size;
                    }

                    break;
                default:
                    _window.WindowState = WindowState.Normal;
                    _window.WindowBorder = value == WindowMode.Borderless ? WindowBorder.Hidden : _options.Resizable ? WindowBorder.Resizable : WindowBorder.Fixed;
                    if (_windowedSize.X > 0)
                    {
                        _window.Size = _windowedSize;
                        _window.Position = _windowedPosition;
                    }

                    break;
            }
        }
    }

    /// <summary>Whether the user can resize the window (windowed mode).</summary>
    public bool Resizable
    {
        get => _window.WindowBorder == WindowBorder.Resizable;
        set
        {
            if (_mode == WindowMode.Windowed)
            {
                _window.WindowBorder = value ? WindowBorder.Resizable : WindowBorder.Fixed;
            }
        }
    }

    public bool TopMost
    {
        get => _window.TopMost;
        set => _window.TopMost = value;
    }

    /// <summary>Top-left corner in screen coordinates.</summary>
    public (int X, int Y) Position
    {
        get => (_window.Position.X, _window.Position.Y);
        set => _window.Position = new Vector2D<int>(value.X, value.Y);
    }

    /// <summary>Resizes the client area (screen coordinates; ignored in fullscreen modes).</summary>
    public void SetSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (_mode is WindowMode.Windowed or WindowMode.Borderless)
        {
            _window.Size = new Vector2D<int>(width, height);
        }
    }

    /// <summary>Centers the window on its monitor.</summary>
    public void Center()
    {
        if (_window.Monitor is { } monitor)
        {
            var bounds = monitor.Bounds;
            _window.Position = bounds.Origin + ((bounds.Size - _window.Size) / 2);
        }
    }

    /// <summary>Sets the window and taskbar icon (several sizes let the platform pick the sharpest); empty restores the default.</summary>
    public void SetIcon(IReadOnlyList<WindowIcon> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        var raw = images.Select(i => new RawImage(i.Width, i.Height, i.Rgba)).ToArray();
        _window.SetWindowIcon(raw);
    }

    /// <summary>The Win32 window handle (HWND) on Windows, for native integrations such as a menu bar; 0 elsewhere.</summary>
    public nint Win32Handle => _window.Native?.Win32 is { } win32 ? win32.Hwnd : 0;

    /// <summary>Size limits in screen coordinates (0 = none). GLFW applies them to resizable windows.</summary>
    public unsafe void SetSizeLimits(int minWidth, int minHeight, int maxWidth, int maxHeight)
    {
        if (_window.Native?.Glfw is not { } handle)
        {
            return;
        }

        const int DontCare = -1;
        GlfwProvider.GLFW.Value.SetWindowSizeLimits(
            (WindowHandle*)handle,
            minWidth > 0 ? minWidth : DontCare,
            minHeight > 0 ? minHeight : DontCare,
            maxWidth > 0 ? maxWidth : DontCare,
            maxHeight > 0 ? maxHeight : DontCare);
    }

    private void ApplyInitialState()
    {
        SetSizeLimits(_options.MinWidth, _options.MinHeight, _options.MaxWidth, _options.MaxHeight);
        if (_options.Icon.Count > 0)
        {
            SetIcon(_options.Icon);
        }

        if (_options.Position is { } position)
        {
            Position = position;
        }
        else
        {
            Center();
        }

        _mode = _options.Mode is WindowMode.Borderless ? WindowMode.Borderless : WindowMode.Windowed;
        Mode = _options.Mode;
    }
}
