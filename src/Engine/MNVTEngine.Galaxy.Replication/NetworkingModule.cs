// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-25.

using MNVTEngine.Scenes;

namespace MNVTEngine;

/// <summary>Registers the networking components so a host that opens any project (the editor, the Player, the headless
/// server, the build) knows them. The components drive themselves from their own <c>Update</c>; the module starts no
/// process-wide services.</summary>
public sealed class NetworkingModule : IEngineModule
{
    public string Id => "networking";

    public void Register(ComponentRegistry components)
    {
        ArgumentNullException.ThrowIfNull(components);
        components.Register<NetworkObject>();
        components.Register<NetworkTransform>();
        components.Register<NetworkClient>();
        components.Register<NetworkServer>();
    }
}
