using MNVTEngine;
using MNVTEngine.Scripting.Missions;
using MNVTEngine.Serialization;
using MNVTEngine.UI;
using MNVTEngine.UI.Widgets;

namespace Platformer;

/// <summary>
/// The HUD made in the UI Editor (<c>Assets/UI/Hud.mnvtui</c>, shown by the <see cref="UIDocument"/> on this object):
/// collected coins, the mission's current objective, and a "Level complete" card that stops the game. Its buttons raise
/// the actions "restart" and "quit". The look is edited in the UI Editor; behaviour stays here.
/// </summary>
public sealed class Hud : Component
{
    /// <summary>The scene that "restart" loads.</summary>
    [AssetType(AssetTypes.Scene)]
    public AssetRef Level;

    private Label? _coins;
    private Label? _objective;
    private Label? _time;
    private UIElement? _complete;
    private MissionRunner? _mission;
    private int _total;
    private float _elapsed;

    public bool IsComplete { get; private set; }

    public override void Start()
    {
        if (GetComponent<UIDocument>() is not { Context: { } ui, Root: { } root })
        {
            return; // no UI system (edit mode) or the layout did not load
        }

        _coins = root.Find<Label>("Coins");
        _objective = root.Find<Label>("Objective");
        _time = root.Find<Label>("CompleteTime");
        _complete = root.Find("Complete");
        _total = GameObject.Scene.FindObjectsOfType<Coin>().Count();
        _mission = GameObject.Scene.FindObjectsOfType<MissionRunner>().FirstOrDefault();
        ui.OnAction("restart", _ => Restart());
        ui.OnAction("quit", _ => Application.Quit());
    }

    public override void Update()
    {
        if (_coins is null || IsComplete)
        {
            return;
        }

        _elapsed += Time.DeltaTime;
        var collected = _total - GameObject.Scene.FindObjectsOfType<Coin>().Count(c => !c.GameObject.IsDestroyed);
        _coins.Text = $"Coins {collected} / {_total}";
        if (_mission?.Missions?.Missions.Values.FirstOrDefault() is not { } mission)
        {
            return;
        }

        if (mission.State == MissionState.Completed)
        {
            IsComplete = true;
            _objective?.Text = "Done!";
            _time?.Text = $"All {_total} coins in {_elapsed:0.0} s";
            _complete?.Visible = true;
            Time.TimeScale = 0f; // the game stops behind the card; a new scene starts at 1 again
        }
        else if (_objective is not null)
        {
            _objective.Text = mission.Objectives.Values.FirstOrDefault(o => o.State == ObjectiveState.Active)?.Info.Description ?? "";
        }
    }

    public void Restart()
    {
        if (Level.IsNone)
        {
            Log.Warn("Hud has no Level to restart.");
            return;
        }

        SceneManager.LoadScene(Level);
    }
}

/// <summary>
/// The pause menu made in the UI Editor (<c>Assets/UI/Pause.mnvtui</c>, shown by the <see cref="UIDocument"/> on this
/// object). Escape opens and closes it; while open, <see cref="Time.TimeScale"/> is 0 so the game and its physics stop.
/// </summary>
public sealed class PauseMenu : Component
{
    private UIContext? _ui;
    private Hud? _hud;

    public override void Start()
    {
        _ui = GetComponent<UIDocument>()?.Context;
        if (_ui is null)
        {
            return;
        }

        _hud = GameObject.Scene.FindObjectsOfType<Hud>().FirstOrDefault();
        _ui.OnAction("resume", _ => Show(false));
        _ui.OnAction("restart", _ => _hud?.Restart());
        _ui.OnAction("quit", _ => Application.Quit());
        Show(false);
    }

    public override void Update()
    {
        if (_ui is not null && Input.GetKeyDown(Key.Escape) && _hud?.IsComplete != true)
        {
            Show(!_ui.Visible);
        }
    }

    private void Show(bool open)
    {
        _ui!.Visible = open;
        Time.TimeScale = open ? 0f : 1f;
    }
}
