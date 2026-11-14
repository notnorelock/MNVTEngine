// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-12.

namespace MNVTEngine.Runtime;

/// <summary>A module's own cache in a <see cref="ResourceCache"/> (world.3d: models and environments).</summary>
public interface IResourceExtension : IDisposable
{
    /// <summary>Drops what was loaded from <paramref name="changed"/> assets; they load again on next use.</summary>
    void Evict(IReadOnlyCollection<Guid> changed);
}
