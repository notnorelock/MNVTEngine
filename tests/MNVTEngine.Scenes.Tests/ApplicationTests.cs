// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

namespace MNVTEngine.Scenes.Tests;

public sealed class ApplicationTests
{
    [Theory]
    [InlineData("https://example.com/download", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("javascript:alert(1)", false)]
    public void OnlyWebAddresses_AreOpened(string address, bool opened)
    {
        Assert.Equal(opened, Application.IsWebAddress(new Uri(address)));
    }

    [Fact]
    public void RelativeAddresses_AreRefused_WithoutStartingAnything()
    {
        Assert.False(Application.IsWebAddress(new Uri("download", UriKind.Relative)));
        Assert.False(Application.OpenUrl(new Uri("C:/game/evil.exe")));
    }
}
