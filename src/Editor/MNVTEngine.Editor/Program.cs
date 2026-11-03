using System.Globalization;
using Microsoft.Extensions.Logging;
using MNVTEngine;
using MNVTEngine.Core.Logging;
using MNVTEngine.Editor;
using MNVTEngine.Platform;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

// Usage: MNVTEngine.Editor [project dir or .mnvtproj] [--play] [--debug-gl]   (no project: the Project Hub opens)
// Automation (tests, docs): [--select ObjectName] [--frames N] [--screenshot out.png]
var logs = new InMemoryLoggerProvider(capacity: 5000);
using var logHub = new LogHub().AddSink(logs).AddSink(new ConsoleLogSink());
using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logHub)); // the hub filters per logger
GameLog.Factory = loggerFactory;
var logger = loggerFactory.CreateLogger("Editor");

var projectArg = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || args[i - 1] is not ("--frames" or "--screenshot" or "--select" or "--focus" or "--break" or "--edit"))).FirstOrDefault();
var recent = new MNVTEngine.Editor.Services.RecentProjects(MNVTEngine.Editor.Services.RecentProjects.DefaultFile);
if (projectArg is null)
{
    var hub = new ProjectHub(loggerFactory, recent, Value("--frames") is { } hubFrames ? int.Parse(hubFrames, CultureInfo.InvariantCulture) : null, Value("--screenshot"));
    using (var hubWindow = new GameWindow(new GameWindowOptions { Title = "MNVTEngine - Projects", Width = 960, Height = 600 }))
    {
        hubWindow.Run(hub);
    }

    if (hub.SelectedProject is null)
    {
        return 0; // closed without choosing
    }

    projectArg = hub.SelectedProject;
}

GameProject project;
try
{
    project = GameProject.Load(projectArg);
    recent.Add(project.ProjectFile);
    LoggingSettings.Load(LoggingSettings.PathFor(project)).ApplyTo(logHub);
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
    Value("--focus"),
    [.. args.Select((a, i) => (a, i)).Where(p => p.a == "--break" && p.i + 1 < args.Length).Select(p => args[p.i + 1])],
    Value("--edit"));

using var window = new GameWindow(new GameWindowOptions
{
    Title = "MNVTEngine Editor",
    Width = 1600,
    Height = 900,
    DebugContext = args.Contains("--debug-gl"),
    FixedTimestep = project.FixedTimestep,
});

var app = new EditorApp(project, registry, loggerFactory, logs, logHub, options);
window.Run(app);
return app.ExitCode;

string? Value(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
