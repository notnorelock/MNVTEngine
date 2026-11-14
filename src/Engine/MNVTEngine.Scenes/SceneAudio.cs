// SPDX-FileCopyrightText: 2026 Norbert Podbiegły (Norelock) <https://github.com/notnorelock>
// SPDX-License-Identifier: MIT
// Part of MNVTEngine. Last modified 2026-11-12.

namespace MNVTEngine;

/// <summary>
/// Sound control of a scene for code that must not depend on the audio module (mission scripts): set by the audio
/// module on every scene it attaches to (<see cref="Scene.GetService{T}"/>); absent when the project has no audio.
/// </summary>
public interface ISceneAudio
{
    /// <exception cref="InvalidOperationException">The object has no audio source.</exception>
    void PlaySound(GameObject target);

    /// <exception cref="InvalidOperationException">The object has no audio source.</exception>
    void StopSound(GameObject target);

    /// <exception cref="InvalidOperationException">The scene has no active mixer, or the mixer no such group.</exception>
    void SetGroupVolume(string group, float volume);

    /// <exception cref="InvalidOperationException">The scene has no active mixer, or the mixer no such group.</exception>
    void SetGroupMuted(string group, bool muted);
}
