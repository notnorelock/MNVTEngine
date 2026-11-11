using MNVTEngine.Mathematics;
using System.Globalization;
using MNVTEngine.Terrain.Geometry;

namespace MNVTEngine.Terrain.Project;

/// <summary>Project-wide settings as TerrainHeaven's project files store them.</summary>
public sealed class ProjectSettingsData
{
    public string SkyColor { get; set; } = "#1e1e1e";

    public bool DayNightCycle { get; set; }

    public double Hour { get; set; } = 12;

    public bool? EnhancedVisuals { get; set; }

    public double? DayLength { get; set; }

    public List<double>? LodDistances { get; set; }
}

/// <summary>
/// The sky and lighting a renderer should show for the current settings, evaluated by <see cref="ProjectSettings.Evaluate"/>.
/// Colors are linear RGB. Directions point from the scene toward the light; celestial positions are relative to the camera.
/// </summary>
public sealed record SkyEnvironment(
    Vector3 Background,
    bool SkyDomeVisible,
    Vector3 SkyZenith,
    Vector3 SkyHorizon,
    Vector3? FogColor,
    double FogNear,
    double FogFar,
    Vector3 SunLightDirection,
    double SunLightIntensity,
    Vector3 SunLightColor,
    bool SunCastsShadows,
    double AmbientIntensity,
    Vector3 AmbientColor,
    bool HemisphereVisible,
    double HemisphereIntensity,
    Vector3 HemisphereSkyColor,
    Vector3 HemisphereGroundColor,
    bool SunVisible,
    Vector3 SunPosition,
    bool MoonVisible,
    Vector3 MoonPosition,
    bool SunGlowVisible,
    double SunGlowOpacity,
    Vector3 SunGlowColor,
    double SunGlowScale,
    double StarOpacity,
    bool CloudsVisible,
    double CloudOpacity,
    Vector3 CloudColor);

/// <summary>
/// Sky color, time of day, day/night cycle and LOD switch distances. Three modes as TerrainHeaven renders them: a flat
/// sky color, a basic day/night cycle, and enhanced visuals (sky dome, fog, sun glow, stars, clouds, shadowed sun).
/// </summary>
public sealed class ProjectSettings
{
    private const double EnvironmentUpdateInterval = 0.1;

    // Muted colors keep the viewport readable and avoid a cinematic/over-saturated look.
    private static readonly (double Hour, int Zenith, int Horizon, int Fog)[] SkyKeyframes =
    [
        (0, 0x101624, 0x222838, 0x121722),
        (5, 0x30394e, 0x8a665e, 0x4b4144),
        (7, 0x6e91ad, 0xc4aba0, 0xa99b91),
        (10, 0x7298ba, 0xbec6c9, 0xaeb5b6),
        (16, 0x6d91ad, 0xbdb7ae, 0xaaa49c),
        (18.5, 0x655f68, 0xc28e72, 0x9b857c),
        (20.5, 0x293146, 0x5c4b52, 0x34333d),
        (24, 0x101624, 0x222838, 0x121722),
    ];

    private static readonly Vector3 WarmSun = Hex(0xffb878);
    private static readonly Vector3 DaylightSun = Hex(0xfff4e8);
    private static readonly Vector3 NightLight = Hex(0x91a6c8);
    private static readonly Vector3 DaySkyFill = Hex(0xb9c9d3);
    private static readonly Vector3 DayGroundFill = Hex(0x62675d);
    private static readonly Vector3 NightSkyFill = Hex(0x48566e);
    private static readonly Vector3 NightGroundFill = Hex(0x262c31);
    private static readonly Vector3 CloudDay = Hex(0xd8d4c9);
    private static readonly Vector3 CloudNight = Hex(0x697385);

    private double _environmentAccumulator;
    private readonly double[] _lodDistances = [30, 80, 160];

    public string SkyColor { get; set; } = "#1e1e1e";

    public bool EnhancedVisuals { get; set; } = true;

    public bool DayNightCycle { get; set; }

    public double Hour { get; set; } = 12;

    /// <summary>Seconds per in-game day.</summary>
    public double DayLength { get; set; } = 240;

    /// <summary>Distances (meters) where LOD0 switches to 1, 1 to 2 and 2 to 3, kept with the world as TerrainHeaven does.</summary>
    public IReadOnlyList<double> LodDistances => _lodDistances;

    /// <summary>Raised when the day/night cycle advanced the hour.</summary>
    public event Action<double>? TimeChanged;

    public void SetLodDistance(int index, double value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, 2);
        _lodDistances[index] = MathUtil.Clamp(double.IsFinite(value) ? value : _lodDistances[index], 1, 5000);
    }

    /// <summary>
    /// Advances the cycle by <paramref name="delta"/> seconds. As in TerrainHeaven only enhanced visuals move the clock,
    /// in steps of at least 0.1 s; returns whether the hour changed.
    /// </summary>
    public bool Advance(double delta)
    {
        if (!EnhancedVisuals || !DayNightCycle)
        {
            return false;
        }

        _environmentAccumulator += Math.Min(delta, 0.25);
        if (_environmentAccumulator < EnvironmentUpdateInterval)
        {
            return false;
        }

        var elapsed = _environmentAccumulator;
        _environmentAccumulator = 0;
        Hour = (Hour + (elapsed * 24 / Math.Max(30, DayLength))) % 24;
        TimeChanged?.Invoke(Hour);
        return true;
    }

    public SkyEnvironment Evaluate()
    {
        if (EnhancedVisuals)
        {
            return EvaluateEnhanced();
        }

        return DayNightCycle ? EvaluateBasicDayNight() : EvaluateFlat();
    }

    private SkyEnvironment EvaluateFlat()
    {
        var color = ParseCssHex(SkyColor);
        return new SkyEnvironment(
            color, false, color, color, null, 0, 0,
            new Vector3(10, 20, 10).Normalized, 1.5, Vector3.One, false,
            1.1, Vector3.One,
            false, 0, Vector3.Zero, Vector3.Zero,
            false, Vector3.Zero, false, Vector3.Zero,
            false, 0, Vector3.Zero, 0, 0, false, 0, Vector3.Zero);
    }

    private SkyEnvironment EvaluateBasicDayNight()
    {
        var angle = (Hour - 6) / 12 * Math.PI;
        var elevation = Math.Sin(angle);
        var (zenith, horizon, _) = GetSkyPalette(Hour);
        var sun = new Vector3(Math.Cos(angle) * 200, Math.Sin(angle) * 200, 0);
        return new SkyEnvironment(
            zenith, false, zenith, horizon, null, 0, 0,
            new Vector3(Math.Cos(angle) * 20, Math.Max(elevation * 20, 0.5), 10).Normalized,
            Math.Max(0, elevation) * 1.1, elevation < 0.3 ? Hex(0xffb47c) : Vector3.One, false,
            0.35 + (Math.Max(0, elevation) * 0.5), horizon,
            false, 0, Vector3.Zero, Vector3.Zero,
            elevation > -0.1, sun, elevation < 0.1, -sun,
            false, 0, Vector3.Zero, 0, 0, false, 0, Vector3.Zero);
    }

    private SkyEnvironment EvaluateEnhanced()
    {
        var angle = (Hour - 6) / 24 * Math.PI * 2;
        var elevation = Math.Sin(angle);
        var daylight = Smoothstep(-0.12, 0.18, elevation);
        var (zenith, horizon, fog) = GetSkyPalette(Hour);

        const double celestialDistance = 260;
        var sun = new Vector3(Math.Cos(angle) * celestialDistance, elevation * celestialDistance, -70);
        var sunVisible = elevation > -0.08;

        // Glow peaks near the horizon (low sun) and fades at high noon, matching a hazy golden-hour look.
        var horizonGlow = 1 - Smoothstep(0.15, 0.55, Math.Abs(elevation));
        var glowOpacity = MathUtil.Clamp(0.35 + (horizonGlow * 0.55), 0, 0.9) * Smoothstep(-0.1, 0.05, elevation);

        var lightDirection = new Vector3(Math.Cos(angle), elevation, -0.28).Normalized;
        if (elevation < 0)
        {
            lightDirection = -lightDirection;
        }

        return new SkyEnvironment(
            zenith, true, zenith, horizon, fog, 130, 520,
            lightDirection, MathUtil.Lerp(0.35, 1.1, daylight), (elevation < 0 ? NightLight : WarmSun).LerpUnclamped(DaylightSun, daylight), true,
            MathUtil.Lerp(0.4, 0.55, daylight), horizon,
            true, MathUtil.Lerp(0.65, 0.85, daylight), NightSkyFill.LerpUnclamped(DaySkyFill, daylight), NightGroundFill.LerpUnclamped(DayGroundFill, daylight),
            sunVisible, sun, elevation < 0.12, -sun,
            sunVisible, glowOpacity, WarmSun.LerpUnclamped(DaylightSun, daylight * 0.5), 130 + (horizonGlow * 90),
            MathUtil.Clamp(0.75 - daylight, 0, 0.75),
            true, MathUtil.Lerp(0.08, 0.22, daylight), CloudNight.LerpUnclamped(CloudDay, daylight));
    }

    /// <summary>Zenith, horizon and fog colors (linear) for an hour, smoothstepped between the keyframes.</summary>
    public static (Vector3 Zenith, Vector3 Horizon, Vector3 Fog) GetSkyPalette(double hour)
    {
        var wrapped = ((hour % 24) + 24) % 24;
        var left = SkyKeyframes[0];
        var right = SkyKeyframes[^1];
        for (var index = 0; index < SkyKeyframes.Length - 1; index++)
        {
            if (wrapped >= SkyKeyframes[index].Hour && wrapped <= SkyKeyframes[index + 1].Hour)
            {
                left = SkyKeyframes[index];
                right = SkyKeyframes[index + 1];
                break;
            }
        }

        var rawT = (wrapped - left.Hour) / Math.Max(0.001, right.Hour - left.Hour);
        var t = rawT * rawT * (3 - (2 * rawT));
        return (Hex(left.Zenith).LerpUnclamped(Hex(right.Zenith), t), Hex(left.Horizon).LerpUnclamped(Hex(right.Horizon), t), Hex(left.Fog).LerpUnclamped(Hex(right.Fog), t));
    }

    public ProjectSettingsData GetData() => new()
    {
        SkyColor = SkyColor,
        EnhancedVisuals = EnhancedVisuals,
        DayNightCycle = DayNightCycle,
        Hour = Hour,
        DayLength = DayLength,
        LodDistances = [.. _lodDistances],
    };

    public void LoadData(ProjectSettingsData? data)
    {
        SkyColor = data?.SkyColor ?? "#1e1e1e";
        EnhancedVisuals = data?.EnhancedVisuals ?? false;
        DayNightCycle = data?.DayNightCycle ?? false;
        Hour = MathUtil.Clamp(data?.Hour ?? 12, 0, 24);
        DayLength = MathUtil.Clamp(data?.DayLength ?? 240, 30, 1200);
        var distances = data?.LodDistances ?? [30, 80, 160];
        double[] defaults = [30, 80, 160];
        for (var index = 0; index < 3; index++)
        {
            SetLodDistance(index, index < distances.Count ? distances[index] : defaults[index]);
        }

        _environmentAccumulator = 0;
    }

    private static double Smoothstep(double min, double max, double value)
    {
        var t = MathUtil.Clamp((value - min) / (max - min), 0, 1);
        return t * t * (3 - (2 * t));
    }

    /// <summary>An sRGB hex color in linear RGB (three.js converts hex colors the same way).</summary>
    public static Vector3 Hex(int rgb) => new(SrgbToLinear((rgb >> 16) & 0xff), SrgbToLinear((rgb >> 8) & 0xff), SrgbToLinear(rgb & 0xff));

    /// <summary>A CSS "#rrggbb" or "#rgb" color in linear RGB; anything else is black (three.js warns and keeps black too).</summary>
    public static Vector3 ParseCssHex(string? color)
    {
        var hex = color?.Trim().TrimStart('#') ?? "";
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        }

        return hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb) ? Hex(rgb) : Vector3.Zero;
    }

    private static double SrgbToLinear(int channel)
    {
        var c = channel / 255.0;
        return c < 0.04045 ? c * 0.0773993808 : Math.Pow((c * 0.9478672986) + 0.0521327014, 2.4);
    }
}
