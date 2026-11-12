using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.GameAssemblies;
using MNVTEngine.Modules;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Build;

/// <summary>Command-line choices layered over a project's build profile. Null means "use the profile's value".</summary>
public sealed record BuildOptions
{
    public string? Profile { get; init; }

    /// <summary>"debug" or "release" (any case).</summary>
    public string? Configuration { get; init; }

    public string? Target { get; init; }

    /// <summary>Absolute, or relative to the current directory.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>"jit" or "aot" (see <see cref="BuildProfile.ScriptingBackends"/>); null for the profile's.</summary>
    public string? ScriptingBackend { get; init; }

    /// <summary>Launch the exported game for this many frames after packaging; null to skip (needs a GPU).</summary>
    public int? TestRunFrames { get; init; }
}

public sealed record BuildResult(bool Succeeded, BuildProfile? Profile, string? OutputDirectory, string? Executable, IReadOnlyList<BuildDiagnostic> Diagnostics, TimeSpan Duration);

/// <summary>
/// Build orchestration for <c>mnvt build</c> and <c>mnvt validate</c>: resolves the profile, compiles C# and
/// <c>.mnvts</c> scripts, compiles content, publishes the host, validates the package in a staging folder and only then
/// replaces the output folder, so a failed build never leaves a half-written game behind.
/// </summary>
public sealed partial class GameBuilder(ILogger logger)
{
    public async Task<BuildResult> BuildAsync(string projectPath, BuildOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var watch = Stopwatch.StartNew();
        var diagnostics = new List<BuildDiagnostic>();
        BuildResult Fail(BuildProfile? profile = null) => new(false, profile, null, null, diagnostics, watch.Elapsed);

        if (LoadProject(projectPath, diagnostics) is not { } project)
        {
            return Fail();
        }

        var profile = ResolveProfile(project, options, diagnostics);
        if (profile is null)
        {
            return Fail();
        }

        var executableName = profile.ExecutableName ?? GamePackage.ExecutableNameFor(project.Name);
        var output = ResolveOutput(project, profile, options, diagnostics);
        ScriptProject? scripts = null;
        try
        {
            scripts = ScriptProject.For(project);
        }
        catch (FileNotFoundException ex)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB301", ex.Message, project.ProjectFile));
        }

        if (scripts is not null && string.Equals(scripts.AssemblyName, executableName, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB105", $"The executable and the C# scripts assembly are both named '{executableName}'; set executableName in the build profile.", project.ProjectFile));
        }

        string engineRoot = "";
        try
        {
            engineRoot = EngineLocation.FindRoot();
        }
        catch (DirectoryNotFoundException ex)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB106", ex.Message));
        }

        var key = ContentKeys.LoadOrCreate(project, diagnostics);
        if (output is null || key is null || diagnostics.HasErrors())
        {
            return Fail(profile);
        }

        var work = Path.Combine(project.RootDirectory, "Library", "Build", profile.Name);
        var stage = Path.Combine(work, "Package");
        DeleteDirectory(stage);
        Directory.CreateDirectory(stage);
        LogBuilding(logger, project.Name, profile.Name, profile.Configuration, profile.Target);

        diagnostics.AddRange(ContentCompiler.CreateMissingMetas(project));
        diagnostics.AddRange(ContentCompiler.ImportModels(project));

        // 1. Game code, compiled with the profile's configuration, also used to load scenes for validation.
        var registry = EngineModuleSet.CreateRegistry();
        GameCode? code = null;
        string? scriptsOutput = null;
        string? scriptsAssemblyPath = null;
        if (scripts is not null)
        {
            scriptsOutput = Path.Combine(work, "Scripts");
            DeleteDirectory(scriptsOutput);
            LogCompilingScripts(logger, scripts.AssemblyName, profile.Configuration);
            var build = await ScriptCompiler.BuildAsync(scripts, profile.Configuration, scriptsOutput, cancellationToken).ConfigureAwait(false);
            scriptsAssemblyPath = build.AssemblyPath;
            diagnostics.AddRange(build.Diagnostics.Select(BuildDiagnostic.FromScript));
            if (!build.Succeeded || (code = DevelopmentScripts.LoadAndRegister(build.AssemblyPath!, registry, diagnostics)) is null || diagnostics.HasErrors())
            {
                return Fail(profile);
            }
        }

        // 2. Content, encrypted into archives: globals (index, project, game code), default resources (precompiled
        //    shaders), one archive per level, shared assets and resources. See ContentLayout.
        var contentDirectory = Path.Combine(work, "Content");
        DeleteDirectory(contentDirectory);
        Directory.CreateDirectory(contentDirectory);
        string? scriptsAssembly = null;
        List<string> modules;
        long? browserHostBytes = null;
        var aot = profile.ScriptingBackend == "aot";
        string? aotSource = null;
        ContentResult? compiled = null;
        FrameworkPruning? pruning = null;
        var archive = new ContentArchiveWriter(contentDirectory, key);
        using (archive)
        {
            ContentResult content;
            try
            {
                ApplyDevToolsDefault(project, profile);
                content = ContentCompiler.Compile(project, registry, archive, scriptsAssemblyPath);
                compiled = content;
                if (aot && content.Modules is { } resolved)
                {
                    // Written while the game's code is loaded: its component types and their serialized members.
                    var roots = AotBootstrap.Roots(resolved, scripts?.AssemblyName);
                    aotSource = AotBootstrap.Source(code?.ComponentTypes ?? [], registry.Types.Values.Distinct().Where(t => roots.Contains(t.Assembly.GetName().Name!)));
                }
            }
            finally
            {
                if (code is not null)
                {
                    DevelopmentScripts.Unload(code, registry);
                }
            }

            diagnostics.AddRange(content.Diagnostics);
            LogContent(logger, content.AssetCount, content.ScriptCount, content.Shaders);
            LogUsage(logger, content);
            if (diagnostics.HasErrors())
            {
                return Fail(profile);
            }

            // 3. Host with its private runtime; dependencies into Dependencies/Managed and Dependencies/Native.
            LogPublishing(logger, executableName, profile.Target);
            // The host is built with the game's modules only (generated bootstrap: references and explicit creation).
            var bootstrap = ModuleBootstrap.Write(content.Modules!, Path.Combine(work, "Bootstrap"),
                aot ? new AotInput(aotSource!, scriptsAssemblyPath, AotBootstrap.Roots(content.Modules!, scripts?.AssemblyName)) : null);
            modules = [.. content.Modules!.Modules.Select(m => m.Id)];
            diagnostics.AddRange(await GamePackager.PublishHostAsync(engineRoot, profile, executableName, key, work, stage, modules.Contains("devtools"), bootstrap, cancellationToken).ConfigureAwait(false));
            if (diagnostics.HasErrors())
            {
                return Fail(profile);
            }

            GamePackager.ArrangeLayout(stage, executableName);
            if (aot)
            {
                GamePackager.RemoveSymbols(stage, executableName, profile.Configuration == "Release");
            }
            else if (scripts is not null)
            {
                scriptsAssembly = GamePackager.AddGameCode(archive.Globals, scriptsOutput!, scripts.AssemblyName, stage, profile.Configuration == "Debug", diagnostics);
            }

            diagnostics.AddRange(await GamePackager.PublishCrashHandlerAsync(engineRoot, profile, work, stage, aot, cancellationToken).ConfigureAwait(false));
            if (modules.Contains("browser"))
            {
                var (browserDiagnostics, browserBytes) = await GamePackager.PublishBrowserHostAsync(engineRoot, profile, work, stage, cancellationToken: cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(browserDiagnostics);
                browserHostBytes = browserBytes;
            }

            if (diagnostics.HasErrors())
            {
                return Fail(profile);
            }

            if (profile.PruneFramework && GamePackager.FrameworkDirectory(stage) is { } framework)
            {
                // Roots: the host, its dependencies and the game's C# code (which the host loads from the archive).
                var roots = Directory.EnumerateFiles(stage, "*.dll")
                    .Concat(Directory.EnumerateFiles(Path.Combine(stage, GamePackage.ManagedFolder), "*.dll"))
                    .Concat(scriptsOutput is not null && Directory.Exists(scriptsOutput) ? Directory.EnumerateFiles(scriptsOutput, "*.dll") : []);
                var pruned = pruning = FrameworkPruner.Prune(framework, roots);
                var prunedSize = GamePackager.FormatSize(pruned.RemovedBytes);
                LogPruned(logger, pruned.Removed.Count, prunedSize, pruned.Kept);
            }
        }

        var data = Path.Combine(stage, GamePackage.DataFolder(executableName));
        Directory.CreateDirectory(data);
        foreach (var file in archive.Files)
        {
            File.Move(file, Path.Combine(data, Path.GetFileName(file)));
        }

        var archives = archive.Archives;
        var archiveList = string.Join(", ", archives.Select(a => $"{a.File} ({a.Entries})"));
        LogArchives(logger, archives.Count, archiveList);
        GamePackager.WriteManifest(stage, new GamePackage
        {
            Name = project.Name,
            ExecutableName = executableName,
            Profile = profile.Name,
            Configuration = profile.Configuration,
            DevTools = modules.Contains("devtools"),
            Modules = modules,
            ScriptingBackend = profile.ScriptingBackend,
            Target = profile.Target,
            EngineVersion = GamePackager.EngineVersion,
            ScriptsAssembly = scriptsAssembly,
            Archives = archives,
        });

        // 4. Validate before touching the output folder.
        LogValidating(logger);
        diagnostics.AddRange(BuildValidator.Validate(stage, key));
        if (diagnostics.HasErrors())
        {
            return Fail(profile);
        }

        try
        {
            ReplaceOutput(stage, output);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB409", $"Could not replace the previous build (is the game running, or a window or terminal open in that folder?): {ex.Message}", output));
            return Fail(profile);
        }

        var size = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        var formattedSize = GamePackager.FormatSize(size);
        LogPackaged(logger, formattedSize, output);
        var reportPath = BuildReport.PathFor(output);
        BuildReport.Write(BuildReport.Create(new BuildReportInput(project, profile, compiled!, modules.Contains("devtools"), diagnostics) { PackageDirectory = output, Pruning = pruning, ScriptingBackend = profile.ScriptingBackend, BrowserHostBytes = browserHostBytes }), reportPath);
        LogReport(logger, reportPath);

        if (options.TestRunFrames is { } frames)
        {
            LogTestRun(logger, frames);
            diagnostics.AddRange(await BuildValidator.RunAsync(output, frames, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false));
        }

        return new BuildResult(!diagnostics.HasErrors(), profile, output, Path.Combine(output, executableName + ".exe"), diagnostics, watch.Elapsed);
    }

    /// <summary>
    /// What a build of <paramref name="options"/>' profile would contain and why, without packaging: C# scripts compiled,
    /// content analysed and modules resolved exactly as <see cref="BuildAsync"/> does (<c>mnvt build analyze</c>).
    /// </summary>
    /// <returns>The report (see <see cref="BuildReport"/>; no sizes), or null when the project does not get that far.</returns>
    public async Task<(Newtonsoft.Json.Linq.JObject? Report, IReadOnlyList<BuildDiagnostic> Diagnostics)> AnalyzeAsync(string projectPath, BuildOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var diagnostics = new List<BuildDiagnostic>();
        if (LoadProject(projectPath, diagnostics) is not { } project || ResolveProfile(project, options, diagnostics) is not { } profile)
        {
            return (null, diagnostics);
        }

        diagnostics.AddRange(ContentCompiler.ImportModels(project));
        var registry = EngineModuleSet.CreateRegistry();
        var code = await DevelopmentScripts.LoadAsync(project, registry, file => LogCompilingScripts(logger, file, "Debug"), cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(code.Diagnostics);
        if (!code.Succeeded)
        {
            return (null, diagnostics);
        }

        try
        {
            ApplyDevToolsDefault(project, profile);
            var content = ContentCompiler.Compile(project, registry, output: null, code.Code?.MainAssembly.Location is { Length: > 0 } location ? location : null);
            diagnostics.AddRange(content.Diagnostics);
            return content.Modules is null ? (null, diagnostics) : (BuildReport.Create(new BuildReportInput(project, profile, content, content.Modules.IsEnabled("devtools"), diagnostics) { ScriptingBackend = profile.ScriptingBackend }), diagnostics);
        }
        finally
        {
            if (code.Code is not null)
            {
                DevelopmentScripts.Unload(code.Code, registry);
            }
        }
    }

    /// <summary>devtools is a module like the others; without a project switch, Debug builds switch it on.</summary>
    private static void ApplyDevToolsDefault(GameProject project, BuildProfile profile)
    {
        if (GamePackager.IncludesDevTools(profile, project))
        {
            project.Modules["devtools"] = true;
        }
    }

    /// <summary>Everything a build checks, without producing output: project, profiles, C# scripts, assets, .mnvts scripts, scenes.</summary>
    public async Task<IReadOnlyList<BuildDiagnostic>> ValidateProjectAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<BuildDiagnostic>();
        if (LoadProject(projectPath, diagnostics) is not { } project)
        {
            return diagnostics;
        }

        diagnostics.AddRange(ContentCompiler.CreateMissingMetas(project));
        diagnostics.AddRange(ContentCompiler.ImportModels(project));
        var registry = EngineModuleSet.CreateRegistry();
        var code = await DevelopmentScripts.LoadAsync(project, registry, file => LogCompilingScripts(logger, file, "Debug"), cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(code.Diagnostics);
        if (!code.Succeeded)
        {
            return diagnostics;
        }

        try
        {
            var content = ContentCompiler.Compile(project, registry, output: null, code.Code?.MainAssembly.Location is { Length: > 0 } location ? location : null);
            diagnostics.AddRange(content.Diagnostics);
            LogContent(logger, content.AssetCount, content.ScriptCount, content.Shaders);
            LogUsage(logger, content);
        }
        finally
        {
            if (code.Code is not null)
            {
                DevelopmentScripts.Unload(code.Code, registry);
            }
        }

        return diagnostics;
    }

    /// <summary>The named profile, else the first with the requested configuration, else "release"; then command-line overrides.</summary>
    public static BuildProfile? ResolveProfile(GameProject project, BuildOptions options, List<BuildDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var profiles = project.EffectiveBuildProfiles;
        string? configuration = null;
        if (options.Configuration is { } requested)
        {
            configuration = BuildProfile.Configurations.FirstOrDefault(c => c.Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (configuration is null)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB102", $"Unknown configuration '{requested}'; use {string.Join(" or ", BuildProfile.Configurations)}."));
                return null;
            }
        }

        BuildProfile? profile;
        if (options.Profile is { } name)
        {
            profile = profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB101", $"No build profile '{name}'; the project has: {string.Join(", ", profiles.Select(p => p.Name))}.", project.ProjectFile));
                return null;
            }
        }
        else
        {
            profile = configuration is not null
                ? profiles.FirstOrDefault(p => p.Configuration == configuration) ?? new BuildProfile { Name = configuration.ToLowerInvariant(), Configuration = configuration }
                : profiles.FirstOrDefault(p => p.Name.Equals("release", StringComparison.OrdinalIgnoreCase)) ?? profiles[0];
        }

        profile = profile.Clone();
        if (configuration is not null)
        {
            profile.Configuration = configuration;
        }

        if (options.ScriptingBackend is { } backend)
        {
            profile.ScriptingBackend = backend.ToLowerInvariant();
        }

        if (!BuildProfile.ScriptingBackends.Contains(profile.ScriptingBackend))
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB111", profile.ScriptingBackend == "il2cpp"
                ? "There is no il2cpp backend: MNVTEngine does not translate IL to C++ (Unity's IL2CPP is part of the Unity editor and cannot be reused). Use \"aot\" for a native executable; see docs/architecture/il2cpp-aot-pipeline.md."
                : $"Unknown scripting backend '{profile.ScriptingBackend}'; use {string.Join(" or ", BuildProfile.ScriptingBackends)}."));
            return null;
        }

        if (options.Target is { } target)
        {
            if (!BuildProfile.SupportedTargets.Contains(target))
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB103", $"Target '{target}' is not supported; supported: {string.Join(", ", BuildProfile.SupportedTargets)}."));
                return null;
            }

            profile.Target = target;
        }

        return profile;
    }

    private static GameProject? LoadProject(string projectPath, List<BuildDiagnostic> diagnostics)
    {
        try
        {
            return WithProfile(GameProject.Load(projectPath), diagnostics);
        }
        catch (SerializationException ex)
        {
            diagnostics.AddRange(ex.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => BuildDiagnostic.FromSerialization("MNVB100", d, ex.SourceName)));
            if (!diagnostics.HasErrors())
            {
                diagnostics.Add(BuildDiagnostic.Error("MNVB100", ex.Message, ex.SourceName));
            }
        }
        catch (FileNotFoundException ex)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB100", ex.Message, projectPath));
        }

        return null;
    }

    /// <summary>
    /// A project from before profiles builds with its inferred profile (MNVB110, without changing the project file);
    /// mixed 2D and 3D content stops the build (MNVB109) until the profile is chosen. Then the project's modules are
    /// resolved; any module error (MNVB6xx) stops the build.
    /// </summary>
    private static GameProject? WithProfile(GameProject project, List<BuildDiagnostic> diagnostics)
    {
        try
        {
            var resolution = ProjectProfileMigration.Resolve(project);
            if (resolution.Inference is { } inference)
            {
                diagnostics.Add(BuildDiagnostic.Warning("MNVB110", $"The project has no profile; building as {resolution.Profile.DisplayName()}. {inference.Describe()} Save it with `mnvt migrate`.", project.ProjectFile));
                project.Profile = resolution.Profile;
            }

            // Module problems (MNVB6xx): unknown, incompatible or required-but-disabled modules, and content of modules the project does not have.
            var errors = ProjectModules.Validate(project).Diagnostics.Where(d => d.Severity == ModuleSeverity.Error)
                .Select(d => BuildDiagnostic.Error(d.Code, d.Message, project.ProjectFile)).ToList();
            diagnostics.AddRange(errors);
            return errors.Count == 0 ? project : null;
        }
        catch (ProfileConflictException ex)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB109", ex.Message, project.ProjectFile));
            return null;
        }
    }

    /// <summary>Refuses output folders that would destroy something other than a previous build.</summary>
    private static string? ResolveOutput(GameProject project, BuildProfile profile, BuildOptions options, List<BuildDiagnostic> diagnostics)
    {
        string output;
        try
        {
            output = options.OutputDirectory is { } custom
                ? Path.GetFullPath(custom)
                : project.ResolveProjectFile(profile.OutputDirectory ?? Path.Combine("Builds", profile.Name));
        }
        catch (ArgumentException ex)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB104", ex.Message));
            return null;
        }

        if (IsSameOrUnder(project.RootDirectory, output) || IsSameOrUnder(output, project.AssetsDirectory) || IsSameOrUnder(output, Path.Combine(project.RootDirectory, "Library")))
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB104", $"Output folder '{output}' overlaps the project's own files; choose a folder such as Builds/<profile>."));
            return null;
        }

        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()
            && GamePackage.FindDataDirectory(output) is null)
        {
            diagnostics.Add(BuildDiagnostic.Error("MNVB104", $"Output folder '{output}' is not empty and does not contain a previous build; refusing to overwrite it."));
            return null;
        }

        return output;
    }

    /// <summary>True if <paramref name="path"/> is <paramref name="ancestor"/> or lies inside it.</summary>
    private static bool IsSameOrUnder(string path, string ancestor)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(ancestor), Path.GetFullPath(path));
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <summary>
    /// Swaps the staged build in. The previous build is first moved aside in one step, which fails without changing
    /// anything if a file in it is in use; if the new build cannot be moved in, the previous one is put back.
    /// </summary>
    private static void ReplaceOutput(string stage, string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var previous = Directory.Exists(output) ? output + ".previous" : null;
        if (previous is not null)
        {
            DeleteDirectory(previous);
            Directory.Move(output, previous);
        }

        try
        {
            if (string.Equals(Path.GetPathRoot(stage), Path.GetPathRoot(output), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(stage, output);
            }
            else
            {
                CopyDirectory(stage, output);
                DeleteDirectory(stage);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteDirectory(output);
            if (previous is not null)
            {
                Directory.Move(previous, output);
            }

            throw;
        }

        if (previous is not null)
        {
            DeleteDirectory(previous);
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    [LoggerMessage(EventId = 8000, Level = LogLevel.Information, Message = "Building '{Project}' with profile '{Profile}' ({Configuration}, {Target})")]
    private static partial void LogBuilding(ILogger logger, string project, string profile, string configuration, string target);

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information, Message = "Compiling C# scripts {Scripts} ({Configuration})")]
    private static partial void LogCompilingScripts(ILogger logger, string scripts, string configuration);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Information, Message = "Compiled content: {Assets} assets, {Scripts} .mnvts scripts, {Shaders} shader stages precompiled")]
    private static partial void LogContent(ILogger logger, int assets, int scripts, int shaders);

    private static void LogUsage(ILogger logger, ContentResult content)
    {
        if (content is { Usage: { } usage, Modules: { } modules })
        {
            var ids = string.Join(", ", modules.Modules.Select(m => m.Id));
            LogModules(logger, ids, usage.Stripped.Count);
        }
    }

    [LoggerMessage(EventId = 8012, Level = LogLevel.Information, Message = "Build report: {Path}")]
    private static partial void LogReport(ILogger logger, string path);

    [LoggerMessage(EventId = 8011, Level = LogLevel.Information, Message = "Framework: {Removed} unreachable files removed ({Size}), {Kept} assemblies kept")]
    private static partial void LogPruned(ILogger logger, int removed, string size, int kept);

    [LoggerMessage(EventId = 8010, Level = LogLevel.Information, Message = "Modules: {Modules}; {Stripped} unused assets not shipped")]
    private static partial void LogModules(ILogger logger, string modules, int stripped);

    [LoggerMessage(EventId = 8007, Level = LogLevel.Information, Message = "Content split into {Count} archives: {Archives}")]
    private static partial void LogArchives(ILogger logger, int count, string archives);

    [LoggerMessage(EventId = 8003, Level = LogLevel.Information, Message = "Publishing {Executable}.exe ({Target}, private .NET runtime)")]
    private static partial void LogPublishing(ILogger logger, string executable, string target);

    [LoggerMessage(EventId = 8004, Level = LogLevel.Information, Message = "Validating package")]
    private static partial void LogValidating(ILogger logger);

    [LoggerMessage(EventId = 8005, Level = LogLevel.Information, Message = "Packaged {Size} into {Output}")]
    private static partial void LogPackaged(ILogger logger, string size, string output);

    [LoggerMessage(EventId = 8006, Level = LogLevel.Information, Message = "Test run: {Frames} frames")]
    private static partial void LogTestRun(ILogger logger, int frames);
}
