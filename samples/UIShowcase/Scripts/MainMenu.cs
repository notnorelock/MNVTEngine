using MNVTEngine;
using MNVTEngine.Graphics.Text;
using MNVTEngine.Mathematics;
using MNVTEngine.UI;
using MNVTEngine.UI.Widgets;

namespace UIShowcase;

/// <summary>
/// Builds the showcase UI from code: a menu with buttons, a settings card (slider, dropdown, checkbox, toggle, radio
/// buttons), text input (single and multi-line), a scrollable list, a timer-driven progress bar, and fonts loaded from
/// assets (an OTF and a bold TTF) without blocking. Clicking "Play" hides the menu so WASD moves the player; while a
/// text field is focused, typing does not move the player.
/// </summary>
public sealed class MainMenu : Component
{
    /// <summary>Title font (TTF asset).</summary>
    public AssetRef TitleFont;

    /// <summary>Body font (OTF asset).</summary>
    public AssetRef BodyFont;

    private UIContext? _ui;
    private Panel _menu = null!;
    private Label _status = null!;
    private ProgressBar _timer = null!;
    private float _timerElapsed = -1f;

    public override void Start()
    {
        _ui = UI.CreateContext();
        _menu = _ui.Root.AddChild(new Panel
        {
            Name = "Menu",
            StyleClass = "panel",
            Width = 560,
            Height = UIUnit.Percent(92),
            Anchor = new Vector2(0.5f, 0.5f),
            Pivot = new Vector2(0.5f, 0.5f),
            Layout = LayoutMode.Column,
            Gap = 10,
        });

        var title = _menu.AddChild(new Label { Text = "MNVTEngine UI", FontSize = 40, Alignment = TextAlign.Center });
        _menu.AddChild(new Label { Text = "Zażółć gęślą jaźń — Unicode, kerning, wrapping", Alignment = TextAlign.Center, TextColor = new Color(0.7f, 0.75f, 0.85f) });

        var buttons = _menu.AddChild(new Panel { Layout = LayoutMode.Row, Gap = 8, Justify = Justify.Center });
        var play = buttons.AddChild(new Button { Text = "Play", StyleClass = "primary", Width = 140, Height = 40, Tooltip = "Hide the menu and move with WASD" });
        var reset = buttons.AddChild(new Button { Text = "Start timer", Width = 140, Height = 40 });
        var quit = buttons.AddChild(new Button { Text = "Quit", Width = 100, Height = 40 });
        quit.Clicked += Application.Quit;
        play.Clicked += () =>
        {
            _menu.Visible = false;
            Log.Info("Play: menu hidden (press Escape to show it again)");
        };
        reset.Clicked += () => _timerElapsed = 0f;

        var settings = _menu.AddChild(new Panel { Layout = LayoutMode.Grid, GridColumns = 2, Gap = 8 });
        settings.AddChild(new Label { Text = "Volume" });
        var volume = settings.AddChild(new Slider { Minimum = 0, Maximum = 100, Step = 5, Value = 80 });
        settings.AddChild(new Label { Text = "Quality" });
        var quality = settings.AddChild(new Dropdown());
        quality.Options.AddRange(["Low", "Medium", "High", "Ultra"]);
        quality.SelectedIndex = 2;
        var music = settings.AddChild(new Checkbox { Text = "Music", Checked = true });
        var vsync = settings.AddChild(new Toggle { Text = "VSync", Checked = !Application.HasWindow || Application.Window.VSync });
        var fullscreen = settings.AddChild(new Toggle { Text = "Borderless fullscreen" });
        settings.AddChild(new Label { Text = "" });
        var easy = settings.AddChild(new RadioButton { Text = "Easy", Group = "difficulty", Checked = true });
        var hard = settings.AddChild(new RadioButton { Text = "Hard", Group = "difficulty" });

        var name = _menu.AddChild(new TextField { Placeholder = "Your name (max 20)", MaxLength = 20, Width = UIUnit.Percent(100) });
        var notes = _menu.AddChild(new TextArea { Placeholder = "Notes: multi-line, word wrap, Ctrl+A/C/V/X", Width = UIUnit.Percent(100), Height = 90 });

        var list = _menu.AddChild(new ScrollView { Width = UIUnit.Percent(100), FlexGrow = 1, MinHeight = 60 });
        for (var i = 1; i <= 30; i++)
        {
            list.Content.AddChild(new Label { Text = $"Saved game {i:00} — level {(i % 5) + 1}", Margin = new Thickness(0, 2, 0, 2) });
        }

        _timer = _menu.AddChild(new ProgressBar { Width = UIUnit.Percent(100), Height = 14, ShowText = true, Maximum = 5f });
        _status = _menu.AddChild(new Label { Text = "Ready.", TextColor = new Color(0.6f, 0.9f, 0.6f) });

        volume.ValueChanged += v => _status.Text = $"Volume {v:0}";
        quality.SelectionChanged += i => _status.Text = $"Quality: {quality.SelectedOption}";
        music.CheckedChanged += on => _status.Text = on ? "Music on" : "Music off";
        vsync.CheckedChanged += on =>
        {
            Application.Window.VSync = on; // the swapchain is recreated with FIFO or MAILBOX/IMMEDIATE
            _status.Text = on ? "VSync on" : "VSync off";
        };
        fullscreen.CheckedChanged += on => Application.Window.Mode = on ? WindowMode.BorderlessFullscreen : WindowMode.Windowed;
        easy.CheckedChanged += on => { if (on) { _status.Text = "Difficulty: easy"; } };
        hard.CheckedChanged += on => { if (on) { _status.Text = "Difficulty: hard"; } };
        name.TextChanged += text =>
        {
            _status.Text = text.Length == 0 ? "Ready." : $"Hello, {text}!";
            Application.Window.Title = text.Length == 0 ? "UIShowcase" : $"UIShowcase - {text}"; // runtime title change
        };
        name.Submitted += text => Log.Info($"Name submitted: {text}");
        notes.TextChanged += text => _status.Text = $"Notes: {text.Length} characters";

        LoadFonts(title, notes);
    }

    public override void Update()
    {
        if (_ui is null)
        {
            return;
        }

        if (!_menu.Visible && Input.GetKeyDown(Key.Escape))
        {
            _menu.Visible = true;
        }

        if (_timerElapsed >= 0f)
        {
            _timerElapsed = Math.Min(_timer.Maximum, _timerElapsed + Time.UnscaledDeltaTime);
            _timer.Value = _timerElapsed;
            if (_timerElapsed >= _timer.Maximum)
            {
                _status.Text = "Timer done (5 s of real time).";
                _timerElapsed = -1f;
            }
        }
    }

    public override void OnDestroy() => _ui?.Dispose();

    private void LoadFonts(Label title, TextArea notes)
    {
        // Read and parsed on a worker; the callbacks run on the game thread, where changing elements is safe.
        if (!TitleFont.IsNone)
        {
            UI.LoadFont(TitleFont, bold => title.Font = bold);
        }

        if (!BodyFont.IsNone)
        {
            UI.LoadFont(BodyFont, body =>
            {
                notes.Font = body;
                _status.Text = $"Notes font loaded asynchronously: {body.FamilyName} {body.StyleName} ({body.Format})";
            });
        }
    }
}