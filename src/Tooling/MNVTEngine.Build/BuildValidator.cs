using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MNVTEngine.Assets;
using MNVTEngine.Runtime;
using MNVTEngine.Scripting.Bytecode;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Build;

/// <summary>
/// Checks an exported game without running it: private .NET runtime, every assembly reference resolvable from the
/// runtime or <c>Dependencies/Managed</c>, required native libraries, no development tooling, every file matching the
/// manifest hash, and an archive that decrypts and authenticates entry by entry, with a loadable project and asset index
/// and verified bytecode for every script. <see cref="RunAsync"/> additionally launches the game.
/// </summary>
public static class BuildValidator
{
    /// <summary>Native libraries the engine loads by name at run time, per target. OpenGL itself (opengl32.dll) comes from the GPU driver.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> RequiredNativeLibraries = new Dictionary<string, string[]>
    {
        ["win-x64"] = ["glfw3.dll", "soft_oal.dll"],
    };

    /// <summary>Assembly name prefixes that must never ship: the editor, build tooling, compilers.</summary>
    public static readonly IReadOnlyList<string> DevelopmentOnlyPrefixes =
        ["MNVTEngine.Editor", "MNVTEngine.Build", "MNVTEngine.Player", "MNVTEngine.CLI", "mnvt.", "Microsoft.Build", "Microsoft.CodeAnalysis"];

    /// <summary>The in-game debug UI: allowed only in games whose manifest says <see cref="GamePackage.DevTools"/>.</summary>
    public static readonly IReadOnlyList<string> DevToolsPrefixes = ["MNVTEngine.DebugUI", "ImGui", "cimgui"];

    private static readonly string[] RuntimeFiles = ["System.Private.CoreLib.dll", "coreclr.dll", "hostpolicy.dll", "Microsoft.NETCore.App.deps.json"];

    /// <param name="key">Content key; null to read it from the game's executable.</param>
    public static IReadOnlyList<BuildDiagnostic> Validate(string gameDirectory, byte[]? key = null)
    {
        var diagnostics = new List<BuildDiagnostic>();
        if (GamePackage.FindDataDirectory(gameDirectory) is not { } data)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB501", $"Not an exported game: no *{GamePackage.DataFolderSuffix}/{GamePackage.ManifestFile}.", gameDirectory));
            return diagnostics;
        }

        GamePackage package;
        try
        {
            package = GamePackage.Load(data);
        }
        catch (SerializationException ex)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB502", ex.Message, ex.SourceName));
            return diagnostics;
        }

        var framework = ValidateRuntime(gameDirectory, package, diagnostics);
        ValidateDependencies(gameDirectory, package, framework, diagnostics);
        ValidateFiles(gameDirectory, package, diagnostics);
        ValidateContent(gameDirectory, data, package, key, diagnostics);
        return diagnostics;
    }

    /// <summary>Starts the game, lets it render <paramref name="frames"/> frames and checks that it exits cleanly. Needs a GPU with OpenGL 4.5.</summary>
    public static async Task<IReadOnlyList<BuildDiagnostic>> RunAsync(string gameDirectory, int frames, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var data = GamePackage.FindDataDirectory(gameDirectory) ?? throw new DirectoryNotFoundException($"'{gameDirectory}' is not an exported game.");
        var exe = Path.Combine(gameDirectory, GamePackage.Load(data).ExecutableName + ".exe");
        var result = await DotnetCli.RunProcessAsync(exe, ["--frames", frames.ToString(System.Globalization.CultureInfo.InvariantCulture)], gameDirectory, timeout, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return [];
        }

        var reason = result.StartError ?? result.ExitCode switch
        {
            1 => "GPU resources leaked",
            2 => "the game content is missing, damaged or cannot be decrypted",
            3 => "the game's C# code could not be loaded",
            _ => $"exit code {result.ExitCode}",
        };
        var tail = string.Join('\n', result.Output.Split('\n').TakeLast(25));
        return [BuildDiagnostic.Error("MNVB520", $"Test run failed: {reason}.\n{tail}", exe)];
    }

    /// <returns>The private framework folder, or null if missing.</returns>
    private static string? ValidateRuntime(string gameDirectory, GamePackage package, List<BuildDiagnostic> diagnostics)
    {
        var name = package.ExecutableName;
        foreach (var file in new[] { name + ".exe", name + ".dll", name + ".runtimeconfig.json" })
        {
            if (!File.Exists(Path.Combine(gameDirectory, file)))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB503", $"Game host file missing: {file}", gameDirectory));
            }
        }

        JObject? config = null;
        try
        {
            config = JObject.Parse(File.ReadAllText(Path.Combine(gameDirectory, name + ".runtimeconfig.json")));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB503", $"Unreadable runtime config: {ex.Message}", name + ".runtimeconfig.json"));
        }

        var version = config?["runtimeOptions"]?["framework"]?["version"]?.Value<string>();
        if (version is null)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB504", "The runtime config names no .NET framework version.", name + ".runtimeconfig.json"));
            return null;
        }

        var runtime = Path.Combine(gameDirectory, GamePackage.RuntimeFolder);
        var framework = Path.Combine(runtime, "shared", "Microsoft.NETCore.App", version);
        var missing = RuntimeFiles.Where(f => !File.Exists(Path.Combine(framework, f))).ToList();
        var fxr = Path.Combine(runtime, "host", "fxr");
        if (!Directory.Exists(fxr) || !Directory.EnumerateFiles(fxr, "hostfxr.dll", SearchOption.AllDirectories).Any())
        {
            missing.Add("host/fxr/*/hostfxr.dll");
        }

        foreach (var file in missing)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB504", $"The private .NET {version} runtime is incomplete: {file} is missing; the game would need an installed .NET.", GamePackage.RuntimeFolder));
        }

        return Directory.Exists(framework) ? framework : null;
    }

    private static void ValidateDependencies(string gameDirectory, GamePackage package, string? framework, List<BuildDiagnostic> diagnostics)
    {
        var managed = Path.Combine(gameDirectory, GamePackage.ManagedFolder);
        var host = Path.Combine(gameDirectory, package.ExecutableName + ".dll");
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { managed, framework }.OfType<string>().Where(Directory.Exists))
        {
            available.UnionWith(Directory.EnumerateFiles(folder, "*.dll").Select(Path.GetFileNameWithoutExtension).OfType<string>());
        }

        // Without deps.json the host resolves assemblies itself, so check every reference of everything that ships.
        var assemblies = (Directory.Exists(managed) ? Directory.EnumerateFiles(managed, "*.dll") : []).Append(host).Where(File.Exists);
        foreach (var assembly in assemblies.Order(StringComparer.Ordinal))
        {
            string[] references;
            try
            {
                references = References(assembly);
            }
            catch (BadImageFormatException)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB505", "Not a valid assembly.", Path.GetRelativePath(gameDirectory, assembly).Replace('\\', '/')));
                continue;
            }

            foreach (var reference in references.Where(r => !available.Contains(r)))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB505", $"References assembly '{reference}', which the game does not ship.", Path.GetRelativePath(gameDirectory, assembly).Replace('\\', '/')));
            }
        }

        foreach (var native in RequiredNativeLibraries.GetValueOrDefault(package.Target, []).Concat(package.DevTools ? ["cimgui.dll"] : []))
        {
            if (!File.Exists(Path.Combine(gameDirectory, GamePackage.NativeFolder, native)))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB506", $"Required native library missing: {GamePackage.NativeFolder}/{native}", gameDirectory));
            }
        }

        foreach (var file in Directory.EnumerateFiles(gameDirectory, "*.dll", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            if (DevelopmentOnlyPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB507", $"Development-only assembly in the game: {fileName}", Path.GetRelativePath(gameDirectory, file)));
            }
            else if (!package.DevTools && DevToolsPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB508", $"Debug UI in a build without dev tools (Release): {fileName}", Path.GetRelativePath(gameDirectory, file)));
            }
        }
    }

    private static string[] References(string assembly)
    {
        using var pe = new PEReader(File.OpenRead(assembly));
        if (!pe.HasMetadata)
        {
            return [];
        }

        var metadata = pe.GetMetadataReader();
        return [.. metadata.AssemblyReferences.Select(h => metadata.GetString(metadata.GetAssemblyReference(h).Name))];
    }

    private static void ValidateFiles(string gameDirectory, GamePackage package, List<BuildDiagnostic> diagnostics)
    {
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in package.Files)
        {
            listed.Add(expected.Path);
            string full;
            try
            {
                full = AssetPaths.Resolve(gameDirectory, expected.Path);
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
            else if (GamePackager.Describe(gameDirectory, full) is var actual && (actual.Size != expected.Size || actual.Sha256 != expected.Sha256))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB508", "File differs from the manifest (modified or damaged).", expected.Path));
            }
        }

        var manifest = $"{GamePackage.DataFolder(package.ExecutableName)}/{GamePackage.ManifestFile}";
        foreach (var file in Directory.EnumerateFiles(gameDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(gameDirectory, file).Replace('\\', '/');
            if (!listed.Contains(relative) && !relative.Equals(manifest, StringComparison.OrdinalIgnoreCase) && !relative.StartsWith("Logs/", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(BuildDiagnostic.Warning("MNVB509", "File is not part of the build.", relative));
            }
        }
    }

    private static void ValidateContent(string gameDirectory, string data, GamePackage package, byte[]? key, List<BuildDiagnostic> diagnostics)
    {
        var archivePath = Path.Combine(data, GamePackage.ArchiveFile);
        var where = $"{GamePackage.DataFolder(package.ExecutableName)}/{GamePackage.ArchiveFile}";
        ContentArchive archive;
        try
        {
            key ??= ContentKeys.ReadFromHost(Path.Combine(gameDirectory, package.ExecutableName + ".dll"))
                ?? throw new InvalidDataException("The game executable carries no content key.");
            archive = ContentArchive.Open(archivePath, key);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or BadImageFormatException or FormatException or ArgumentException)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB514", $"Game content cannot be opened: {ex.Message}", where));
            return;
        }

        using (archive)
        {
            var damaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                try
                {
                    archive.Read(entry);
                }
                catch (InvalidDataException ex)
                {
                    damaged.Add(entry);
                    diagnostics.Add(BuildDiagnostic.Error("MNVB515", ex.Message, where));
                }
            }

            try
            {
                GameProject.LoadPackaged(archive, data);
            }
            catch (SerializationException ex)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB510", $"Packaged project does not load: {ex.Message}", where));
            }

            AssetDatabase assets;
            try
            {
                assets = AssetDatabase.OpenPackaged(archive);
            }
            catch (InvalidDataException ex)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB512", ex.Message, where));
                return;
            }

            foreach (var record in assets.Assets.Where(r => !archive.Contains(AssetDatabase.AssetsEntryFolder + r.Path)))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB512", "Asset listed in the index is not in the content.", record.Path));
            }

            foreach (var script in assets.Assets.Where(a => a.Type == AssetTypes.Script && !damaged.Contains(AssetDatabase.CompiledEntry(a.Guid))))
            {
                try
                {
                    BytecodeSerializer.Read(assets.ReadCompiled(script.Guid)!);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    diagnostics.Add(BuildDiagnostic.Error("MNVB511", $"Script bytecode missing or invalid: {ex.Message}", script.Path));
                }
            }

            if (package.ScriptsAssembly is { } scripts && !archive.Contains(scripts))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB513", $"The game's C# assembly '{scripts}' is not in the content.", where));
            }
        }
    }
}
