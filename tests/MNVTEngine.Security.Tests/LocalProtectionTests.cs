// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-26.

using System.Text;

namespace MNVTEngine.Security.Tests;

/// <summary>Local protection: round trips, keeps purposes apart, and treats anything it cannot read as missing.</summary>
public sealed class LocalProtectionTests
{
    [Fact]
    public void Text_RoundTrips_OnlyForItsPurpose_AndIsNotStoredInTheClear()
    {
        Assert.SkipUnless(LocalProtection.IsAvailable, "DPAPI is Windows only.");

        var protectedText = LocalProtection.ProtectString("token-123", "Tests.Tokens");

        Assert.DoesNotContain("token-123", Encoding.UTF8.GetString(Convert.FromBase64String(protectedText)), StringComparison.Ordinal);
        Assert.Equal("token-123", LocalProtection.UnprotectString(protectedText, "Tests.Tokens"));
        Assert.Null(LocalProtection.UnprotectString(protectedText, "Tests.Other"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("dG9rZW4tMTIz")] // base64, but never protected
    public void WhatCannotBeRead_IsMissing_NotAnError(string? stored)
    {
        Assert.Null(LocalProtection.UnprotectString(stored, "Tests.Tokens"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void APurpose_IsRequired(string purpose)
    {
        Assert.Throws<ArgumentException>(() => LocalProtection.Unprotect([1, 2, 3], purpose));
    }
}
