using MNVTEngine.Modules;
using MNVTEngine.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Build;

/// <param name="Name">Display name, e.g. "Empty 3D Project".</param>
/// <param name="Profile">The profile of every project created from it.</param>
public sealed record ProjectTemplate(string Id, string Name, string Description, ProjectProfile Profile, string Directory);

/// <summary>
/// Project creation from <c>templates/*</c>. A template is a folder with a <c>template.json</c> (<c>id</c>, <c>name</c>,
/// <c>description</c> and <c>profile</c>, "2d" or "3d", which its .mnvtproj must declare too). Tokens are replaced in file names (<c>__Name__</c>, <c>__Identifier__</c>) and in text files
/// (<c>{{Name}}</c>, <c>{{Identifier}}</c>, <c>{{EngineRoot}}</c>). Every new project gets its own content encryption key.
/// </summary>
public static class ProjectTemplates
{
    public const string ManifestFile = "template.json";

    /// <summary>The template <c>mnvt new --profile</c> uses: <c>empty-2d</c> or <c>empty-3d</c>.</summary>
    public static string DefaultTemplateId(ProjectProfile profile) => "empty-" + profile.Id();

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mnvtproj", ".mnvt", ".mnvtprefab", ".mnvts", ".meta", ".cs", ".csproj", ".props", ".json", ".md", ".txt", ".gitignore",
    };

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "Library", "Builds" };

    public static IReadOnlyList<ProjectTemplate> List(string templatesDirectory)
    {
        if (!System.IO.Directory.Exists(templatesDirectory))
        {
            return [];
        }

        var templates = new List<ProjectTemplate>();
        foreach (var directory in System.IO.Directory.EnumerateDirectories(templatesDirectory).Order(StringComparer.Ordinal))
        {
            var manifest = Path.Combine(directory, ManifestFile);
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                var json = JObject.Parse(File.ReadAllText(manifest));
                var id = json.Value<string>("id") ?? Path.GetFileName(directory);
                if (!ProjectProfiles.TryParse(json.Value<string>("profile"), out var profile))
                {
                    throw new InvalidDataException($"Template manifest '{manifest}' needs \"profile\": \"2d\" or \"3d\".");
                }

                templates.Add(new ProjectTemplate(id, json.Value<string>("name") ?? id, json.Value<string>("description") ?? "", profile, directory));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Template manifest '{manifest}' is invalid: {ex.Message}", ex);
            }
        }

        return templates;
    }

    /// <summary>Creates <paramref name="targetDirectory"/> from the template and checks that the new project loads.</summary>
    /// <returns>The new .mnvtproj file.</returns>
    /// <exception cref="ArgumentException">The name is not a valid project name.</exception>
    /// <exception cref="IOException">The target folder exists and is not empty.</exception>
    public static string Create(ProjectTemplate template, string name, string targetDirectory, string engineRoot)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!GamePackage.IsValidExecutableName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid project name: use letters, digits, '_', '-' or '.', starting with a letter.", nameof(name));
        }

        if (System.IO.Directory.Exists(targetDirectory) && System.IO.Directory.EnumerateFileSystemEntries(targetDirectory).Any())
        {
            throw new IOException($"'{targetDirectory}' already exists and is not empty.");
        }

        var tokens = new Dictionary<string, string>
        {
            ["Name"] = name,
            ["Identifier"] = name.Replace('-', '_'),
            ["EngineRoot"] = Path.GetFullPath(engineRoot).Replace('\\', '/').TrimEnd('/'),
        };

        foreach (var file in System.IO.Directory.EnumerateFiles(template.Directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(template.Directory, file);
            if (relative == ManifestFile || relative.Split(Path.DirectorySeparatorChar).SkipLast(1).Any(SkippedFolders.Contains))
            {
                continue;
            }

            foreach (var (token, value) in tokens)
            {
                relative = relative.Replace($"__{token}__", value, StringComparison.Ordinal);
            }

            var destination = Path.Combine(targetDirectory, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var extension = Path.GetExtension(file).Length > 0 ? Path.GetExtension(file) : Path.GetFileName(file);
            if (TextExtensions.Contains(extension))
            {
                var text = File.ReadAllText(file);
                foreach (var (token, value) in tokens)
                {
                    text = text.Replace($"{{{{{token}}}}}", value, StringComparison.Ordinal);
                }

                File.WriteAllText(destination, text);
            }
            else
            {
                File.Copy(file, destination);
            }
        }

        ContentKeys.Create(targetDirectory);
        var project = GameProject.Load(targetDirectory);
        if (project.Profile != template.Profile)
        {
            throw new InvalidDataException($"Template '{template.Id}' is a {template.Profile.DisplayName()} template, but its project file declares {(project.Profile is { } p ? p.DisplayName() : "no profile")}.");
        }

        return project.ProjectFile;
    }
}
