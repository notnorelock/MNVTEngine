using System.Globalization;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using MNVTEngine.Assets;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build;

/// <summary>
/// Assembles an exported game (layout: <see cref="GamePackage"/>). Publishes the engine's game host for the target with
/// its private .NET runtime in <c>Dependencies/Runtime</c>, moves engine and package assemblies to
/// <c>Dependencies/Managed</c> and native libraries to <c>Dependencies/Native</c>, adds the game's C# code to the
/// encrypted content archive, and writes the manifest with a hash of every file.
/// </summary>
public static class GamePackager
{
    public static TimeSpan PublishTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public static string EngineVersion =>
        typeof(GameProject).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>The devtools module (in-game debug UI): the project's switch decides; without one, Debug builds have it and Release builds do not.</summary>
    public static bool IncludesDevTools(BuildProfile profile, GameProject project) =>
        (project ?? throw new ArgumentNullException(nameof(project))).Modules.TryGetValue("devtools", out var on) ? on
        : (profile ?? throw new ArgumentNullException(nameof(profile))).Configuration != "Release";

    /// <param name="workDirectory">Per-profile build cache (<c>Library/Build/&lt;profile&gt;</c>); holds the intermediate output of the engine build.</param>
    /// <param name="devTools">Compile the debug UI into the game host (MnvtDevTools).</param>
    /// <param name="bootstrap">The game's <see cref="ModuleBootstrap"/> props (MnvtBootstrap); null builds the host with every module.</param>
    public static async Task<IReadOnlyList<BuildDiagnostic>> PublishHostAsync(
        string engineRoot, BuildProfile profile, string executableName, byte[] contentKey, string workDirectory, string packageDirectory, bool devTools, string? bootstrap = null, CancellationToken cancellationToken = default)
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
            "-o", packageDirectory,
            "--artifacts-path", Path.Combine(workDirectory, "artifacts"), // isolates game builds from each other and from the engine's own bin/obj
            "-nologo", "-v:q", "-clp:NoSummary;ForceNoAlign",
            "-p:GenerateFullPaths=true",
            $"-p:MnvtGameName={executableName}",
            $"-p:MnvtConsole={(profile.ShowsConsole ? "true" : "false")}",
            $"-p:MnvtContentKey={Convert.ToHexString(contentKey)}",
            $"-p:MnvtDevTools={(devTools ? "true" : "false")}",
            "-p:Deterministic=true",
            "-p:ContinuousIntegrationBuild=true",
        ];
        if (profile.Configuration == "Release")
        {
            arguments.Add("-p:DebugType=none");
        }

        if (bootstrap is not null)
        {
            arguments.Add($"-p:MnvtBootstrap={bootstrap}");
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
    /// Leaves only the executable, the host assembly and its runtime config in the root: other managed assemblies (and
    /// their symbols) go to <c>Dependencies/Managed</c>, native libraries to <c>Dependencies/Native</c>. The deps.json is
    /// removed: the host resolves these folders itself (see MNVTEngine.GameHost's HostLayout).
    /// </summary>
    /// <summary>The private runtime's shared/Microsoft.NETCore.App/&lt;version&gt; folder of a staged game; null when there is none.</summary>
    public static string? FrameworkDirectory(string packageDirectory)
    {
        var shared = Path.Combine(packageDirectory, GamePackage.RuntimeFolder, "shared", "Microsoft.NETCore.App");
        return Directory.Exists(shared) ? Directory.EnumerateDirectories(shared).SingleOrDefault() : null;
    }

    public static void ArrangeLayout(string packageDirectory, string executableName)
    {
        var managed = Path.Combine(packageDirectory, GamePackage.ManagedFolder);
        var native = Path.Combine(packageDirectory, GamePackage.NativeFolder);
        Directory.CreateDirectory(managed);
        Directory.CreateDirectory(native);
        File.Delete(Path.Combine(packageDirectory, executableName + ".deps.json"));
        foreach (var file in Directory.GetFiles(packageDirectory, "*.dll"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Equals(executableName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = IsManaged(file) ? managed : native;
            File.Move(file, Path.Combine(target, Path.GetFileName(file)));
            var symbols = Path.ChangeExtension(file, ".pdb");
            if (File.Exists(symbols))
            {
                File.Move(symbols, Path.Combine(target, Path.GetFileName(symbols)));
            }
        }
    }

    public static bool IsManaged(string file)
    {
        using var pe = new PEReader(File.OpenRead(file));
        return pe.HasMetadata;
    }

    /// <summary>
    /// Adds the game's compiled C# code to the content archive under <c>Scripts/</c>. Assemblies the host already ships
    /// (engine, shared packages) are left out: the host's copies are always used at run time.
    /// </summary>
    /// <returns>Archive entry of the main assembly.</returns>
    public static string AddGameCode(ContentArchive.Writer archive, string scriptsOutput, string assemblyName, string packageDirectory, bool includeSymbols, List<BuildDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var shipped = Path.Combine(packageDirectory, GamePackage.ManagedFolder);
        foreach (var file in Directory.EnumerateFiles(scriptsOutput).Order(StringComparer.Ordinal))
        {
            var extension = Path.GetExtension(file).ToUpperInvariant();
            if ((extension != ".DLL" && !(includeSymbols && extension == ".PDB"))
                || File.Exists(Path.Combine(shipped, Path.GetFileNameWithoutExtension(file) + ".dll")))
            {
                continue;
            }

            archive.Add(GamePackage.ScriptsEntryFolder + Path.GetFileName(file), File.ReadAllBytes(file));
        }

        if (Directory.Exists(Path.Combine(scriptsOutput, "runtimes")))
        {
            // shortcut: game code's own native libraries are not packaged; add them to Dependencies/Native when a game needs one.
            diagnostics.Add(BuildDiagnostic.Warning("MNVB404", "The C# scripts depend on packages with native libraries (runtimes/ folder); those are not packaged.", scriptsOutput));
        }

        return GamePackage.ScriptsEntryFolder + assemblyName + ".dll";
    }

    /// <summary>Writes <c>&lt;Game&gt;_Data/package.json</c> listing every file of the game folder with its SHA-256.</summary>
    public static GamePackage WriteManifest(string packageDirectory, GamePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var data = Path.Combine(packageDirectory, GamePackage.DataFolder(package.ExecutableName));
        var manifest = Path.Combine(data, GamePackage.ManifestFile);
        package.Files = [.. Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(manifest), StringComparison.OrdinalIgnoreCase))
            .Select(f => Describe(packageDirectory, f))];
        package.Save(data);
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
