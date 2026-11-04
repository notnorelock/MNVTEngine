using MNVTEngine.Mathematics;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.UI.Tests;

public sealed class LayoutAndHierarchyTests
{
    [Fact]
    public void Hierarchy_AddsMovesReordersAndRemoves_AndRefusesCycles()
    {
        using var ui = new UIHarness();
        var a = ui.Context.Root.AddChild(new Panel { Name = "a" });
        var b = a.AddChild(new Panel { Name = "b" });
        var c = ui.Context.Root.AddChild(new Panel { Name = "c" });
        Assert.Same(ui.Context, b.Context);
        Assert.Same(b, ui.Context.Root.Find("b"));
        Assert.Throws<InvalidOperationException>(() => b.AddChild(a));

        c.AddChild(b); // reparenting removes it from the old parent
        Assert.Empty(a.Children);
        Assert.Same(c, b.Parent);
        c.SetSiblingIndex(0);
        Assert.Equal(["c", "a"], ui.Context.Root.Children.Select(e => e.Name));

        Assert.True(c.RemoveChild(b));
        Assert.Null(b.Context);
        Assert.False(c.RemoveChild(b));
    }

    [Theory]
    [InlineData("120", UIUnitKind.Pixels, 120f)]
    [InlineData("12.5px", UIUnitKind.Pixels, 12.5f)]
    [InlineData("50%", UIUnitKind.Percent, 50f)]
    [InlineData(" auto ", UIUnitKind.Auto, 0f)]
    public void Units_Parse(string text, UIUnitKind kind, float value) => Assert.Equal(new UIUnit(kind, value), UIUnit.Parse(text));

    [Fact]
    public void Units_RejectGarbage() => Assert.Throws<FormatException>(() => UIUnit.Parse("wide"));

    [Fact]
    public void Absolute_AnchorsPivotsPercentAndMinMax()
    {
        using var ui = new UIHarness(800, 600);
        var centered = ui.Context.Root.AddChild(new Panel { Width = 200, Height = UIUnit.Percent(50), Anchor = new Vector2(0.5f, 0.5f), Pivot = new Vector2(0.5f, 0.5f) });
        var corner = ui.Context.Root.AddChild(new Panel { Width = UIUnit.Percent(10), Height = 20, Anchor = new Vector2(1f, 1f), Pivot = new Vector2(1f, 1f), Left = -10, Top = -10 });
        var clamped = ui.Context.Root.AddChild(new Panel { Width = UIUnit.Percent(100), Height = 10, MaxWidth = 300, MinHeight = 40 });
        ui.Frame();

        Assert.Equal(new Rect(300, 150, 200, 300), centered.Bounds);
        Assert.Equal(new Rect(800 - 10 - 80, 600 - 10 - 20, 80, 20), corner.Bounds);
        Assert.Equal(new Vector2(300, 40), clamped.Bounds.Size);

        ui.Size = new Vector2(1000, 400); // resolution change re-lays out
        ui.Frame();
        Assert.Equal(new Rect(400, 100, 200, 200), centered.Bounds);
    }

    [Fact]
    public void Row_SharesFreeSpaceByFlexGrow_WithGapsPaddingAndCrossAlignment()
    {
        using var ui = new UIHarness(1000, 500);
        var row = ui.Context.Root.AddChild(new Panel { Width = 600, Height = 100, Layout = LayoutMode.Row, Gap = 10, Padding = 20, AlignItems = Align.Center });
        var fixedChild = row.AddChild(new Panel { Width = 100, Height = 20 });
        var grow1 = row.AddChild(new Panel { FlexGrow = 1, Height = 20 });
        var grow3 = row.AddChild(new Panel { FlexGrow = 3, Height = 40, Margin = new Thickness(5, 0, 5, 0) });
        ui.Frame();

        // content 560 wide: 100 + 10 + g1 + 10 + 5 + g3 + 5 = 560 -> g1 + g3 = 430
        Assert.Equal(new Rect(20, 40, 100, 20), fixedChild.Bounds);
        Assert.Equal(107.5f, grow1.Bounds.Width, 3);
        Assert.Equal(322.5f, grow3.Bounds.Width, 3);
        Assert.Equal(20 + 100 + 10 + 107.5f + 10 + 5, grow3.Bounds.X, 3);
        Assert.Equal(30f, grow3.Bounds.Y, 3); // centered in the 60 px content height
    }

    [Fact]
    public void Column_ShrinksFlexibleChildren_WhenContentDoesNotFit()
    {
        using var ui = new UIHarness();
        var column = ui.Context.Root.AddChild(new Panel { Width = 200, Height = 300, Layout = LayoutMode.Column });
        column.AddChild(new Panel { Height = 200 });
        var list = column.AddChild(new Panel { Height = 250, FlexGrow = 1, MinHeight = 40 });
        var footer = column.AddChild(new Panel { Height = 30 });
        ui.Frame();
        Assert.Equal(70f, list.Bounds.Height, 2); // 300 - 200 - 30
        Assert.Equal(270f, footer.Bounds.Y, 2);
    }

    [Fact]
    public void Column_StretchesAutoWidths_AndJustifiesSpaceBetween()
    {
        using var ui = new UIHarness();
        var column = ui.Context.Root.AddChild(new Panel { Width = 300, Height = 300, Layout = LayoutMode.Column, Justify = Justify.SpaceBetween });
        var top = column.AddChild(new Panel { Height = 50 });
        var bottom = column.AddChild(new Panel { Height = 50, AlignSelf = Align.End, Width = 100 });
        ui.Frame();
        Assert.Equal(new Rect(0, 0, 300, 50), top.Bounds);
        Assert.Equal(new Rect(200, 250, 100, 50), bottom.Bounds);
    }

    [Fact]
    public void Grid_PlacesCellsRowByRow()
    {
        using var ui = new UIHarness();
        var grid = ui.Context.Root.AddChild(new Panel { Width = 310, Layout = LayoutMode.Grid, GridColumns = 3, Gap = 5 });
        var cells = Enumerable.Range(0, 5).Select(i => grid.AddChild(new Panel { Height = 20 + (i * 2) })).ToList();
        ui.Frame();
        Assert.Equal(new Rect(0, 0, 100, 20), cells[0].Bounds);
        Assert.Equal(new Rect(210, 0, 100, 24), cells[2].Bounds);
        Assert.Equal(new Rect(105, 24 + 5, 100, 28), cells[4].Bounds);
        Assert.Equal(24 + 5 + 28, grid.Bounds.Height); // auto height from the rows
    }

    [Fact]
    public void AutoSize_FollowsText_AndUIScaleKeepsLogicalLayout()
    {
        using var ui = new UIHarness(800, 600, scale: 2f);
        var label = ui.Context.Root.AddChild(new Label { Text = "Zażółć gęślą jaźń", FontSize = 20 });
        ui.Frame();
        Assert.Equal(new Vector2(400, 300), ui.Context.Size); // logical size = window / scale
        Assert.InRange(label.Bounds.Width, 120f, 220f);
        Assert.InRange(label.Bounds.Height, 20f, 32f);

        var width = label.Bounds.Width;
        label.Text += " más";
        ui.Frame();
        Assert.True(label.Bounds.Width > width);
        Assert.Equal(2, ui.Context.Statistics.LayoutPasses - 1); // one pass per change, none for idle frames
        ui.Frame();
        ui.Frame();
        Assert.Equal(2, ui.Context.Statistics.LayoutPasses - 1);
    }

    [Fact]
    public void WrappedLabel_GrowsDown_AndClipChildrenRestrictsHits()
    {
        using var ui = new UIHarness();
        var box = ui.Context.Root.AddChild(new Panel { Width = 120, Layout = LayoutMode.Column, Overflow = Overflow.Clip, Height = 60 });
        var label = box.AddChild(new Label { Text = "The quick brown fox jumps over the lazy dog", Wrap = true });
        var below = box.AddChild(new Button { Text = "Hidden", Height = 30 });
        ui.Frame();
        Assert.Equal(120f, label.Bounds.Width);
        Assert.True(label.Bounds.Height > 40f);
        Assert.True(below.Bounds.Y >= 60f);
        Assert.Null(ui.Context.HitTest(new Vector2(10, below.Bounds.Y + 5))); // clipped away: not hittable
    }
}
