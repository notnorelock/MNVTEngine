// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Mathematics;
using MNVTEngine.Reactivity;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.UI.Tests;

public sealed class CompositionTests
{
    [Fact]
    public void Bindings_FollowSignals_AndStopWhenTheElementIsDisposed()
    {
        using var ui = new UIHarness();
        var name = Signal.Create("Ann");
        var label = V.Text(() => "Hi " + name.Value);
        ui.Context.Root.AddChild(label);
        Assert.Equal("Hi Ann", label.Text);

        name.Value = "Bob";
        Assert.Equal("Hi Bob", label.Text);

        label.Dispose();
        Assert.True(label.IsDisposed);
        Assert.Null(label.Parent);
        name.Value = "Cid";
        Assert.Equal("Hi Bob", label.Text);
    }

    [Fact]
    public void Mount_ReplacesContent_AndDisposesTheOld()
    {
        using var ui = new UIHarness();
        var first = ui.Context.Mount(() => V.Column(V.Text("one")));
        var second = ui.Context.Mount(() => V.Column(V.Text("two")));
        Assert.True(first.IsDisposed);
        Assert.Same(second, Assert.Single(ui.Context.Root.Children));
    }

    [Fact]
    public void Show_BuildsOnlyTheShownBranch_AndRebuildsOnlyOnChange()
    {
        using var ui = new UIHarness();
        var on = Signal.Create(false);
        var built = new List<string>();
        var show = V.Show(() => on.Value, () => { built.Add("then"); return V.Text("yes"); }, () => { built.Add("else"); return V.Text("no"); });
        ui.Context.Root.AddChild(show);
        var no = Assert.IsType<Label>(Assert.Single(show.Children));
        Assert.Equal("no", no.Text);

        on.Value = true;
        Assert.True(no.IsDisposed);
        Assert.Equal("yes", ((Label)Assert.Single(show.Children)).Text);
        on.Value = true;
        Assert.Equal(["else", "then"], built);
    }

    [Fact]
    public void For_KeepsElementsPerKey_MovesOnReorder_AndDisposesRemovedKeys()
    {
        using var ui = new UIHarness();
        var items = Signal.Create<IReadOnlyList<(int Id, string Name)>>([(1, "a"), (2, "b"), (3, "c")]);
        var list = V.For(() => items.Value, i => i.Id, (item, _) => V.Text(() => item.Value.Name));
        ui.Context.Root.AddChild(list);
        var b = (Label)list.Children[1];

        items.Value = [(3, "c"), (2, "B"), (1, "a")];
        Assert.Equal(3, list.Builds);
        Assert.Same(b, list.Children[1]);
        Assert.Equal("B", b.Text); // replaced under the same key: updated in place
        Assert.Equal("c", ((Label)list.Children[0]).Text);

        items.Value = [(2, "B")];
        Assert.Equal(1, list.Count);
        Assert.Same(b, Assert.Single(list.Children));
    }

    [Fact]
    public void For_RejectsDuplicateKeys()
    {
        using var ui = new UIHarness();
        var handler = ReactiveRuntime.Current.ErrorHandler;
        Exception? failure = null;
        ReactiveRuntime.Current.ErrorHandler = ex => failure = ex;
        try
        {
            var items = Signal.Create<IReadOnlyList<int>>([1]);
            ui.Context.Root.AddChild(V.For(() => items.Value, i => i, (item, _) => V.Text("")));
            items.Value = [1, 1];
            Assert.Contains("duplicate key", failure?.ToString() ?? "", StringComparison.Ordinal);
        }
        finally
        {
            ReactiveRuntime.Current.ErrorHandler = handler;
        }
    }

    [Fact]
    public void Pages_BuildEachPageOnce_AndKeepIt()
    {
        using var ui = new UIHarness();
        var index = Signal.Create(0);
        var pages = V.Pages(() => index.Value, () => V.Text("A"), () => V.Text("B"));
        ui.Context.Root.AddChild(pages);
        Assert.Equal(1, pages.BuiltCount);
        var a = pages.Children[0];

        index.Value = 1;
        index.Value = 0;
        Assert.Equal(2, pages.BuiltCount);
        Assert.Same(a, pages.Children[0]);
        Assert.True(a.Visible);
        Assert.False(pages.Children[1].Visible);
    }

    [Fact]
    public void Keyed_RebuildsWhenTheKeyChanges()
    {
        using var ui = new UIHarness();
        var screen = Signal.Create("menu");
        var region = V.Keyed(() => screen.Value, s => V.Text(s));
        ui.Context.Root.AddChild(region);
        var menu = region.Children[0];

        screen.Value = "menu";
        Assert.Equal(1, region.Builds);
        screen.Value = "game";
        Assert.True(menu.IsDisposed);
        Assert.Equal("game", ((Label)Assert.Single(region.Children)).Text);
    }

    [Fact]
    public void Controls_AreTwoWayBound()
    {
        using var ui = new UIHarness();
        var text = Signal.Create("");
        var on = Signal.Create(false);
        var field = V.TextField(text);
        var toggle = V.Switch(on, "Sound");
        ui.Context.Root.AddChild(V.Column(field, toggle).Width(300));
        ui.Frame();

        ui.Click(field);
        ui.Type("hey");
        Assert.Equal("hey", text.Value);
        text.Value = "set";
        Assert.Equal("set", field.Text);

        ui.Click(toggle);
        Assert.True(on.Value);
        on.Value = false;
        Assert.False(toggle.Checked);
    }

    [Fact]
    public void Tweens_ReachTheirTarget_AndAfterRunsOnce()
    {
        using var ui = new UIHarness();
        var box = ui.Context.Root.AddChild(new Panel { Width = 10, Height = 10 });
        var ran = 0;
        box.AnimateOpacity(0f, Motion.Tween(0.1f)).After(0.1f, () => ran++);
        Assert.True(box.IsAnimating());
        for (var i = 0; i < 20; i++)
        {
            ui.Frame();
        }

        Assert.Equal(0f, box.Opacity);
        Assert.Equal(1, ran);
        Assert.False(box.IsAnimating());
    }

    [Fact]
    public void Springs_Settle_AtTheTarget()
    {
        using var ui = new UIHarness();
        var box = ui.Context.Root.AddChild(new Panel { Width = 10, Height = 10 });
        box.AnimateScale(1.5f, Motion.Spring(300f, 0.7f));
        for (var i = 0; i < 180; i++)
        {
            ui.Frame();
        }

        Assert.Equal(1.5f, box.RenderScale, 3);
        Assert.False(box.IsAnimating());
    }

    [Fact]
    public void Dialog_TrapsFocus_ClosesOnEscape_AndRestoresFocus()
    {
        using var ui = new UIHarness();
        ui.Context.UseDesign();
        var behind = ui.Context.Root.AddChild(new Button { Text = "Behind" });
        ui.Frame();
        behind.Focus();
        var ok = V.Button("OK");
        var cancel = V.Button("Cancel", ButtonVariant.Secondary);
        var dialog = Dialog.Open(ui.Context, "Leave?", V.Text("Unsaved changes."), true, cancel, ok);
        ui.Frame();
        Assert.Same(cancel, ui.Context.Focused);

        ui.Press(Key.Tab);
        Assert.Same(ok, ui.Context.Focused);
        ui.Press(Key.Tab);
        Assert.NotSame(behind, ui.Context.Focused); // Tab cycles inside the dialog

        ui.Press(Key.Escape);
        Assert.Same(behind, ui.Context.Focused);
        Assert.Null(ui.Context.FocusScope);
        for (var i = 0; i < 60; i++)
        {
            ui.Frame();
        }

        Assert.True(dialog.IsDisposed);
    }

    [Fact]
    public void LoadingButton_RefusesClicks()
    {
        using var ui = new UIHarness();
        var clicks = 0;
        var button = ui.Context.Root.AddChild(V.Button("Save", onClick: () => clicks++).Size(120, 40));
        ui.Frame();
        button.Loading = true;
        ui.Click(button);
        Assert.Equal(0, clicks);
        button.Loading = false;
        ui.Click(button);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void DesignTheme_FollowsTokens_AndLoadsTheBuiltInFonts()
    {
        using var ui = new UIHarness();
        ui.Context.UseDesign();
        var theme = ui.Context.Theme;
        Design.Current.Value = DesignTokens.MidnightHighContrast();
        Assert.NotSame(theme, ui.Context.Theme);
        Design.Current.Value = DesignTokens.Midnight();

        foreach (var font in new[] { DesignFonts.Body, DesignFonts.BodyStrong, DesignFonts.Display, DesignFonts.DisplayStrong, DesignFonts.Icons })
        {
            Assert.NotNull(font);
        }

        var title = ui.Context.Root.AddChild(V.Title("Zażółć"));
        Assert.Same(DesignFonts.DisplayStrong, title.ResolveStyle().Font);
    }
}
