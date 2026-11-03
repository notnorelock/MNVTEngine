using System.Globalization;
using Microsoft.Extensions.Logging;
using MNVTEngine;
using MNVTEngine.Core.Logging;
using MNVTEngine.Editor;
using MNVTEngine.Platform;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

// Usage: MNVTEngine.Editor <project dir or .mnvtproj> [--play] [--debug-gl]
// Automation (tests, docs): [--select ObjectName] [--frames N] [--screenshot out.png]
using var logs = new InMemoryLoggerProvider(capacity: 5000, minimumLevel: LogLevel.Information);
using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs).AddSimpleConsole(o => o.SingleLine = true));
var logger = loggerFactory.CreateLogger("Editor");

var projectArg = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || args[i - 1] is not ("--frames" or "--screenshot" or "--select" or "--focus"))).FirstOrDefault();
if (projectArg is null)
{
    Console.Error.WriteLine("Usage: MNVTEngine.Editor <project> [--play]");
    return 2;
}

GameProject project;
try
{
    project = GameProject.Load(projectArg);
}
catch (Exception ex) when (ex is SerializationException or FileNotFoundException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

// Game components are compiled and loaded from the project's scripts project by ScriptReloadController.
var registry = EngineComponents.CreateRegistry();

var options = new EditorOptions(
    Value("--frames") is { } frames ? int.Parse(frames, CultureInfo.InvariantCulture) : null,
    Value("--screenshot"),
    args.Contains("--play"),
    Value("--select"),
    Value("--focus"));

using var window = new GameWindow(new GameWindowOptions
{
    Title = "MNVTEngine Editor",
    Width = 1600,
    Height = 900,
    DebugContext = args.Contains("--debug-gl"),
    FixedTimestep = project.FixedTimestep,
});

var app = new EditorApp(project, registry, loggerFactory, logs, options);
window.Run(app);
return app.ExitCode;

string? Value(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
