using System.Runtime.Serialization;

namespace MNVTEngine.Modules;

/// <summary>
/// A project's primary rendering mode (<c>"profile"</c> in the .mnvtproj). Exactly one per project: 2D and 3D are
/// mutually exclusive, and there is no hybrid profile. The profile decides which engine modules a project gets.
/// </summary>
public enum ProjectProfile
{
    [EnumMember(Value = "2d")]
    TwoD,

    [EnumMember(Value = "3d")]
    ThreeD,
}

public static class ProjectProfiles
{
    public static IReadOnlyList<ProjectProfile> All { get; } = [ProjectProfile.TwoD, ProjectProfile.ThreeD];

    /// <summary>The identifier used in project files and on the command line: "2d" or "3d".</summary>
    public static string Id(this ProjectProfile profile) => profile switch
    {
        ProjectProfile.TwoD => "2d",
        ProjectProfile.ThreeD => "3d",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown project profile."),
    };

    public static string DisplayName(this ProjectProfile profile) => profile switch
    {
        ProjectProfile.TwoD => "2D",
        ProjectProfile.ThreeD => "3D",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown project profile."),
    };

    /// <summary>Parses "2d" or "3d" (any case); false for anything else.</summary>
    public static bool TryParse(string? text, out ProjectProfile profile)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(text, candidate.Id(), StringComparison.OrdinalIgnoreCase))
            {
                profile = candidate;
                return true;
            }
        }

        profile = default;
        return false;
    }

    /// <exception cref="FormatException">Not "2d" or "3d".</exception>
    public static ProjectProfile Parse(string? text) =>
        TryParse(text, out var profile) ? profile : throw new FormatException($"Unknown project profile '{text}'; use 2d or 3d.");
}
