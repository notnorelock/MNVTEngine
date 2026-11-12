using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles;

/// <summary>
/// Emitter shapes. 2D shapes lie in the XY plane; the main emission axis of the directional shapes is +Y (up), so
/// fire, smoke and fountains need no rotation in either profile.
/// </summary>
public enum ParticleShapeKind
{
    /// <summary>From the emitter's position, along +Y.</summary>
    Point,

    /// <summary>2D: inside a circle (XY), outward.</summary>
    Circle,

    /// <summary>2D: on a circle's edge (XY), outward.</summary>
    CircleEdge,

    /// <summary>2D: inside a rectangle (XY), along +Y.</summary>
    Rectangle,

    /// <summary>2D: on a rectangle's edge (XY), outward from its center.</summary>
    RectangleEdge,

    /// <summary>2D: along a line on X, along +Y.</summary>
    Line,

    /// <summary>3D: inside a sphere, outward.</summary>
    Sphere,

    /// <summary>3D: on a sphere's surface, outward.</summary>
    SphereSurface,

    /// <summary>3D: inside the upper half of a sphere (+Y), outward.</summary>
    Hemisphere,

    /// <summary>3D: from a disc of <see cref="ShapeModule.Radius"/> at the base, spreading by <see cref="ShapeModule.Angle"/> around +Y.</summary>
    Cone,

    /// <summary>3D: inside a box, along +Y.</summary>
    Box,

    /// <summary>3D: on a box's surface, outward along the face normal.</summary>
    BoxSurface,
}

/// <summary>Where particles start and which way they move. Position, rotation and scale place the shape relative to the emitter.</summary>
public sealed class ShapeModule
{
    public bool Enabled { get; set; } = true;

    public ParticleShapeKind Kind { get; set; } = ParticleShapeKind.Cone;

    /// <summary>Circle, sphere, hemisphere and cone base radius.</summary>
    public float Radius { get; set; } = 1f;

    /// <summary>Part of the radius particles start in, measured from the edge: 1 fills the shape, 0 emits from its edge only.</summary>
    public float RadiusThickness { get; set; } = 1f;

    /// <summary>Cone half-angle in degrees.</summary>
    public float Angle { get; set; } = 25f;

    /// <summary>Degrees of the circle, sphere or cone used, from +X counterclockwise (360 is all of it).</summary>
    public float Arc { get; set; } = 360f;

    /// <summary>Line length.</summary>
    public float Length { get; set; } = 1f;

    /// <summary>Rectangle (X, Y) and box size.</summary>
    public Vector3 Size { get; set; } = Vector3.One;

    public Vector3 Position { get; set; }

    /// <summary>Euler angles in degrees.</summary>
    public Vector3 Rotation { get; set; }

    public Vector3 Scale { get; set; } = Vector3.One;

    /// <summary>Blends the direction toward a random one: 0 keeps the shape's, 1 is fully random.</summary>
    public float RandomizeDirection { get; set; }

    /// <summary>True for the shapes that lie in the XY plane (the 2D profile's shapes; Point is shared).</summary>
    public static bool IsPlanar(ParticleShapeKind kind) => kind is ParticleShapeKind.Circle or ParticleShapeKind.CircleEdge
        or ParticleShapeKind.Rectangle or ParticleShapeKind.RectangleEdge or ParticleShapeKind.Line;
}
