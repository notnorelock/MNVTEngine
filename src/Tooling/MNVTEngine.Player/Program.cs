using Microsoft.Extensions.Logging;
using MNVTEngine.Build;
using MNVTEngine.Graphics.OpenGL;
using MNVTEngine.Runtime;

// Usage: MNVTEngine.Player <project> [--frames N] [--screenshot out.png] [--debug-gl]
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
return DevelopmentRun.Run(
    args,
    loggerFactory,
    (window, log) => GLGraphicsDevice.Create(window.GraphicsContext, window.FramebufferWidth, window.FramebufferHeight, log),
    MNVTEngine.Audio.OpenAL.OpenALAudioDevice.Create);
