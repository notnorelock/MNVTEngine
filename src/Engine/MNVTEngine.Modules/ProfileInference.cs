namespace MNVTEngine.Modules;

/// <summary>A component found in a scene or prefab that only one profile has.</summary>
/// <param name="ComponentType">Serialized component id, e.g. "MNVTEngine.MeshRenderer".</param>
/// <param name="Source">Where it was found (asset path), for diagnostics.</param>
public sealed record ProfileEvidence(string ComponentType, string Source);

/// <summary>
/// The profile of a project that predates profiles, read from its content. 2D-only content gives 2D, 3D-only content
/// gives 3D. Content of both is a <see cref="IsConflict">conflict</see>: nothing is chosen, the user decides. Content of
/// neither gives 2D, the engine's only mode before profiles existed.
/// </summary>
public sealed class ProfileInference
{
    // Serialized ids of the built-in components that belong to one profile. Shared components (Camera, Transform,
    // audio, UI, scripting) and game components say nothing about the profile.
    private static readonly HashSet<string> TwoDComponents = new(StringComparer.Ordinal)
    {
        "MNVTEngine.SpriteRenderer", "MNVTEngine.SpriteAnimator", "MNVTEngine.Tilemap", "MNVTEngine.Light2D", "MNVTEngine.GlobalLight2D",
        "MNVTEngine.Rigidbody2D", "MNVTEngine.BoxCollider2D", "MNVTEngine.CircleCollider2D", "MNVTEngine.TilemapCollider2D",
    };

    private static readonly HashSet<string> ThreeDComponents = new(StringComparer.Ordinal)
    {
        "MNVTEngine.MeshRenderer", "MNVTEngine.Light", "MNVTEngine.AmbientLight", "MNVTEngine.SceneEnvironment",
        "MNVTEngine.ModelAnimator", "MNVTEngine.ModelAnimatorController", "MNVTEngine.WorldTerrain",
        "MNVTEngine.Rigidbody", "MNVTEngine.BoxCollider", "MNVTEngine.SphereCollider", "MNVTEngine.CapsuleCollider",
        "MNVTEngine.CylinderCollider", "MNVTEngine.MeshCollider", "MNVTEngine.FixedJoint", "MNVTEngine.BallJoint",
        "MNVTEngine.HingeJoint", "MNVTEngine.DistanceJoint", "MNVTEngine.SpringJoint", "MNVTEngine.CharacterController",
    };

    private ProfileInference(ProjectProfile? profile, IReadOnlyList<ProfileEvidence> twoD, IReadOnlyList<ProfileEvidence> threeD)
    {
        Profile = profile;
        TwoDEvidence = twoD;
        ThreeDEvidence = threeD;
    }

    /// <summary>The inferred profile; null when <see cref="IsConflict"/>.</summary>
    public ProjectProfile? Profile { get; }

    public bool IsConflict => Profile is null;

    /// <summary>No profile-specific content at all: 2D was chosen as the pre-profile default, not from content.</summary>
    public bool IsDefault => TwoDEvidence.Count == 0 && ThreeDEvidence.Count == 0;

    public IReadOnlyList<ProfileEvidence> TwoDEvidence { get; }

    public IReadOnlyList<ProfileEvidence> ThreeDEvidence { get; }

    /// <summary>The profile that owns a built-in component; null for shared and game components.</summary>
    public static ProjectProfile? ProfileOf(string componentType) =>
        TwoDComponents.Contains(componentType) ? ProjectProfile.TwoD : ThreeDComponents.Contains(componentType) ? ProjectProfile.ThreeD : null;

    public static ProfileInference FromComponents(IEnumerable<ProfileEvidence> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var twoD = new List<ProfileEvidence>();
        var threeD = new List<ProfileEvidence>();
        foreach (var component in components)
        {
            switch (ProfileOf(component.ComponentType))
            {
                case ProjectProfile.TwoD:
                    twoD.Add(component);
                    break;
                case ProjectProfile.ThreeD:
                    threeD.Add(component);
                    break;
            }
        }

        ProjectProfile? profile = (twoD.Count > 0, threeD.Count > 0) switch
        {
            (true, true) => null,
            (false, true) => ProjectProfile.ThreeD,
            _ => ProjectProfile.TwoD,
        };
        return new ProfileInference(profile, twoD, threeD);
    }

    /// <summary>One line for logs and errors.</summary>
    public string Describe()
    {
        static string Sample(IReadOnlyList<ProfileEvidence> evidence) =>
            string.Join(", ", evidence.Select(e => $"{e.ComponentType} in {e.Source}").Distinct().Take(3)) + (evidence.Count > 3 ? ", ..." : "");

        return Profile switch
        {
            null => $"The project has both 2D content ({Sample(TwoDEvidence)}) and 3D content ({Sample(ThreeDEvidence)}); choose its profile explicitly.",
            _ when IsDefault => "No 2D- or 3D-specific content; 2D, the engine's default before profiles, was chosen.",
            ProjectProfile.ThreeD => $"Only 3D content ({Sample(ThreeDEvidence)}).",
            _ => $"Only 2D content ({Sample(TwoDEvidence)}).",
        };
    }
}
