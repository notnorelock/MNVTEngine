using System.Globalization;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using MNVTEngine.IO.Packages;
using MNVTEngine.Runtime;

namespace MNVTEngine.Build;

/// <summary>
/// Assembles an exported game (layout: <see cref="GamePackage"/>). Publishes the engine's game host for the target with
/// its private .NET runtime in <c>Dependencies/Runtime</c>, moves engine and package assemblies to
/// <c>Dependencies/Managed</c> and native libraries to <c>Dependencies/Native</c>, adds the game's C# code to the
/// globals package, and writes the signed manifest with a hash of every file.
/// </summary>
public static class GamePackager
{
    public static TimeSpan PublishTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The devtools module (in-game debug UI): the project's switch decides; without one, Debug builds have it and Release builds do not.</summary>
    public static bool IncludesDevTools(BuildProfile profile, GameProject project) =>
        (project ?? throw new ArgumentNullException(nameof(project))).Modules.TryGetValue("devtools", out var on) ? on
        : (profile ?? throw new ArgumentNullException(nameof(profile))).Configuration != "Release";

    /// <param name="workDirectory">Per-profile build cache (<c>Library/Build/&lt;profile&gt;</c>); holds the intermediate output of the engine build.</param>
    /// <param name="devTools">Compile the debug UI into the game host (MnvtDevTools).</param>
    /// <param name="bootstrap">The game's <see cref="ModuleBootstrap"/> props (MnvtBootstrap); null builds the host with every module.</param>
    /// <param name="packageKey">The packages' encryption key as embedded (<see cref="EmbeddedPackageKey"/>); null for unencrypted packages.</param>
    /// <param name="verificationKey">The public key the game verifies its packages and manifest with.</param>
    /// <param name="version">The build's engine version (<see cref="PipelineVersion"/>) the engine assemblies are stamped with; null for a development version.</param>
    public static async Task<IReadOnlyList<BuildDiagnostic>> PublishHostAsync(
        string engineRoot, BuildProfile profile, string executableName, string? packageKey, PackageVerificationKey verificationKey, string workDirectory, string packageDirectory, bool devTools, string? bootstrap = null, PipelineVersion? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(verificationKey);
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
            $"-p:MnvtPackageKey={packageKey}",
            $"-p:MnvtVerificationKey={Convert.ToHexString(Convert.FromBase64String(verificationKey.ToBase64()))}",
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

        if (version is not null)
        {
            arguments.Add(version.MsBuildProperty);
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
    /// <summary>
    /// Publishes MNVTCrashHandler.exe into the staged game's root (after <see cref="ArrangeLayout"/>). JIT games: a
    /// framework-dependent handler on the game's private runtime, its dependencies in Dependencies/Managed. AOT games:
    /// a native, ahead-of-time compiled handler (the game has no .NET runtime).
    /// </summary>
    public static async Task<IReadOnlyList<BuildDiagnostic>> PublishCrashHandlerAsync(
        string engineRoot, BuildProfile profile, string workDirectory, string packageDirectory, bool aot, PipelineVersion? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var output = Path.Combine(workDirectory, "CrashHandler");
        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }

        List<string> arguments =
        [
            "publish", EngineLocation.CrashHandlerProject(engineRoot),
            "-c", profile.Configuration,
            "-r", profile.Target,
            "--self-contained", "false",
            "-o", output,
            "--artifacts-path", Path.Combine(workDirectory, "artifacts-crash-handler"),
            "-nologo", "-v:q", "-clp:NoSummary;ForceNoAlign",
            "-p:Deterministic=true",
            "-p:ContinuousIntegrationBuild=true",
            "-p:DebugType=none",
        ];
        if (aot)
        {
            arguments.AddRange(["-p:PublishAot=true", "-p:TreatWarningsAsErrors=false"]);
        }

        if (version is not null)
        {
            arguments.Add(version.MsBuildProperty);
        }

        var result = await DotnetCli.RunAsync(arguments, engineRoot, PublishTimeout, cancellationToken).ConfigureAwait(false);
        if (result.StartError is { } error)
        {
            return [BuildDiagnostic.Error("MNVB402", error)];
        }

        if (result.ExitCode != 0)
        {
            return [BuildDiagnostic.Error("MNVB405", $"The crash handler could not be published (exit code {result.ExitCode}).\n{result.Output.Trim()}")];
        }

        var managed = Path.Combine(packageDirectory, GamePackage.ManagedFolder);
        foreach (var file in Directory.EnumerateFiles(output))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var handlerFile = name.StartsWith("MNVTCrashHandler.", StringComparison.OrdinalIgnoreCase);
            var target = handlerFile ? Path.Combine(packageDirectory, name) : Path.Combine(managed, name);
            if (!handlerFile && File.Exists(target))
            {
                continue; // a dependency the game ships already (MNVTEngine.CrashReporting)
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }

        return [];
    }

    /// <summary>CEF in a game with the browser module: libcef.dll, its data files and locales (a folder of its own: the game loads every library directly in Dependencies/Native at startup).</summary>
    public const string ChromiumFolder = GamePackage.NativeFolder + "/Chromium";

    /// <summary>CEF's Vulkan loader and SwiftShader (software WebGL) are left out: a vulkan-1.dll next to the game could stand in for the system's Vulkan loader.</summary>
    private static readonly string[] BrowserHostExcluded = ["vulkan-1.dll", "vk_swiftshader.dll", "vk_swiftshader_icd.json", "CefSharp.BrowserSubprocess.exe", "CefSharp.BrowserSubprocess.dll", "CefSharp.BrowserSubprocess.runtimeconfig.json"];

    private static readonly string[] BrowserHostFiles = ["MNVTBrowserHost.exe", "MNVTBrowserHost.dll", ManagedPath + "CefSharp.Core.dll", ChromiumPath + "CefSharp.Core.Runtime.dll", ChromiumPath + "libcef.dll", ChromiumPath + "icudtl.dat", ChromiumPath + "resources.pak", ChromiumPath + "Ijwhost.dll"];

    private const string ManagedPath = GamePackage.ManagedFolder + "/";

    private const string ChromiumPath = ChromiumFolder + "/";

    /// <summary>
    /// Publishes MNVTBrowserHost.exe with CEF and sorts it into the staged game's layout (after <see cref="ArrangeLayout"/>):
    /// the executable next to the game's, CefSharp in Dependencies/Managed, CEF in <see cref="ChromiumFolder"/>, only
    /// the locale <paramref name="locale"/> of CEF's translations. The host runs on the game's private runtime; AOT
    /// games, which have none, get one in Dependencies/Runtime (CefSharp is C++/CLI: it needs CoreCLR). Returns the
    /// bytes the browser added with the diagnostics.
    /// </summary>
    public static async Task<(IReadOnlyList<BuildDiagnostic> Diagnostics, long Bytes)> PublishBrowserHostAsync(
        string engineRoot, BuildProfile profile, string workDirectory, string packageDirectory, string locale = "en-US", PipelineVersion? version = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var output = Path.Combine(workDirectory, "BrowserHost");
        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }

        var needsRuntime = FrameworkDirectory(packageDirectory) is null;
        List<string> arguments =
        [
            "publish", EngineLocation.BrowserHostProject(engineRoot),
            "-c", profile.Configuration,
            "-r", profile.Target,
            "--self-contained", "false",
            "-o", output,
            "--artifacts-path", Path.Combine(workDirectory, "artifacts-browser-host"),
            "-nologo", "-v:q", "-clp:NoSummary;ForceNoAlign",
            "-p:Deterministic=true",
            "-p:ContinuousIntegrationBuild=true",
            "-p:DebugType=none",
            $"-p:MnvtPrivateRuntime={(needsRuntime ? "true" : "false")}",
            .. version is null ? Array.Empty<string>() : [version.MsBuildProperty],
        ];
        var result = await DotnetCli.RunAsync(arguments, engineRoot, PublishTimeout, cancellationToken).ConfigureAwait(false);
        if (result.StartError is { } error)
        {
            return ([BuildDiagnostic.Error("MNVB402", error)], 0);
        }

        if (result.ExitCode != 0)
        {
            return ([BuildDiagnostic.Error("MNVB406", $"The browser host (MNVTBrowserHost.exe, CEF) could not be published (exit code {result.ExitCode}).\n{result.Output.Trim()}")], 0);
        }

        long bytes = 0;
        void Place(string source, string relative, bool keepExisting)
        {
            var target = Path.Combine(packageDirectory, relative);
            if (keepExisting && File.Exists(target))
            {
                return; // the game ships it already (Newtonsoft.Json, the protocol, the runtime of a JIT game)
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            bytes += new FileInfo(source).Length;
        }

        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(output, file).Replace('\\', '/');
            var name = Path.GetFileName(file);
            if (relative.StartsWith(GamePackage.RuntimeFolder + "/", StringComparison.OrdinalIgnoreCase))
            {
                Place(file, relative, keepExisting: true);
            }
            else if (BrowserHostExcluded.Contains(name, StringComparer.OrdinalIgnoreCase) || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || (relative.StartsWith("locales/", StringComparison.OrdinalIgnoreCase) && !Path.GetFileNameWithoutExtension(name).Equals(locale, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // CEF's translations are 51 MB: only the locale the game runs CEF with
            }
            else if (name.StartsWith("MNVTBrowserHost.", StringComparison.OrdinalIgnoreCase))
            {
                Place(file, name, keepExisting: false);
            }
            else if (relative.Contains('/', StringComparison.Ordinal) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !IsManaged(file) || IsMixedMode(file))
            {
                // libcef.dll, paks, data, locales/, licenses, notices; and CefSharp's C++/CLI assemblies, which need Ijwhost.dll next to them.
                Place(file, ChromiumPath + relative, keepExisting: false);
            }
            else
            {
                Place(file, ManagedPath + name, keepExisting: true);
            }
        }

        var missing = BrowserHostFiles.Where(f => !File.Exists(Path.Combine(packageDirectory, f))).ToList();
        if (missing.Count > 0)
        {
            return ([BuildDiagnostic.Error("MNVB406", $"The published browser host is missing {string.Join(", ", missing)}.")], bytes);
        }

        return ([], bytes);
    }

    /// <summary>The private runtime's shared/Microsoft.NETCore.App/&lt;version&gt; folder of a staged game; null when there is none.</summary>
    public static string? FrameworkDirectory(string packageDirectory)
    {
        var shared = Path.Combine(packageDirectory, GamePackage.RuntimeFolder, "shared", "Microsoft.NETCore.App");
        return Directory.Exists(shared) ? Directory.EnumerateDirectories(shared).SingleOrDefault() : null;
    }

    /// <summary>
    /// Removes the symbol files of an ahead-of-time build: the managed .pdb files of the compiled assemblies are never
    /// used by a native executable; Debug keeps the executable's own native .pdb.
    /// </summary>
    public static void RemoveSymbols(string packageDirectory, string executableName, bool all)
    {
        foreach (var file in Directory.EnumerateFiles(packageDirectory, "*.pdb", SearchOption.AllDirectories).ToList())
        {
            if (all || !Path.GetFileNameWithoutExtension(file).Equals(executableName, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
            }
        }
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

    /// <summary>A C++/CLI assembly: managed metadata and native code (not IL only).</summary>
    public static bool IsMixedMode(string file)
    {
        using var stream = File.OpenRead(file);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        return pe.PEHeaders.CorHeader is { } cor && (cor.Flags & System.Reflection.PortableExecutable.CorFlags.ILOnly) == 0;
    }

    public static bool IsManaged(string file)
    {
        using var pe = new PEReader(File.OpenRead(file));
        return pe.HasMetadata;
    }

    /// <summary>
    /// Adds the game's compiled C# code to the globals package under <c>Scripts/</c>. Assemblies the host already ships
    /// (engine, shared packages) are left out: the host's copies are always used at run time.
    /// </summary>
    /// <returns>Package entry of the main assembly.</returns>
    public static string AddGameCode(PackageWriter archive, string scriptsOutput, string assemblyName, string packageDirectory, bool includeSymbols, List<BuildDiagnostic> diagnostics)
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

    /// <summary>Writes <c>&lt;Game&gt;_Data/package.json</c> listing every file of the game folder with its SHA-256, and its signature.</summary>
    public static GamePackage WriteManifest(string packageDirectory, GamePackage package, PackageSigningKey signer)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(signer);
        var data = Path.Combine(packageDirectory, GamePackage.DataFolder(package.ExecutableName));
        string[] excluded = [Path.GetFullPath(Path.Combine(data, GamePackage.ManifestFile)), Path.GetFullPath(Path.Combine(data, GamePackage.SignatureFile))];
        // Packages are identified by their header hash in Packages (it covers everything but the signature, whose ECDSA
        // bytes are randomized), so the manifest of identical inputs is identical.
        package.Files = [.. Directory.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !excluded.Contains(Path.GetFullPath(f), StringComparer.OrdinalIgnoreCase) && !f.EndsWith(GamePackage.PackageExtension, StringComparison.OrdinalIgnoreCase))
            .Select(f => Describe(packageDirectory, f))];
        package.SigningKeyId = signer.PublicKey.Id;
        package.Save(data, signer);
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
