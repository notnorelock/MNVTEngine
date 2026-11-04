using System.Diagnostics;
using ImGuiNET;
using Microsoft.Extensions.Logging;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.DebugUI.Panels;
using MNVTEngine.Diagnostics;
using MNVTEngine.Graphics;
using MNVTEngine.Runtime;
using Debug = MNVTEngine.Diagnostics.Debug;

namespace MNVTEngine.DebugUI;

/// <summary>
/// The in-game debug interface: a HUD over the game plus dockable debug windows (profiler, console, budgets, renderer,
/// ECS, memory, scripting, physics, assets) and debug-draw rendering. It owns a Dear ImGui context of its own, separate
/// from the editor's: every entry point makes that context current and restores the previous one.
/// <list type="bullet">
/// <item>Shortcuts (defaults): F1 HUD, F2 profiler, F3 console, F4 debug drawing, F5 warnings, F6 all windows. Shortcut
/// keys are hidden from gameplay until released.</item>
/// <item>While an interactive window wants the keyboard or mouse (typing in the console, dragging a window), gameplay
/// sees no keyboard/mouse input. With only the HUD visible, gameplay input is untouched.</item>
/// </list>
/// </summary>
public sealed partial class DebugOverlay : IRuntimeOverlay
{
    private readonly List<DebugPanel> _panels = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ILogger _logger;
    private readonly (Func<DiagnosticsKeyBindings, string> Binding, Action Toggle)[] _shortcuts;
    private RuntimeOverlayContext _context = null!;
    private ImGuiRenderer _renderer = null!;
    private DebugDrawRenderer _draw = null!;
    private Key[] _keys = [];
    private double _lastTime;
    private bool _frameOpen;
    private bool _loaded;
    private string _appliedTheme = "";

    public DebugOverlay(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shortcuts =
        [
            (k => k.ToggleOverlay, () => HudVisible = !HudVisible),
            (k => k.ToggleProfiler, () => Toggle<ProfilerPanel>()),
            (k => k.ToggleConsole, () => Toggle<ConsolePanel>()),
            (k => k.ToggleDebugDrawing, () => Debug.DrawingEnabled = !Debug.DrawingEnabled),
            (k => k.ToggleWarnings, () => WarningsVisible = !WarningsVisible),
            (k => k.ToggleWindows, () => Get<WindowsPanel>()!.Open = !Get<WindowsPanel>()!.Open),
        ];
    }

    public RuntimeDiagnostics Diagnostics => _context.Diagnostics;

    public RuntimeOverlayContext Context => _context;

    public bool HudVisible { get; set; }

    public bool WarningsVisible { get; set; } = true;

    /// <summary>False hides and disables the whole interface (debug draws still render) without unloading it.</summary>
    public bool Enabled { get; set; } = true;

    public IReadOnlyList<DebugPanel> Panels => _panels;

    /// <summary>True while a window that takes input is open (the HUD never takes input).</summary>
    public bool IsInteractive => _panels.Any(p => p.Open && p.Interactive);

    public T? Get<T>()
        where T : DebugPanel => _panels.OfType<T>().FirstOrDefault();

    /// <summary>Adds a custom debug window (listed in the Windows panel).</summary>
    public void AddPanel(DebugPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (_panels.Any(p => p.Name == panel.Name))
        {
            throw new ArgumentException($"A debug panel named '{panel.Name}' already exists.", nameof(panel));
        }

        _panels.Add(panel);
    }

    public void Load(RuntimeOverlayContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var settings = context.Diagnostics.Settings;
        var display = context.Display();
        var previous = ImGui.GetCurrentContext();
        _renderer = new ImGuiRenderer(context.Device, context.Renderer.DefaultMaterial, context.FontPath, 15f, display.FramebufferScale);
        ImGui.SetCurrentContext(_renderer.Context);
        try
        {
            var io = ImGui.GetIO();
            io.ConfigFlags &= ~ImGuiConfigFlags.NavEnableKeyboard; // a focused debug window must not swallow gameplay keys
            if (context.LayoutFile is { } layout && File.Exists(layout))
            {
                ImGui.LoadIniSettingsFromDisk(layout);
            }

            _draw = new DebugDrawRenderer();
            HudVisible = settings.ShowOverlayByDefault;
            foreach (var panel in (DebugPanel[])[new WindowsPanel(), new ProfilerPanel(), new ConsolePanel(), new BudgetsPanel(), new RendererPanel(), new EcsPanel(), new MemoryPanel(), new ScriptingPanel(), new PhysicsPanel(), new AssetsPanel()])
            {
                if (!settings.DisabledPanels.Contains(panel.Name, StringComparer.OrdinalIgnoreCase))
                {
                    _panels.Add(panel);
                }
            }

            foreach (var panel in _panels)
            {
                panel.Attach(this);
            }

            _keys = [.. _shortcuts.Select(s => ParseKey(s.Binding(settings.Keys)))];
            RegisterCommands();
            _loaded = true;
        }
        finally
        {
            ImGui.SetCurrentContext(previous);
        }
    }

    public void BeginFrame()
    {
        if (!_loaded || _frameOpen)
        {
            return;
        }

        var previous = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(_renderer.Context);
        try
        {
            var input = _context.Input;
            input.ReleaseCapture();
            var settings = _context.Diagnostics.Settings;
            if (settings.ShortcutsEnabled && Enabled)
            {
                for (var i = 0; i < _keys.Length; i++)
                {
                    if (_keys[i] != Key.Unknown && input.WasKeyPressed(_keys[i]))
                    {
                        input.Suppress(_keys[i]);
                        _shortcuts[i].Toggle();
                    }
                }
            }

            ApplyTheme(settings.Overlay);
            var display = _context.Display();
            var now = _clock.Elapsed.TotalSeconds;
            ImGuiInput.NewFrame(input, display.Width, display.Height, display.FramebufferScale, (float)(now - _lastTime));
            _lastTime = now;
            ImGui.NewFrame();
            _frameOpen = true;

            if (Enabled)
            {
                if (HudVisible)
                {
                    HudPanel.Draw(this);
                }

                foreach (var panel in _panels)
                {
                    if (panel.Open)
                    {
                        panel.DrawWindow();
                    }
                }
            }

            Get<PhysicsPanel>()?.DrawColliders();

            var io = ImGui.GetIO();
            var interactive = Enabled && IsInteractive;
            input.Capture(interactive && io.WantCaptureKeyboard, interactive && io.WantCaptureMouse);
        }
        finally
        {
            ImGui.SetCurrentContext(previous);
        }
    }

    public void Render(Camera2D? camera, RenderTarget2D? target)
    {
        if (!_loaded)
        {
            return;
        }

        BeginFrame(); // a frame without updates (e.g. paused) still draws the UI
        var previous = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(_renderer.Context);
        try
        {
            var display = _context.Display();
            if (Debug.DrawingEnabled)
            {
                _draw.Render(ImGui.GetBackgroundDrawList(), _context.Diagnostics.DrawBuffer.Commands, camera, display);
            }

            ImGui.Render();
            _frameOpen = false;
            _renderer.Render(ImGui.GetDrawData(), (int)(display.Width * display.FramebufferScale), (int)(display.Height * display.FramebufferScale), target);
            var io = ImGui.GetIO();
            if (io.WantSaveIniSettings && _context.LayoutFile is { } layout)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(layout))!);
                ImGui.SaveIniSettingsToDisk(layout);
                io.WantSaveIniSettings = false;
            }
        }
        finally
        {
            ImGui.SetCurrentContext(previous);
        }
    }

    public void Dispose()
    {
        if (!_loaded)
        {
            return;
        }

        _loaded = false;
        _context.Input.ReleaseCapture();
        foreach (var command in OverlayCommands)
        {
            _context.Diagnostics.Commands.Unregister(command);
        }

        foreach (var panel in _panels.OfType<IDisposable>())
        {
            panel.Dispose();
        }

        var previous = ImGui.GetCurrentContext();
        var own = _renderer.Context;
        _renderer.Dispose(); // destroys the context
        if (previous != own)
        {
            ImGui.SetCurrentContext(previous);
        }
    }

    internal void Toggle<T>()
        where T : DebugPanel
    {
        if (Get<T>() is { } panel)
        {
            panel.Open = !panel.Open;
        }
    }

    private static readonly string[] OverlayCommands = ["overlay", "window", "clear"];

    private void RegisterCommands()
    {
        var commands = _context.Diagnostics.Commands;
        foreach (var name in OverlayCommands)
        {
            commands.Unregister(name);
        }

        commands
            .Register("overlay", "Shows or hides the HUD.", "[on|off]", args => Result(HudVisible = args.Count == 0 ? !HudVisible : args[0] == "on", "HUD"))
            .Register("window", "Opens or closes a debug window: " + string.Join(", ", _panels.Select(p => p.Name.ToLowerInvariant())), "<name> [on|off]", args =>
            {
                if (args.Count == 0 || _panels.FirstOrDefault(p => p.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase)) is not { } panel)
                {
                    return DebugCommandResult.Fail("Usage: window <" + string.Join("|", _panels.Select(p => p.Name.ToLowerInvariant())) + "> [on|off]");
                }

                panel.Open = args.Count < 2 ? !panel.Open : args[1] == "on";
                return Result(panel.Open, panel.Name);
            })
            .Register("clear", "Clears the console.", _ =>
            {
                Get<ConsolePanel>()?.Clear();
                return DebugCommandResult.Ok();
            });
    }

    private static DebugCommandResult Result(bool on, string what) => DebugCommandResult.Ok($"{what} {(on ? "on" : "off")}.");

    private Key ParseKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Key.Unknown;
        }

        if (Enum.TryParse<Key>(name, ignoreCase: true, out var key))
        {
            return key;
        }

        LogBadKey(_logger, name);
        return Key.Unknown;
    }

    private void ApplyTheme(OverlaySettings overlay)
    {
        if (_appliedTheme == overlay.Theme)
        {
            return;
        }

        _appliedTheme = overlay.Theme;
        switch (overlay.Theme.ToLowerInvariant())
        {
            case "light":
                ImGui.StyleColorsLight();
                break;
            case "classic":
                ImGui.StyleColorsClassic();
                break;
            default:
                Gui.ApplyDarkTheme();
                break;
        }

        var style = ImGui.GetStyle();
        style.WindowRounding = 4f;
        var background = style.Colors[(int)ImGuiCol.WindowBg];
        style.Colors[(int)ImGuiCol.WindowBg] = background with { W = 0.96f }; // the game shows through a little; windows don't
    }

    [LoggerMessage(EventId = 9100, Level = LogLevel.Warning, Message = "Debug shortcut key '{Key}' is not a key name; that shortcut is off")]
    private static partial void LogBadKey(ILogger logger, string key);
}
