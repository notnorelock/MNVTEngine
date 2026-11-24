// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using MNVTEngine.Build;

namespace MNVTEngine.Build.Tests;

public sealed class SolutionFileTests
{
    [Fact]
    public void ReferencesTheScriptsProject_WithStableGuids()
    {
        var sln = SolutionFile.Build("MyGame", "Scripts/MyGame.Scripts.csproj");

        Assert.StartsWith("﻿\r\nMicrosoft Visual Studio Solution File, Format Version 12.00", sln, StringComparison.Ordinal);
        Assert.Contains("\"MyGame.Scripts\", \"Scripts\\MyGame.Scripts.csproj\"", sln, StringComparison.Ordinal); // backslashes, as Visual Studio writes
        Assert.Contains("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}", sln, StringComparison.Ordinal); // C# project type
        Assert.Contains(".Release|Any CPU.Build.0 = Release|Any CPU", sln, StringComparison.Ordinal);
        Assert.EndsWith("EndGlobal\r\n", sln, StringComparison.Ordinal);

        Assert.Equal(sln, SolutionFile.Build("MyGame", "Scripts/MyGame.Scripts.csproj")); // deterministic
    }

    [Fact]
    public void AProjectWithoutScripts_GetsAnEmptySolution()
    {
        var sln = SolutionFile.Build("Assets", null);
        Assert.DoesNotContain(".csproj", sln, StringComparison.Ordinal);
        Assert.Contains("EndGlobal", sln, StringComparison.Ordinal);
    }
}
