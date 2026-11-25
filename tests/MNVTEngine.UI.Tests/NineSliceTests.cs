// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Mathematics;

namespace MNVTEngine.UI.Tests;

public sealed class NineSliceTests
{
    [Fact]
    public void Pieces_KeepCornersAtScale_AndStretchEdgesAndCentre()
    {
        // A 12x12 texture with a 4-texel frame, drawn at 2 pixels per texel into a 100x40 box.
        var pieces = NineSlice.Pieces(new Rect(10, 20, 100, 40), 12, 12, Thickness.All(4), scale: 2f);

        Assert.Equal(9, pieces.Count);
        Assert.Equal((new Rect(10, 20, 8, 8), new Rect(0, 0, 4, 4)), pieces[0]); // top-left corner
        Assert.Equal((new Rect(18, 20, 84, 8), new Rect(4, 0, 4, 4)), pieces[1]); // top edge, stretched
        Assert.Equal((new Rect(18, 28, 84, 24), new Rect(4, 4, 4, 4)), pieces[4]); // centre
        Assert.Equal((new Rect(102, 52, 8, 8), new Rect(8, 8, 4, 4)), pieces[8]); // bottom-right corner
    }

    [Fact]
    public void Pieces_ShrinkTheFrameEvenly_WhenTheBoxIsSmallerThanIt()
    {
        var pieces = NineSlice.Pieces(new Rect(0, 0, 8, 40), 12, 12, Thickness.All(4), scale: 2f);

        Assert.Equal(new Rect(0, 0, 4, 8), pieces[0].Dest); // 16 px of frame into 8: half each side
        Assert.DoesNotContain(pieces, p => p.Dest.Width <= 0 || p.Dest.Height <= 0); // no centre column left
    }

    [Fact]
    public void Pieces_WithoutAFrame_StretchTheWholeTexture()
    {
        var piece = Assert.Single(NineSlice.Pieces(new Rect(0, 0, 50, 30), 16, 8, default));

        Assert.Equal((new Rect(0, 0, 50, 30), new Rect(0, 0, 16, 8)), piece);
    }
}
