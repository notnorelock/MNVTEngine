// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.UI.Widgets;
using Xunit;

namespace MNVTEngine.UI.Tests;

public sealed class FitAndScrollTests
{
    [Theory]
    [InlineData(2048f, 1f)] // at the design width
    [InlineData(4096f, 1f)] // never grows past it by default
    [InlineData(1024f, 0.5f)] // half as wide: half as large
    [InlineData(320f, 1f / 2.2f)] // never smaller than the floor
    public void FitScale_FollowsTheWindowWidth_WithinLimits(float window, float expected) =>
        Assert.Equal(expected, UIContext.FitScale(window, 2048f, 1f / 2.2f, 1f), 3);

    [Fact]
    public void FitScale_WithoutReference_IsOne() => Assert.Equal(1f, UIContext.FitScale(800f, null, 0.5f, 1f));

    [Fact]
    public void Ease_ApproachesTheTarget_WithoutOvershoot_AndSettles()
    {
        var offset = 0f;
        for (var frame = 0; frame < 120 && offset != 100f; frame++)
        {
            var next = ScrollView.Ease(offset, 100f, 16f, 1f / 60f);
            Assert.InRange(next, offset, 100f);
            offset = next;
        }

        Assert.Equal(100f, offset); // snaps once it is close
    }
}
