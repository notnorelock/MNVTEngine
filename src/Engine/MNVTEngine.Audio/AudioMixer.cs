using MNVTEngine.Serialization;
using Newtonsoft.Json;

namespace MNVTEngine;

/// <summary>One mixer channel: its own volume and mute, multiplied with its parent's (up to Master).</summary>
public sealed class AudioMixerGroup
{
    public string Name { get; set; } = "Group";

    /// <summary>Parent group; empty means Master.</summary>
    public string Parent { get; set; } = "";

    /// <summary>0 = silent, 1 = unchanged.</summary>
    public float Volume { get; set; } = 1f;

    public bool Muted { get; set; }
}

/// <summary>
/// Volume groups for the scene's audio (Master plus groups such as Music, Effects, Voice). An
/// <see cref="AudioSource"/> plays through the group named in <see cref="AudioSource.Group"/>; its effective volume is
/// its own volume times every group from its own up to Master. Changes apply on the next frame to sounds that are
/// already playing. One mixer per scene: the first enabled one is used.
/// </summary>
public sealed class AudioMixer : Component
{
    public const string MasterGroup = "Master";
    private List<AudioMixerGroup> _groups = [new() { Name = "Music" }, new() { Name = "Effects" }];

    [SerializeField]
    public float MasterVolume { get; set; } = 1f;

    [SerializeField]
    public bool MasterMuted { get; set; }

    /// <summary>Groups besides Master. Names are unique; a missing or cyclic parent counts as Master.</summary>
    [SerializeField]
    [HideInInspector] // edited by the mixer's own inspector UI
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<AudioMixerGroup> Groups { get => _groups; set => _groups = value ?? []; }

    public AudioMixerGroup? Find(string name) => _groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sets a group's volume ("Master" included).</summary>
    /// <exception cref="KeyNotFoundException">No such group.</exception>
    public void SetVolume(string group, float volume)
    {
        volume = Math.Max(0f, volume);
        if (IsMaster(group))
        {
            MasterVolume = volume;
        }
        else
        {
            (Find(group) ?? throw new KeyNotFoundException($"The mixer has no group '{group}'.")).Volume = volume;
        }
    }

    /// <exception cref="KeyNotFoundException">No such group.</exception>
    public void SetMuted(string group, bool muted)
    {
        if (IsMaster(group))
        {
            MasterMuted = muted;
        }
        else
        {
            (Find(group) ?? throw new KeyNotFoundException($"The mixer has no group '{group}'.")).Muted = muted;
        }
    }

    /// <summary>The group's volume times its parents' (0 if any is muted). Unknown groups play through Master.</summary>
    public float EffectiveVolume(string? group)
    {
        var volume = MasterMuted ? 0f : MasterVolume;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = string.IsNullOrEmpty(group) ? null : Find(group); current is not null && visited.Add(current.Name); current = Find(current.Parent))
        {
            volume *= current.Muted ? 0f : current.Volume;
        }

        return volume;
    }

    private static bool IsMaster(string group) => string.IsNullOrEmpty(group) || string.Equals(group, MasterGroup, StringComparison.OrdinalIgnoreCase);
}
