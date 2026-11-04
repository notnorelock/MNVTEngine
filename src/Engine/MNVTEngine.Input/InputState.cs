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
    private bool _uiKeyboard;
    private bool _uiMouse;

    /// <summary>Cursor position in window pixels, origin top-left.</summary>
    public Vector2 MousePosition { get; private set; }

    /// <summary>Cursor movement since the previous frame.</summary>
    public Vector2 MouseDelta { get; private set; }

    /// <summary>Wheel movement this frame; positive Y is away from the user.</summary>
    public Vector2 ScrollDelta => MouseHidden ? Vector2.Zero : _scroll;

    /// <summary>Characters typed this frame (layout- and IME-aware), for text fields.</summary>
    public string TextInput => KeyboardHidden ? "" : _text.ToString();

    /// <summary>True while the debug UI or the game UI owns the keyboard: gameplay key queries read as released.</summary>
    public bool KeyboardCaptured => KeyboardHidden;

    /// <summary>True while the debug UI or the game UI owns the mouse: button and wheel queries read as idle (the position stays visible).</summary>
    public bool MouseCaptured => MouseHidden;

    /// <summary>
    /// Input as the game UI sees it: everything except what the debug UI (drawn above it) captured. The game UI captures
    /// with <see cref="CaptureForUI"/>, which hides input from gameplay only.
    /// </summary>
    public InputView ForUI => new(this);

    internal bool DebugKeyboardCaptured => _keyboardCaptured;

    internal bool DebugMouseCaptured => _mouseCaptured;

    internal Vector2 RawScroll => _scroll;

    internal string RawText => _text.ToString();

    private bool KeyboardHidden => _keyboardCaptured || _uiKeyboard;

    private bool MouseHidden => _mouseCaptured || _uiMouse;

    private Vector2 _scroll;

    public bool IsKeyDown(Key key) => Visible(key) && _keyDown[(int)key];

    public bool WasKeyPressed(Key key) => Visible(key) && _keyPressed[(int)key];

    public bool WasKeyReleased(Key key) => Visible(key) && _keyReleased[(int)key];

    public bool IsMouseButtonDown(MouseButton button) => !MouseHidden && _buttonDown[(int)button];

    public bool WasMouseButtonPressed(MouseButton button) => !MouseHidden && _buttonPressed[(int)button];

    public bool WasMouseButtonReleased(MouseButton button) => !MouseHidden && _buttonReleased[(int)button];

    /// <summary>
    /// The game UI takes the keyboard (a focused text field) and/or the mouse (the cursor is over UI) from gameplay for
    /// this frame. Cleared by <see cref="BeginFrame"/>: the UI decides again every frame.
    /// </summary>
    public void CaptureForUI(bool keyboard, bool mouse) => (_uiKeyboard, _uiMouse) = (_uiKeyboard || keyboard, _uiMouse || mouse);

    /// <summary>
    /// Hides keyboard and/or mouse from gameplay until <see cref="ReleaseCapture"/> (used by the runtime debug UI while it
    /// has focus). The debug UI reads input after releasing the capture at the start of its frame.
    /// </summary>
    public void Capture(bool keyboard, bool mouse) => (_keyboardCaptured, _mouseCaptured) = (keyboard, mouse);

    public void ReleaseCapture() => (_keyboardCaptured, _mouseCaptured) = (false, false);

    internal bool RawKeyDown(Key key) => !_keySuppressed[(int)key] && _keyDown[(int)key];

    internal bool RawKeyPressed(Key key) => !_keySuppressed[(int)key] && _keyPressed[(int)key];

    /// <param name="kind">0 down, 1 pressed, 2 released.</param>
    internal bool RawButton(MouseButton button, int kind) => (kind == 0 ? _buttonDown : kind == 1 ? _buttonPressed : _buttonReleased)[(int)button];

    /// <summary>Hides one key from gameplay until it is released (debug shortcuts).</summary>
    public void Suppress(Key key)
    {
        if (key != Key.Unknown)
        {
            _keySuppressed[(int)key] = true;
        }
    }

    private bool Visible(Key key) => !KeyboardHidden && !_keySuppressed[(int)key];

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
        (_uiKeyboard, _uiMouse) = (false, false);
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

/// <summary>Input seen by the game UI: hidden only by the debug UI's capture, not by the game UI's own.</summary>
public readonly struct InputView(InputState state)
{
    public Vector2 MousePosition => state.MousePosition;

    public bool KeyboardAvailable => !state.DebugKeyboardCaptured;

    public bool MouseAvailable => !state.DebugMouseCaptured;

    public Vector2 ScrollDelta => MouseAvailable ? state.RawScroll : Vector2.Zero;

    public string TextInput => KeyboardAvailable ? state.RawText : "";

    public bool IsKeyDown(Key key) => KeyboardAvailable && state.RawKeyDown(key);

    public bool WasKeyPressed(Key key) => KeyboardAvailable && state.RawKeyPressed(key);

    public bool IsMouseButtonDown(MouseButton button) => MouseAvailable && state.RawButton(button, 0);

    public bool WasMouseButtonPressed(MouseButton button) => MouseAvailable && state.RawButton(button, 1);

    public bool WasMouseButtonReleased(MouseButton button) => MouseAvailable && state.RawButton(button, 2);
}