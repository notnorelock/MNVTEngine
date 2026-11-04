using Microsoft.Extensions.Logging;
using MNVTEngine.Platform;
using MNVTEngine.Samples.HelloWorld;

// Usage: HelloWorld [--frames N] [--screenshot out.png] [--gpu-validation]
var frames = ArgValue("--frames") is { } f ? int.Parse(f, System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
var screenshot = ArgValue("--screenshot");

using var logHub = new MNVTEngine.Core.Logging.LogHub(new MNVTEngine.Core.Logging.LogLevels { Default = LogLevel.Debug }).AddSink(new MNVTEngine.Core.Logging.ConsoleLogSink());
using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logHub));
var logger = loggerFactory.CreateLogger("HelloWorld");

using var window = new GameWindow(new GameWindowOptions
{
    Title = "MNVTEngine - Hello World",
    DebugContext = args.Contains("--gpu-validation"),
    OpenGL = false,
});

var game = new HelloWorldGame(logger, frames, screenshot);
window.Run(game);
return game.ExitCode;

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
