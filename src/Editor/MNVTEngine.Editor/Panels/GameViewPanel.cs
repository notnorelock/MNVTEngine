using ImGuiNET;
using Microsoft.Extensions.Logging;
using MNVTEngine.DebugUI;
using MNVTEngine.DebugUI.ImGuiBackend;
using MNVTEngine.Mathematics;
using MNVTEngine.Runtime;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// The active scene through its main camera, as the player would see it. While playing, keyboard and mouse are
/// forwarded to the game only when this panel has focus; mouse coordinates are relative to the view.
/// In Play Mode the game's debug overlay (its own ImGui context) draws into the view: F1-F6 while the view has focus.
/// </summary>
internal sealed class GameViewPanel : IDisposable
{
    private readonly EditorContext _context;
    private readonly ViewTarget _view;
    private Vector2 _origin;
    private bool _focused;
    private bool _hovered;
    private bool _visible;
    private int _focusFrames;
    private DebugOverlay? _overlay;

    public GameViewPanel(EditorContext context)
    {
        _context = context;
        _view = new ViewTarget(context, "GameView");
    }

    public void Draw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, System.Numerics.Vector2.Zero);
        var visible = ImGui.Begin("Game");
        ImGui.PopStyleVar();
        _visible = visible;
        _focused = visible && ImGui.IsWindowFocused();
        _context.CenterDockId = ImGui.GetWindowDockID();
        if (visible)
        {
            var size = ImGui.GetContentRegionAvail();
            _view.Request((int)size.X, (int)size.Y);
            _origin = ImGui.GetCursorScreenPos().ToEngine();
            Gui.Image(_view.Id, _view.Target, size);
            _hovered = ImGui.IsItemHovered();
            if (_context.PlayMode.IsPlaying && !_focused)
            {
                ImGui.SetCursorScreenPos(_origin.ToSys() + new System.Numerics.Vector2(8f, 8f));
                Gui.Muted("Click to give the game keyboard focus");
            }
        }

        ImGui.End();
    }

    /// <summary>Copies window input into the game's input state, then lets the debug overlay take its share. Call before ticking Play Mode.</summary>
    public void FeedInput()
    {
        var game = _context.PlayMode.Input;
        game.BeginFrame();
        _context.PlayMode.ViewportSize = new Vector2(_view.Target.Width, _view.Target.Height);
        UpdateOverlay();
        if (!_context.PlayMode.IsPlaying || !_focused)
        {
            game.ReleaseAll();
            _overlay?.BeginFrame();
            return;
        }

        CopyInput(game);
        _overlay?.BeginFrame();
    }

    /// <summary>The overlay lives exactly as long as a play session with diagnostics.</summary>
    private void UpdateOverlay()
    {
        var diagnostics = _context.PlayMode.Diagnostics;
        if (_overlay is not null && (diagnostics is null || !ReferenceEquals(_overlay.Diagnostics, diagnostics)))
        {
            _overlay.Dispose();
            _overlay = null;
        }

        if (_overlay is null && diagnostics is not null)
        {
            _overlay = new DebugOverlay(_context.Logger);
            _overlay.Load(new RuntimeOverlayContext(
                _context.Device, _context.Renderer, diagnostics, _context.PlayMode.Input,
                () => new OverlayDisplay(Math.Max(1, _view.Target.Width), Math.Max(1, _view.Target.Height), 1f),
                () => _context.PlayMode.PlayScene,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf"),
                _context.Logs,
                Path.Combine(_context.Session.Project.RootDirectory, "Library", "Editor", "game-debug-ui.ini")));
        }
    }

    private void CopyInput(InputState game)
    {
        var window = _context.Window.Input;
        foreach (var key in Enum.GetValues<Key>())
        {
            game.SetKey(key, window.IsKeyDown(key));
        }

        foreach (var button in Enum.GetValues<MouseButton>())
        {
            game.SetMouseButton(button, window.IsMouseButtonDown(button));
        }

        game.SetMousePosition(window.MousePosition - _origin);
        if (_hovered)
        {
            game.AddScroll(window.ScrollDelta);
        }

        foreach (var c in window.TextInput)
        {
            game.AddText(c);
        }
    }

    public void Render()
    {
        if (!_visible)
        {
            return; // tab hidden behind another panel
        }

        _view.EnsureSize();
        var diagnostics = _context.PlayMode.Diagnostics;
        var gpu = diagnostics?.GpuTimer;
        diagnostics?.SceneRendering();
        using (new Graphics.GpuScope(gpu, "Scene"))
        {
            _context.SceneRenderer.Render(_context.ActiveScene, _view.Target.Width, _view.Target.Height, _view.Target);
        }

        if (_context.PlayMode.PlayScene is { } playing && UI.UISystem.For(playing) is { } ui)
        {
            using var sample = Core.Diagnostics.Profiler.Sample("UI");
            _context.Device.SetRenderTarget(_view.Target);
            ui.Render(_context.GameUI, _view.Target.Width, _view.Target.Height);
        }

        diagnostics?.SceneRendered();
        if (_overlay is not null)
        {
            using var sample = Core.Diagnostics.Profiler.Sample("Debug Overlay");
            using var gpuScope = new Graphics.GpuScope(gpu, "Debug Overlay");
            var camera = _context.ActiveScene.MainCamera is { } main ? SceneRenderer.CreateView(main, _view.Target.Width, _view.Target.Height) : null;
            _overlay.Render(camera, _view.Target);
        }
    }

    /// <summary>Ends the play session's diagnostics frame (after the Game view rendered, even when it is hidden).</summary>
    public void EndDiagnosticsFrame()
    {
        if (_context.PlayMode is { Diagnostics: { } diagnostics, AdvancedThisFrame: true } play)
        {
            diagnostics.EndFrame(play.Time);
        }
    }

    /// <summary>Brings the Game tab to front on its next draw (works even before the window first exists).</summary>
    public void Focus() => _focusFrames = 5;

    /// <summary>
    /// Applies a pending <see cref="Focus"/>. Call after all panels are drawn: focusing by name selects the docked tab.
    /// Repeated for a few frames because on startup new windows take focus as they appear and docking settles later.
    /// </summary>
    public void ApplyPendingFocus()
    {
        if (_focusFrames > 0)
        {
            ImGui.SetWindowFocus("Game");
            _focusFrames--;
        }
    }

    public void Dispose()
    {
        _overlay?.Dispose();
        _view.Dispose();
    }
}
