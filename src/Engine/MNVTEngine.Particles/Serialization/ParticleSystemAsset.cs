using MNVTEngine.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Particles;

/// <summary>
/// The <c>.mnvtparticle</c> asset: <see cref="ParticleSystemSettings"/> as JSON with a schema version. Shared by the
/// importer (validation, dependencies) and the runtime. Only data: no delegates, no runtime objects; textures and
/// materials are <c>asset:</c> references.
/// <code>
/// { "schemaVersion": 1, "main": { "duration": 2, "startLifetime": { "max": 1.5 } }, "emission": { "rateOverTime": { "max": 30 } },
///   "shape": { "kind": "circle", "radius": 0.5 }, "renderer": { "texture": "asset:&lt;guid&gt;", "blend": "additive" } }
/// </code>
/// </summary>
public static class ParticleSystemAsset
{
    public const string Extension = ".mnvtparticle";
    public const int CurrentSchemaVersion = 1;
    public const int MaxParticlesLimit = 1_000_000;

    /// <exception cref="SerializationException">Malformed JSON, unknown fields, or invalid values; every problem is listed.</exception>
    public static ParticleSystemSettings Parse(string json, string sourceName)
    {
        var diagnostics = new List<SerializationDiagnostic>();
        ParticleSystemSettings? settings = null;
        try
        {
            var root = JObject.Parse(json);
            new JsonMigrationPipeline(CurrentSchemaVersion).Migrate(root, diagnostics);
            root.Remove("schemaVersion");
            settings = root.ToObject<ParticleSystemSettings>(CreateSerializer());
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Error, "", ex.Message));
        }

        if (settings is null)
        {
            diagnostics.Add(new SerializationDiagnostic(DiagnosticSeverity.Error, "", "The particle system is empty."));
        }
        else
        {
            diagnostics.AddRange(Validate(settings).Select(p => new SerializationDiagnostic(DiagnosticSeverity.Error, p.Path, p.Message)));
        }

        return diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)
            ? throw new SerializationException(sourceName, string.Join(" ", diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())), diagnostics)
            : settings!;
    }

    public static string Serialize(ParticleSystemSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = new JObject { ["schemaVersion"] = CurrentSchemaVersion };
        root.Merge(JObject.FromObject(settings, CreateSerializer()));
        return root.ToString(Formatting.Indented);
    }

    /// <summary>Values the simulation cannot run with; empty when valid.</summary>
    public static IReadOnlyList<(string Path, string Message)> Validate(ParticleSystemSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var problems = new List<(string, string)>();
        void Check(bool ok, string path, string message)
        {
            if (!ok)
            {
                problems.Add((path, message));
            }
        }

        var main = settings.Main;
        Check(float.IsFinite(main.Duration) && main.Duration > 0f, "main.duration", "must be greater than 0.");
        Check(main.MaxParticles is >= 0 and <= MaxParticlesLimit, "main.maxParticles", $"must be 0 to {MaxParticlesLimit}.");
        Check(float.IsFinite(main.StartDelay) && main.StartDelay >= 0f, "main.startDelay", "must be 0 or more.");
        Check(float.IsFinite(main.SimulationSpeed) && main.SimulationSpeed >= 0f, "main.simulationSpeed", "must be 0 or more.");
        var sheet = settings.TextureSheetAnimation;
        Check(sheet.Columns is >= 1 and <= 256 && sheet.Rows is >= 1 and <= 256, "textureSheetAnimation", "columns and rows must be 1 to 256.");
        foreach (var burst in settings.Emission.Bursts)
        {
            Check(float.IsFinite(burst.Time) && burst.Time >= 0f && burst.Cycles >= 0 && burst.Probability is >= 0f and <= 1f, "emission.bursts", "time must be 0 or more, cycles 0 or more, probability 0 to 1.");
        }

        Check(float.IsFinite(settings.Noise.Frequency) && float.IsFinite(settings.Noise.ScrollSpeed), "noise", "frequency and scroll speed must be finite numbers.");
        var collision = settings.Collision;
        Check(collision.Bounce is >= 0f and <= 1f && collision.Dampen is >= 0f and <= 1f && collision.LifetimeLoss is >= 0f and <= 1f, "collision", "bounce, dampen and lifetime loss must be 0 to 1.");
        Check(collision.Planes.TrueForAll(p => p.Normal.SqrMagnitude > 1e-12f), "collision.planes", "every plane needs a non-zero normal.");
        Check(settings.SubEmitters.MaxEmitsPerStep >= 0, "subEmitters.maxEmitsPerStep", "must be 0 or more.");
        Check(settings.SubEmitters.Emitters.TrueForAll(e => e.Count >= 0 && e.Probability is >= 0f and <= 1f && e.InheritVelocity is >= 0f and <= 1f), "subEmitters.emitters", "count must be 0 or more; probability and inherit velocity 0 to 1.");
        foreach (var (path, curve) in Curves(settings))
        {
            problems.AddRange(curve.Validate(path).Select(p => (path, p)));
        }

        return problems;
    }

    /// <summary>Assets the system references (texture, material), for dependency tracking.</summary>
    public static IEnumerable<AssetRef> References(ParticleSystemSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new[] { settings.Renderer.Texture, settings.Renderer.Material }.Where(r => !r.IsNone);
    }

    private static IEnumerable<(string, MinMaxCurve)> Curves(ParticleSystemSettings s) =>
    [
        ("main.startLifetime", s.Main.StartLifetime), ("main.startSpeed", s.Main.StartSpeed), ("main.startSize", s.Main.StartSize),
        ("main.startRotation", s.Main.StartRotation), ("main.gravityModifier", s.Main.GravityModifier),
        ("emission.rateOverTime", s.Emission.RateOverTime), ("emission.rateOverDistance", s.Emission.RateOverDistance),
        ("velocityOverLifetime.x", s.VelocityOverLifetime.X), ("velocityOverLifetime.y", s.VelocityOverLifetime.Y), ("velocityOverLifetime.z", s.VelocityOverLifetime.Z),
        ("forceOverLifetime.x", s.ForceOverLifetime.X), ("forceOverLifetime.y", s.ForceOverLifetime.Y), ("forceOverLifetime.z", s.ForceOverLifetime.Z),
        ("sizeOverLifetime.size", s.SizeOverLifetime.Size), ("rotationOverLifetime.angularVelocity", s.RotationOverLifetime.AngularVelocity),
        ("textureSheetAnimation.frameOverTime", s.TextureSheetAnimation.FrameOverTime), ("noise.strength", s.Noise.Strength),
    ];

    private static JsonSerializer CreateSerializer()
    {
        var serializer = MnvtJson.CreateSerializer();
        serializer.Converters.Add(new AssetRefConverter());
        serializer.ObjectCreationHandling = ObjectCreationHandling.Replace; // lists and modules from the file replace the defaults
        return serializer;
    }
}
