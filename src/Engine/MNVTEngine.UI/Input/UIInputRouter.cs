using MNVTEngine.Mathematics;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.UI;

/// <summary>
/// Turns one frame of <see cref="InputView"/> into UI events:
/// <list type="bullet">
/// <item><b>Pointer.</b> Hover enter and exit. A press captures the pointer until release. A click needs press and
/// release on the same element; a second click within 0.4 s is a double click. The wheel bubbles from the hovered
/// element.</item>
/// <item><b>Keys.</b> Sent to the focused element and bubbled to its ancestors. Held editing keys repeat after 0.45 s,
/// 30 times a second; the engine synthesizes the repeat, independent of the OS.</item>
/// <item><b>Unhandled keys.</b> Tab and Shift+Tab move focus in tab order, Enter and Space activate the focused element,
/// Escape clears focus.</item>
/// <item><b>Text input</b> goes to the focused element.</item>
/// </list>
/// </summary>
internal sealed class UIInputRouter(UIContext context)
{
    private const float RepeatDelay = 0.45f;
    private const float RepeatInterval = 1f / 30f;
    private const float DoubleClickSeconds = 0.4f;
    private static readonly Key[] RoutedKeys =
    [
        Key.Tab, Key.Enter, Key.Space, Key.Escape, Key.Backspace, Key.Delete, Key.Left, Key.Right, Key.Up, Key.Down,
        Key.Home, Key.End, Key.PageUp, Key.PageDown, Key.A, Key.C, Key.V, Key.X, Key.Insert,
    ];

    private static readonly Key[] RepeatingKeys = [Key.Backspace, Key.Delete, Key.Left, Key.Right, Key.Up, Key.Down, Key.PageUp, Key.PageDown, Key.Enter];

    private Vector2 _lastPointer = new(float.NaN, float.NaN);
    private Key _held;
    private float _heldTime;
    private float _nextRepeat;
    private UIElement? _lastClicked;
    private float _sinceClick = float.MaxValue;
    private float _hoverTime;
    private Tooltip? _tooltip;
    private bool _pointerAvailable;

    public void Process(InputView input, float dt, bool pointerBlocked)
    {
        _sinceClick += dt;
        var pointer = input.MousePosition / context.Scale;
        _pointerAvailable = input.MouseAvailable && !pointerBlocked;
        ProcessPointer(input, pointer);
        ProcessKeys(input, dt);
        if (input.TextInput is { Length: > 0 } text && context.Focused is { } focused)
        {
            Route(focused, new TextInputEventArgs { Text = text }, (e, a) => e.OnTextInput(a));
        }

        UpdateTooltip(pointer, dt);
        context.WantsMouse = _pointerAvailable && (context.Pressed is not null || context.Hovered is not null);
    }

    private void ProcessPointer(InputView input, Vector2 pointer)
    {
        var hit = _pointerAvailable ? context.HitTest(pointer) : null;
        var args = new PointerEventArgs { Position = pointer };
        if (hit != context.Hovered)
        {
            var previous = context.Hovered;
            context.Hovered = hit;
            _hoverTime = 0f;
            previous?.OnPointerExit(args);
            hit?.OnPointerEnter(args);
            context.MarkVisualDirty();
        }

        if (context.Pressed is { } captured && pointer != _lastPointer && input.IsMouseButtonDown(MouseButton.Left))
        {
            captured.OnPointerDrag(new PointerEventArgs { Position = pointer, Button = MouseButton.Left });
        }

        _lastPointer = pointer;
        if (input.ScrollDelta != Vector2.Zero && hit is not null)
        {
            Route(hit, new PointerEventArgs { Position = pointer, Scroll = input.ScrollDelta }, (e, a) => e.OnScroll(a));
        }

        if (_pointerAvailable && input.WasMouseButtonPressed(MouseButton.Left))
        {
            var target = hit is not null && hit.IsEnabledInHierarchy ? hit : null;
            context.Pressed = target;
            context.SetFocus(FocusableAncestor(target));
            if (target is not null)
            {
                Route(target, new PointerEventArgs { Position = pointer, Button = MouseButton.Left }, (e, a) => e.OnPointerDown(a));
            }

            context.MarkVisualDirty();
        }

        if (input.WasMouseButtonReleased(MouseButton.Left) && context.Pressed is { } pressed)
        {
            context.Pressed = null;
            var up = new PointerEventArgs { Position = pointer, Button = MouseButton.Left };
            pressed.OnPointerUp(up);
            if (hit is not null && UIContext.IsSelfOrAncestor(pressed, hit) && pressed.IsEnabledInHierarchy)
            {
                var count = _lastClicked == pressed && _sinceClick < DoubleClickSeconds ? 2 : 1;
                (_lastClicked, _sinceClick) = (pressed, 0f);
                pressed.OnClick(new PointerEventArgs { Position = pointer, Button = MouseButton.Left, ClickCount = count, Target = pressed });
            }

            context.MarkVisualDirty();
        }
    }

    private void ProcessKeys(InputView input, float dt)
    {
        var control = input.IsKeyDown(Key.ControlLeft) || input.IsKeyDown(Key.ControlRight);
        var shift = input.IsKeyDown(Key.ShiftLeft) || input.IsKeyDown(Key.ShiftRight);
        var alt = input.IsKeyDown(Key.AltLeft) || input.IsKeyDown(Key.AltRight);
        foreach (var key in RoutedKeys)
        {
            if (input.WasKeyPressed(key))
            {
                (_held, _heldTime, _nextRepeat) = (key, 0f, RepeatDelay);
                Dispatch(key, control, shift, alt, repeat: false);
            }
        }

        if (_held != Key.Unknown && input.IsKeyDown(_held) && Array.IndexOf(RepeatingKeys, _held) >= 0)
        {
            _heldTime += dt;
            while (_heldTime >= _nextRepeat)
            {
                _nextRepeat += RepeatInterval;
                Dispatch(_held, control, shift, alt, repeat: true);
            }
        }
        else if (_held != Key.Unknown && !input.IsKeyDown(_held))
        {
            _held = Key.Unknown;
        }
    }

    private void Dispatch(Key key, bool control, bool shift, bool alt, bool repeat)
    {
        var args = new KeyEventArgs { Key = key, Control = control, Shift = shift, Alt = alt, Repeat = repeat };
        if (context.Focused is { } focused)
        {
            Route(focused, args, (e, a) => e.OnKeyDown(a));
        }

        if (args.Handled)
        {
            return;
        }

        switch (key)
        {
            case Key.Tab when !repeat:
                MoveFocus(shift ? -1 : 1);
                break;
            case Key.Enter or Key.Space when context.Focused is { } target && !repeat:
                target.OnClick(new PointerEventArgs { Position = target.Bounds.Center, Button = MouseButton.Left, ClickCount = 1, Target = target });
                context.MarkVisualDirty();
                break;
            case Key.Escape:
                context.SetFocus(null);
                break;
        }
    }

    private void MoveFocus(int direction)
    {
        var order = context.Overlay.Descendants().Concat(context.Root.Descendants())
            .Where(e => e.Focusable && e.IsEnabledInHierarchy && UIContext.IsShown(e))
            .Select((e, i) => (Element: e, Index: i))
            .OrderBy(p => p.Element.TabIndex).ThenBy(p => p.Index)
            .Select(p => p.Element).ToList();
        if (order.Count == 0)
        {
            return;
        }

        var current = context.Focused is { } f ? order.IndexOf(f) : -1;
        var next = current < 0 ? (direction > 0 ? 0 : order.Count - 1) : (current + direction + order.Count) % order.Count;
        context.SetFocus(order[next]);
    }

    private void UpdateTooltip(Vector2 pointer, float dt)
    {
        _hoverTime += dt;
        var text = context.Hovered?.Tooltip;
        var show = text is not null && context.Pressed is null && _hoverTime >= context.TooltipDelay;
        if (show && (_tooltip is null || _tooltip.Text != text))
        {
            _tooltip?.RemoveFromParent();
            _tooltip = context.Overlay.AddChild(new Tooltip { Text = text!, Left = pointer.X + 14f, Top = pointer.Y + 18f, ZIndex = int.MaxValue });
        }
        else if (!show && _tooltip is not null)
        {
            _tooltip.RemoveFromParent();
            _tooltip = null;
        }
    }

    private static UIElement? FocusableAncestor(UIElement? element)
    {
        for (var e = element; e is not null; e = e.Parent)
        {
            if (e.Focusable)
            {
                return e;
            }
        }

        return null;
    }

    private static void Route<T>(UIElement target, T args, Action<UIElement, T> invoke)
        where T : UIEventArgs
    {
        args.Target = target;
        for (var e = target; e is not null && !args.Handled; e = e.Parent)
        {
            invoke(e, args);
        }
    }
}
