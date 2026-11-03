using MNVTEngine.Assets;
using MNVTEngine.Runtime;
using MNVTEngine.Scripting.Bytecode;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Build;

/// <summary>
/// Checks an exported game without running it: self-contained runtime, every managed and native dependency listed by
/// the host's deps.json present, required native libraries, no development tooling, every file matching the manifest
/// hash, loadable project, and verified bytecode for every script. <see cref="RunAsync"/> additionally launches it.
/// </summary>
public static class BuildValidator
{
    /// <summary>Native libraries the engine loads by name at run time, per target. OpenGL itself (opengl32.dll) comes from the GPU driver.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> RequiredNativeLibraries = new Dictionary<string, string[]>
    {
        ["win-x64"] = ["glfw3.dll"],
    };

    /// <summary>Assembly name prefixes that must never ship: the editor, build tooling, compilers.</summary>
    public static readonly IReadOnlyList<string> DevelopmentOnlyPrefixes =
        ["MNVTEngine.Editor", "MNVTEngine.Build", "MNVTEngine.Player", "MNVTEngine.CLI", "mnvt.", "ImGui", "cimgui", "Microsoft.Build", "Microsoft.CodeAnalysis"];

    private static readonly string[] DependencyAssetKinds = ["runtime", "native"];

    public static IReadOnlyList<BuildDiagnostic> Validate(string packageDirectory)
    {
        var diagnostics = new List<BuildDiagnostic>();
        var content = Path.Combine(packageDirectory, GamePackage.ContentFolder);
        if (!GamePackage.IsPackage(content))
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB501", $"Not an exported game: {GamePackage.ContentFolder}/{GamePackage.ManifestFile} is missing.", packageDirectory));
            return diagnostics;
        }

        GamePackage package;
        try
        {
            package = GamePackage.Load(content);
        }
        catch (SerializationException ex)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB502", ex.Message, ex.SourceName));
            return diagnostics;
        }

        ValidateHost(packageDirectory, package, diagnostics);
        ValidateFiles(packageDirectory, package, diagnostics);
        ValidateContent(content, package, diagnostics);
        return diagnostics;
    }

    /// <summary>Starts the game, lets it render <paramref name="frames"/> frames and checks that it exits cleanly. Needs a GPU with OpenGL 4.5.</summary>
    public static async Task<IReadOnlyList<BuildDiagnostic>> RunAsync(string packageDirectory, int frames, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var package = GamePackage.Load(Path.Combine(packageDirectory, GamePackage.ContentFolder));
        var exe = Path.Combine(packageDirectory, package.ExecutableName + ".exe");
        var result = await DotnetCli.RunProcessAsync(exe, ["--frames", frames.ToString(System.Globalization.CultureInfo.InvariantCulture)], packageDirectory, timeout, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return [];
        }

        var reason = result.StartError ?? result.ExitCode switch
        {
            1 => "GPU resources leaked",
            2 => "the project or its startup scene could not be loaded",
            3 => "the game's C# code could not be loaded",
            _ => $"exit code {result.ExitCode}",
        };
        var tail = string.Join('\n', result.Output.Split('\n').TakeLast(25));
        return [BuildDiagnostic.Error("MNVB520", $"Test run failed: {reason}.\n{tail}", exe)];
    }

    private static void ValidateHost(string packageDirectory, GamePackage package, List<BuildDiagnostic> diagnostics)
    {
        var name = package.ExecutableName;
        foreach (var file in new[] { name + ".exe", name + ".dll", name + ".runtimeconfig.json", name + ".deps.json" })
        {
            if (!File.Exists(Path.Combine(packageDirectory, file)))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB503", $"Game host file missing: {file}", packageDirectory));
            }
        }

        if (ReadJson(Path.Combine(packageDirectory, name + ".runtimeconfig.json"), diagnostics) is { } runtimeConfig
            && runtimeConfig["runtimeOptions"]?["includedFrameworks"] is null)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB504", "The game is not self-contained: it would need an installed .NET runtime.", name + ".runtimeconfig.json"));
        }

        if (ReadJson(Path.Combine(packageDirectory, name + ".deps.json"), diagnostics) is { } deps && deps["targets"] is JObject targets)
        {
            // Self-contained publishing flattens every runtime and native asset into the application folder.
            var assets = targets.Properties().SelectMany(t => t.Value.Children<JProperty>())
                .SelectMany(library => DependencyAssetKinds.SelectMany(kind => (library.Value[kind] as JObject)?.Properties() ?? []))
                .Select(asset => Path.GetFileName(asset.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets.Where(a => !File.Exists(Path.Combine(packageDirectory, a))))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB505", $"Dependency listed in deps.json is missing: {asset}", name + ".deps.json"));
            }
        }

        foreach (var native in RequiredNativeLibraries.GetValueOrDefault(package.Target, []))
        {
            if (!File.Exists(Path.Combine(packageDirectory, native)))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB506", $"Required native library missing: {native}", packageDirectory));
            }
        }

        foreach (var file in Directory.EnumerateFiles(packageDirectory, "*.dll", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            if (DevelopmentOnlyPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB507", $"Development-only assembly in the game: {fileName}", Path.GetRelativePath(packageDirectory, file)));
            }
        }
    }

    private static void ValidateFiles(string packageDirectory, GamePackage package, List<BuildDiagnostic> diagnostics)
    {
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in package.Files)
        {
            listed.Add(expected.Path);
            string full;
            try
            {
                full = AssetPaths.Resolve(packageDirectory, expected.Path);
            }
            catch (ArgumentException ex)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB508", ex.Message, GamePackage.ManifestFile));
                continue;
            }

            if (!File.Exists(full))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB508", "File listed in the manifest is missing.", expected.Path));
            }
            else if (GamePackager.Describe(packageDirectory, full) is var actual && (actual.Size != expected.Size || actual.Sha256 != expected.Sha256))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB508", "File differs from the manifest (modified or damaged).", expected.Path));
            }
        }

        var manifest = $"{GamePackage.ContentFolder}/{GamePackage.ManifestFile}";
        foreach (var file in Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(packageDirectory, file).Replace('\\', '/');
            if (!listed.Contains(relative) && !relative.Equals(manifest, StringComparison.OrdinalIgnoreCase) && !relative.StartsWith("Logs/", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(BuildDiagnostic.Warning("MNVB509", "File is not part of the build.", relative));
            }
        }
    }

    private static void ValidateContent(string content, GamePackage package, List<BuildDiagnostic> diagnostics)
    {
        try
        {
            GameProject.Load(content);
        }
        catch (Exception ex) when (ex is SerializationException or FileNotFoundException)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB510", $"Packaged project does not load: {ex.Message}", GamePackage.ContentFolder));
        }

        var assets = AssetDatabase.OpenReadOnly(Path.Combine(content, "Assets"));
        foreach (var problem in assets.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(BuildDiagnostic.FromSerialization("MNVB512", problem));
        }

        foreach (var script in assets.Assets.Where(a => a.Type == AssetTypes.Script))
        {
            var bytecode = GamePackage.BytecodePath(content, script.Guid);
            try
            {
                BytecodeSerializer.Read(File.ReadAllBytes(bytecode));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB511", $"Script bytecode missing or invalid: {ex.Message}", script.Path));
            }
        }

        if (package.ScriptsAssembly is { } scripts && !File.Exists(Path.Combine(content, scripts)))
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB513", "The game's C# assembly is missing.", $"{GamePackage.ContentFolder}/{scripts}"));
        }
    }

    private static JObject? ReadJson(string path, List<BuildDiagnostic> diagnostics)
    {
        if (!File.Exists(path))
        {
            return null; // reported as a missing host file
        }

        try
        {
            return JObject.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB503", $"Unreadable: {ex.Message}", Path.GetFileName(path)));
            return null;
        }
    }
}
