namespace MNVTEngine.Modules.Tests;

public sealed class ProjectProfileTests
{
    [Theory]
    [InlineData("2d", ProjectProfile.TwoD)]
    [InlineData("3D", ProjectProfile.ThreeD)]
    public void ProfilesParseFromTheirIds_InAnyCase(string text, ProjectProfile expected)
    {
        Assert.Equal(expected, ProjectProfiles.Parse(text));
        Assert.Equal(text.ToLowerInvariant(), expected.Id());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hybrid")]
    [InlineData("2d3d")]
    public void ThereIsNoOtherProfile(string? text)
    {
        Assert.False(ProjectProfiles.TryParse(text, out _));
        Assert.Throws<FormatException>(() => ProjectProfiles.Parse(text));
    }

    [Fact]
    public void OnlyTwoDContent_InfersTwoD_AndOnlyThreeDContent_InfersThreeD()
    {
        var twoD = ProfileInference.FromComponents([new("MNVTEngine.Camera", "a.mnvt"), new("MNVTEngine.SpriteRenderer", "a.mnvt"), new("Game.Player", "a.mnvt")]);
        Assert.Equal(ProjectProfile.TwoD, twoD.Profile);
        Assert.False(twoD.IsDefault);
        Assert.Contains("SpriteRenderer in a.mnvt", twoD.Describe(), StringComparison.Ordinal);

        var threeD = ProfileInference.FromComponents([new("MNVTEngine.MeshRenderer", "b.mnvt"), new("MNVTEngine.WorldTerrain", "b.mnvt"), new("MNVTEngine.UIDocument", "b.mnvt")]);
        Assert.Equal(ProjectProfile.ThreeD, threeD.Profile);
        Assert.Equal(2, threeD.ThreeDEvidence.Count);
    }

    [Fact]
    public void MixedContent_IsAConflict_NeverAGuess()
    {
        var mixed = ProfileInference.FromComponents([new("MNVTEngine.Tilemap", "Level.mnvt"), new("MNVTEngine.Rigidbody", "Props/Crate.mnvtprefab")]);
        Assert.True(mixed.IsConflict);
        Assert.Null(mixed.Profile);
        Assert.Contains("Tilemap in Level.mnvt", mixed.Describe(), StringComparison.Ordinal);
        Assert.Contains("Rigidbody in Props/Crate.mnvtprefab", mixed.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void SharedContentOnly_FallsBackToTwoD_AndSaysSo()
    {
        var shared = ProfileInference.FromComponents([new("MNVTEngine.Camera", "a.mnvt"), new("MNVTEngine.AudioSource", "a.mnvt")]);
        Assert.Equal(ProjectProfile.TwoD, shared.Profile);
        Assert.True(shared.IsDefault);
        Assert.Contains("default", shared.Describe(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MNVTEngine.Camera")]
    [InlineData("MNVTEngine.Transform")]
    [InlineData("MNVTEngine.AudioSource")]
    [InlineData("MNVTEngine.UIDocument")]
    [InlineData("MNVTEngine.MissionRunner")]
    public void SharedComponents_BelongToNoProfile(string id) => Assert.Null(ProfileInference.ProfileOf(id));
}
