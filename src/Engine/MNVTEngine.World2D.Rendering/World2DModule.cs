// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-12.

using MNVTEngine.Runtime;
using MNVTEngine.Scenes;

namespace MNVTEngine.World2D.Rendering;

/// <summary>The "world.2d" engine module: 2D components and the sprite pass.</summary>
public sealed class World2DModule : IEngineModule, IRenderIntegration
{
    public string Id => "world.2d";

    public void Register(ComponentRegistry components) => World2DComponents.Register(components);

    public IScenePass CreatePass(RenderServices services) => new SpriteScenePass(services);
}
