using MNVTEngine.Graphics.Vulkan.ShaderCompilation;
using Microsoft.Extensions.Logging;
using MNVTEngine.Build;
using MNVTEngine.Graphics.Vulkan;
using MNVTEngine.Runtime;

// Usage: MNVTEngine.Player <project> [--frames N] [--screenshot out.png] [--gpu-validation] [--overlay] [--no-diagnostics] [--no-crash-reporter]
// Crashes leave a report in %LOCALAPPDATA%/MNVTEngine/Crashes/Player (docs/engine/crash-reporting.md).
if (MNVTEngine.CrashReporting.CrashReporter.Run(args, new(
    "MNVTEngine Player",
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MNVTEngine", "Crashes", "Player"))) is { } crashHandlerExit)
{
    return crashHandlerExit;
}

var debugLogs = new MNVTEngine.Core.Logging.InMemoryLoggerProvider(capacity: 5000);
using var logHub = new MNVTEngine.Core.Logging.LogHub().AddSink(new MNVTEngine.Core.Logging.ConsoleLogSink()).AddSink(debugLogs);
using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logHub));
MNVTEngine.GameLog.Factory = loggerFactory;
return DevelopmentRun.Run(
    args,
    loggerFactory,
    (window, log) => VulkanGraphicsDevice.Create(window.VulkanSurface, window.FramebufferWidth, window.FramebufferHeight, log, VulkanDeviceOptions.ForApplication("Player", window.Options.VSync, window.Options.GraphicsValidation).WithShaderc()),
    MNVTEngine.Audio.OpenAL.OpenALAudioDevice.Create,
    new DevelopmentTools(log => new MNVTEngine.DebugUI.DebugOverlay(log), debugLogs));
