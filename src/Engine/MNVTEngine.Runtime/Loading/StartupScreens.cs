using MNVTEngine.Graphics;
using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;
using MNVTEngine.UI;
using MNVTEngine.UI.Widgets;
using Image = MNVTEngine.UI.Widgets.Image;

namespace MNVTEngine.Runtime.Loading;

/// <summary>Resolves splash/loading screen assets (textures, fonts) for the views; both may return null when missing.</summary>
public sealed record StartupAssets(Font DefaultFont, Func<AssetRef, Texture2D?> Texture, Func<AssetRef, Font?> Font);

/// <summary>Splash and loading screens take no input: an input state nothing writes to.</summary>
internal static class NoInputSource
{
    public static readonly InputState State = new();
}

/// <summary>
/// A loading screen drawn with the game UI, from a standalone context: it works before any scene exists. Shows real
/// progress (or an indeterminate bar while the amount of work is unknown), the current task and rotating tips, and
/// fades in and out.
/// </summary>
public sealed class LoadingScreenView : IDisposable
{
    private readonly UIContext _ui;
    private readonly LoadingScreenOptions _options;
    private readonly ProgressBar? _bar;
    private readonly Label? _task;
    private readonly Label? _tip;
    private readonly Panel _root;
    private float _tipTime;
    private int _tipIndex;
    private float _alpha;

    public LoadingScreenView(LoadingScreenOptions options, StartupAssets assets)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(assets);
        _options = options;
        _ui = new UIContext(assets.DefaultFont);
        var font = options.Font.IsNone ? null : assets.Font(options.Font);
        _root = _ui.Root.AddChild(new Panel
        {
            Width = UIUnit.Percent(100),
            Height = UIUnit.Percent(100),
            BackgroundColor = options.Background,
            Layout = LayoutMode.Column,
            Justify = Justify.Center,
            AlignItems = Align.Center,
            Gap = 14,
            Opacity = 0f,
        });
        if (!options.Logo.IsNone && assets.Texture(options.Logo) is { } logo)
        {
            _root.AddChild(new Image { Texture = logo, Width = 160, Height = 160 });
        }

        _root.AddChild(new Label { Text = options.Title, FontSize = 30, TextColor = options.Foreground, Font = font });
        if (options.ShowProgress)
        {
            _bar = _root.AddChild(new ProgressBar { Width = UIUnit.Percent(40), MinWidth = 240, Height = 10, ShowText = false, Indeterminate = true });
            _bar.Style.Accent = options.Accent;
            _bar.Style.Background = new Color(options.Foreground.R, options.Foreground.G, options.Foreground.B, 0.15f);
        }

        if (options.ShowTask || options.ShowPercentage)
        {
            _task = _root.AddChild(new Label { TextColor = options.Foreground with { A = 0.65f }, FontSize = 15, Font = font });
        }

        if (options.Tips.Count > 0)
        {
            _tip = _root.AddChild(new Label { Text = options.Tips[0], TextColor = options.Foreground with { A = 0.8f }, Wrap = true, Alignment = TextAlign.Center, Width = UIUnit.Percent(60), Font = font, Margin = new Thickness(0, 24, 0, 0) });
        }
    }

    /// <summary>True while visible or fading.</summary>
    public bool IsVisible => _alpha > 0f || !Closing;

    /// <summary>Set to fade out; the view is done when <see cref="IsVisible"/> turns false.</summary>
    public bool Closing { get; set; }

    /// <param name="progress">0..1, or null when the amount of work is not known yet.</param>
    public void Update(float? progress, string? task, Vector2 windowSize, float framebufferScale, float deltaSeconds)
    {
        var fade = _options.FadeSeconds <= 0f ? 1f : deltaSeconds / _options.FadeSeconds;
        _alpha = Math.Clamp(_alpha + (Closing ? -fade : fade), 0f, 1f);
        _root.Opacity = _alpha;
        if (_bar is not null)
        {
            _bar.Indeterminate = progress is null;
            _bar.Value = progress ?? 0f;
        }

        if (_task is not null)
        {
            var percent = _options.ShowPercentage && progress is { } p ? $"{p * 100f:0}%" : null;
            var text = _options.ShowTask ? task : null;
            _task.Text = string.Join("  ·  ", new[] { percent, text }.Where(s => !string.IsNullOrEmpty(s)));
        }

        if (_tip is not null && (_tipTime += deltaSeconds) >= _options.TipSeconds)
        {
            _tipTime = 0f;
            _tipIndex = (_tipIndex + 1) % _options.Tips.Count;
            _tip.Text = _options.Tips[_tipIndex];
        }

        _ui.Update(NoInputSource.State.ForUI, windowSize, framebufferScale, deltaSeconds);
    }

    public void Render(UIRenderer renderer, int framebufferWidth, int framebufferHeight) => renderer.Render(_ui, framebufferWidth, framebufferHeight);

    public void Dispose() => _ui.Dispose();
}

/// <summary>
/// The startup splash: background color or image, logo, title, subtitle and optional "Made with MNVTEngine", with a
/// fade-in, a hold of at least the minimum duration (and until the first scene has loaded), and a fade-out. Skippable
/// with a click or key once loading is done, when allowed.
/// </summary>
public sealed class SplashScreenView : IDisposable
{
    private readonly UIContext _ui;
    private readonly SplashScreenSettings _settings;
    private readonly Panel _root;
    private readonly ProgressBar? _bar;
    private float _time;
    private float _fadeOutTime = -1f;

    public SplashScreenView(SplashScreenSettings settings, StartupAssets assets, AssetRef logo, AssetRef background)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(assets);
        _settings = settings;
        _ui = new UIContext(assets.DefaultFont);
        _root = _ui.Root.AddChild(new Panel { Width = UIUnit.Percent(100), Height = UIUnit.Percent(100), BackgroundColor = settings.Background, Opacity = 0f });
        if (!background.IsNone && assets.Texture(background) is { } image)
        {
            _root.AddChild(new Image { Texture = image, Width = UIUnit.Percent(100), Height = UIUnit.Percent(100), ScaleMode = ImageScaleMode.Fill });
        }

        var column = _root.AddChild(new Panel
        {
            Width = UIUnit.Percent(100),
            Height = UIUnit.Percent(100),
            Layout = LayoutMode.Column,
            Justify = Justify.Center,
            AlignItems = Align.Center,
            Gap = 12,
        });
        if (!logo.IsNone && assets.Texture(logo) is { } logoTexture)
        {
            column.AddChild(new Image { Texture = logoTexture, Width = 240, Height = 240 });
        }

        if (settings.Title is { Length: > 0 } title)
        {
            column.AddChild(new Label { Text = title, FontSize = 44, TextColor = settings.Foreground });
        }

        if (settings.Subtitle is { Length: > 0 } subtitle)
        {
            column.AddChild(new Label { Text = subtitle, FontSize = 18, TextColor = settings.Foreground with { A = 0.7f } });
        }

        if (settings.ShowProgress)
        {
            _bar = column.AddChild(new ProgressBar { Width = 260, Height = 6, Indeterminate = true, Margin = new Thickness(0, 20, 0, 0) });
        }

        if (settings.ShowEngineBranding)
        {
            _root.AddChild(new Label { Text = "Made with MNVTEngine", FontSize = 14, TextColor = settings.Foreground with { A = 0.5f }, Anchor = new Vector2(0.5f, 1f), Pivot = new Vector2(0.5f, 1f), Top = -24 });
        }
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
        if (_fadeOutTime < 0f && loaded && (_time >= _settings.MinimumDuration || (skip && _settings.Skippable && _time >= _settings.FadeIn)))
        {
            _fadeOutTime = 0f;
        }

        float alpha;
        if (_fadeOutTime >= 0f)
        {
            _fadeOutTime += deltaSeconds;
            alpha = _settings.FadeOut <= 0f ? 0f : 1f - Math.Clamp(_fadeOutTime / _settings.FadeOut, 0f, 1f);
            IsVisible = alpha > 0f;
        }
        else
        {
            alpha = _settings.FadeIn <= 0f ? 1f : Math.Clamp(_time / _settings.FadeIn, 0f, 1f);
        }

        _root.Opacity = alpha;
        if (_bar is not null)
        {
            _bar.Indeterminate = progress is null;
            _bar.Value = progress ?? 0f;
        }

        _ui.Update(NoInputSource.State.ForUI, windowSize, framebufferScale, deltaSeconds);
    }

    public void Render(UIRenderer renderer, int framebufferWidth, int framebufferHeight) => renderer.Render(_ui, framebufferWidth, framebufferHeight);

    public void Dispose() => _ui.Dispose();
}
