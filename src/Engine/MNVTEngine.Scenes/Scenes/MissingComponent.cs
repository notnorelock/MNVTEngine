// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-03.

using Newtonsoft.Json.Linq;

namespace MNVTEngine.Scenes;

/// <summary>
/// Stand-in for a component whose type is not registered (deleted script, assembly not loaded).
/// It does nothing at runtime but keeps the original type id and data, so saving the scene
/// does not lose them and the component returns once its type is available again.
/// </summary>
public sealed class MissingComponent : Component
{
    public MissingComponent()
        : this("<unknown>", [])
    {
    }

    public MissingComponent(string typeId, JObject data, bool enabled = true)
    {
        TypeId = typeId;
        Data = data;
        Enabled = enabled;
    }

    public string TypeId { get; }

    public JObject Data { get; }
}
