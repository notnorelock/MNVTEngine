using MNVTEngine.Mathematics;

namespace MNVTEngine;

/// <summary>What the loading screen shows. Defaults come from the project's <c>loadingScreen</c> settings.</summary>
public sealed class LoadingScreenOptions
{
    public string Title { get; set; } = "Loading...";

    public Color Background { get; set; } = new(0.04f, 0.04f, 0.05f, 1f);

    public Color Foreground { get; set; } = new(0.92f, 0.93f, 0.95f, 1f);

    public Color Accent { get; set; } = new(0.27f, 0.55f, 1f, 1f);

    /// <summary>Texture asset shown above the title; none for no logo.</summary>
    public AssetRef Logo { get; set; }

    /// <summary>Font asset (TTF/OTF) for the texts; none for the built-in font.</summary>
    public AssetRef Font { get; set; }

    /// <summary>Shows a bar with the real progress (or an indeterminate bar while the amount of work is unknown).</summary>
    public bool ShowProgress { get; set; } = true;

    public bool ShowPercentage { get; set; } = true;

    /// <summary>Shows the current step ("Decoding Textures/hero.png").</summary>
    public bool ShowTask { get; set; } = true;

    /// <summary>Rotated every <see cref="TipSeconds"/>.</summary>
    public IReadOnlyList<string> Tips { get; set; } = [];

    public float TipSeconds { get; set; } = 5f;

    /// <summary>Fade in and out, in seconds.</summary>
    public float FadeSeconds { get; set; } = 0.25f;

    public LoadingScreenOptions Clone() => (LoadingScreenOptions)MemberwiseClone();
}

/// <summary>A loading screen opened by game code; report progress, then dispose to hide it.</summary>
public interface ILoadingScreenHandle : IDisposable
{
    /// <summary>Fraction done, 0..1, or null for "unknown" (an indeterminate bar, never a made-up number).</summary>
    void Report(float? progress, string? task = null);
}

/// <summary>Shows loading screens: automatically while scenes load, or manually around the game's own work.</summary>
public interface ILoadingScreenHost
{
    ILoadingScreenHandle Show(LoadingScreenOptions options);
}

public static class LoadingScreen
{
    private static ILoadingScreenHost? _host;

    /// <summary>Used for scene loads (<see cref="SceneManager.LoadScene(string)"/>); null hides scene loads (the old scene stays visible).</summary>
    public static LoadingScreenOptions? Options { get; set; }

    /// <summary>
    /// Shows a loading screen over the game until the handle is disposed, e.g. while the game generates a level on a
    /// worker thread. The window keeps rendering it; report progress from the game thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">No game host (tests, tools).</exception>
    public static ILoadingScreenHandle Show(LoadingScreenOptions? options = null) =>
        (_host ?? throw new InvalidOperationException("There is no game host to show a loading screen."))
            .Show(options ?? Options ?? new LoadingScreenOptions());

    /// <summary>Set by the game host.</summary>
    public static void SetHost(ILoadingScreenHost? host) => _host = host;
}
