using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles;

/// <summary>A particle being born, dying or colliding, in world space.</summary>
/// <param name="Normal">The surface normal for collisions; zero otherwise.</param>
public readonly record struct ParticleEvent(SubEmitterEvent Kind, Vector3 Position, Vector3 Velocity, Color Color, Vector3 Normal);

/// <summary>
/// The events of one simulation step, recorded only for the kinds something listens to (sub-emitters, collision
/// events). Cleared each step; the array is reused.
/// </summary>
public sealed class ParticleEventBuffer
{
    private ParticleEvent[] _items = new ParticleEvent[16];

    public int Count { get; private set; }

    public bool RecordBirths { get; internal set; }

    public bool RecordDeaths { get; internal set; }

    public bool RecordCollisions { get; internal set; }

    public ParticleEvent this[int index] => index < Count ? _items[index] : throw new ArgumentOutOfRangeException(nameof(index));

    public ReadOnlySpan<ParticleEvent> AsSpan() => _items.AsSpan(0, Count);

    internal void Add(in ParticleEvent item)
    {
        if (Count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        _items[Count++] = item;
    }

    internal void Clear() => Count = 0;

    /// <summary>Sets what to record from the settings: events nothing uses are not recorded.</summary>
    internal void Configure(ParticleSystemSettings settings)
    {
        var subEmitters = settings.SubEmitters.Enabled ? settings.SubEmitters.Emitters : null;
        RecordBirths = subEmitters?.Exists(s => s.Event == SubEmitterEvent.Birth) ?? false;
        RecordDeaths = subEmitters?.Exists(s => s.Event == SubEmitterEvent.Death) ?? false;
        RecordCollisions = settings.Collision.Enabled && (settings.Collision.SendCollisionEvents || (subEmitters?.Exists(s => s.Event == SubEmitterEvent.Collision) ?? false));
    }
}
