using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles;

/// <summary>
/// The alive particles of one system, one array per property (structure of arrays), packed at the front: index 0 to
/// <see cref="Count"/> - 1. A dying particle is replaced by the last one, so removal is O(1) and nothing is allocated
/// per particle; the arrays grow (doubling, up to the system's maximum) and are reused afterwards. Renderers read them
/// between simulation steps, on the same thread.
/// </summary>
public sealed class ParticleBuffer
{
    private const int InitialCapacity = 32;

    public int Count { get; private set; }

    public int Capacity => Position.Length;

    /// <summary>Emitter-local or world space (see <see cref="MainModule.SimulationSpace"/>).</summary>
    public Vector3[] Position { get; private set; } = [];

    public Vector3[] Velocity { get; private set; } = [];

    public Vector3[] StartSize { get; private set; } = [];

    /// <summary>Current size (start size times the size module).</summary>
    public Vector3[] Size { get; private set; } = [];

    /// <summary>Degrees; Z is the facing-axis rotation used by 2D and billboards.</summary>
    public Vector3[] Rotation { get; private set; } = [];

    /// <summary>Degrees per second, from the start rotation speed.</summary>
    public Vector3[] AngularVelocity { get; private set; } = [];

    public Color[] StartColor { get; private set; } = [];

    /// <summary>Current color (start color times the color module).</summary>
    public Color[] Color { get; private set; } = [];

    /// <summary>Seconds since emission.</summary>
    public float[] Age { get; private set; } = [];

    public float[] Lifetime { get; private set; } = [];

    /// <summary>Sprite-sheet frame (0 when the module is off).</summary>
    public int[] Frame { get; private set; } = [];

    /// <summary>Seed of the particle's stable random numbers (<see cref="ParticleRandom.Value"/>).</summary>
    public uint[] Seed { get; private set; } = [];

    /// <summary>Adds a particle and returns its index; -1 when <paramref name="max"/> particles are alive.</summary>
    public int Add(int max)
    {
        if (Count >= max)
        {
            return -1;
        }

        if (Count == Capacity)
        {
            Grow(Math.Min(max, Math.Max(InitialCapacity, Capacity * 2)));
        }

        return Count++;
    }

    /// <summary>Removes particle <paramref name="index"/> by moving the last one into its place.</summary>
    public void RemoveAt(int index)
    {
        var last = --Count;
        if (index == last)
        {
            return;
        }

        Position[index] = Position[last];
        Velocity[index] = Velocity[last];
        StartSize[index] = StartSize[last];
        Size[index] = Size[last];
        Rotation[index] = Rotation[last];
        AngularVelocity[index] = AngularVelocity[last];
        StartColor[index] = StartColor[last];
        Color[index] = Color[last];
        Age[index] = Age[last];
        Lifetime[index] = Lifetime[last];
        Frame[index] = Frame[last];
        Seed[index] = Seed[last];
    }

    /// <summary>Removes every particle (keeps the arrays).</summary>
    public void Clear() => Count = 0;

    /// <summary>Drops particles beyond <paramref name="max"/> (the maximum was lowered).</summary>
    public void Truncate(int max) => Count = Math.Min(Count, Math.Max(0, max));

    /// <summary>Bytes held by the arrays (diagnostics).</summary>
    public long MemoryBytes => Capacity * (long)((6 * 12) + (2 * 16) + (2 * 4) + 4 + 4);

    private void Grow(int capacity)
    {
        Position = Resize(Position, capacity);
        Velocity = Resize(Velocity, capacity);
        StartSize = Resize(StartSize, capacity);
        Size = Resize(Size, capacity);
        Rotation = Resize(Rotation, capacity);
        AngularVelocity = Resize(AngularVelocity, capacity);
        StartColor = Resize(StartColor, capacity);
        Color = Resize(Color, capacity);
        Age = Resize(Age, capacity);
        Lifetime = Resize(Lifetime, capacity);
        Frame = Resize(Frame, capacity);
        Seed = Resize(Seed, capacity);
    }

    private static T[] Resize<T>(T[] array, int capacity)
    {
        Array.Resize(ref array, capacity);
        return array;
    }
}
