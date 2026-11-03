using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build;

/// <summary>
/// Assembles the package: publishes the engine's game host self-contained for the target (the .NET runtime, engine
/// assemblies, managed packages and their native libraries such as glfw3.dll all land next to the executable), adds the
/// game's C# code, and writes the manifest with a hash of every file.
/// </summary>
public static class GamePackager
{
    public static TimeSpan PublishTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public static string EngineVersion =>
        typeof(GameProject).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <param name="workDirectory">Per-profile build cache (<c>Library/Build/&lt;profile&gt;</c>); holds the intermediate output of the engine build.</param>
    public static async Task<IReadOnlyList<BuildDiagnostic>> PublishHostAsync(
        string engineRoot, BuildProfile profile, string executableName, string workDirectory, string packageDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var hostProject = EngineLocation.GameHostProject(engineRoot);
        if (!File.Exists(hostProject))
        {
            return [BuildDiagnostic.Error("MNVB401", $"Game host project not found: {hostProject}")];
        }

        List<string> arguments =
        [
            "publish", hostProject,
            "-c", profile.Configuration,
            "-r", profile.Target,
            "--self-contained", "true",
            "-o", packageDirectory,
            "--artifacts-path", Path.Combine(workDirectory, "artifacts"), // isolates game builds from each other and from the engine's own bin/obj
            "-nologo", "-v:q", "-clp:NoSummary;ForceNoAlign",
            "-p:GenerateFullPaths=true",
            $"-p:MnvtGameName={executableName}",
            $"-p:MnvtConsole={(profile.ShowsConsole ? "true" : "false")}",
            "-p:Deterministic=true",
            "-p:ContinuousIntegrationBuild=true",
        ];
        if (profile.Configuration == "Release")
        {
            arguments.Add("-p:DebugType=none");
        }

        var result = await DotnetCli.RunAsync(arguments, engineRoot, PublishTimeout, cancellationToken).ConfigureAwait(false);
        if (result.StartError is { } error)
        {
            return [BuildDiagnostic.Error("MNVB402", error)];
        }

        List<BuildDiagnostic> diagnostics = [.. ScriptCompiler.ParseDiagnostics(result.Output).Select(BuildDiagnostic.FromScript)];
        if (result.ExitCode != 0 && !diagnostics.HasErrors())
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB403", $"dotnet publish exited with code {result.ExitCode}.\n{result.Output.Trim()}"));
        }

        return diagnostics;
    }

    /// <summary>
    /// Copies the game's compiled C# code into <c>Content/Scripts</c>. Assemblies the host already ships (engine,
    /// shared packages) are left out: the host's copies are always used at run time.
    /// </summary>
    /// <returns>Path of the main assembly relative to Content.</returns>
    public static string CopyGameCode(string scriptsOutput, string assemblyName, string packageDirectory, bool includeSymbols, List<BuildDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var target = Path.Combine(packageDirectory, GamePackage.ContentFolder, GamePackage.ScriptsFolder);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(scriptsOutput).Order(StringComparer.Ordinal))
        {
            var extension = Path.GetExtension(file).ToUpperInvariant();
            if (extension != ".DLL" && !(includeSymbols && extension == ".PDB"))
            {
                continue;
            }

            if (File.Exists(Path.Combine(packageDirectory, Path.GetFileNameWithoutExtension(file) + ".dll")))
            {
                continue;
            }

            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        if (Directory.Exists(Path.Combine(scriptsOutput, "runtimes")))
        {
            // shortcut: game code's own native libraries are not packaged; add RID-specific copying when a game needs one.
            diagnostics.Add(BuildDiagnostic.Warning("MNVB404", "The C# scripts depend on packages with native libraries (runtimes/ folder); those are not packaged.", scriptsOutput));
        }

        return $"{GamePackage.ScriptsFolder}/{assemblyName}.dll";
    }

    /// <summary>Writes Content/package.json listing every file in the package with its SHA-256.</summary>
    public static GamePackage WriteManifest(string packageDirectory, GamePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var content = Path.Combine(packageDirectory, GamePackage.ContentFolder);
        var manifest = Path.Combine(content, GamePackage.ManifestFile);
        package.Files = [.. Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(manifest), StringComparison.OrdinalIgnoreCase))
            .Select(f => Describe(packageDirectory, f))];
        package.Save(content);
        return package;
    }

    public static PackagedFile Describe(string packageDirectory, string file)
    {
        using var stream = File.OpenRead(file);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new PackagedFile(Path.GetRelativePath(packageDirectory, file).Replace('\\', '/'), stream.Length, hash);
    }

    internal static string FormatSize(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.0} MB");
}
