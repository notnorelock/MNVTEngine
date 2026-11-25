// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Scenes;

namespace MNVTEngine.GameHost;

/// <summary>The engine modules built into this executable.</summary>
internal static class HostModules
{
    // shortcut: every module until the build generates this list from the project's resolved modules (phase B3).
    public static IReadOnlyList<IEngineModule> Create(Microsoft.Extensions.Logging.ILogger logger) =>
    [
        new World2D.Rendering.World2DModule(),
        new World3D.Rendering.World3DModule(),
        new AudioModule(Audio.OpenAL.OpenALAudioDevice.Create, logger),
        new ScriptingModule(),
        new UI.UIModule(),
        new Physics2DModule(),
        new Physics3DModule(),
        new TerrainModule(),
        new NetworkingModule(),
    ];
}
