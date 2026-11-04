using Microsoft.Extensions.Logging;
using MNVTEngine.Build;
using MNVTEngine.Graphics.OpenGL;
using MNVTEngine.Runtime;

// Usage: MNVTEngine.Player <project> [--frames N] [--screenshot out.png] [--debug-gl] [--overlay] [--no-diagnostics]
var debugLogs = new MNVTEngine.Core.Logging.InMemoryLoggerProvider(capacity: 5000);
using var logHub = new MNVTEngine.Core.Logging.LogHub().AddSink(new MNVTEngine.Core.Logging.ConsoleLogSink()).AddSink(debugLogs);
using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logHub));
MNVTEngine.GameLog.Factory = loggerFactory;
return DevelopmentRun.Run(
    args,
    loggerFactory,
    (window, log) => GLGraphicsDevice.Create(window.GraphicsContext, window.FramebufferWidth, window.FramebufferHeight, log),
    MNVTEngine.Audio.OpenAL.OpenALAudioDevice.Create,
    new DevelopmentTools(log => new MNVTEngine.DebugUI.DebugOverlay(log), debugLogs));
