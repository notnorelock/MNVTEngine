// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-24.

using MNVTEngine.Obfuscation.Configuration;
using MNVTEngine.Obfuscation.Naming;

namespace MNVTEngine.Obfuscation.Tests;

public sealed class NameGeneratorTests
{
    private static NameGenerator Make(string scheme) => new(NameGenerator.SeedBytes("test-seed"), scheme);

    [Fact]
    public void UnicodeScheme_UsesOnlyTheAccentedICapitals()
    {
        var generator = Make(NamingSchemes.Unicode);
        for (var i = 0; i < 200; i++)
        {
            var name = generator.Create($"[Game]T:Game.Type{i}", _ => false, originalLength: 12);
            Assert.NotEqual(0, name.Length);
            Assert.All(name, c => Assert.Contains(c, "ÌÍÎÏ"));
        }
    }

    [Fact]
    public void LettersScheme_StartsWithALetter_AndIsAscii()
    {
        var generator = Make(NamingSchemes.Letters);
        var name = generator.Create("[Game]T:Game.Player", _ => false, originalLength: 6);
        Assert.True(char.IsAsciiLetter(name[0]), name);
        Assert.All(name, c => Assert.True(char.IsAsciiLetterOrDigit(c), name));
    }

    [Fact]
    public void SameSeedAndId_GiveTheSameName_DifferentIdsDiffer()
    {
        var a = Make(NamingSchemes.Unicode).Create("[Game]T:Game.A", _ => false);
        var b = Make(NamingSchemes.Unicode).Create("[Game]T:Game.A", _ => false);
        var c = Make(NamingSchemes.Unicode).Create("[Game]T:Game.B", _ => false);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void ACollision_GrowsTheName_InsteadOfRepeating()
    {
        var generator = Make(NamingSchemes.Letters);
        var first = generator.Create("[Game]M:Game.T::M", _ => false);
        var second = generator.Create("[Game]M:Game.T::M", name => name == first);
        Assert.NotEqual(first, second);
    }
}
