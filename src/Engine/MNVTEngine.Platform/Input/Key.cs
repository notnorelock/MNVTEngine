namespace MNVTEngine.Platform.Input;

/// <summary>Physical keyboard keys (US layout names). Names match Silk.NET's key names so the mapping is by name.</summary>
public enum Key
{
    Unknown = 0,
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Number0, Number1, Number2, Number3, Number4, Number5, Number6, Number7, Number8, Number9,
    Space, Enter, Escape, Tab, Backspace, Delete, Insert, Home, End, PageUp, PageDown,
    Left, Right, Up, Down,
    ShiftLeft, ShiftRight, ControlLeft, ControlRight, AltLeft, AltRight,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Minus, Equal, Comma, Period, Slash, Semicolon, Apostrophe, LeftBracket, RightBracket, BackSlash, GraveAccent,
}

public enum MouseButton
{
    Left,
    Right,
    Middle,
}
