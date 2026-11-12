using Microsoft.Extensions.Logging;
using MNVTEngine.GameAssemblies;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;

namespace MNVTEngine.Build;

public sealed record GameCodeResult(GameCode? Code, IReadOnlyList<BuildDiagnostic> Diagnostics)
{
    public bool Succeeded => !Diagnostics.HasErrors();
}

/// <summary>Loads a project's C# game code for development runs and validation: compiles when stale, loads, registers.</summary>
public static class DevelopmentScripts
{
    /// <summary>Debug build into <c>Library/ScriptAssemblies</c> (skipped when up to date), then <see cref="LoadAndRegister"/>.</summary>
    /// <param name="onCompile">Called before an actual compile, e.g. to log progress.</param>
    public static async Task<GameCodeResult> LoadAsync(GameProject project, ComponentRegistry registry, Action<string>? onCompile = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ScriptProject? scripts;
        try
        {
            scripts = ScriptProject.For(project);
        }
        catch (FileNotFoundException ex)
        {
            return new GameCodeResult(null, [BuildDiagnostic.Error("MNVB301", ex.Message, project.ProjectFile)]);
        }

        if (scripts is null)
        {
            return new GameCodeResult(null, []);
        }

        var diagnostics = new List<BuildDiagnostic>();
        if (!scripts.IsBuildUpToDate())
        {
            onCompile?.Invoke(scripts.ProjectFile);
            var build = await ScriptCompiler.BuildAsync(scripts, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(build.Diagnostics.Select(BuildDiagnostic.FromScript));
            if (!build.Succeeded)
            {
                return new GameCodeResult(null, diagnostics);
            }
        }

        var code = LoadAndRegister(scripts.AssemblyPath, registry, diagnostics);
        return new GameCodeResult(code, diagnostics);
    }

    /// <summary>Loads compiled game code into a collectible context and registers its components; null (with errors) on failure.</summary>
    public static GameCode? LoadAndRegister(string assemblyPath, ComponentRegistry registry, List<BuildDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var load = new GameAssemblyHost().Load(assemblyPath);
        diagnostics.AddRange(load.Diagnostics.Select(BuildDiagnostic.FromScript));
        if (load.Code is null)
        {
            return null;
        }

        diagnostics.AddRange(GameAssemblyHost.Register(registry, null, load.Code).Select(BuildDiagnostic.FromScript));
        return load.Code;
    }

    /// <summary>Removes game components from <paramref name="registry"/> and unloads the code.</summary>
    public static void Unload(GameCode code, ComponentRegistry registry)
    {
        GameAssemblyHost.Register(registry, code, null);
        GameAssemblyHost.Unload(code);
    }
}

/// <summary>Plays a project from its sources (<c>mnvt run</c>, MNVTEngine.Player): compiles stale C# scripts, then runs it.</summary>
public static partial class DevelopmentRun
{
    /// <returns>The <see cref="GameApplication.Run"/> exit code.</returns>
    public static int Run(string[] args, ILoggerFactory loggerFactory, GraphicsDeviceFactory createDevice, Audio.AudioDeviceFactory? createAudio = null, DevelopmentTools? tools = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var logger = loggerFactory.CreateLogger("Player");
        return GameApplication.Run(args, createDevice, loggerFactory, EngineModuleSet.All(createAudio, logger), (project, registry) =>
        {
            var models = ContentCompiler.ImportModels(project);
            var result = DevelopmentScripts.LoadAsync(project, registry, file => LogCompiling(logger, file)).GetAwaiter().GetResult();
            foreach (var diagnostic in models.Concat(result.Diagnostics))
            {
                LogDiagnostic(logger, diagnostic.Severity == BuildSeverity.Error ? LogLevel.Error : LogLevel.Warning, diagnostic);
            }

            return result.Succeeded;
        }, tools: tools, importers: EngineImporters.All());
    }

    [LoggerMessage(EventId = 6000, Level = LogLevel.Information, Message = "Compiling scripts {Project}...")]
    private static partial void LogCompiling(ILogger logger, string project);

    [LoggerMessage(EventId = 6001, Message = "{Diagnostic}")]
    private static partial void LogDiagnostic(ILogger logger, LogLevel level, BuildDiagnostic diagnostic);
}
