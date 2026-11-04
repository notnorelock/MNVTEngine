using MNVTEngine.Scenes;

namespace MNVTEngine.Runtime;

/// <summary>Every built-in component type (scene basics, scripting, physics, audio, UI). Hosts start from this registry and add game components.</summary>
public static class EngineComponents
{
    public static ComponentRegistry CreateRegistry() =>
        UI.UIComponents.Register(AudioComponents.Register(Physics2DComponents.Register(ScriptingComponents.Register(ComponentRegistry.CreateDefault()))));
}

/// <summary>Creates the host's audio output (backend chosen by the host, e.g. OpenAL).</summary>
/// <exception cref="InvalidOperationException">No output is available; the host falls back to a silent device.</exception>
public delegate Audio.IAudioDevice AudioDeviceFactory(Microsoft.Extensions.Logging.ILogger logger);

public static partial class AudioDevices
{
    /// <summary>The factory's device, or a silent <see cref="Audio.NullAudioDevice"/> (with a warning) if there is no factory or no output.</summary>
    public static Audio.IAudioDevice CreateOrSilent(AudioDeviceFactory? factory, Microsoft.Extensions.Logging.ILogger logger)
    {
        if (factory is null)
        {
            return new Audio.NullAudioDevice();
        }

        try
        {
            return factory(logger);
        }
        catch (InvalidOperationException ex)
        {
            LogNoAudio(logger, ex);
            return new Audio.NullAudioDevice();
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(EventId = 3300, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "No audio output; the game runs silently")]
    private static partial void LogNoAudio(Microsoft.Extensions.Logging.ILogger logger, Exception exception);
}
