using MNVTEngine.Mathematics;
using Silk.NET.Input;
using Key = MNVTEngine.Key;
using MouseButton = MNVTEngine.MouseButton;
using SilkKey = Silk.NET.Input.Key;
using SilkMouseButton = Silk.NET.Input.MouseButton;

namespace MNVTEngine.Platform;

/// <summary>Forwards Silk.NET device events into an <see cref="InputState"/>.</summary>
internal sealed class SilkInputBridge : IDisposable
{
    private static readonly Dictionary<SilkKey, Key> KeyMap = BuildKeyMap();

    private readonly IInputContext _context;
    private readonly InputState _state;

    public SilkInputBridge(IInputContext context, InputState state)
    {
        _context = context;
        _state = state;
        foreach (var keyboard in context.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyUp += OnKeyUp;
        }

        foreach (var mouse in context.Mice)
        {
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnScroll;
        }
    }

    /// <summary>Keys of <see cref="Key"/> that have no Silk.NET counterpart. Expected to be empty.</summary>
    public static IEnumerable<Key> UnmappedKeys =>
        Enum.GetValues<Key>().Where(k => k != Key.Unknown && !KeyMap.ContainsValue(k));

    public void Dispose() => _context.Dispose();

    private static Dictionary<SilkKey, Key> BuildKeyMap()
    {
        var map = new Dictionary<SilkKey, Key>();
        foreach (var key in Enum.GetValues<Key>())
        {
            if (key != Key.Unknown && Enum.TryParse<SilkKey>(key.ToString(), out var silk))
            {
                map[silk] = key;
            }
        }

        return map;
    }

    private void OnKeyDown(IKeyboard keyboard, SilkKey key, int scancode) => _state.SetKey(KeyMap.GetValueOrDefault(key), true);

    private void OnKeyUp(IKeyboard keyboard, SilkKey key, int scancode) => _state.SetKey(KeyMap.GetValueOrDefault(key), false);

    private void OnMouseDown(IMouse mouse, SilkMouseButton button)
    {
        if (Map(button) is { } b)
        {
            _state.SetMouseButton(b, true);
        }
    }

    private void OnMouseUp(IMouse mouse, SilkMouseButton button)
    {
        if (Map(button) is { } b)
        {
            _state.SetMouseButton(b, false);
        }
    }

    private void OnMouseMove(IMouse mouse, System.Numerics.Vector2 position) => _state.SetMousePosition(new Vector2(position.X, position.Y));

    private void OnScroll(IMouse mouse, ScrollWheel wheel) => _state.AddScroll(new Vector2(wheel.X, wheel.Y));

    private static MouseButton? Map(SilkMouseButton button) => button switch
    {
        SilkMouseButton.Left => MouseButton.Left,
        SilkMouseButton.Right => MouseButton.Right,
        SilkMouseButton.Middle => MouseButton.Middle,
        _ => null,
    };
}
