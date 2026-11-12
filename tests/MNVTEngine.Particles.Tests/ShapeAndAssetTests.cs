using MNVTEngine.Core.Timing;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;
using MNVTEngine.Serialization;

namespace MNVTEngine.Particles.Tests;

public sealed class ShapeAndAssetTests
{
    private static List<(Vector3 Position, Vector3 Direction)> Samples(ShapeModule shape, int count = 500)
    {
        var random = new ParticleRandom(99);
        return [.. Enumerable.Range(0, count).Select(_ => ShapeSampler.Sample(shape, ref random))];
    }

    [Fact]
    public void Shapes_SampleInsideTheirGeometry_WithUnitDirections()
    {
        Assert.All(Samples(new ShapeModule { Kind = ParticleShapeKind.Circle, Radius = 2f }), s => Assert.True(s.Position.Magnitude <= 2.0001f && s.Position.Z == 0f));
        Assert.All(Samples(new ShapeModule { Kind = ParticleShapeKind.CircleEdge, Radius = 2f }), s => Assert.Equal(2f, s.Position.Magnitude, 3));
        Assert.All(Samples(new ShapeModule { Kind = ParticleShapeKind.Rectangle, Size = new Vector3(4f, 2f, 0f) }), s => Assert.True(Math.Abs(s.Position.X) <= 2f && Math.Abs(s.Position.Y) <= 1f && s.Direction == Vector3.Up));
        Assert.All(Samples(new ShapeModule { Kind = ParticleShapeKind.RectangleEdge, Size = new Vector3(4f, 2f, 0f) }), s => Assert.True(Math.Abs(Math.Abs(s.Position.X) - 2f) < 1e-4f || Math.Abs(Math.Abs(s.Position.Y) - 1f) < 1e-4f));
        Assert.All(Samples(new ShapeModule { Kind = ParticleShapeKind.Line, Length = 3f }), s => Assert.True(Math.Abs(s.Position.X) <= 1.5f && s.Position.Y == 0f));
        Assert.All(Samples(new ShapeModule { Kind = ParticleShapeKind.Sphere, Radius = 1f }), s => Assert.True(s.Position.Magnitude <= 1.0001f));
        Assert.All(Samples(new ShapeModule { Kind = ParticleShapeKind.SphereSurface, Radius = 1f }), s => Assert.Equal(1f, s.Position.Magnitude, 3));
        Assert.All(Samples(new ShapeModule { Kind = ParticleShapeKind.Hemisphere }), s => Assert.True(s.Position.Y >= 0f && s.Direction.Y >= 0f));
        Assert.All(Samples(new ShapeModule { Kind = ParticleShapeKind.Cone, Angle = 30f }), s => Assert.True(Vector3.Angle(s.Direction, Vector3.Up) <= 30.01f));
        Assert.All(Samples(new ShapeModule { Kind = ParticleShapeKind.Box, Size = new Vector3(2f, 2f, 2f) }), s => Assert.True(Math.Abs(s.Position.X) <= 1f && Math.Abs(s.Position.Z) <= 1f));
        Assert.All(Samples(new ShapeModule { Kind = ParticleShapeKind.BoxSurface, Size = new Vector3(2f, 2f, 2f) }), s => Assert.Equal(1f, Math.Max(Math.Abs(s.Position.X), Math.Max(Math.Abs(s.Position.Y), Math.Abs(s.Position.Z))), 4));
        Assert.All(Samples(new ShapeModule { Kind = ParticleShapeKind.Sphere }).Concat(Samples(new ShapeModule { Kind = ParticleShapeKind.Cone, RandomizeDirection = 0.5f })), s => Assert.Equal(1f, s.Direction.Magnitude, 3));
    }

    [Fact]
    public void ShapeTransform_MovesRotatesAndScales()
    {
        var shape = new ShapeModule { Kind = ParticleShapeKind.Point, Position = new Vector3(1f, 2f, 3f), Rotation = new Vector3(0f, 0f, -90f) };
        var (position, direction) = Samples(shape, 1)[0];
        Assert.Equal(new Vector3(1f, 2f, 3f), position);
        Assert.True(Vector3.Distance(direction, Vector3.Right) < 1e-4f);
        Assert.All(Samples(new ShapeModule { Kind = ParticleShapeKind.CircleEdge, Scale = new Vector3(3f, 1f, 1f) }), s => Assert.True(Math.Abs(s.Position.X) <= 3.0001f));
        Assert.All(Samples(new ShapeModule { Kind = ParticleShapeKind.CircleEdge, Arc = 90f }), s => Assert.True(s.Position.X >= -1e-4f && s.Position.Y >= -1e-4f));
    }

    [Fact]
    public void Asset_RoundTrips_Validates_AndListsReferences()
    {
        var texture = new AssetRef(Guid.Parse("8e000000-0000-4000-8000-000000000001"));
        var settings = new ParticleSystemSettings();
        settings.Main.Duration = 2f;
        settings.Main.StartColor = new MinMaxGradient(ParticleGradient.Between(Color.Red, Color.Yellow));
        settings.Emission.AddBurst(new ParticleBurst { Time = 0.5f, Count = new MinMaxCurve(10f, 20f), Cycles = 2 });
        settings.SizeOverLifetime.Enabled = true;
        settings.Renderer.Texture = texture;
        settings.Renderer.Blend = ParticleBlendMode.Additive;

        var json = ParticleSystemAsset.Serialize(settings);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains($"\"texture\": \"{texture}\"", json, StringComparison.Ordinal);
        var loaded = ParticleSystemAsset.Parse(json, "test.mnvtparticle");
        Assert.Equal(json, ParticleSystemAsset.Serialize(loaded));
        Assert.Equal((2f, 20f, ParticleBlendMode.Additive), (loaded.Main.Duration, loaded.Emission.Bursts.Single().Count.Max, loaded.Renderer.Blend));
        Assert.Equal(Color.Yellow.G, loaded.Main.StartColor.Evaluate(1f).G);
        Assert.Equal([texture], ParticleSystemAsset.References(loaded));
        Assert.Single(loaded.EnabledModules());

        var minimal = ParticleSystemAsset.Parse("""{ "schemaVersion": 1, "main": { "maxParticles": 50 } }""", "minimal");
        Assert.Equal((50, 5f), (minimal.Main.MaxParticles, minimal.Main.Duration)); // missing values take the defaults

        var bad = Assert.Throws<SerializationException>(() => ParticleSystemAsset.Parse("""{ "schemaVersion": 1, "main": { "duration": 0, "maxParticles": -1 }, "textureSheetAnimation": { "columns": 0 } }""", "bad"));
        Assert.Contains(bad.Diagnostics, d => d.Path == "main.duration");
        Assert.Contains(bad.Diagnostics, d => d.Path == "main.maxParticles");
        Assert.Contains(bad.Diagnostics, d => d.Path == "textureSheetAnimation");
        Assert.Throws<SerializationException>(() => ParticleSystemAsset.Parse("""{ "schemaVersion": 1, "nope": true }""", "unknown"));
        Assert.Throws<SerializationException>(() => ParticleSystemAsset.Parse("""{ "schemaVersion": 99 }""", "newer"));
    }

    [Fact]
    public void Component_PlaysOnStart_RunsWithTheScene_AndSerializesInline()
    {
        var registry = ComponentRegistry.CreateDefault();
        ParticleComponents.Register(registry);
        var scene = new Scene();
        var system = scene.CreateGameObject("Fire").AddComponent<ParticleSystem>();
        system.Main.AutoRandomSeed = false;
        system.Emission.RateOverTime = 20f;
        system.Emission.AddBurst(new ParticleBurst { Count = 5 });
        scene.Start();
        for (var i = 0; i < 10; i++)
        {
            scene.Update(new FrameTime(0.1f, 0.1f, 0.1 * (i + 1), 0.1 * (i + 1), i + 1));
        }

        Assert.True(system.IsPlaying && system.IsEmitting);
        Assert.InRange(system.ParticleCount, 24, 25);

        var json = new SceneSerializer(registry).Serialize(scene);
        var copy = new SceneSerializer(registry).Deserialize(json, "copy").Scene.FindObjectsOfType<ParticleSystem>().Single();
        Assert.Equal((20f, 1), (copy.Emission.RateOverTime.Max, copy.Emission.Bursts.Count));

        // Lists in scene data replace the defaults (Newtonsoft would otherwise add to them): the default color over
        // lifetime gradient has white keys, which must not survive next to the scene's.
        system.Settings.ColorOverLifetime.Color = new MinMaxGradient(ParticleGradient.Between(Color.Red, Color.Blue));
        var reloaded = new SceneSerializer(registry).Deserialize(new SceneSerializer(registry).Serialize(scene), "reload").Scene.FindObjectsOfType<ParticleSystem>().First();
        Assert.Equal(2, reloaded.Settings.ColorOverLifetime.Color.GradientMax!.ColorKeys.Count);
        Assert.Equal(Color.Red, reloaded.Settings.ColorOverLifetime.Color.Evaluate(0f));
        Assert.Equal(0, copy.ParticleCount); // particles are runtime state

        var once = scene.CreateGameObject("Once").AddComponent<ParticleSystem>();
        once.Loop = false;
        once.Duration = 0.1f;
        once.Main.StartLifetime = 0.1f;
        once.Main.StopAction = ParticleStopAction.Disable;
        var stopped = 0;
        once.Stopped += _ => stopped++;
        once.Play();
        for (var i = 0; i < 5; i++)
        {
            scene.Update(new FrameTime(0.1f, 0.1f, 1.0 + (0.1 * i), 1.0 + (0.1 * i), 20 + i));
        }

        Assert.Equal((1, false), (stopped, once.GameObject.ActiveSelf));
    }
}
