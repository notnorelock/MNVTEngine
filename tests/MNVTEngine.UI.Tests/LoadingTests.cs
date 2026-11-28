// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Mathematics;
using MNVTEngine.Reactivity;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.UI.Tests;

public sealed class LoadingTests
{
    [Fact]
    public void Gradient_SamplesBetweenStops_AndClampsOutside()
    {
        var g = new Gradient([new(1f, Color.Blue), new(0f, Color.Red), new(0.5f, Color.Green)]); // sorted on construction
        Assert.Equal(Color.Red, g.Sample(-1f));
        Assert.Equal(Color.Green, g.Sample(0.5f));
        Assert.Equal(Color.Blue, g.Sample(2f));
        Assert.Equal(0.5f, g.Sample(0.25f).R, 1e-4f);
        Assert.Equal(g, new Gradient([new(0f, Color.Red), new(0.5f, Color.Green), new(1f, Color.Blue)]));
        Assert.Throws<ArgumentException>(() => new Gradient([new(0f, Color.Red)]));
    }

    [Fact]
    public void Shimmer_StartsAndEndsOffScreen_SoTheLoopHasNoJump()
    {
        Assert.Equal(-Skeleton.BandHalfWidth, Skeleton.SweepX(0d, 1.6f, 800f), 1e-3f);
        Assert.Equal(800f + Skeleton.BandHalfWidth, Skeleton.SweepX(1.6d - 1e-6, 1.6f, 800f), 0.1f);
        Assert.True(Skeleton.SweepX(0.8d, 1.6f, 800f) is > 390f and < 410f);
        Assert.Equal(0f, Skeleton.Breath(0d, 1.6f, 0.25f), 1e-5f);
        Assert.Equal(0.25f, Skeleton.Breath(1.2d, 1.6f, 0.25f), 1e-4f); // the peak, half of the 2.4 s breath
        Assert.Equal(Skeleton.Breath(0.1d, 1.6f, 0.25f), Skeleton.Breath(2.5d, 1.6f, 0.25f), 1e-4f);
    }

    [Fact]
    public void AQuickLoad_NeverShowsItsSkeleton()
    {
        using var ui = new UIHarness();
        var state = Signal.Create(LoadState.Loading);
        var region = ui.Context.Root.AddChild(V.Loading(() => state.Value, () => V.SkeletonList(), () => V.Text("ready")));
        ui.Frame(0.05f);
        Assert.Empty(region.Descendants().OfType<Skeleton>());
        state.Value = LoadState.Loaded;
        ui.Frame();
        Assert.Single(region.Descendants().OfType<Label>(), l => l.Text == "ready");
    }

    [Fact]
    public void ASlowLoad_ShowsItsSkeleton_ForAMoment_ThenTheContent()
    {
        using var ui = new UIHarness();
        var state = Signal.Create(LoadState.Loading);
        var region = ui.Context.Root.AddChild(V.Loading(() => state.Value, () => V.SkeletonList(3), () => V.Text("ready")));
        ui.Frame(0.2f);
        Assert.Equal(3 * 3, region.Descendants().OfType<Skeleton>().Count()); // an avatar and two lines a row
        state.Value = LoadState.Loaded;
        ui.Frame(0.1f);
        Assert.NotEmpty(region.Descendants().OfType<Skeleton>()); // stays its minimum, no flicker
        ui.Frame(0.3f);
        Assert.Empty(region.Descendants().OfType<Skeleton>());
        Assert.Single(region.Descendants().OfType<Label>(), l => l.Text == "ready");

        state.Value = LoadState.Loading; // a reload keeps the content
        ui.Frame(0.5f);
        Assert.Empty(region.Descendants().OfType<Skeleton>());
        state.Value = LoadState.Failed;
        ui.Frame();
        Assert.Contains(region.Descendants().OfType<Label>(), l => l.Text == "Could not load this");
    }

    [Fact]
    public void ReducedMotion_StillsSkeletons_AndSkipsMoves()
    {
        using var ui = new UIHarness();
        var skeleton = ui.Context.Root.AddChild(V.SkeletonCircle(20f));
        var box = ui.Context.Root.AddChild(new UIElement());
        Design.ReducedMotion = true;
        try
        {
            Assert.False(skeleton.Tick(0.016f));
            box.AnimateTranslate(new Vector2(40f, 0f), Motion.Tween(1f));
            Assert.Equal(new Vector2(40f, 0f), box.Translate);
        }
        finally
        {
            Design.ReducedMotion = false;
        }

        Assert.True(skeleton.Tick(0.016f));
    }
}
