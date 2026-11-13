using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MNVTEngine.Assets;
using MNVTEngine.IO.Packages;
using MNVTEngine.Runtime;
using MNVTEngine.Scripting.Bytecode;
using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Build;

/// <summary>
/// Checks an exported game without running it: private .NET runtime, every assembly reference resolvable from the
/// runtime or <c>Dependencies/Managed</c>, required native libraries, no development tooling, every file matching the
/// manifest hash, a manifest signed by the key the executable trusts, and packages that verify (signature, identity,
/// every chunk), decrypt and contain no private keys, with a loadable runtime configuration and asset index
/// and verified bytecode for every script. <see cref="RunAsync"/> additionally launches the game.
/// </summary>
public static class BuildValidator
{
    /// <summary>
    /// Native libraries of every module, per target: what builds without a module list (<see cref="GamePackage.Modules"/>)
    /// must ship. Newer builds need the libraries of their modules only (<see cref="RequiredNatives"/>). Vulkan itself
    /// (vulkan-1.dll) comes from the GPU driver.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> RequiredNativeLibraries = new Dictionary<string, string[]>
    {
        ["win-x64"] = ["glfw3.dll", "soft_oal.dll", "vma.dll"],
    };

    /// <summary>The native libraries a game must ship: those of its modules, plus Dear ImGui with dev tools.</summary>
    public static IReadOnlyList<string> RequiredNatives(GamePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        IEnumerable<string> natives = package.Modules is { } modules
            ? modules.Where(id => Modules.ModuleCatalog.Builtin.TryGet(id, out _)).SelectMany(id => Modules.ModuleCatalog.Builtin[id].NativeLibraries)
            : RequiredNativeLibraries.GetValueOrDefault(package.Target, []);
        return [.. natives.Concat(package.DevTools ? ["cimgui.dll"] : []).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Assembly name prefixes that must never ship: the editor, build tooling, compilers.</summary>
    public static readonly IReadOnlyList<string> DevelopmentOnlyPrefixes =
        ["MNVTEngine.Editor", "MNVTEngine.Build", "MNVTEngine.Player", "MNVTEngine.CLI", "mnvt.", "Microsoft.Build", "Microsoft.CodeAnalysis",
         "MNVTEngine.Graphics.Vulkan.ShaderCompiler", "Silk.NET.Shaderc", "shaderc_shared"]; // games run on precompiled SPIR-V

    /// <summary>The in-game debug UI: allowed only in games whose manifest says <see cref="GamePackage.DevTools"/>.</summary>
    public static readonly IReadOnlyList<string> DevToolsPrefixes = ["MNVTEngine.DebugUI", "ImGui", "cimgui"];

    private static readonly string[] RuntimeFiles = ["System.Private.CoreLib.dll", "coreclr.dll", "hostpolicy.dll", "Microsoft.NETCore.App.deps.json"];

    /// <param name="keys">Package encryption keys; null to read the key from the game's executable.</param>
    public static IReadOnlyList<BuildDiagnostic> Validate(string gameDirectory, PackageKeyRing? keys = null)
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

        // An ahead-of-time game is one native executable: no runtime, no host assembly, no managed dependencies.
        var framework = package.ScriptingBackend == "aot" ? ValidateNativeHost(gameDirectory, package, diagnostics) : ValidateRuntime(gameDirectory, package, diagnostics);
        ValidateDependencies(gameDirectory, package, framework, diagnostics);
        ValidateFiles(gameDirectory, package, diagnostics);
        ValidateContent(gameDirectory, data, package, keys, diagnostics);
        return diagnostics;
    }

    /// <summary>Starts the game, lets it render <paramref name="frames"/> frames and checks that it exits cleanly. Needs a GPU with Vulkan 1.4.</summary>
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
    private static string? ValidateNativeHost(string gameDirectory, GamePackage package, List<BuildDiagnostic> diagnostics)
    {
        if (!File.Exists(Path.Combine(gameDirectory, package.ExecutableName + ".exe")))
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB503", $"Game host file missing: {package.ExecutableName}.exe", gameDirectory));
        }

        // The browser host (CefSharp is C++/CLI) runs on CoreCLR: AOT games with the browser module ship a runtime for it.
        var browser = package.Modules?.Contains("browser") == true;
        string[] forbidden = browser ? [package.ExecutableName + ".dll"] : [package.ExecutableName + ".dll", GamePackage.RuntimeFolder, GamePackage.ManagedFolder];
        foreach (var file in forbidden.Where(f => Path.Exists(Path.Combine(gameDirectory, f)) && !(Directory.Exists(Path.Combine(gameDirectory, f)) && !Directory.EnumerateFileSystemEntries(Path.Combine(gameDirectory, f)).Any())))
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB503", $"An ahead-of-time game ships no IL host or .NET runtime, but has {file}.", gameDirectory));
        }

        if (!browser)
        {
            return null;
        }

        var framework = GamePackager.FrameworkDirectory(gameDirectory);
        if (framework is null || !File.Exists(Path.Combine(gameDirectory, "MNVTBrowserHost.exe")))
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB406", "The browser module needs MNVTBrowserHost.exe and a .NET runtime in Dependencies/Runtime; the package has not both.", gameDirectory));
        }

        return framework;
    }

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

        if (Directory.Exists(framework))
        {
            foreach (var entry in FrameworkPruner.MissingDeps(framework))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB504", $"The runtime's {FrameworkPruner.DepsFile} lists {entry}, which is missing; the game would not start.", GamePackage.RuntimeFolder));
            }
        }

        return Directory.Exists(framework) ? framework : null;
    }

    /// <summary>
    /// References that are never resolved at run time, so the game need not ship them. Only with a reason: CefSharp's
    /// off-screen browser returns screenshots as System.Drawing Bitmaps, an API MNVTBrowserHost does not call (it
    /// takes the pixels from paint events); the assembly loads only when such a method runs.
    /// </summary>
    private static readonly HashSet<(string Assembly, string Reference)> NeverLoaded = [("CefSharp.OffScreen", "System.Drawing.Common")];

    private static void ValidateDependencies(string gameDirectory, GamePackage package, string? framework, List<BuildDiagnostic> diagnostics)
    {
        var managed = Path.Combine(gameDirectory, GamePackage.ManagedFolder);
        var host = Path.Combine(gameDirectory, package.ExecutableName + ".dll");
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The browser's C++/CLI assemblies ship with CEF (GamePackager.ChromiumFolder), where MNVTBrowserHost resolves them.
        var chromium = Path.Combine(gameDirectory, GamePackager.ChromiumFolder);
        foreach (var folder in new[] { managed, framework, chromium }.OfType<string>().Where(Directory.Exists))
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

            var name = Path.GetFileNameWithoutExtension(assembly);
            foreach (var reference in references.Where(r => !available.Contains(r) && !NeverLoaded.Contains((name, r))))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB505", $"References assembly '{reference}', which the game does not ship.", Path.GetRelativePath(gameDirectory, assembly).Replace('\\', '/')));
            }
        }

        foreach (var native in RequiredNatives(package))
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

        ValidateModuleAssemblies(gameDirectory, package, managed, diagnostics);
    }

    /// <summary>
    /// MNVB517: an assembly or native library of a module the game does not have ships anyway. With dev tools, what the
    /// debug UI references is allowed (its known couplings: it reads physics, audio and UI state).
    /// </summary>
    private static void ValidateModuleAssemblies(string gameDirectory, GamePackage package, string managed, List<BuildDiagnostic> diagnostics)
    {
        if (package.Modules is null)
        {
            return;
        }

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(package.DevTools ? ["MNVTEngine.DebugUI"] : []);
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            var file = Path.Combine(managed, name + ".dll");
            if (allowed.Add(name) && File.Exists(file))
            {
                foreach (var reference in References(file))
                {
                    pending.Push(reference);
                }
            }
        }

        // A module the debug UI pulls in (any of its assemblies is referenced) ships whole with dev tools.
        foreach (var module in Modules.ModuleCatalog.Builtin.Modules.Where(m => !package.Modules.Contains(m.Id) && !m.RuntimeAssemblies.Any(allowed.Contains)))
        {
            var files = module.RuntimeAssemblies.Select(a => Path.Combine(GamePackage.ManagedFolder, a + ".dll"))
                .Concat(module.NativeLibraries.Select(n => Path.Combine(GamePackage.NativeFolder, n)));
            foreach (var file in files.Where(f => File.Exists(Path.Combine(gameDirectory, f))))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB517", $"Module '{module.Id}' is not part of the game, but its file ships: {file.Replace('\\', '/')}", file.Replace('\\', '/')));
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

        var data = GamePackage.DataFolder(package.ExecutableName);
        string[] manifest = [$"{data}/{GamePackage.ManifestFile}", $"{data}/{GamePackage.SignatureFile}", .. package.PackageFiles.Select(p => $"{data}/{p}")]; // packages: checked by identity
        foreach (var file in Directory.EnumerateFiles(gameDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(gameDirectory, file).Replace('\\', '/');
            if (!listed.Contains(relative) && !manifest.Contains(relative, StringComparer.OrdinalIgnoreCase) && !relative.StartsWith("Logs/", StringComparison.OrdinalIgnoreCase)
                && !relative.StartsWith($"{data}/{GameContent.DlcFolder}/", StringComparison.OrdinalIgnoreCase) && !relative.StartsWith(GameContent.ModsFolder + "/", StringComparison.OrdinalIgnoreCase)) // extra content, added after the build
            {
                diagnostics.Add(BuildDiagnostic.Warning("MNVB509", "File is not part of the build.", relative));
            }
        }
    }

    private static readonly byte[] PrivateKeyMarker = "PRIVATE KEY-----"u8.ToArray();

    /// <param name="keys">Encryption keys; null reads the key embedded in the game's host assembly.</param>
    private static void ValidateContent(string gameDirectory, string data, GamePackage package, PackageKeyRing? keys, List<BuildDiagnostic> diagnostics)
    {
        var dataFolder = GamePackage.DataFolder(package.ExecutableName);
        var where = $"{dataFolder}/{string.Join(", ", package.PackageFiles)}";
        var host = Path.Combine(gameDirectory, package.ExecutableName + ".dll");
        PackageVerificationKey? signer = null;
        try
        {
            if (package.ScriptingBackend != "aot")
            {
                signer = ContentKeys.ReadVerificationKeyFromHost(host);
                if (signer is null)
                {
                    diagnostics.Add(BuildDiagnostic.Error("MNVB519", "The game executable carries no package verification key; it would refuse to start.", package.ExecutableName + ".dll"));
                }

                keys ??= ContentKeys.ReadFromHost(host) is { } embedded ? new PackageKeyRing(embedded) : new PackageKeyRing();
            }
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or FormatException or ArgumentException)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB514", $"The game executable's keys cannot be read: {ex.Message}", package.ExecutableName + ".dll"));
            return;
        }

        if (signer is not null && !GamePackage.VerifySignature(data, File.ReadAllBytes(Path.Combine(data, GamePackage.ManifestFile)), signer))
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB519", $"The build manifest's signature ({GamePackage.SignatureFile}) is missing or invalid; the game would refuse to start.", $"{dataFolder}/{GamePackage.ManifestFile}"));
        }
        else if (signer is null && package.ScriptingBackend == "aot")
        {
            // The public key is compiled into the native executable; packages are checked against whichever key signed them.
            diagnostics.Add(new BuildDiagnostic(BuildSeverity.Info, "MNVB519","Signatures checked for consistency only: an ahead-of-time game's verification key is compiled into its executable; run the game (--run) for the full check.", where));
        }

        if (keys is null)
        {
            // The key is compiled into the native executable, where it cannot be read back; mnvt build passes it.
            diagnostics.Add(BuildDiagnostic.Warning("MNVB514", "Content not checked: an ahead-of-time game's key is compiled into its executable. Validate with the project's key, or run the game (--run).", where));
            return;
        }

        GameContent content;
        try
        {
            content = package.OpenContent(data, new PackageOpenOptions(keys, signer is null ? null : [signer], RequireSignature: signer is not null));
        }
        catch (IOException ex)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB514", $"Game content cannot be opened: {ex.Message}", where));
            return;
        }

        using (content)
        {
            var damaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in content.Entries)
            {
                try
                {
                    if (content.Read(entry).AsSpan().IndexOf(PrivateKeyMarker) >= 0)
                    {
                        diagnostics.Add(BuildDiagnostic.Error("MNVB518", "The game content contains a private key; signing and other private keys must never ship.", $"{content.PackageOf(entry)}: {entry}"));
                    }
                }
                catch (IOException ex)
                {
                    damaged.Add(entry);
                    diagnostics.Add(BuildDiagnostic.Error("MNVB515", ex.Message, $"{dataFolder}/{content.PackageOf(entry)}"));
                }
            }

            ValidateLayout(package, content, dataFolder, diagnostics);

            try
            {
                GameProject.LoadPackaged(content);
            }
            catch (SerializationException ex)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB510", $"Runtime configuration does not load: {ex.Message}", where));
            }

            AssetDatabase assets;
            try
            {
                assets = AssetDatabase.OpenPackaged(content.Files);
            }
            catch (InvalidDataException ex)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB512", ex.Message, where));
                return;
            }

            // Models ship as their imported artifact, not as the source file.
            foreach (var record in assets.Assets.Where(r => !content.Contains(r.Type == AssetTypes.Model
                ? AssetDatabase.ArtifactEntry(r.Guid, Assets.Models.ModelArtifact.Name)
                : AssetDatabase.AssetsEntryFolder + r.Path)))
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

            if (package.ScriptsAssembly is { } scripts && !content.Contains(scripts))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB513", $"The game's C# assembly '{scripts}' is not in the content.", where));
            }
        }
    }

    /// <summary>
    /// Split content (MNVB516): every listed package exists with the kind it claims; globals hold the runtime
    /// configuration and index; each level holds its scene; and the index's package of every asset is where the asset really is.
    /// </summary>
    private static void ValidateLayout(GamePackage package, GameContent content, string dataFolder, List<BuildDiagnostic> diagnostics)
    {
        if (package.Packages.Count(a => a.Kind == ContentPackageKinds.Globals) != 1 || package.Packages[0].File != GamePackage.GlobalsPackage)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB516", $"The first package must be the only globals package, {GamePackage.GlobalsPackage}.", dataFolder));
        }

        foreach (var entry in new[] { AssetDatabase.IndexEntry, GameProject.RuntimeConfigEntry })
        {
            if (content.PackageOf(entry) != GamePackage.GlobalsPackage)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB516", $"'{entry}' must be in {GamePackage.GlobalsPackage}.", dataFolder));
            }
        }

        JObject index;
        try
        {
            index = JObject.Parse(System.Text.Encoding.UTF8.GetString(content.Read(AssetDatabase.IndexEntry)));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return; // reported by the asset index check
        }

        var items = index["assets"]?.OfType<JObject>().ToList() ?? [];
        foreach (var item in items)
        {
            var path = item.Value<string>("path");
            var expected = item.Value<string>("package");
            if (path is not null && expected is not null && content.PackageOf(AssetDatabase.AssetsEntryFolder + path) is { } actual && actual != expected)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB516", $"The asset index places this asset in {expected}, but it is in {actual}.", path));
            }
        }

        // Each level holds exactly one scene (its own); scene names are only in the encrypted index, never in the manifest.
        foreach (var level in package.Packages.Where(a => a.Kind == ContentPackageKinds.Level))
        {
            if (items.Count(i => i.Value<string>("type") == AssetTypes.Scene && i.Value<string>("package") == level.File) != 1)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB516", "A level package must hold exactly one scene.", $"{dataFolder}/{level.File}"));
            }
        }
    }
}
