using MNVTEngine.Particles;
using MNVTEngine.Scenes;

namespace MNVTEngine.Editor.Services;

/// <summary>
/// Plays the particle systems of the selected object in edit mode, so they can be tuned without entering Play Mode
/// (in Play Mode the scene simulates them itself). Selecting another object stops and clears the previous preview and
/// starts the new one. The systems are the scene's own: the Scene view draws them with the project's particle renderer.
/// </summary>
public sealed class ParticlePreview
{
    private readonly List<ParticleSystem> _systems = [];
    private GameObject? _target;

    /// <summary>The previewed systems (the selected object's and its children's).</summary>
    public IReadOnlyList<ParticleSystem> Systems => _systems;

    public bool IsPaused { get; private set; }

    /// <summary>Preview time multiplier (on top of each system's own simulation speed).</summary>
    public float Speed { get; set; } = 1f;

    /// <summary>Seconds the preview has run since it last (re)started.</summary>
    public float Time { get; private set; }

    /// <param name="selected">The selected object; null previews nothing.</param>
    /// <param name="playMode">In Play Mode nothing is previewed (the running scene simulates).</param>
    public void Update(GameObject? selected, bool playMode, float deltaSeconds)
    {
        var target = playMode || selected is null || selected.IsDestroyed ? null : selected;
        if (target != _target)
        {
            StopAll();
            _target = target;
            if (target is not null)
            {
                Collect(target);
                Restart();
            }
        }

        if (_target is null || IsPaused)
        {
            return;
        }

        var dt = Math.Clamp(deltaSeconds, 0f, 0.1f) * Math.Max(0f, Speed);
        Time += dt;
        foreach (var system in _systems)
        {
            if (!system.IsDestroyed)
            {
                system.Simulation.Step(dt, system.Pose());
            }
        }
    }

    public void Play()
    {
        IsPaused = false;
        foreach (var system in _systems)
        {
            system.Play();
        }
    }

    public void Pause()
    {
        IsPaused = true;
        foreach (var system in _systems)
        {
            system.Pause();
        }
    }

    public void Restart()
    {
        IsPaused = false;
        Time = 0f;
        foreach (var system in _systems)
        {
            system.Restart();
        }
    }

    /// <summary>Stops and clears the preview; <see cref="Play"/> starts it again.</summary>
    public void Stop()
    {
        foreach (var system in _systems)
        {
            system.Stop(ParticleStopBehavior.StopEmittingAndClear);
        }

        IsPaused = true;
        Time = 0f;
    }

    private void StopAll()
    {
        foreach (var system in _systems.Where(s => !s.IsDestroyed))
        {
            system.Stop(ParticleStopBehavior.StopEmittingAndClear);
        }

        _systems.Clear();
        IsPaused = false;
    }

    private void Collect(GameObject root)
    {
        _systems.AddRange(root.GetComponents<ParticleSystem>());
        foreach (var child in root.Transform.Children)
        {
            Collect(child.GameObject);
        }
    }
}
