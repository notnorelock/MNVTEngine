using Microsoft.Extensions.Logging;
using MNVTEngine.Build;
using MNVTEngine.GameAssemblies;
using MNVTEngine.Graphics.OpenGL;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;

// Usage: MNVTEngine.Player <project> [--frames N] [--screenshot out.png] [--debug-gl]
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
var logger = loggerFactory.CreateLogger("Player");

return GameApplication.Run(
    args,
    (window, log) => GLGraphicsDevice.Create(window.GraphicsContext, window.FramebufferWidth, window.FramebufferHeight, log),
    loggerFactory,
    (project, registry) => LoadScripts(project, registry, logger));

static bool LoadScripts(GameProject project, ComponentRegistry registry, ILogger logger)
{
    ScriptProject? scripts;
    try
    {
        scripts = ScriptProject.For(project);
    }
    catch (FileNotFoundException ex)
    {
        PlayerLog.Diagnostic(logger, LogLevel.Critical, ex.Message);
        return false;
    }

    if (scripts is null)
    {
        return true;
    }

    if (!scripts.IsBuildUpToDate())
    {
        PlayerLog.Compiling(logger, scripts.ProjectFile);
        var build = ScriptCompiler.BuildAsync(scripts).GetAwaiter().GetResult();
        foreach (var diagnostic in build.Diagnostics)
        {
            PlayerLog.Diagnostic(logger, diagnostic.Severity == ScriptDiagnosticSeverity.Error ? LogLevel.Error : LogLevel.Warning, diagnostic);
        }

        if (!build.Succeeded)
        {
            return false;
        }
    }

    var load = new GameAssemblyHost().Load(scripts.AssemblyPath);
    var diagnostics = load.Diagnostics.Concat(GameAssemblyHost.Register(registry, null, load.Code)).ToList();
    foreach (var diagnostic in diagnostics)
    {
        PlayerLog.Diagnostic(logger, diagnostic.Severity == ScriptDiagnosticSeverity.Error ? LogLevel.Error : LogLevel.Warning, diagnostic);
    }

    return load.Code is not null && diagnostics.All(d => d.Severity != ScriptDiagnosticSeverity.Error);
}

internal static partial class PlayerLog
{
    [LoggerMessage(EventId = 6000, Level = LogLevel.Information, Message = "Compiling scripts {Project}...")]
    public static partial void Compiling(ILogger logger, string project);

    [LoggerMessage(EventId = 6001, Message = "{Diagnostic}")]
    public static partial void Diagnostic(ILogger logger, LogLevel level, object diagnostic);
}
