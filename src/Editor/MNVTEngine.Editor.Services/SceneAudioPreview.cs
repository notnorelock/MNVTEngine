using MNVTEngine.Audio;
using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;

namespace MNVTEngine.Editor.Services;

/// <summary>
/// Unity's Scene audio toggle: while on (and not in Play Mode), the edit scene's looping, play-on-start audio sources
/// play in the editor, heard from the Scene view camera, so ambience can be placed and tuned without pressing Play.
/// Off, Play Mode or another scene stops them.
/// </summary>
public sealed class SceneAudioPreview(IAudioDevice? device, Func<AssetRef, byte[]> readClip)
{
    private Scene? _scene;
    private double _time;
    private long _frames;

    public bool Enabled { get; set; }

    /// <summary>Whether the editor has an audio output to preview on.</summary>
    public bool Available => device is not null;

    /// <summary>Call every editor frame.</summary>
    /// <param name="listener">The Scene view camera's position.</param>
    public void Update(Scene editScene, bool playing, Vector3 listener, float deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(editScene);
        if (!Enabled || playing || device is null)
        {
            Stop();
            return;
        }

        if (!ReferenceEquals(_scene, editScene))
        {
            Stop();
            _scene = editScene;
        }

        var system = AudioSystem.For(editScene) ?? AudioSystem.Attach(editScene, device, readClip);
        foreach (var source in editScene.FindObjectsOfType<AudioSource>())
        {
            var ambient = source.IsActiveAndEnabled && source.Loop && source.PlayOnStart && !source.Clip.IsNone;
            if (ambient && !source.IsPlaying)
            {
                source.Play();
            }
            else if (!ambient && source.IsPlaying)
            {
                source.Stop();
            }
        }

        _time += deltaSeconds;
        system.Update(editScene.World, new FrameTime(deltaSeconds, deltaSeconds, _time, _time, ++_frames));
        device.ListenerPosition = (Vector2)listener; // the Scene view camera, not the scene's listener
    }

    /// <summary>Stops every previewed source.</summary>
    public void Stop()
    {
        if (_scene is null)
        {
            return;
        }

        foreach (var source in _scene.FindObjectsOfType<AudioSource>())
        {
            source.Stop();
        }

        _scene = null;
    }
}
