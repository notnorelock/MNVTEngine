// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MNVTEngine.Assets;
using MNVTEngine.Core.Timing;
using MNVTEngine.Runtime;
using MNVTEngine.Scenes;

namespace MNVTEngine.Build;

/// <summary>
/// Runs a game's startup scene as a headless dedicated server: it loads the project's content and C# code, simulates
/// the scene (fixed update and update) at a fixed tick, and hosts a <see cref="NetworkServer"/> so clients can connect
/// and replicate — all with no window, graphics device or audio. If the scene has no <see cref="NetworkServer"/>, one
/// is added. This is the standalone server for a game built on the engine.
/// </summary>
public static partial class DedicatedServerHost
{
    public static async Task<int> RunAsync(string projectPath, int port, int tickRate, ILoggerFactory loggerFactory, CancellationToken cancellationToken, Action<NetworkServer>? onStarted = null)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ILogger logger = loggerFactory.CreateLogger("MNVTEngine.Server");

        GameProject project = GameProject.Load(projectPath);
        ComponentRegistry registry = EngineModuleSet.CreateRegistry(project.Profile);
        GameCodeResult code = await DevelopmentScripts.LoadAsync(project, registry, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!code.Succeeded)
        {
            foreach (BuildDiagnostic diagnostic in code.Diagnostics)
            {
                LogCodeError(logger, diagnostic.ToString());
            }

            return 1;
        }

        var assets = AssetDatabase.OpenReadOnly(project.AssetsDirectory, EngineImporters.All());
        var serializer = new SceneSerializer(registry, assets);
        if (string.IsNullOrEmpty(project.StartupScene))
        {
            LogNoStartupScene(logger, project.Name);
            return 1;
        }

        SceneLoadResult loaded = serializer.Deserialize(await File.ReadAllTextAsync(project.ResolveAsset(project.StartupScene), cancellationToken).ConfigureAwait(false), project.StartupScene);
        if (loaded.HasErrors)
        {
            foreach (var diagnostic in loaded.Diagnostics)
            {
                LogSceneError(logger, diagnostic.ToString() ?? "");
            }

            return 1;
        }

        Scene scene = loaded.Scene;
        var provider = new PrefabProvider(serializer, assets, loggerFactory.CreateLogger<PrefabProvider>());
        scene.Prefabs = provider;
        scene.TextAssets = provider;
        scene.Input = new InputState();

        NetworkServer server = scene.FindObjectsOfType<NetworkServer>().FirstOrDefault() ?? scene.CreateGameObject("NetworkServer").AddComponent<NetworkServer>();
        if (port != 0)
        {
            server.Port = port;
        }

        server.StartServer(); // Open the socket now; the scene would otherwise defer the component's Start to the first update.
        scene.Start();
        LogListening(logger, project.Name, server.BoundPort, tickRate);
        onStarted?.Invoke(server);

        RunLoop(scene, Math.Max(1, tickRate), cancellationToken);
        LogStopped(logger);
        return 0;
    }

    private static void RunLoop(Scene scene, int tickRate, CancellationToken cancellationToken)
    {
        double step = 1.0 / tickRate;
        var clock = Stopwatch.StartNew();
        double nextDue = clock.Elapsed.TotalSeconds;
        double total = 0;
        long frame = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            scene.FixedUpdate((float)step);
            scene.Update(new FrameTime((float)step, (float)step, total, total, frame++));
            total += step;

            nextDue += step;
            double remaining = nextDue - clock.Elapsed.TotalSeconds;
            if (remaining > 0.001)
            {
                cancellationToken.WaitHandle.WaitOne((int)(remaining * 1000));
            }
            else if (remaining < 0)
            {
                nextDue = clock.Elapsed.TotalSeconds; // Behind schedule: resync instead of spiralling.
            }
        }
    }

    [LoggerMessage(EventId = 9140, Level = LogLevel.Information, Message = "Dedicated server for '{Game}' listening on UDP {Port} at {TickRate} Hz")]
    private static partial void LogListening(ILogger logger, string game, int port, int tickRate);

    [LoggerMessage(EventId = 9141, Level = LogLevel.Information, Message = "Dedicated server stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(EventId = 9142, Level = LogLevel.Error, Message = "Game code did not compile: {Diagnostic}")]
    private static partial void LogCodeError(ILogger logger, string diagnostic);

    [LoggerMessage(EventId = 9143, Level = LogLevel.Error, Message = "The startup scene has errors: {Diagnostic}")]
    private static partial void LogSceneError(ILogger logger, string diagnostic);

    [LoggerMessage(EventId = 9144, Level = LogLevel.Error, Message = "Project '{Game}' has no startup scene to serve")]
    private static partial void LogNoStartupScene(ILogger logger, string game);
}
