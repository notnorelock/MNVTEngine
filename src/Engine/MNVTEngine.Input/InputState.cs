using MNVTEngine.Mathematics;

namespace MNVTEngine;

/// <summary>
/// Per-frame keyboard and mouse state fed by the platform backend. "Pressed" and "Released" are edges
/// for the current frame; a press and release inside one frame reports both edges.
/// </summary>
public sealed class InputState
{
    private static readonly int KeyCount = Enum.GetValues<Key>().Length;
    private static readonly int ButtonCount = Enum.GetValues<MouseButton>().Length;

    private readonly bool[] _keyDown = new bool[KeyCount];
    private readonly bool[] _keyPressed = new bool[KeyCount];
    private readonly bool[] _keyReleased = new bool[KeyCount];
    private readonly bool[] _buttonDown = new bool[ButtonCount];
    private readonly bool[] _buttonPressed = new bool[ButtonCount];
    private readonly bool[] _buttonReleased = new bool[ButtonCount];
    private readonly Dictionary<string, InputAxis> _axes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Horizontal"] = new InputAxis([Key.A, Key.Left], [Key.D, Key.Right]),
        ["Vertical"] = new InputAxis([Key.S, Key.Down], [Key.W, Key.Up]),
    };

    private readonly bool[] _keySuppressed = new bool[KeyCount];
    private readonly System.Text.StringBuilder _text = new();
    private Vector2 _previousMousePosition;
    private bool _keyboardCaptured;
    private bool _mouseCaptured;

    /// <summary>Cursor position in window pixels, origin top-left.</summary>
    public Vector2 MousePosition { get; private set; }

    /// <summary>Cursor movement since the previous frame.</summary>
    public Vector2 MouseDelta { get; private set; }

    /// <summary>Wheel movement this frame; positive Y is away from the user.</summary>
    public Vector2 ScrollDelta => _mouseCaptured ? Vector2.Zero : _scroll;

    /// <summary>Characters typed this frame (layout- and IME-aware), for text fields.</summary>
    public string TextInput => _keyboardCaptured ? "" : _text.ToString();

    /// <summary>True while a debug UI owns the keyboard: key queries read as released.</summary>
    public bool KeyboardCaptured => _keyboardCaptured;

    /// <summary>True while a debug UI owns the mouse: button and wheel queries read as idle (the position stays visible).</summary>
    public bool MouseCaptured => _mouseCaptured;

    private Vector2 _scroll;

    public bool IsKeyDown(Key key) => Visible(key) && _keyDown[(int)key];

    public bool WasKeyPressed(Key key) => Visible(key) && _keyPressed[(int)key];

    public bool WasKeyReleased(Key key) => Visible(key) && _keyReleased[(int)key];

    public bool IsMouseButtonDown(MouseButton button) => !_mouseCaptured && _buttonDown[(int)button];

    public bool WasMouseButtonPressed(MouseButton button) => !_mouseCaptured && _buttonPressed[(int)button];

    public bool WasMouseButtonReleased(MouseButton button) => !_mouseCaptured && _buttonReleased[(int)button];

    /// <summary>
    /// Hides keyboard and/or mouse from gameplay until <see cref="ReleaseCapture"/> (used by the runtime debug UI while it
    /// has focus). The debug UI reads input after releasing the capture at the start of its frame.
    /// </summary>
    public void Capture(bool keyboard, bool mouse) => (_keyboardCaptured, _mouseCaptured) = (keyboard, mouse);

    public void ReleaseCapture() => (_keyboardCaptured, _mouseCaptured) = (false, false);

    /// <summary>Hides one key from gameplay until it is released (debug shortcuts).</summary>
    public void Suppress(Key key)
    {
        if (key != Key.Unknown)
        {
            _keySuppressed[(int)key] = true;
        }
    }

    private bool Visible(Key key) => !_keyboardCaptured && !_keySuppressed[(int)key];

    /// <summary>Digital axis value in [-1, 1]. Built-in axes: "Horizontal" (A/D, arrows) and "Vertical" (S/W, arrows).</summary>
    /// <exception cref="KeyNotFoundException">No axis with this name is defined.</exception>
    public float GetAxis(string name)
    {
        if (!_axes.TryGetValue(name, out var axis))
        {
            throw new KeyNotFoundException($"Input axis '{name}' is not defined. Defined axes: {string.Join(", ", _axes.Keys)}.");
        }

        var value = 0f;
        if (axis.Negative.Any(IsKeyDown))
        {
            value -= 1f;
        }

        if (axis.Positive.Any(IsKeyDown))
        {
            value += 1f;
        }

        return value;
    }

    public void DefineAxis(string name, InputAxis axis)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(axis);
        _axes[name] = axis;
    }

    /// <summary>Clears per-frame edges. The host calls this before pumping window events.</summary>
    public void BeginFrame()
    {
        Array.Clear(_keyPressed);
        Array.Clear(_keyReleased);
        Array.Clear(_buttonPressed);
        Array.Clear(_buttonReleased);
        _scroll = Vector2.Zero;
        _text.Clear();
        for (var i = 0; i < _keySuppressed.Length; i++)
        {
            _keySuppressed[i] &= _keyDown[i]; // a suppressed key stays hidden until released
        }

        MouseDelta = MousePosition - _previousMousePosition;
        _previousMousePosition = MousePosition;
    }

    public void SetKey(Key key, bool down)
    {
        if (key == Key.Unknown)
        {
            return;
        }

        Set(_keyDown, _keyPressed, _keyReleased, (int)key, down);
    }

    public void SetMouseButton(MouseButton button, bool down) =>
        Set(_buttonDown, _buttonPressed, _buttonReleased, (int)button, down);

    public void SetMousePosition(Vector2 position) => MousePosition = position;

    public void AddScroll(Vector2 delta) => _scroll += delta;

    public void AddText(char character) => _text.Append(character);

    /// <summary>Releases everything held, e.g. when the window loses focus and key-up events will never arrive.</summary>
    public void ReleaseAll()
    {
        for (var i = 0; i < _keyDown.Length; i++)
        {
            Set(_keyDown, _keyPressed, _keyReleased, i, false);
        }

        for (var i = 0; i < _buttonDown.Length; i++)
        {
            Set(_buttonDown, _buttonPressed, _buttonReleased, i, false);
        }
    }

    private static void Set(bool[] down, bool[] pressed, bool[] released, int index, bool isDown)
    {
        if (down[index] == isDown)
        {
            return; // Key repeat or duplicate event.
        }

        down[index] = isDown;
        (isDown ? pressed : released)[index] = true;
    }
}

/// <summary>Keys that drive an axis towards -1 and +1.</summary>
public sealed record InputAxis(IReadOnlyList<Key> Negative, IReadOnlyList<Key> Positive);
