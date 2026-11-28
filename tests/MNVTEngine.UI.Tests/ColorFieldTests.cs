// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-28.

using MNVTEngine.Mathematics;
using MNVTEngine.Reactivity;
using MNVTEngine.UI.Widgets;

namespace MNVTEngine.UI.Tests;

public sealed class ColorFieldTests
{
    [Theory]
    [InlineData("#C077CE")]
    [InlineData("#00FF00")]
    [InlineData("#808080")]
    [InlineData("#141A26")]
    public void Hsv_RoundTrips(string hex)
    {
        Assert.True(Color.TryParseHex(hex, out var color));
        var (h, s, v) = V.ToHsv(color);
        Assert.Equal(hex + "FF", V.FromHsv(h, s, v).ToHex(), ignoreCase: true); // ToHex adds the alpha
    }

    [Fact]
    public void TypingAHex_SetsTheColor_AndTheSlidersFollow()
    {
        using var ui = new UIHarness();
        var color = Signal.Create(Color.Red);
        var field = ui.Context.Root.AddChild(V.ColorField(color));
        ui.Frame();
        var text = field.Descendants().OfType<TextField>().Single();
        text.Text = "#00FF00";
        ui.Frame();
        Assert.Equal("#00FF00FF", color.Value.ToHex(), ignoreCase: true);
        Assert.Equal(120f, field.Descendants().OfType<Slider>().First().Value, 0.5f); // hue
        text.Text = "#zz"; // not a color: nothing changes
        ui.Frame();
        Assert.Equal("#00FF00FF", color.Value.ToHex(), ignoreCase: true);
    }
}
