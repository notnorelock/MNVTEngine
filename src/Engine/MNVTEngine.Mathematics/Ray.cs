using System.Globalization;

namespace MNVTEngine.Mathematics;

/// <summary>Half-line from <see cref="Origin"/> along a unit-length <see cref="Direction"/>.</summary>
public readonly struct Ray : IEquatable<Ray>
{
    /// <param name="direction">Normalized by the constructor.</param>
    public Ray(Vector3 origin, Vector3 direction)
    {
        Origin = origin;
        Direction = direction.Normalized;
    }

    public Vector3 Origin { get; }
    public Vector3 Direction { get; }

    public Vector3 GetPoint(float distance) => Origin + (Direction * distance);

    public static bool operator ==(Ray a, Ray b) => a.Equals(b);
    public static bool operator !=(Ray a, Ray b) => !a.Equals(b);

    public bool Equals(Ray other) => Origin.Equals(other.Origin) && Direction.Equals(other.Direction);
    public override bool Equals(object? obj) => obj is Ray other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Origin, Direction);

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"Ray(origin: {Origin}, direction: {Direction})");
}
