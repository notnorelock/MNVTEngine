using Microsoft.Extensions.Logging;
using MNVTEngine.Graphics.OpenGL;
using MNVTEngine.Runtime;

// The whole host: pick the graphics backend, register this assembly's components, run the project.
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));

return GameApplication.Run(
    args,
    (window, logger) => GLGraphicsDevice.Create(window.GraphicsContext, window.FramebufferWidth, window.FramebufferHeight, logger),
    loggerFactory,
    registry => registry.RegisterAssembly(typeof(Program).Assembly));
