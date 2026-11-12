using MNVTEngine.Modules;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Runtime;

/// <summary>The profile of a project as tools use it: the saved one, or one inferred for a project that predates profiles.</summary>
/// <param name="Inference">The content scan behind an inferred profile; null when the project file sets the profile.</param>
public sealed record ProfileResolution(ProjectProfile Profile, ProfileInference? Inference)
{
    public bool IsInferred => Inference is not null;
}

/// <summary>A pre-profile project has both 2D and 3D content: its profile must be chosen, never guessed.</summary>
public sealed class ProfileConflictException(ProfileInference inference)
    : InvalidOperationException(inference.Describe() + " Use `mnvt migrate <project> --profile 2d|3d` or the editor.")
{
    public ProfileInference Inference { get; } = inference;
}

/// <summary>
/// Gives projects created before profiles (schema version 1) a profile. The profile is inferred from the components in
/// the project's scenes and prefabs (<see cref="ProfileInference"/>); mixed content is a conflict that only an explicit
/// choice resolves. <see cref="Migrate"/> saves the result into the project file with a backup of the old one.
/// </summary>
public static class ProjectProfileMigration
{
    private static readonly string[] ContentExtensions = [".mnvt", ".mnvtprefab"];

    /// <summary>Scans the scenes and prefabs under Assets. Files that are not valid JSON are listed in <paramref name="unreadable"/> and skipped.</summary>
    public static ProfileInference Infer(GameProject project, ICollection<string>? unreadable = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var evidence = new List<ProfileEvidence>();
        if (Directory.Exists(project.AssetsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(project.AssetsDirectory, "*", SearchOption.AllDirectories)
                .Where(f => ContentExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal))
            {
                var source = Path.GetRelativePath(project.AssetsDirectory, file).Replace('\\', '/');
                try
                {
                    CollectComponents(JToken.Parse(File.ReadAllText(file)), source, evidence);
                }
                catch (JsonException)
                {
                    // A broken scene says nothing about the profile; the scene loader reports it with details.
                    unreadable?.Add(source);
                }
            }
        }

        return ProfileInference.FromComponents(evidence);
    }

    /// <summary>The saved profile, or the inferred one for a version 1 project (not saved).</summary>
    /// <exception cref="ProfileConflictException">Version 1 project with both 2D and 3D content.</exception>
    public static ProfileResolution Resolve(GameProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Profile is { } saved)
        {
            return new ProfileResolution(saved, null);
        }

        var inference = Infer(project);
        return inference.Profile is { } inferred ? new ProfileResolution(inferred, inference) : throw new ProfileConflictException(inference);
    }

    /// <summary>
    /// Saves a profile into a version 1 project: <paramref name="choice"/> when given, else the inferred one. Projects
    /// that already have a profile are left alone (and must match <paramref name="choice"/>).
    /// </summary>
    /// <returns>The saved profile and, for a migrated project, the content scan.</returns>
    /// <exception cref="ProfileConflictException">Mixed content and no <paramref name="choice"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="choice"/> contradicts the saved profile or content of only the other profile.</exception>
    /// <exception cref="IOException">The project file cannot be written.</exception>
    public static ProfileResolution Migrate(GameProject project, ProjectProfile? choice = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Profile is { } saved)
        {
            return choice is { } wanted && wanted != saved
                ? throw new InvalidOperationException($"The project's profile is already {saved.DisplayName()}; it is not changed by migration.")
                : new ProfileResolution(saved, null);
        }

        var inference = Infer(project);
        var profile = (choice, inference.Profile) switch
        {
            ({ } chosen, null) => chosen,
            ({ } chosen, { } inferred) when chosen == inferred || inference.IsDefault => chosen,
            ({ } chosen, _) => throw new InvalidOperationException($"Cannot make the project {chosen.DisplayName()}: {inference.Describe()}"),
            (null, { } inferred) => inferred,
            (null, null) => throw new ProfileConflictException(inference),
        };
        project.SaveProfile(profile);
        return new ProfileResolution(profile, inference);
    }

    // Scenes and prefabs store components as { "type": "<id>", ... } in "components" arrays.
    private static void CollectComponents(JToken token, string source, List<ProfileEvidence> evidence)
    {
        switch (token)
        {
            case JObject obj:
                foreach (var property in obj.Properties())
                {
                    if (property is { Name: "components", Value: JArray components })
                    {
                        foreach (var component in components.OfType<JObject>())
                        {
                            if (component.Value<string>("type") is { } type)
                            {
                                evidence.Add(new ProfileEvidence(type, source));
                            }
                        }
                    }

                    CollectComponents(property.Value, source, evidence);
                }

                break;
            case JArray array:
                foreach (var item in array)
                {
                    CollectComponents(item, source, evidence);
                }

                break;
        }
    }
}
