using ImGuiNET;
using MNVTEngine.Editor.ImGuiBackend;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.Panels;

/// <summary>
/// The active scene through its main camera, as the player would see it. While playing, keyboard and mouse are
/// forwarded to the game only when this panel has focus; mouse coordinates are relative to the view.
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

    /// <summary>Copies window input into the game's input state. Call before ticking Play Mode.</summary>
    public void FeedInput()
    {
        var game = _context.PlayMode.Input;
        game.BeginFrame();
        if (!_context.PlayMode.IsPlaying || !_focused)
        {
            game.ReleaseAll();
            return;
        }

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
        _context.SceneRenderer.Render(_context.ActiveScene, _view.Target.Width, _view.Target.Height, _view.Target);
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

    public void Dispose() => _view.Dispose();
}
