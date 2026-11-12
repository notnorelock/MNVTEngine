using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Runtime.Loading;

/// <summary>Resolves splash/loading screen assets (textures, fonts) for the views; both may return null when missing.</summary>
/// <param name="EngineLogo">The MNVTEngine wordmark (white), for "Made with MNVTEngine"; null shows the words.</param>
public sealed record StartupAssets(Font DefaultFont, Func<AssetRef, Texture2D?> Texture, Func<AssetRef, Font?> Font, Texture2D? EngineLogo = null);

/// <summary>
/// Draws the startup screens with the sprite batch and the text renderer: they appear before any scene exists, and
/// need no UI module. Coordinates are framebuffer pixels from the top-left.
/// </summary>
public sealed class StartupCanvas(Renderer2D renderer) : IDisposable
{
    private readonly GlyphAtlas _atlas = new(renderer.Device);
    private readonly Texture2D _white = renderer.Device.CreateTexture(1, 1, [255, 255, 255, 255], SamplerState.PointClamp, "Startup.White");
    private SpriteBatch? _batch;
    private int _width;
    private int _height;

    public GlyphAtlas Atlas => _atlas;

    /// <summary>Starts drawing a screen of the given size on the back buffer.</summary>
    public void Begin(int width, int height)
    {
        (_width, _height) = (width, height);
        renderer.Device.SetRenderTarget(null);
        var camera = new Camera2D(width, height) { Position = new Vector2(width * 0.5f, height * 0.5f) };
        camera.UsePixelUnits();
        _batch = renderer.Begin(camera, SpriteSortMode.Deferred);
    }

    public void End()
    {
        renderer.End();
        _batch = null;
    }

    public void Rect(float x, float y, float width, float height, Color color) => Image(_white, x, y, width, height, color);

    // The batch places quads by their center (pivot 0.5, 0.5); the canvas works with top-left corners.
    public void Image(Texture2D texture, float x, float y, float width, float height, Color color) =>
        Batch.Draw(texture, new Vector2(x + (width * 0.5f), _height - y - (height * 0.5f)), new Vector2(width, height), color);

    /// <summary>Text with its top edge at <paramref name="y"/>, centered on <paramref name="centerX"/>; wraps at <paramref name="maxWidth"/>.</summary>
    /// <returns>The height the text took.</returns>
    public float Text(string text, Font font, float size, float centerX, float y, Color color, float maxWidth = float.PositiveInfinity)
    {
        if (text.Length == 0)
        {
            return 0f;
        }

        var layout = TextLayout.Create(text, new TextStyle(font, size, TextAlign.Center, Wrap: !float.IsPositiveInfinity(maxWidth)), maxWidth);
        Batch.DrawText(_atlas, layout, new Vector2(centerX - (layout.Size.X * 0.5f), _height - y), color);
        return layout.Size.Y;
    }

    /// <summary>Height a text would take.</summary>
    public static float Measure(string text, Font font, float size, float maxWidth = float.PositiveInfinity) =>
        text.Length == 0 ? 0f : TextLayout.Create(text, new TextStyle(font, size, TextAlign.Center, Wrap: !float.IsPositiveInfinity(maxWidth)), maxWidth).Size.Y;

    /// <summary>A progress bar; with no value, a segment sweeps across it (work of unknown size).</summary>
    public void Bar(float x, float y, float width, float height, float? value, float time, Color track, Color fill)
    {
        Rect(x, y, width, height, track);
        if (value is { } v)
        {
            Rect(x, y, width * Math.Clamp(v, 0f, 1f), height, fill);
        }
        else
        {
            var segment = width * 0.3f;
            var start = ((time * 0.8f) % 1.3f - 0.3f) * width;
            var left = Math.Max(x, x + start);
            var right = Math.Min(x + width, x + start + segment);
            if (right > left)
            {
                Rect(left, y, right - left, height, fill);
            }
        }
    }

    public int Width => _width;

    public int Height => _height;

    public void Dispose()
    {
        _atlas.Dispose();
        _white.Dispose();
    }

    private SpriteBatch Batch => _batch ?? throw new InvalidOperationException("Call Begin before drawing.");
}

/// <summary>
/// A loading screen: works before any scene exists. Shows real progress (or an indeterminate bar while the amount of
/// work is unknown), the current task and rotating tips. It appears at full opacity (the game must not show through
/// while loading) and fades out when closed.
/// </summary>
public sealed class LoadingScreenView : IDisposable
{
    private readonly LoadingScreenOptions _options;
    private readonly Font _font;
    private readonly Texture2D? _logo;
    private float? _progress;
    private string _task = "";
    private float _time;
    private float _tipTime;
    private int _tipIndex;
    private float _alpha = 1f;

    public LoadingScreenView(LoadingScreenOptions options, StartupAssets assets)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(assets);
        _options = options;
        _font = (options.Font.IsNone ? null : assets.Font(options.Font)) ?? assets.DefaultFont;
        _logo = options.Logo.IsNone ? null : assets.Texture(options.Logo);
    }

    /// <summary>Current opacity: 1 while open, falling to 0 while closing.</summary>
    public float Opacity => _alpha;

    /// <summary>True while visible or fading.</summary>
    public bool IsVisible => _alpha > 0f || !Closing;

    /// <summary>Set to fade out; the view is done when <see cref="IsVisible"/> turns false.</summary>
    public bool Closing { get; set; }

    /// <param name="progress">0..1, or null when the amount of work is not known yet.</param>
    public void Update(float? progress, string? task, Vector2 windowSize, float framebufferScale, float deltaSeconds)
    {
        var fade = _options.FadeSeconds <= 0f ? 1f : deltaSeconds / _options.FadeSeconds;
        _alpha = Closing ? Math.Max(0f, _alpha - fade) : 1f; // reopened while fading out: opaque again at once
        _progress = progress;
        _time += deltaSeconds;
        var percent = _options.ShowPercentage && progress is { } p ? $"{p * 100f:0}%" : null;
        _task = string.Join("  ·  ", new[] { percent, _options.ShowTask ? task : null }.Where(s => !string.IsNullOrEmpty(s)));
        if (_options.Tips.Count > 0 && (_tipTime += deltaSeconds) >= _options.TipSeconds)
        {
            _tipTime = 0f;
            _tipIndex = (_tipIndex + 1) % _options.Tips.Count;
        }
    }

    public void Render(StartupCanvas canvas, int framebufferWidth, int framebufferHeight)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        Color Fade(Color c) => c with { A = c.A * _alpha };
        var foreground = _options.Foreground;
        var (w, h) = ((float)framebufferWidth, (float)framebufferHeight);
        var tip = _options.Tips.Count > 0 ? _options.Tips[_tipIndex] : "";
        var tipWidth = w * 0.6f;
        var barWidth = Math.Max(240f, w * 0.4f);

        // A centered column: logo, title, bar, task, tip, 14 px apart (24 above the tip).
        const float Gap = 14f;
        var heights = new List<float>();
        if (_logo is not null)
        {
            heights.Add(160f);
        }

        heights.Add(StartupCanvas.Measure(_options.Title, _font, 30f));
        if (_options.ShowProgress)
        {
            heights.Add(10f);
        }

        var showTask = _options.ShowTask || _options.ShowPercentage;
        if (showTask)
        {
            heights.Add(StartupCanvas.Measure(_task.Length > 0 ? _task : " ", _font, 15f));
        }

        var tipHeight = tip.Length > 0 ? StartupCanvas.Measure(tip, _font, 16f, tipWidth) : 0f;
        var total = heights.Sum() + (Gap * (heights.Count - 1)) + (tip.Length > 0 ? 24f + tipHeight : 0f);
        var y = (h - total) * 0.5f;

        canvas.Begin(framebufferWidth, framebufferHeight);
        canvas.Rect(0f, 0f, w, h, Fade(_options.Background));
        if (_logo is not null)
        {
            canvas.Image(_logo, (w - 160f) * 0.5f, y, 160f, 160f, Fade(Color.White));
            y += 160f + Gap;
        }

        y += canvas.Text(_options.Title, _font, 30f, w * 0.5f, y, Fade(foreground)) + Gap;
        if (_options.ShowProgress)
        {
            canvas.Bar((w - barWidth) * 0.5f, y, barWidth, 10f, _progress, _time, Fade(foreground with { A = 0.15f }), Fade(_options.Accent));
            y += 10f + Gap;
        }

        if (showTask)
        {
            y += canvas.Text(_task, _font, 15f, w * 0.5f, y, Fade(foreground with { A = 0.65f })) + Gap;
        }

        if (tip.Length > 0)
        {
            canvas.Text(tip, _font, 16f, w * 0.5f, y - Gap + 24f, Fade(foreground with { A = 0.8f }), tipWidth);
        }

        canvas.End();
    }

    public void Dispose()
    {
        // Textures and fonts belong to the startup assets' owner.
    }
}

/// <summary>
/// The startup splash: background color or image, logo, title, subtitle and optional "Made with MNVTEngine", with a
/// fade-in, a hold of at least the minimum duration (and until the first scene has loaded), and a fade-out. Skippable
/// with a click or key once loading is done, when allowed.
/// </summary>
public sealed class SplashScreenView : IDisposable
{
    private readonly SplashScreenSettings _settings;
    private readonly StartupAssets _assets;
    private readonly Texture2D? _logo;
    private readonly Texture2D? _background;
    private float? _progress;
    private float _alpha;
    private float _time;
    private float _fadeOutTime = -1f;

    public SplashScreenView(SplashScreenSettings settings, StartupAssets assets, AssetRef logo, AssetRef background)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(assets);
        _settings = settings;
        _assets = assets;
        _logo = logo.IsNone ? null : assets.Texture(logo);
        _background = background.IsNone ? null : assets.Texture(background);
    }

    /// <summary>True until the fade-out has finished.</summary>
    public bool IsVisible { get; private set; } = true;

    /// <summary>True once the splash covers the screen (after the fade-in): the scene underneath may switch invisibly.</summary>
    public bool IsOpaque => _time >= _settings.FadeIn && _fadeOutTime < 0f;

    /// <param name="loaded">The first scene is ready: the splash may end after its minimum duration.</param>
    /// <param name="skip">A key or click this frame.</param>
    public void Update(bool loaded, float? progress, bool skip, Vector2 windowSize, float framebufferScale, float deltaSeconds)
    {
        _time += deltaSeconds;
        _progress = progress;
        if (_fadeOutTime < 0f && loaded && (_time >= _settings.MinimumDuration || (skip && _settings.Skippable && _time >= _settings.FadeIn)))
        {
            _fadeOutTime = 0f;
        }

        if (_fadeOutTime >= 0f)
        {
            _fadeOutTime += deltaSeconds;
            _alpha = _settings.FadeOut <= 0f ? 0f : 1f - Math.Clamp(_fadeOutTime / _settings.FadeOut, 0f, 1f);
            IsVisible = _alpha > 0f;
        }
        else
        {
            _alpha = _settings.FadeIn <= 0f ? 1f : Math.Clamp(_time / _settings.FadeIn, 0f, 1f);
        }
    }

    public void Render(StartupCanvas canvas, int framebufferWidth, int framebufferHeight)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        Color Fade(Color c) => c with { A = c.A * _alpha };
        var font = _assets.DefaultFont;
        var foreground = _settings.Foreground;
        var (w, h) = ((float)framebufferWidth, (float)framebufferHeight);
        canvas.Begin(framebufferWidth, framebufferHeight);
        canvas.Rect(0f, 0f, w, h, Fade(_settings.Background));
        if (_background is not null)
        {
            canvas.Image(_background, 0f, 0f, w, h, Fade(Color.White));
        }

        // A centered column: logo, title, subtitle, bar, 12 px apart (20 more above the bar).
        const float Gap = 12f;
        var title = _settings.Title ?? "";
        var subtitle = _settings.Subtitle ?? "";
        var heights = new List<float>();
        if (_logo is not null)
        {
            heights.Add(240f);
        }

        if (title.Length > 0)
        {
            heights.Add(StartupCanvas.Measure(title, font, 44f));
        }

        if (subtitle.Length > 0)
        {
            heights.Add(StartupCanvas.Measure(subtitle, font, 18f));
        }

        if (_settings.ShowProgress)
        {
            heights.Add(26f);
        }

        var y = (h - (heights.Sum() + (Gap * Math.Max(0, heights.Count - 1)))) * 0.5f;
        if (_logo is not null)
        {
            canvas.Image(_logo, (w - 240f) * 0.5f, y, 240f, 240f, Fade(Color.White));
            y += 240f + Gap;
        }

        if (title.Length > 0)
        {
            y += canvas.Text(title, font, 44f, w * 0.5f, y, Fade(foreground)) + Gap;
        }

        if (subtitle.Length > 0)
        {
            y += canvas.Text(subtitle, font, 18f, w * 0.5f, y, Fade(foreground with { A = 0.7f })) + Gap;
        }

        if (_settings.ShowProgress)
        {
            canvas.Bar((w - 260f) * 0.5f, y + 20f, 260f, 6f, _progress, _time, Fade(foreground with { A = 0.15f }), Fade(foreground));
        }

        if (_settings.ShowEngineBranding)
        {
            // "Made with" over the wordmark, 24 px above the bottom, tinted like the rest of the splash's text.
            if (_assets.EngineLogo is { } engineLogo)
            {
                var logoHeight = 140f * engineLogo.Height / engineLogo.Width;
                var top = h - 24f - logoHeight;
                canvas.Image(engineLogo, (w - 140f) * 0.5f, top, 140f, logoHeight, Fade(foreground with { A = 0.6f }));
                canvas.Text("Made with", font, 13f, w * 0.5f, top - 4f - StartupCanvas.Measure("Made with", font, 13f), Fade(foreground with { A = 0.5f }));
            }
            else
            {
                canvas.Text("Made with MNVTEngine", font, 14f, w * 0.5f, h - 24f - StartupCanvas.Measure("Made with MNVTEngine", font, 14f), Fade(foreground with { A = 0.5f }));
            }
        }

        canvas.End();
    }

    public void Dispose()
    {
        // Textures belong to the startup assets' owner.
    }
}
