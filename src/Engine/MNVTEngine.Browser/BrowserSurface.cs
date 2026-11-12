using MNVTEngine.BrowserHosting;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Browser;

/// <summary>
/// A browser page shown on this GameObject: <c>BrowserSurface2D</c> on a sprite, <c>BrowserSurface3D</c> on a mesh.
/// Opens its <see cref="Browser"/> in <see cref="Start"/> and closes it when destroyed; while enabled and
/// <see cref="Interactive"/>, the mouse over the page and the keyboard (after a click on the page) go to the page and
/// not to gameplay.
/// </summary>
public abstract class BrowserSurface : Component
{
    private (int X, int Y)? _lastPixel;
    private bool _textureShown;

    /// <summary>The first page (see <see cref="BrowserOptions.Url"/>).</summary>
    [SerializeField]
    public string Url { get; set; } = "mnvt://game/index.html";

    [SerializeField]
    public int PageWidth { get; set; } = 1280;

    [SerializeField]
    public int PageHeight { get; set; } = 720;

    [SerializeField]
    public bool Transparent { get; set; }

    [SerializeField]
    public int FrameRate { get; set; } = 30;

    [SerializeField]
    public BrowserSecurityProfile Profile { get; set; } = BrowserSecurityProfile.TrustedLocal;

    [SerializeField]
    public List<string> AllowedDomains { get; set; } = [];

    [SerializeField]
    public BrowserCapabilities Capabilities { get; set; }

    [SerializeField]
    public bool Muted { get; set; }

    /// <summary>Mouse and keyboard reach the page.</summary>
    [SerializeField]
    public bool Interactive { get; set; } = true;

    /// <summary>The open browser (null before <see cref="Start"/>, or without the browser module).</summary>
    public BrowserInstance? Browser { get; private set; }

    /// <summary>The page has keyboard focus (a click on it gives it; a click elsewhere or Escape takes it).</summary>
    public bool HasFocus { get; private set; }

    /// <summary>The page pixel under the mouse this frame, if any.</summary>
    public (int X, int Y)? Hover { get; private set; }

    public BrowserOptions CreateOptions() => new()
    {
        Url = Url,
        Width = PageWidth,
        Height = PageHeight,
        Transparent = Transparent,
        FrameRate = FrameRate,
        Profile = Profile,
        AllowedDomains = [.. AllowedDomains],
        Capabilities = Capabilities,
        Muted = Muted,
    };

    public override void Start()
    {
        if (GameObject.Scene.GetService<BrowserService>() is not { } service)
        {
            Log.Error("No browser service: the project does not have the browser module.");
            return;
        }

        Browser = service.Create(CreateOptions());
        Configure(Browser);
    }

    /// <summary>Adjusts the new browser before its first frame (texture orientation).</summary>
    protected virtual void Configure(BrowserInstance browser)
    {
    }

    public override void OnEnable() => Browser?.SetVisible(true);

    public override void OnDisable()
    {
        Browser?.SetVisible(false);
        Release();
    }

    public override void Update()
    {
        if (Browser is { FramesUploaded: > 0 } shown && !_textureShown)
        {
            // Shown once the first page frame is a texture: before that, the id is no asset and would log a missing texture.
            ShowTexture(new AssetRef(shown.TextureId));
            _textureShown = true;
        }

        if (Browser is not { State: BrowserState.Ready } browser || !Interactive || GameObject.Scene.Input is not { } input)
        {
            return;
        }

        var pixel = PagePixel(input.MousePosition);
        Hover = pixel;
        var modifiers = BrowserInput.Modifiers(input);
        if (pixel is { } p)
        {
            if (_lastPixel != p)
            {
                browser.SendMouseMove(p.X, p.Y, modifiers);
            }

            foreach (var (button, target) in BrowserInput.Buttons)
            {
                if (input.WasMouseButtonPressed(button))
                {
                    browser.SendMouseButton(p.X, p.Y, target, released: false, 1, modifiers);
                }

                if (input.WasMouseButtonReleased(button))
                {
                    browser.SendMouseButton(p.X, p.Y, target, released: true, 1, modifiers);
                }
            }

            if (input.ScrollDelta is { } scroll && (scroll.X != 0f || scroll.Y != 0f))
            {
                browser.SendMouseWheel(p.X, p.Y, (int)(scroll.X * 120f), (int)(scroll.Y * 120f));
            }

            if (input.WasMouseButtonPressed(MouseButton.Left))
            {
                Focus(true);
            }
        }
        else
        {
            if (_lastPixel is not null)
            {
                browser.SendMouseLeave();
            }

            if (input.WasMouseButtonPressed(MouseButton.Left))
            {
                Focus(false);
            }
        }

        _lastPixel = pixel;
        if (HasFocus)
        {
            if (input.WasKeyPressed(Key.Escape))
            {
                Focus(false);
            }
            else
            {
                BrowserInput.ForwardKeys(browser, input, modifiers);
            }
        }

        // shortcut: the first surface under the mouse takes input; overlapping surfaces see it hidden.
        input.CaptureForUI(keyboard: HasFocus, mouse: pixel is not null);
    }

    public override void OnDestroy()
    {
        Browser?.Dispose();
        Browser = null;
        if (_textureShown)
        {
            ShowTexture(default);
            _textureShown = false;
        }
    }

    /// <summary>Shows the browser's texture on the renderer (or clears it: <c>default</c>).</summary>
    protected abstract void ShowTexture(AssetRef texture);

    /// <summary>
    /// The page's texture coordinates under a window pixel (origin top-left): u to the right, v down from the top of
    /// the page, both 0 to 1; null when the pointer is not over the page.
    /// </summary>
    protected abstract Vector2? PageUV(Vector2 screen);

    /// <summary>Converts <see cref="PageUV"/> to page pixels.</summary>
    public (int X, int Y)? PagePixel(Vector2 screen)
    {
        if (Browser is not { } browser || PageUV(screen) is not { } uv || uv.X < 0f || uv.X > 1f || uv.Y < 0f || uv.Y > 1f)
        {
            return null;
        }

        return (Math.Clamp((int)(uv.X * browser.Width), 0, browser.Width - 1), Math.Clamp((int)(uv.Y * browser.Height), 0, browser.Height - 1));
    }

    private void Focus(bool focused)
    {
        HasFocus = focused;
        Browser?.SetFocus(focused);
    }

    private void Release()
    {
        if (_lastPixel is not null)
        {
            Browser?.SendMouseLeave();
        }

        _lastPixel = null;
        Hover = null;
        if (HasFocus)
        {
            Focus(false);
        }
    }
}

/// <summary>Engine input to Chromium input: Windows virtual-key codes and modifier flags.</summary>
public static class BrowserInput
{
    internal static readonly (MouseButton Engine, BrowserMouseButton Page)[] Buttons =
        [(MouseButton.Left, BrowserMouseButton.Left), (MouseButton.Middle, BrowserMouseButton.Middle), (MouseButton.Right, BrowserMouseButton.Right)];

    /// <summary>The Windows virtual-key code of a key (0 for <see cref="Key.Unknown"/>).</summary>
    public static int VirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 0x41 + (key - Key.A),
        >= Key.Number0 and <= Key.Number9 => 0x30 + (key - Key.Number0),
        >= Key.F1 and <= Key.F12 => 0x70 + (key - Key.F1),
        Key.Space => 0x20,
        Key.Enter => 0x0D,
        Key.Escape => 0x1B,
        Key.Tab => 0x09,
        Key.Backspace => 0x08,
        Key.Delete => 0x2E,
        Key.Insert => 0x2D,
        Key.Home => 0x24,
        Key.End => 0x23,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.ShiftLeft or Key.ShiftRight => 0x10,
        Key.ControlLeft or Key.ControlRight => 0x11,
        Key.AltLeft or Key.AltRight => 0x12,
        Key.Minus => 0xBD,
        Key.Equal => 0xBB,
        Key.Comma => 0xBC,
        Key.Period => 0xBE,
        Key.Slash => 0xBF,
        Key.Semicolon => 0xBA,
        Key.Apostrophe => 0xDE,
        Key.LeftBracket => 0xDB,
        Key.RightBracket => 0xDD,
        Key.BackSlash => 0xDC,
        Key.GraveAccent => 0xC0,
        _ => 0,
    };

    public static BrowserModifiers Modifiers(InputState input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var modifiers = BrowserModifiers.None;
        if (input.IsKeyDown(Key.ShiftLeft) || input.IsKeyDown(Key.ShiftRight))
        {
            modifiers |= BrowserModifiers.Shift;
        }

        if (input.IsKeyDown(Key.ControlLeft) || input.IsKeyDown(Key.ControlRight))
        {
            modifiers |= BrowserModifiers.Control;
        }

        if (input.IsKeyDown(Key.AltLeft) || input.IsKeyDown(Key.AltRight))
        {
            modifiers |= BrowserModifiers.Alt;
        }

        return modifiers;
    }

    /// <summary>
    /// Sends this frame's key presses and releases, and the typed text, to a page.
    /// shortcut: no key repeat while a key is held (the engine reports presses once); add repeat timing if text editing needs it.
    /// </summary>
    public static void ForwardKeys(BrowserInstance browser, InputState input, BrowserModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(input);
        foreach (var key in Enum.GetValues<Key>())
        {
            var code = VirtualKey(key);
            if (code == 0)
            {
                continue;
            }

            if (input.WasKeyPressed(key))
            {
                browser.SendKey(code, released: false, modifiers);
                if (key == Key.Enter)
                {
                    browser.SendText("\r", modifiers); // Chromium submits forms on the Enter character, as Windows sends it
                }
            }

            if (input.WasKeyReleased(key))
            {
                browser.SendKey(code, released: true, modifiers);
            }
        }

        if (input.TextInput is { Length: > 0 } text)
        {
            browser.SendText(text, modifiers);
        }
    }
}
