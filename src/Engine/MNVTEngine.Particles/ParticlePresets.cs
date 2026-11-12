using MNVTEngine.Mathematics;

namespace MNVTEngine.Particles;

/// <summary>
/// Starting configurations for common effects. Each preset is ordinary <see cref="ParticleSystemSettings"/>: editable
/// like any other system, with no special behavior. <c>planar</c> picks 2D shapes (XY plane) instead of 3D ones.
/// Presets set no texture: assign one (a soft dot suits most of them).
/// </summary>
public static class ParticlePresets
{
    public static IReadOnlyList<string> Names { get; } =
        ["Fire", "Smoke", "Sparks", "Explosion", "Dust", "Rain", "Snow", "Magic sparkle", "Energy beam", "Footsteps dust", "Water splash"];

    /// <exception cref="ArgumentException">Unknown preset name.</exception>
    public static ParticleSystemSettings Create(string name, bool planar) => name switch
    {
        "Fire" => Fire(planar),
        "Smoke" => Smoke(planar),
        "Sparks" => Sparks(planar),
        "Explosion" => Explosion(planar),
        "Dust" => Dust(planar),
        "Rain" => Rain(planar),
        "Snow" => Snow(planar),
        "Magic sparkle" => Magic(planar),
        "Energy beam" => Beam(planar),
        "Footsteps dust" => Footsteps(planar),
        "Water splash" => Splash(planar),
        _ => throw new ArgumentException($"No particle preset '{name}'; presets: {string.Join(", ", Names)}.", nameof(name)),
    };

    private static ParticleSystemSettings New(float lifetimeMin, float lifetimeMax, float speedMin, float speedMax, float sizeMin, float sizeMax, int max)
    {
        var s = new ParticleSystemSettings();
        s.Main.StartLifetime = new MinMaxCurve(lifetimeMin, lifetimeMax);
        s.Main.StartSpeed = new MinMaxCurve(speedMin, speedMax);
        s.Main.StartSize = new MinMaxCurve(sizeMin, sizeMax);
        s.Main.MaxParticles = max;
        return s;
    }

    private static MinMaxGradient Fade(Color from, Color to, float peakAlpha = 1f) =>
        new(new ParticleGradient([new(0f, from), new(1f, to)], [new(0f, 0f), new(0.15f, peakAlpha), new(1f, 0f)]));

    private static ParticleSystemSettings Fire(bool planar)
    {
        var s = New(0.6f, 1.1f, 1.5f, 2.5f, 0.5f, 0.9f, 300);
        s.Main.StartRotation = new MinMaxCurve(0f, 360f);
        s.Emission.RateOverTime = 60f;
        s.Shape.Kind = planar ? ParticleShapeKind.Line : ParticleShapeKind.Cone;
        s.Shape.Length = 0.6f;
        s.Shape.Radius = 0.3f;
        s.Shape.Angle = 10f;
        s.ColorOverLifetime.Enabled = true;
        s.ColorOverLifetime.Color = Fade(new Color(1f, 0.6f, 0.1f), new Color(0.5f, 0.05f, 0.02f), 0.8f);
        s.SizeOverLifetime.Enabled = true;
        s.SizeOverLifetime.Size = new MinMaxCurve(ParticleCurve.Linear(1f, 0.2f));
        return s;
    }

    private static ParticleSystemSettings Smoke(bool planar)
    {
        var s = New(2f, 3.5f, 0.5f, 1.2f, 0.6f, 1.2f, 200);
        s.Main.StartRotation = new MinMaxCurve(0f, 360f);
        s.Main.StartColor = new Color(0.5f, 0.5f, 0.5f, 1f);
        s.Main.SimulationSpace = ParticleSimulationSpace.World;
        s.Emission.RateOverTime = 15f;
        s.Shape.Kind = planar ? ParticleShapeKind.Line : ParticleShapeKind.Cone;
        s.Shape.Angle = 15f;
        s.Shape.Radius = 0.3f;
        s.ColorOverLifetime.Enabled = true;
        s.ColorOverLifetime.Color = Fade(Color.White, Color.White, 0.5f);
        s.SizeOverLifetime.Enabled = true;
        s.SizeOverLifetime.Size = new MinMaxCurve(ParticleCurve.Linear(0.6f, 2f));
        s.RotationOverLifetime.Enabled = true;
        s.RotationOverLifetime.AngularVelocity = new MinMaxCurve(-30f, 30f);
        return s;
    }

    private static ParticleSystemSettings Sparks(bool planar)
    {
        var s = New(0.4f, 1f, 4f, 8f, 0.05f, 0.1f, 300);
        s.Main.StartColor = new MinMaxGradient { Mode = GradientValueMode.RandomBetweenColors, ColorMin = new Color(1f, 0.7f, 0.2f), ColorMax = new Color(1f, 1f, 0.7f) };
        s.Main.GravityModifier = 1f;
        s.Main.SimulationSpace = ParticleSimulationSpace.World;
        s.Emission.RateOverTime = 0f;
        s.Emission.AddBurst(new ParticleBurst { Count = new MinMaxCurve(40f, 60f), Cycles = 0, Interval = 1f });
        s.Shape.Kind = planar ? ParticleShapeKind.Circle : ParticleShapeKind.Sphere;
        s.Shape.Radius = 0.1f;
        s.Renderer.Blend = ParticleBlendMode.Additive;
        s.Renderer.RenderMode = ParticleRenderMode.Stretched;
        s.Renderer.SpeedScale = 0.05f;
        s.Renderer.LengthScale = 2f;
        return s;
    }

    private static ParticleSystemSettings Explosion(bool planar)
    {
        var s = New(0.5f, 1.2f, 3f, 7f, 0.4f, 1f, 200);
        s.Main.Duration = 1f;
        s.Main.Loop = false;
        s.Main.StartRotation = new MinMaxCurve(0f, 360f);
        s.Emission.RateOverTime = 0f;
        s.Emission.AddBurst(new ParticleBurst { Count = 80 });
        s.Shape.Kind = planar ? ParticleShapeKind.Circle : ParticleShapeKind.Sphere;
        s.Shape.Radius = 0.3f;
        s.VelocityOverLifetime.Enabled = true;
        s.VelocityOverLifetime.SpeedModifier = new MinMaxCurve(ParticleCurve.Linear(1f, 0.1f));
        s.ColorOverLifetime.Enabled = true;
        s.ColorOverLifetime.Color = new MinMaxGradient(new ParticleGradient(
            [new(0f, new Color(1f, 1f, 0.8f)), new(0.2f, new Color(1f, 0.5f, 0.1f)), new(0.6f, new Color(0.3f, 0.3f, 0.3f)), new(1f, new Color(0.2f, 0.2f, 0.2f))],
            [new(0f, 1f), new(0.6f, 0.6f), new(1f, 0f)]));
        s.SizeOverLifetime.Enabled = true;
        s.SizeOverLifetime.Size = new MinMaxCurve(ParticleCurve.Linear(0.5f, 1.5f));
        s.Renderer.Blend = ParticleBlendMode.Additive;
        return s;
    }

    private static ParticleSystemSettings Dust(bool planar)
    {
        var s = New(4f, 8f, 0.05f, 0.2f, 0.03f, 0.08f, 300);
        s.Main.StartColor = new Color(0.9f, 0.85f, 0.7f, 0.6f);
        s.Emission.RateOverTime = 20f;
        s.Shape.Kind = planar ? ParticleShapeKind.Rectangle : ParticleShapeKind.Box;
        s.Shape.Size = new Vector3(8f, 4f, 8f);
        s.Shape.RandomizeDirection = 1f;
        s.ColorOverLifetime.Enabled = true;
        s.ColorOverLifetime.Color = Fade(Color.White, Color.White, 0.6f);
        return s;
    }

    private static ParticleSystemSettings Rain(bool planar)
    {
        var s = New(1f, 1.5f, 10f, 12f, 0.03f, 0.05f, 1000);
        s.Main.StartColor = new Color(0.7f, 0.8f, 1f, 0.6f);
        s.Main.SimulationSpace = ParticleSimulationSpace.World;
        s.Emission.RateOverTime = 300f;
        s.Shape.Kind = planar ? ParticleShapeKind.Line : ParticleShapeKind.Box;
        s.Shape.Length = 16f;
        s.Shape.Size = new Vector3(16f, 0.1f, 16f);
        s.Shape.Rotation = new Vector3(0f, 0f, 180f); // emit downward
        s.Renderer.RenderMode = ParticleRenderMode.Stretched;
        s.Renderer.SpeedScale = 0.04f;
        return s;
    }

    private static ParticleSystemSettings Snow(bool planar)
    {
        var s = New(6f, 9f, 0.5f, 1f, 0.06f, 0.15f, 600);
        s.Main.StartColor = new Color(0.95f, 0.97f, 1f, 0.9f);
        s.Main.SimulationSpace = ParticleSimulationSpace.World;
        s.Emission.RateOverTime = 40f;
        s.Shape.Kind = planar ? ParticleShapeKind.Line : ParticleShapeKind.Box;
        s.Shape.Length = 16f;
        s.Shape.Size = new Vector3(16f, 0.1f, 16f);
        s.Shape.Rotation = new Vector3(0f, 0f, 180f);
        s.VelocityOverLifetime.Enabled = true;
        s.VelocityOverLifetime.X = new MinMaxCurve { Mode = CurveMode.RandomBetweenCurves, CurveMin = ParticleCurve.Linear(-0.4f, 0.4f), CurveMax = ParticleCurve.Linear(0.4f, -0.4f) };
        return s;
    }

    private static ParticleSystemSettings Magic(bool planar)
    {
        var s = New(0.8f, 1.6f, 0.2f, 0.8f, 0.2f, 0.5f, 200);
        s.Main.StartColor = new MinMaxGradient { Mode = GradientValueMode.RandomBetweenColors, ColorMin = new Color(0.4f, 0.6f, 1f), ColorMax = new Color(1f, 0.5f, 1f) };
        s.Emission.RateOverTime = 40f;
        s.Shape.Kind = planar ? ParticleShapeKind.CircleEdge : ParticleShapeKind.SphereSurface;
        s.Shape.Radius = 1f;
        s.RotationOverLifetime.Enabled = true;
        s.RotationOverLifetime.AngularVelocity = new MinMaxCurve(-180f, 180f);
        s.ColorOverLifetime.Enabled = true;
        s.ColorOverLifetime.Color = Fade(Color.White, Color.White);
        s.Renderer.Blend = ParticleBlendMode.Additive;
        return s;
    }

    private static ParticleSystemSettings Beam(bool planar)
    {
        var s = New(0.3f, 0.5f, 12f, 14f, 0.15f, 0.25f, 300);
        s.Main.StartColor = new Color(0.4f, 0.9f, 1f);
        s.Emission.RateOverTime = 200f;
        s.Shape.Kind = ParticleShapeKind.Point;
        s.Shape.Rotation = new Vector3(0f, 0f, -90f); // along +X
        s.ColorOverLifetime.Enabled = true;
        s.ColorOverLifetime.Color = Fade(Color.White, new Color(0.2f, 0.4f, 1f));
        s.Renderer.Blend = ParticleBlendMode.Additive;
        s.Renderer.RenderMode = ParticleRenderMode.Stretched;
        s.Renderer.SpeedScale = 0.05f;
        _ = planar;
        return s;
    }

    private static ParticleSystemSettings Footsteps(bool planar)
    {
        var s = New(0.4f, 0.8f, 0.5f, 1.5f, 0.15f, 0.35f, 100);
        s.Main.Duration = 0.5f;
        s.Main.Loop = false;
        s.Main.StartColor = new Color(0.75f, 0.68f, 0.55f, 0.8f);
        s.Main.GravityModifier = 0.2f;
        s.Emission.RateOverTime = 0f;
        s.Emission.AddBurst(new ParticleBurst { Count = new MinMaxCurve(6f, 10f) });
        s.Shape.Kind = planar ? ParticleShapeKind.Line : ParticleShapeKind.Hemisphere;
        s.Shape.Length = 0.4f;
        s.Shape.Radius = 0.2f;
        s.Shape.RandomizeDirection = planar ? 0.4f : 0f;
        s.ColorOverLifetime.Enabled = true;
        s.ColorOverLifetime.Color = Fade(Color.White, Color.White);
        s.SizeOverLifetime.Enabled = true;
        s.SizeOverLifetime.Size = new MinMaxCurve(ParticleCurve.Linear(0.6f, 1.4f));
        return s;
    }

    private static ParticleSystemSettings Splash(bool planar)
    {
        var s = New(0.5f, 0.9f, 2.5f, 5f, 0.06f, 0.14f, 200);
        s.Main.Duration = 1f;
        s.Main.Loop = false;
        s.Main.StartColor = new Color(0.6f, 0.8f, 1f, 0.9f);
        s.Main.GravityModifier = 1f;
        s.Main.SimulationSpace = ParticleSimulationSpace.World;
        s.Emission.RateOverTime = 0f;
        s.Emission.AddBurst(new ParticleBurst { Count = new MinMaxCurve(30f, 45f) });
        s.Shape.Kind = planar ? ParticleShapeKind.Point : ParticleShapeKind.Cone;
        s.Shape.Angle = 35f;
        s.Shape.Radius = 0.2f;
        s.Shape.RandomizeDirection = planar ? 0.35f : 0f;
        s.Renderer.RenderMode = ParticleRenderMode.Stretched;
        s.Renderer.SpeedScale = 0.03f;
        return s;
    }
}
