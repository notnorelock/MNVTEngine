namespace MNVTEngine.Terrain.Geometry;

/// <summary>three.js MathUtils equivalents used throughout the tools.</summary>
public static class MathUtil
{
    public const double Deg2Rad = Math.PI / 180;
    public const double Rad2Deg = 180 / Math.PI;

    public static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

    public static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    public static double DegToRad(double degrees) => degrees * Deg2Rad;

    public static double RadToDeg(double radians) => radians * Rad2Deg;

    /// <summary>JavaScript Math.round: halves round up (toward +infinity), unlike Math.Round's banker's rounding.</summary>
    public static double JsRound(double value) => Math.Floor(value + 0.5);

    public static double Hypot(double x, double y) => Math.Sqrt((x * x) + (y * y));

    public static double Smoothstep(double t) => t * t * (3 - (2 * t));
}
