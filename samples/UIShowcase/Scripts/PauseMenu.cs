using MNVTEngine;
using MNVTEngine.UI;

namespace UIShowcase;

/// <summary>
/// Binds the pause menu made in the editor's UI Editor (<c>Assets/UI/PauseMenu.mnvtui</c>), which the
/// <see cref="UIDocument"/> on the same object shows. P opens and closes it (Escape brings back the main menu); its
/// buttons raise the actions "resume" and "quit". Edit the layout in the UI Editor (double-click it in the Project
/// panel); behaviour stays here.
/// </summary>
public sealed class PauseMenu : Component
{
    private UIContext? _ui;

    public override void Start()
    {
        _ui = GetComponent<UIDocument>()?.Context;
        if (_ui is null)
        {
            return; // no UI system (edit mode) or no UIDocument
        }

        _ui.Visible = false;
        _ui.OnAction("resume", _ => _ui.Visible = false);
        _ui.OnAction("quit", _ => Application.Quit());
    }

    public override void Update()
    {
        if (_ui is not null && Input.GetKeyDown(Key.P))
        {
            _ui.Visible = !_ui.Visible;
        }
    }
}
