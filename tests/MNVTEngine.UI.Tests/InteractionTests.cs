using MNVTEngine.Mathematics;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.UI.Tests;

public sealed class InteractionTests
{
    [Fact]
    public void Clicks_Dispatch_Bubble_AndOnlyCountWhenReleasedOverTheElement()
    {
        using var ui = new UIHarness();
        var panel = ui.Context.Root.AddChild(new Panel { Width = 300, Height = 200, BackgroundColor = Color.Black });
        var button = panel.AddChild(new Button { Text = "Play", Width = 120, Height = 40 });
        var clicks = 0;
        var bubbled = 0;
        button.Clicked += () => clicks++;
        panel.PointerPressed += (_, e) => bubbled += e.Target == panel ? 0 : 1;
        string? action = null;
        button.Action = "play";
        ui.Context.ActionInvoked += (_, name) => action = name;
        ui.Frame();

        ui.Click(button);
        Assert.Equal(1, clicks);
        Assert.Equal("play", action);
        Assert.Equal(1, bubbled); // pointer-down bubbles from the button to the panel (only clicks are handled by the button)
        Assert.Same(button, ui.Context.Focused);

        // Press on the button, release outside: no click.
        ui.Input.SetMousePosition(button.Bounds.Center);
        ui.Input.SetMouseButton(MouseButton.Left, true);
        ui.Frame();
        Assert.Same(button, ui.Context.Pressed);
        ui.Input.SetMousePosition(new Vector2(500, 500));
        ui.Input.SetMouseButton(MouseButton.Left, false);
        ui.Frame();
        Assert.Equal(1, clicks);
        Assert.Null(ui.Context.Pressed);

        button.Enabled = false;
        ui.Click(button);
        Assert.Equal(1, clicks);
        Assert.Equal(UIState.Disabled, button.State & UIState.Disabled);
    }

    [Fact]
    public void HoverAndCapture_HideInputFromGameplay_OnlyWhileTheUIUsesIt()
    {
        using var ui = new UIHarness();
        var button = ui.Context.Root.AddChild(new Button { Text = "B", Width = 100, Height = 40 });
        var field = ui.Context.Root.AddChild(new TextField { Top = 100, Width = 200 });
        ui.Frame();
        ui.Move(button.Bounds.Center);
        Assert.Same(button, ui.Context.Hovered);
        Assert.True(ui.Context.WantsMouse);
        Assert.Equal(UIState.Hover, button.State & UIState.Hover);
        ui.Move(new Vector2(700, 500));
        Assert.Null(ui.Context.Hovered);
        Assert.False(ui.Context.WantsMouse);

        ui.Click(field);
        Assert.True(ui.Context.WantsKeyboard);
        ui.Press(Key.Escape);
        Assert.Null(ui.Context.Focused);
        Assert.False(ui.Context.WantsKeyboard);
    }

    [Fact]
    public void Tab_CyclesFocusInTabOrder_AndEnterActivates()
    {
        using var ui = new UIHarness();
        var column = ui.Context.Root.AddChild(new Panel { Layout = LayoutMode.Column, Width = 200 });
        var first = column.AddChild(new Button { Text = "1", TabIndex = 1 });
        var second = column.AddChild(new Button { Text = "2", TabIndex = 0 });
        var check = column.AddChild(new Checkbox { Text = "Music", TabIndex = 2 });
        var hidden = column.AddChild(new Button { Text = "x", Visible = false });
        ui.Frame();

        ui.Press(Key.Tab);
        Assert.Same(second, ui.Context.Focused);
        ui.Press(Key.Tab);
        Assert.Same(first, ui.Context.Focused);
        ui.Press(Key.Tab);
        Assert.Same(check, ui.Context.Focused);
        ui.Press(Key.Space);
        Assert.True(check.Checked);
        ui.Press(Key.Tab);
        Assert.Same(second, ui.Context.Focused); // wraps, skipping the hidden button
        ui.Press(Key.Tab, shift: true);
        Assert.Same(check, ui.Context.Focused);
        Assert.NotSame(hidden, ui.Context.Focused);
    }

    [Fact]
    public void TextField_UndoesAndRedoesByWord_AndShiftClickSelects()
    {
        using var ui = new UIHarness();
        var field = ui.Context.Root.AddChild(new TextField { Width = 300 });
        ui.Frame();
        ui.Click(field);
        ui.Type("hello world");

        ui.Press(Key.Z, control: true);
        Assert.Equal("hello", field.Text); // " world" was one step
        ui.Press(Key.Z, control: true);
        Assert.Equal("", field.Text);
        Assert.False(field.Editor.CanUndo);
        ui.Press(Key.Y, control: true);
        Assert.Equal("hello", field.Text);
        ui.Press(Key.Z, control: true, shift: true); // Ctrl+Shift+Z redoes too
        Assert.Equal("hello world", field.Text);

        ui.Press(Key.Backspace);
        ui.Press(Key.Backspace);
        ui.Press(Key.Backspace);
        Assert.Equal("hello wo", field.Text);
        ui.Press(Key.Z, control: true); // the three deletes were one step
        Assert.Equal("hello world", field.Text);
        ui.Type("!");
        Assert.False(field.Editor.CanRedo); // a new edit drops what was undone

        // Shift-click extends the selection from the caret to where the pointer is.
        ui.Press(Key.Home);
        ui.Frame(0.5f); // so the next click is not a double click (which selects a word)
        ui.Input.SetKey(Key.ShiftLeft, true);
        ui.Click(new Vector2(field.Bounds.Max.X - 2f, field.Bounds.Center.Y));
        ui.Input.SetKey(Key.ShiftLeft, false);
        Assert.Equal("hello world!", field.Editor.SelectedText);

        field.Text = "set from code";
        Assert.False(field.Editor.CanUndo); // nothing to undo into
    }

    [Fact]
    public void TextField_TypesUnicode_EditsSelectsCopiesPastes_AndRespectsLimits()
    {
        using var ui = new UIHarness();
        var field = ui.Context.Root.AddChild(new TextField { Width = 300 });
        var submitted = "";
        field.Submitted += t => submitted = t;
        ui.Frame();
        ui.Click(field);

        ui.Type("Zażółć gęślą");
        Assert.Equal("Zażółć gęślą", field.Text);
        ui.Press(Key.Backspace);
        ui.Press(Key.Left, control: true); // word left
        Assert.Equal(7, field.Editor.Caret);
        ui.Press(Key.End, shift: true);
        Assert.Equal("gęśl", field.Editor.SelectedText);
        ui.Press(Key.C, control: true);
        Assert.Equal("gęśl", ui.Clipboard.Text);
        ui.Press(Key.Home);
        ui.Press(Key.V, control: true);
        Assert.Equal("gęślZażółć gęśl", field.Text);
        ui.Press(Key.A, control: true);
        ui.Press(Key.X, control: true);
        Assert.Equal("", field.Text);
        ui.Type("line\nbreak");
        Assert.Equal("line break", field.Text); // single line: newlines become spaces
        ui.Press(Key.Enter);
        Assert.Equal("line break", submitted);

        field.Text = "";
        field.MaxLength = 3;
        field.Validate = t => t.All(char.IsDigit);
        ui.Type("1a2345");
        Assert.Equal("123", field.Text);
        field.ReadOnly = true;
        ui.Press(Key.Backspace);
        Assert.Equal("123", field.Text);
    }

    [Fact]
    public void TextField_Password_NeverReachesTheClipboard()
    {
        using var ui = new UIHarness();
        var field = ui.Context.Root.AddChild(new TextField { Width = 200, Password = true });
        ui.Frame();
        ui.Click(field);
        ui.Type("secret");
        ui.Press(Key.A, control: true);
        ui.Press(Key.C, control: true);
        ui.Press(Key.X, control: true);
        Assert.Null(ui.Clipboard.Text);
        Assert.Equal("secret", field.Text);
    }

    [Fact]
    public void TextArea_HasNewlines_VisualLineMoves_AndKeyRepeat()
    {
        using var ui = new UIHarness();
        var area = ui.Context.Root.AddChild(new TextArea { Width = 160, Height = 120 });
        ui.Frame();
        ui.Click(area);
        ui.Type("first");
        ui.Press(Key.Enter);
        ui.Type("second line");
        Assert.Equal("first\nsecond line", area.Text);
        ui.Press(Key.Up);
        Assert.InRange(area.Editor.Caret, 0, 5); // on the first line
        ui.Press(Key.End, control: true);
        Assert.Equal(area.Text.Length, area.Editor.Caret);

        // Holding Backspace repeats after the delay.
        ui.Input.SetKey(Key.Backspace, true);
        ui.Frame();
        for (var i = 0; i < 40; i++)
        {
            ui.Frame(1f / 60f);
        }

        ui.Input.SetKey(Key.Backspace, false);
        ui.Frame();
        Assert.True(area.Text.Length < "first\nsecond line".Length - 3, $"repeat deleted too little: '{area.Text}'");
    }

    [Fact]
    public void Editor_MovesBySurrogatePairsAndWords()
    {
        var editor = new TextEditor { Text = "a😀 bc" };
        editor.SetCaret(1);
        editor.MoveRight(word: false, extend: false);
        Assert.Equal(3, editor.Caret); // over both halves of the emoji
        editor.Backspace();
        Assert.Equal("a bc", editor.Text);
        editor.SetCaret(4);
        editor.Backspace(word: true);
        Assert.Equal("a ", editor.Text);
        Assert.Equal(4, TextEditor.TextLength("a😀 b"));
    }

    [Fact]
    public void SliderCheckboxRadioToggle_Interact()
    {
        using var ui = new UIHarness();
        var column = ui.Context.Root.AddChild(new Panel { Layout = LayoutMode.Column, Width = 300, Gap = 4 });
        var slider = column.AddChild(new Slider { Minimum = 0, Maximum = 10, Step = 1, Height = 20 });
        var a = column.AddChild(new RadioButton { Text = "A", Group = "g" });
        var b = column.AddChild(new RadioButton { Text = "B", Group = "g" });
        var toggle = column.AddChild(new Toggle { Text = "Fullscreen" });
        ui.Frame();

        ui.Click(new Vector2(slider.Bounds.X + 8 + ((slider.Bounds.Width - 16) * 0.7f), slider.Bounds.Center.Y));
        Assert.Equal(7f, slider.Value);
        ui.Press(Key.Right);
        Assert.Equal(8f, slider.Value);
        ui.Input.SetMousePosition(slider.Bounds.Center);
        ui.Input.SetMouseButton(MouseButton.Left, true);
        ui.Frame();
        ui.Input.SetMousePosition(new Vector2(slider.Bounds.X - 50, slider.Bounds.Center.Y)); // dragging past the end clamps
        ui.Frame();
        ui.Input.SetMouseButton(MouseButton.Left, false);
        ui.Frame();
        Assert.Equal(0f, slider.Value);

        ui.Click(a);
        ui.Click(b);
        Assert.False(a.Checked);
        Assert.True(b.Checked);
        ui.Click(b);
        Assert.True(b.Checked); // radio buttons do not turn off by clicking
        ui.Click(toggle);
        Assert.True(toggle.Checked);
    }

    [Fact]
    public void Dropdown_OpensInTheOverlay_SelectsAndClosesOnOutsideClick()
    {
        using var ui = new UIHarness();
        var dropdown = ui.Context.Root.AddChild(new Dropdown { Width = 200 });
        dropdown.Options.AddRange(["Low", "Medium", "High"]);
        var selected = -1;
        dropdown.SelectionChanged += i => selected = i;
        ui.Frame();

        ui.Click(dropdown);
        Assert.True(dropdown.IsOpen);
        ui.Frame();
        var item = ui.Context.Overlay.Descendants().OfType<Label>().Single(l => l.Text == "High");
        ui.Click(item);
        Assert.Equal(2, selected);
        Assert.False(dropdown.IsOpen);
        Assert.Equal("High", dropdown.SelectedOption);

        ui.Click(dropdown);
        ui.Click(new Vector2(700, 550));
        Assert.False(dropdown.IsOpen);
        Assert.Equal(2, dropdown.SelectedIndex);
    }

    [Fact]
    public void ScrollView_ScrollsWithTheWheel_WithinItsContent()
    {
        using var ui = new UIHarness();
        var scroll = ui.Context.Root.AddChild(new ScrollView { Width = 200, Height = 100 });
        for (var i = 0; i < 20; i++)
        {
            scroll.Content.AddChild(new Label { Text = $"Item {i}", Height = 20 });
        }

        ui.Frame();
        Assert.Equal(400f, scroll.ContentHeight, 1);
        ui.Input.SetMousePosition(scroll.Bounds.Center);
        ui.Input.AddScroll(new Vector2(0, -2));
        ui.Frame();
        Assert.Equal(96f, scroll.ScrollOffset, 1);
        ui.Input.SetMousePosition(scroll.Bounds.Center);
        ui.Input.AddScroll(new Vector2(0, -100));
        ui.Frame();
        Assert.Equal(300f, scroll.ScrollOffset, 1); // clamped to content - view
        Assert.Equal(-300f, scroll.Content.Bounds.Y, 1);
    }

    [Fact]
    public void Tooltip_AppearsAfterTheDelay_AndGoesAway()
    {
        using var ui = new UIHarness();
        var button = ui.Context.Root.AddChild(new Button { Text = "?", Tooltip = "Opens the settings", Width = 40, Height = 40 });
        ui.Frame();
        ui.Move(button.Bounds.Center);
        Assert.Empty(ui.Context.Overlay.Children);
        for (var i = 0; i < 40; i++)
        {
            ui.Frame();
        }

        var tip = Assert.IsType<Tooltip>(Assert.Single(ui.Context.Overlay.Children));
        Assert.Equal("Opens the settings", tip.Text);
        ui.Move(new Vector2(600, 500));
        Assert.Empty(ui.Context.Overlay.Children);
    }

    [Fact]
    public void Themes_LayerTypeStateClassAndInline()
    {
        using var ui = new UIHarness();
        var theme = UITheme.Dark;
        theme[".danger"].Background = Color.Red;
        theme[".danger:hover"].Background = Color.Yellow;
        ui.Context.Theme = theme;
        var button = ui.Context.Root.AddChild(new Button { Text = "Delete", StyleClass = "danger", Width = 100, Height = 30 });
        ui.Move(new Vector2(700, 500));
        Assert.Equal(Color.Red, button.ResolveStyle().Background);
        ui.Move(button.Bounds.Center);
        Assert.Equal(Color.Yellow, button.ResolveStyle().Background);
        button.StateStyles[UIState.Hover] = new UIStyle { Background = Color.Green };
        Assert.Equal(Color.Green, button.ResolveStyle().Background);
        button.BackgroundColor = Color.Blue;
        ui.Move(new Vector2(700, 500));
        Assert.Equal(Color.Blue, button.ResolveStyle().Background);
    }
}
